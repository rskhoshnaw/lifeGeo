using System.Globalization;
using LifeDrop.Application.Transfers;
using LifeDrop.Domain.Common;
using Npgsql;

namespace LifeDrop.Infrastructure.Transfers;

// Transfers of goods between warehouses (plan step 5.60). Every change of stock and of the transfer happens in one transaction, with the transfer row locked, so two people can never
// receive or cancel the same transfer twice and the goods are never in two places or in none.
public sealed class PostgresTransferStore(NpgsqlDataSource dataSource) : ITransferStore
{
    private static string NumberText(long number) => "T-" + number.ToString("000000", CultureInfo.InvariantCulture);

    public async Task<(TransferResult Result, CreatedTransferDto? Created)> CreateAsync(TenantId tenantId, UserId actorId, NewTransfer transfer, Guid? staffOnly, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, actorId, cancellationToken);

        // A retry of a lost answer returns the transfer that was made.
        await using (var replay = new NpgsqlCommand("SELECT id, number, created_by, from_warehouse_id, to_warehouse_id FROM stock_transfers WHERE tenant_id = @tenant AND client_ref = @ref", connection, transaction))
        {
            replay.Parameters.AddWithValue("tenant", tenantId.Value);
            replay.Parameters.AddWithValue("ref", transfer.ClientRef);
            await using var reader = await replay.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
                return reader.GetGuid(2) == actorId.Value && reader.GetGuid(3) == transfer.FromWarehouseId && reader.GetGuid(4) == transfer.ToWarehouseId
                    ? (TransferResult.Success, new CreatedTransferDto(reader.GetGuid(0), NumberText(reader.GetInt64(1)), true))
                    : (TransferResult.Conflict, null);
        }

        // Both must be active company warehouses; a worker must work in the sending one.
        await using (var check = new NpgsqlCommand("""
            SELECT (SELECT count(*) FROM warehouses WHERE tenant_id = @tenant AND id IN (@from, @to) AND status = 'active' AND kind = 'company'),
                   (@staff::uuid IS NULL OR EXISTS (SELECT 1 FROM warehouse_staff WHERE tenant_id = @tenant AND warehouse_id = @from AND user_id = @staff))
            """, connection, transaction))
        {
            check.Parameters.AddWithValue("tenant", tenantId.Value);
            check.Parameters.AddWithValue("from", transfer.FromWarehouseId);
            check.Parameters.AddWithValue("to", transfer.ToWarehouseId);
            check.Parameters.Add(new NpgsqlParameter("staff", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = (object?)staffOnly ?? DBNull.Value });
            await using var reader = await check.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            if (reader.GetInt64(0) != 2) return (TransferResult.NotFound, null);
            if (!reader.GetBoolean(1)) return (TransferResult.Forbidden, null);
        }

        long number;
        await using (var counter = new NpgsqlCommand("""
            INSERT INTO stock_transfer_counters (tenant_id, last_number) VALUES (@tenant, 1)
            ON CONFLICT (tenant_id) DO UPDATE SET last_number = stock_transfer_counters.last_number + 1
            RETURNING last_number
            """, connection, transaction))
        {
            counter.Parameters.AddWithValue("tenant", tenantId.Value);
            number = Convert.ToInt64(await counter.ExecuteScalarAsync(cancellationToken));
        }

        var id = Guid.NewGuid();
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO stock_transfers (id, tenant_id, number, from_warehouse_id, to_warehouse_id, note, client_ref, created_by)
            VALUES (@id, @tenant, @number, @from, @to, @note, @ref, @actor)
            """, connection, transaction))
        {
            insert.Parameters.AddWithValue("id", id);
            insert.Parameters.AddWithValue("tenant", tenantId.Value);
            insert.Parameters.AddWithValue("number", number);
            insert.Parameters.AddWithValue("from", transfer.FromWarehouseId);
            insert.Parameters.AddWithValue("to", transfer.ToWarehouseId);
            insert.Parameters.Add(new NpgsqlParameter("note", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)transfer.Note ?? DBNull.Value });
            insert.Parameters.AddWithValue("ref", transfer.ClientRef);
            insert.Parameters.AddWithValue("actor", actorId.Value);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        var lineNo = 0;
        foreach (var line in transfer.Lines)
        {
            lineNo++;
            // The goods leave the sender now; a quantity the sender does not hold is refused (everything is rolled back).
            var table = line.Bucket == "damaged" ? "warehouse_damaged_stock" : "warehouse_stock";
            await using (var take = new NpgsqlCommand($"""
                UPDATE {table} SET quantity = quantity - @quantity, updated_at = now(), updated_by = @actor
                WHERE tenant_id = @tenant AND warehouse_id = @from AND product_id = @product AND quantity >= @quantity
                """, connection, transaction))
            {
                take.Parameters.AddWithValue("tenant", tenantId.Value);
                take.Parameters.AddWithValue("actor", actorId.Value);
                take.Parameters.AddWithValue("from", transfer.FromWarehouseId);
                take.Parameters.AddWithValue("product", line.ProductId);
                take.Parameters.AddWithValue("quantity", line.Quantity);
                if (await take.ExecuteNonQueryAsync(cancellationToken) == 0)
                {
                    var known = await ProductExistsAsync(connection, transaction, tenantId, line.ProductId, cancellationToken);
                    return (known ? TransferResult.TooMuch : TransferResult.InvalidData, null);
                }
            }
            await using var item = new NpgsqlCommand("""
                INSERT INTO stock_transfer_items (tenant_id, transfer_id, line_no, product_id, product_code, product_name, unit, bucket, quantity)
                SELECT @tenant, @id, @line, p.id, p.code, p.name, p.unit, @bucket, @quantity FROM products p WHERE p.tenant_id = @tenant AND p.id = @product
                """, connection, transaction);
            item.Parameters.AddWithValue("tenant", tenantId.Value);
            item.Parameters.AddWithValue("id", id);
            item.Parameters.AddWithValue("line", lineNo);
            item.Parameters.AddWithValue("product", line.ProductId);
            item.Parameters.AddWithValue("bucket", line.Bucket);
            item.Parameters.AddWithValue("quantity", line.Quantity);
            await item.ExecuteNonQueryAsync(cancellationToken);
        }
        await AuditAsync(connection, transaction, tenantId, actorId, "transfer.sent", id, cancellationToken);
        await NotifyAsync(connection, transaction, tenantId, actorId, "transfer.sent", id, transfer.ToWarehouseId, null, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (TransferResult.Success, new CreatedTransferDto(id, NumberText(number), false));
    }

    private static async Task<bool> ProductExistsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, TenantId tenantId, Guid productId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT 1 FROM products WHERE tenant_id = @tenant AND id = @id", connection, transaction);
        command.Parameters.AddWithValue("tenant", tenantId.Value);
        command.Parameters.AddWithValue("id", productId);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    // The active company warehouses goods can be sent to (a worker may not read the warehouse list, so this has its own answer).
    public async Task<IReadOnlyList<TransferWarehouseDto>> ListWarehousesAsync(TenantId tenantId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, null, cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT w.id, w.code, w.name, pl.name FROM warehouses w LEFT JOIN places pl ON pl.tenant_id = w.tenant_id AND pl.id = w.city_id
            WHERE w.tenant_id = @tenant AND w.status = 'active' AND w.kind = 'company' ORDER BY w.code COLLATE "C"
            """, connection, transaction);
        command.Parameters.AddWithValue("tenant", tenantId.Value);
        var items = new List<TransferWarehouseDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) items.Add(new TransferWarehouseDto(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3)));
        return items;
    }

    private const string HeadSql = """
        SELECT t.id, t.number, t.from_warehouse_id, f.code, f.name, t.to_warehouse_id, w.code, w.name, t.status, t.note, cu.email, t.created_at, cl.email, t.closed_at, t.close_note
        FROM stock_transfers t
        JOIN warehouses f ON f.tenant_id = t.tenant_id AND f.id = t.from_warehouse_id
        JOIN warehouses w ON w.tenant_id = t.tenant_id AND w.id = t.to_warehouse_id
        LEFT JOIN users cu ON cu.tenant_id = t.tenant_id AND cu.id = t.created_by
        LEFT JOIN users cl ON cl.tenant_id = t.tenant_id AND cl.id = t.closed_by
        """;

    private const string StaffFilter = """
        (@staff::uuid IS NULL OR EXISTS (SELECT 1 FROM warehouse_staff ws WHERE ws.tenant_id = t.tenant_id AND ws.user_id = @staff AND ws.warehouse_id IN (t.from_warehouse_id, t.to_warehouse_id)))
        """;

    public async Task<IReadOnlyList<TransferDto>> ListAsync(TenantId tenantId, string? status, Guid? warehouseId, Guid? staffOnly, int limit, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, null, cancellationToken);
        await using var command = new NpgsqlCommand($"""
            {HeadSql}
            WHERE t.tenant_id = @tenant AND (@status::text IS NULL OR t.status = @status) AND (@warehouse::uuid IS NULL OR @warehouse IN (t.from_warehouse_id, t.to_warehouse_id)) AND {StaffFilter}
            ORDER BY t.created_at DESC, t.id DESC LIMIT @limit
            """, connection, transaction);
        AddFilters(command, tenantId, status, warehouseId, staffOnly);
        command.Parameters.AddWithValue("limit", limit);
        return await ReadAsync(command, connection, transaction, tenantId, cancellationToken);
    }

    public async Task<TransferDto?> GetAsync(TenantId tenantId, Guid transferId, Guid? staffOnly, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, null, cancellationToken);
        await using var command = new NpgsqlCommand($"{HeadSql} WHERE t.tenant_id = @tenant AND t.id = @id AND {StaffFilter}", connection, transaction);
        AddFilters(command, tenantId, null, null, staffOnly);
        command.Parameters.AddWithValue("id", transferId);
        return (await ReadAsync(command, connection, transaction, tenantId, cancellationToken)).FirstOrDefault();
    }

    private static void AddFilters(NpgsqlCommand command, TenantId tenantId, string? status, Guid? warehouseId, Guid? staffOnly)
    {
        command.Parameters.AddWithValue("tenant", tenantId.Value);
        command.Parameters.Add(new NpgsqlParameter("status", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)status ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("warehouse", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = (object?)warehouseId ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("staff", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = (object?)staffOnly ?? DBNull.Value });
    }

    private static async Task<IReadOnlyList<TransferDto>> ReadAsync(NpgsqlCommand head, NpgsqlConnection connection, NpgsqlTransaction transaction, TenantId tenantId, CancellationToken cancellationToken)
    {
        var heads = new List<(Guid Id, long Number, Guid From, string FromCode, string FromName, Guid To, string ToCode, string ToName, string Status, string? Note, string? By, DateTimeOffset At, string? ClosedBy, DateTimeOffset? ClosedAt, string? CloseNote)>();
        await using (var reader = await head.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken))
                heads.Add((reader.GetGuid(0), reader.GetInt64(1), reader.GetGuid(2), reader.GetString(3), reader.GetString(4), reader.GetGuid(5), reader.GetString(6), reader.GetString(7), reader.GetString(8),
                    reader.IsDBNull(9) ? null : reader.GetString(9), reader.IsDBNull(10) ? null : reader.GetString(10), reader.GetFieldValue<DateTimeOffset>(11),
                    reader.IsDBNull(12) ? null : reader.GetString(12), reader.IsDBNull(13) ? null : reader.GetFieldValue<DateTimeOffset>(13), reader.IsDBNull(14) ? null : reader.GetString(14)));
        var lines = new Dictionary<Guid, List<TransferLineDto>>();
        if (heads.Count > 0)
        {
            await using var items = new NpgsqlCommand("""
                SELECT transfer_id, line_no, product_id, product_code, product_name, unit, bucket, quantity, received_quantity
                FROM stock_transfer_items WHERE tenant_id = @tenant AND transfer_id = ANY(@ids) ORDER BY transfer_id, line_no
                """, connection, transaction);
            items.Parameters.AddWithValue("tenant", tenantId.Value);
            items.Parameters.AddWithValue("ids", heads.Select(h => h.Id).ToArray());
            await using var reader = await items.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var id = reader.GetGuid(0);
                if (!lines.TryGetValue(id, out var list)) lines[id] = list = [];
                list.Add(new TransferLineDto(reader.GetInt32(1), reader.GetGuid(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetDecimal(7),
                    reader.IsDBNull(8) ? null : reader.GetDecimal(8)));
            }
        }
        return heads.Select(h => new TransferDto(h.Id, NumberText(h.Number), h.From, h.FromCode, h.FromName, h.To, h.ToCode, h.ToName, h.Status, h.Note, h.By, h.At, h.ClosedBy, h.ClosedAt, h.CloseNote,
            lines.TryGetValue(h.Id, out var l) ? l : [])).ToList();
    }

    // Locks the transfer and returns its warehouses when it is still "sent".
    private static async Task<(TransferResult? Problem, Guid From, Guid To)> LockSentAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, TenantId tenantId, Guid transferId, CancellationToken cancellationToken)
    {
        await using var lockRow = new NpgsqlCommand("SELECT status, from_warehouse_id, to_warehouse_id FROM stock_transfers WHERE tenant_id = @tenant AND id = @id FOR UPDATE", connection, transaction);
        lockRow.Parameters.AddWithValue("tenant", tenantId.Value);
        lockRow.Parameters.AddWithValue("id", transferId);
        await using var reader = await lockRow.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return (TransferResult.NotFound, default, default);
        return reader.GetString(0) != "sent" ? (TransferResult.Conflict, default, default) : (null, reader.GetGuid(1), reader.GetGuid(2));
    }

    private static async Task<bool> WorksInAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, TenantId tenantId, Guid? staffOnly, Guid warehouseId, CancellationToken cancellationToken)
    {
        if (staffOnly is null) return true;
        await using var command = new NpgsqlCommand("SELECT 1 FROM warehouse_staff WHERE tenant_id = @tenant AND warehouse_id = @warehouse AND user_id = @user", connection, transaction);
        command.Parameters.AddWithValue("tenant", tenantId.Value);
        command.Parameters.AddWithValue("warehouse", warehouseId);
        command.Parameters.AddWithValue("user", staffOnly.Value);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    public async Task<TransferResult> ReceiveAsync(TenantId tenantId, UserId actorId, Guid transferId, string? note, IReadOnlyDictionary<int, decimal> received, Guid? staffOnly, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, actorId, cancellationToken);
        var (problem, from, to) = await LockSentAsync(connection, transaction, tenantId, transferId, cancellationToken);
        if (problem is { } p) return p;
        var creator = await CreatorAsync(connection, transaction, tenantId, transferId, cancellationToken);
        // Whoever may not receive here is told the transfer does not exist (the same answer as for a transfer of other warehouses).
        if (!await WorksInAsync(connection, transaction, tenantId, staffOnly, to, cancellationToken)) return TransferResult.NotFound;

        var lines = new List<(int LineNo, Guid Product, string Bucket, decimal Quantity)>();
        await using (var read = new NpgsqlCommand("SELECT line_no, product_id, bucket, quantity FROM stock_transfer_items WHERE tenant_id = @tenant AND transfer_id = @id ORDER BY line_no", connection, transaction))
        {
            read.Parameters.AddWithValue("tenant", tenantId.Value);
            read.Parameters.AddWithValue("id", transferId);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) lines.Add((reader.GetInt32(0), reader.GetGuid(1), reader.GetString(2), reader.GetDecimal(3)));
        }
        if (received.Keys.Any(k => lines.All(l => l.LineNo != k))) return TransferResult.InvalidData;
        foreach (var line in lines)
        {
            var arrived = received.TryGetValue(line.LineNo, out var value) ? value : line.Quantity;
            if (arrived > line.Quantity) return TransferResult.InvalidData;
            await using (var set = new NpgsqlCommand("UPDATE stock_transfer_items SET received_quantity = @arrived WHERE tenant_id = @tenant AND transfer_id = @id AND line_no = @line", connection, transaction))
            {
                set.Parameters.AddWithValue("tenant", tenantId.Value);
                set.Parameters.AddWithValue("id", transferId);
                set.Parameters.AddWithValue("line", line.LineNo);
                set.Parameters.AddWithValue("arrived", arrived);
                await set.ExecuteNonQueryAsync(cancellationToken);
            }
            if (arrived == 0) continue;
            await AddStockAsync(connection, transaction, tenantId, actorId, to, line.Product, line.Bucket, arrived, cancellationToken);
        }
        await CloseAsync(connection, transaction, tenantId, actorId, transferId, "received", note, cancellationToken);
        await AuditAsync(connection, transaction, tenantId, actorId, "transfer.received", transferId, cancellationToken);
        await NotifyAsync(connection, transaction, tenantId, actorId, "transfer.received", transferId, from, creator, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return TransferResult.Success;
    }

    public async Task<TransferResult> CancelAsync(TenantId tenantId, UserId actorId, Guid transferId, string? note, Guid? staffOnly, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, actorId, cancellationToken);
        var (problem, from, to) = await LockSentAsync(connection, transaction, tenantId, transferId, cancellationToken);
        if (problem is { } p) return p;
        if (!await WorksInAsync(connection, transaction, tenantId, staffOnly, from, cancellationToken)) return TransferResult.NotFound;
        var lines = new List<(Guid Product, string Bucket, decimal Quantity)>();
        await using (var read = new NpgsqlCommand("SELECT product_id, bucket, quantity FROM stock_transfer_items WHERE tenant_id = @tenant AND transfer_id = @id", connection, transaction))
        {
            read.Parameters.AddWithValue("tenant", tenantId.Value);
            read.Parameters.AddWithValue("id", transferId);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) lines.Add((reader.GetGuid(0), reader.GetString(1), reader.GetDecimal(2)));
        }
        foreach (var line in lines) await AddStockAsync(connection, transaction, tenantId, actorId, from, line.Product, line.Bucket, line.Quantity, cancellationToken);
        await CloseAsync(connection, transaction, tenantId, actorId, transferId, "cancelled", note, cancellationToken);
        await AuditAsync(connection, transaction, tenantId, actorId, "transfer.cancelled", transferId, cancellationToken);
        await NotifyAsync(connection, transaction, tenantId, actorId, "transfer.cancelled", transferId, to, null, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return TransferResult.Success;
    }

    private static async Task<Guid> CreatorAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, TenantId tenantId, Guid transferId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT created_by FROM stock_transfers WHERE tenant_id = @tenant AND id = @id", connection, transaction);
        command.Parameters.AddWithValue("tenant", tenantId.Value);
        command.Parameters.AddWithValue("id", transferId);
        return (Guid)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    // Plan step 5.61: tells the active people who work in the warehouse (and one more user, the sender of a received transfer) - never the person who did it.
    private static async Task NotifyAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, TenantId tenantId, UserId actorId, string kind, Guid transferId, Guid warehouseId, Guid? alsoUser, CancellationToken cancellationToken)
    {
        await using var notify = new NpgsqlCommand("""
            INSERT INTO notifications (tenant_id, user_id, kind, entity_id, actor_id)
            SELECT DISTINCT @tenant, u.id, @kind, @id::text, @actor
            FROM users u
            WHERE u.tenant_id = @tenant AND u.is_active AND u.id <> @actor
              AND (u.id = @also OR EXISTS (SELECT 1 FROM warehouse_staff s WHERE s.tenant_id = u.tenant_id AND s.warehouse_id = @warehouse AND s.user_id = u.id))
            """, connection, transaction);
        notify.Parameters.AddWithValue("tenant", tenantId.Value);
        notify.Parameters.AddWithValue("kind", kind);
        notify.Parameters.AddWithValue("id", transferId);
        notify.Parameters.AddWithValue("actor", actorId.Value);
        notify.Parameters.AddWithValue("warehouse", warehouseId);
        notify.Parameters.Add(new NpgsqlParameter("also", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = (object?)alsoUser ?? DBNull.Value });
        await notify.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task AddStockAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, TenantId tenantId, UserId actorId, Guid warehouseId, Guid productId, string bucket, decimal quantity, CancellationToken cancellationToken)
    {
        var table = bucket == "damaged" ? "warehouse_damaged_stock" : "warehouse_stock";
        await using var add = new NpgsqlCommand($"""
            INSERT INTO {table} (tenant_id, warehouse_id, product_id, quantity, updated_by) VALUES (@tenant, @warehouse, @product, @quantity, @actor)
            ON CONFLICT (tenant_id, warehouse_id, product_id) DO UPDATE SET quantity = {table}.quantity + EXCLUDED.quantity, updated_at = now(), updated_by = EXCLUDED.updated_by
            """, connection, transaction);
        add.Parameters.AddWithValue("tenant", tenantId.Value);
        add.Parameters.AddWithValue("warehouse", warehouseId);
        add.Parameters.AddWithValue("product", productId);
        add.Parameters.AddWithValue("quantity", quantity);
        add.Parameters.AddWithValue("actor", actorId.Value);
        await add.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task CloseAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, TenantId tenantId, UserId actorId, Guid transferId, string status, string? note, CancellationToken cancellationToken)
    {
        await using var close = new NpgsqlCommand("UPDATE stock_transfers SET status = @status, closed_by = @actor, closed_at = now(), close_note = @note WHERE tenant_id = @tenant AND id = @id", connection, transaction);
        close.Parameters.AddWithValue("tenant", tenantId.Value);
        close.Parameters.AddWithValue("id", transferId);
        close.Parameters.AddWithValue("status", status);
        close.Parameters.AddWithValue("actor", actorId.Value);
        close.Parameters.Add(new NpgsqlParameter("note", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)note ?? DBNull.Value });
        await close.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<NpgsqlTransaction> BeginAsync(NpgsqlConnection connection, TenantId tenantId, UserId? actorId, CancellationToken cancellationToken)
    {
        var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var set = new NpgsqlCommand("SELECT set_config('app.current_tenant', @tenant, true), set_config('app.current_actor', @actor, true)", connection, transaction);
        set.Parameters.AddWithValue("tenant", tenantId.Value.ToString());
        set.Parameters.AddWithValue("actor", actorId?.Value.ToString() ?? "");
        await set.ExecuteNonQueryAsync(cancellationToken);
        return transaction;
    }

    private static async Task AuditAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, TenantId tenantId, UserId actorId, string action, Guid entity, CancellationToken cancellationToken)
    {
        await using var audit = new NpgsqlCommand("""
            INSERT INTO audit_logs (id, tenant_id, actor_id, action, entity_id, occurred_at) VALUES (gen_random_uuid(), @tenant, @actor, @action, @entity, now())
            """, connection, transaction);
        audit.Parameters.AddWithValue("tenant", tenantId.Value);
        audit.Parameters.AddWithValue("actor", actorId.Value);
        audit.Parameters.AddWithValue("action", action);
        audit.Parameters.AddWithValue("entity", entity.ToString());
        await audit.ExecuteNonQueryAsync(cancellationToken);
    }
}
