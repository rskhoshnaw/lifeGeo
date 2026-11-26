using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LifeDrop.Application.Returns;
using LifeDrop.Domain.Common;
using Npgsql;
using NpgsqlTypes;

namespace LifeDrop.Infrastructure.Returns;

// Customer returns (plan step 5.54; the rules are in ReturnService and migration 0079). Every write is one transaction with its audit row; a decision also tells the people concerned.
public sealed class PostgresReturnStore(NpgsqlDataSource dataSource) : IReturnStore
{
    public static string FormatNumber(long number) => $"R-{number:D6}";

    public async Task<(ReturnResult Result, CreatedReturnDto? Return)> CreateAsync(TenantId tenantId, UserId actorId, NewReturn value, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, actorId, cancellationToken);

        // A retry of the same send gives back the return that exists (same author and customer), else it is a clash.
        await using (var again = new NpgsqlCommand("SELECT id, number, currency, total, created_by, customer_id FROM returns WHERE tenant_id = @tenant AND client_ref = @ref", connection, transaction))
        {
            again.Parameters.AddWithValue("tenant", tenantId.Value);
            again.Parameters.AddWithValue("ref", value.ClientRef);
            await using var reader = await again.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
                return reader.GetGuid(4) == actorId.Value && reader.GetGuid(5) == value.CustomerId
                    ? (ReturnResult.Success, new CreatedReturnDto(reader.GetGuid(0), FormatNumber(reader.GetInt64(1)), reader.GetString(2), reader.GetDecimal(3), true))
                    : (ReturnResult.Conflict, null);
        }

        if (value.WarehouseId is { } warehouse)
        {
            await using var check = new NpgsqlCommand("SELECT status, kind FROM warehouses WHERE tenant_id = @tenant AND id = @id", connection, transaction);
            check.Parameters.AddWithValue("tenant", tenantId.Value);
            check.Parameters.AddWithValue("id", warehouse);
            await using var reader = await check.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return (ReturnResult.NotFound, null);
            if (reader.GetString(0) != "active" || reader.GetString(1) != "company") return (ReturnResult.InvalidData, null);
        }

        long number;
        await using (var counter = new NpgsqlCommand("""
            INSERT INTO return_counters (tenant_id, last_number) VALUES (@tenant, 1)
            ON CONFLICT (tenant_id) DO UPDATE SET last_number = return_counters.last_number + 1
            RETURNING last_number
            """, connection, transaction))
        {
            counter.Parameters.AddWithValue("tenant", tenantId.Value);
            number = Convert.ToInt64(await counter.ExecuteScalarAsync(cancellationToken));
        }

        var id = Guid.NewGuid();
        try
        {
            await using (var insert = new NpgsqlCommand("""
                INSERT INTO returns (id, tenant_id, number, customer_id, order_id, warehouse_id, created_by, currency, total, note, client_ref)
                VALUES (@id, @tenant, @number, @customer, @order, @warehouse, @actor, @currency, @total, @note, @ref)
                """, connection, transaction))
            {
                insert.Parameters.AddWithValue("id", id);
                insert.Parameters.AddWithValue("tenant", tenantId.Value);
                insert.Parameters.AddWithValue("number", number);
                insert.Parameters.AddWithValue("customer", value.CustomerId);
                insert.Parameters.Add(new NpgsqlParameter("order", NpgsqlDbType.Uuid) { Value = (object?)value.OrderId ?? DBNull.Value });
                insert.Parameters.Add(new NpgsqlParameter("warehouse", NpgsqlDbType.Uuid) { Value = (object?)value.WarehouseId ?? DBNull.Value });
                insert.Parameters.AddWithValue("actor", actorId.Value);
                insert.Parameters.AddWithValue("currency", value.Currency);
                insert.Parameters.AddWithValue("total", value.Total);
                insert.Parameters.Add(new NpgsqlParameter("note", NpgsqlDbType.Text) { Value = (object?)value.Note ?? DBNull.Value });
                insert.Parameters.AddWithValue("ref", value.ClientRef);
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }
            var lineNo = 0;
            foreach (var line in value.Lines)
            {
                lineNo++;
                await using var item = new NpgsqlCommand("""
                    INSERT INTO return_items (tenant_id, return_id, line_no, product_id, product_code, product_name, unit, quantity, unit_price, line_total, reason, destination)
                    VALUES (@tenant, @return, @line, @product, @code, @name, @unit, @quantity, @price, @total, @reason, @destination)
                    """, connection, transaction);
                item.Parameters.AddWithValue("tenant", tenantId.Value);
                item.Parameters.AddWithValue("return", id);
                item.Parameters.AddWithValue("line", lineNo);
                item.Parameters.AddWithValue("product", line.ProductId);
                item.Parameters.AddWithValue("code", line.ProductCode);
                item.Parameters.AddWithValue("name", line.ProductName);
                item.Parameters.AddWithValue("unit", line.Unit);
                item.Parameters.AddWithValue("quantity", line.Quantity);
                item.Parameters.AddWithValue("price", line.UnitPrice);
                item.Parameters.AddWithValue("total", line.LineTotal);
                item.Parameters.AddWithValue("reason", line.Reason);
                item.Parameters.AddWithValue("destination", line.Destination);
                await item.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return (ReturnResult.Conflict, null);   // the same ClientRef arrived twice at the same moment: the sender's retry gets the one that won
        }
        await AuditAsync(connection, transaction, tenantId, actorId, "return.created", id, cancellationToken);
        // The people who decide are told: returns.approve inside their data scope (the whole company, the region of the customer, or their own customers).
        await using (var notify = new NpgsqlCommand("""
            INSERT INTO notifications (tenant_id, user_id, kind, entity_id, actor_id)
            SELECT DISTINCT @tenant, ur.user_id, 'return.new', @id::text, @actor
            FROM user_roles ur
            JOIN roles r ON r.tenant_id = ur.tenant_id AND r.id = ur.role_id
            JOIN role_permissions rp ON rp.tenant_id = r.tenant_id AND rp.role_id = r.id AND rp.permission_key = 'returns.approve'
            JOIN users u ON u.tenant_id = ur.tenant_id AND u.id = ur.user_id AND u.is_active
            WHERE ur.tenant_id = @tenant AND ur.user_id <> @actor
              AND (r.data_scope = 'tenant'
                   OR (r.data_scope = 'region' AND EXISTS (SELECT 1 FROM team_members tm JOIN teams t ON t.tenant_id = tm.tenant_id AND t.id = tm.team_id
                                                           JOIN customers c ON c.tenant_id = t.tenant_id AND c.id = @customer AND c.region_id = t.region_id
                                                           WHERE tm.tenant_id = ur.tenant_id AND tm.user_id = ur.user_id))
                   OR (r.data_scope = 'own' AND EXISTS (SELECT 1 FROM customers c WHERE c.tenant_id = ur.tenant_id AND c.id = @customer AND c.owner_user_id = ur.user_id)))
            """, connection, transaction))
        {
            notify.Parameters.AddWithValue("tenant", tenantId.Value);
            notify.Parameters.AddWithValue("id", id);
            notify.Parameters.AddWithValue("actor", actorId.Value);
            notify.Parameters.AddWithValue("customer", value.CustomerId);
            await notify.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return (ReturnResult.Success, new CreatedReturnDto(id, FormatNumber(number), value.Currency, value.Total, false));
    }

    public async Task<IReadOnlyDictionary<Guid, decimal>> ReturnedQuantitiesAsync(TenantId tenantId, Guid orderId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, null, cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT i.product_id, sum(i.quantity) FROM returns r JOIN return_items i ON i.tenant_id = r.tenant_id AND i.return_id = r.id
            WHERE r.tenant_id = @tenant AND r.order_id = @order AND r.status IN ('requested', 'approved') AND i.decision IS DISTINCT FROM 'rejected' GROUP BY i.product_id
            """, connection, transaction);
        command.Parameters.AddWithValue("tenant", tenantId.Value);
        command.Parameters.AddWithValue("order", orderId);
        var result = new Dictionary<Guid, decimal>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result[reader.GetGuid(0)] = reader.GetDecimal(1);
        return result;
    }

    private const string Columns = """
        r.id, r.number, r.customer_id, c.code, c.name, r.order_id, o.number, r.warehouse_id, w.name, r.status, r.currency, r.total, r.note, r.created_by, cu.email, r.decision_note, du.email, r.decided_at, r.created_at
        """;

    private const string Joins = """
        FROM returns r
        JOIN customers c ON c.tenant_id = r.tenant_id AND c.id = r.customer_id
        LEFT JOIN orders o ON o.tenant_id = r.tenant_id AND o.id = r.order_id
        LEFT JOIN warehouses w ON w.tenant_id = r.tenant_id AND w.id = r.warehouse_id
        LEFT JOIN users cu ON cu.tenant_id = r.tenant_id AND cu.id = r.created_by
        LEFT JOIN users du ON du.tenant_id = r.tenant_id AND du.id = r.decided_by
        """;

    private static string ViewFilter(ReturnView view) => view switch
    {
        ReturnView.Everything => "TRUE",
        ReturnView.Region => "(r.created_by = @actor OR c.owner_user_id = @actor OR c.region_id = @region)",
        _ => "r.created_by = @actor"
    };

    private static ReturnDto Read(NpgsqlDataReader r) => new(r.GetGuid(0), FormatNumber(r.GetInt64(1)), r.GetGuid(2), r.GetString(3), r.GetString(4), r.IsDBNull(5) ? null : r.GetGuid(5),
        r.IsDBNull(6) ? null : "O-" + r.GetInt64(6).ToString("D6"), r.IsDBNull(7) ? null : r.GetGuid(7), r.IsDBNull(8) ? null : r.GetString(8), r.GetString(9), r.GetString(10), r.GetDecimal(11),
        r.IsDBNull(12) ? null : r.GetString(12), r.GetGuid(13), r.IsDBNull(14) ? null : r.GetString(14), r.IsDBNull(15) ? null : r.GetString(15), r.IsDBNull(16) ? null : r.GetString(16),
        r.IsDBNull(17) ? null : r.GetFieldValue<DateTimeOffset>(17), r.GetFieldValue<DateTimeOffset>(18), []);

    private static void AddFilters(NpgsqlCommand command, TenantId tenantId, UserId actorId, Guid? regionId)
    {
        command.Parameters.AddWithValue("tenant", tenantId.Value);
        command.Parameters.AddWithValue("actor", actorId.Value);
        command.Parameters.Add(new NpgsqlParameter("region", NpgsqlDbType.Uuid) { Value = (object?)regionId ?? DBNull.Value });
    }

    public async Task<IReadOnlyList<ReturnDto>> ListAsync(TenantId tenantId, UserId actorId, ReturnView view, Guid? regionId, Guid? customerId, string? status, int limit,
        DateTimeOffset? before, Guid? beforeId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, null, cancellationToken);
        var items = new List<ReturnDto>();
        await using (var command = new NpgsqlCommand($"""
            SELECT {Columns} {Joins}
            WHERE r.tenant_id = @tenant AND {ViewFilter(view)}
              AND (@customer IS NULL OR r.customer_id = @customer) AND (@status IS NULL OR r.status = @status)
              AND (@before IS NULL OR (r.created_at, r.id) < (@before, @beforeId))
            ORDER BY r.created_at DESC, r.id DESC LIMIT @limit
            """, connection, transaction))
        {
            AddFilters(command, tenantId, actorId, regionId);
            command.Parameters.Add(new NpgsqlParameter("customer", NpgsqlDbType.Uuid) { Value = (object?)customerId ?? DBNull.Value });
            command.Parameters.Add(new NpgsqlParameter("status", NpgsqlDbType.Text) { Value = (object?)status ?? DBNull.Value });
            command.Parameters.Add(new NpgsqlParameter("before", NpgsqlDbType.TimestampTz) { Value = (object?)before ?? DBNull.Value });
            command.Parameters.Add(new NpgsqlParameter("beforeId", NpgsqlDbType.Uuid) { Value = (object?)beforeId ?? DBNull.Value });
            command.Parameters.AddWithValue("limit", limit);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) items.Add(Read(reader));
        }
        return await WithLinesAsync(connection, transaction, tenantId, items, cancellationToken);
    }

    public async Task<ReturnDto?> GetAsync(TenantId tenantId, UserId actorId, ReturnView view, Guid? regionId, Guid returnId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, null, cancellationToken);
        ReturnDto? found;
        await using (var command = new NpgsqlCommand($"SELECT {Columns} {Joins} WHERE r.tenant_id = @tenant AND r.id = @id AND {ViewFilter(view)}", connection, transaction))
        {
            AddFilters(command, tenantId, actorId, regionId);
            command.Parameters.AddWithValue("id", returnId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            found = await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
        }
        return found is null ? null : (await WithLinesAsync(connection, transaction, tenantId, [found], cancellationToken))[0];
    }

    private static async Task<IReadOnlyList<ReturnDto>> WithLinesAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, TenantId tenantId, List<ReturnDto> items, CancellationToken cancellationToken)
    {
        if (items.Count == 0) return items;
        var lines = new Dictionary<Guid, List<ReturnLineDto>>();
        await using (var command = new NpgsqlCommand("""
            SELECT return_id, line_no, product_id, product_code, product_name, unit, quantity, unit_price, line_total, reason, destination, stock_applied, decision
            FROM return_items WHERE tenant_id = @tenant AND return_id = ANY(@ids) ORDER BY return_id, line_no
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("tenant", tenantId.Value);
            command.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = items.Select(i => i.Id).ToArray() });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var id = reader.GetGuid(0);
                if (!lines.TryGetValue(id, out var list)) lines[id] = list = [];
                list.Add(new ReturnLineDto(reader.GetInt32(1), reader.GetGuid(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetDecimal(6), reader.GetDecimal(7),
                    reader.GetDecimal(8), reader.GetString(9), reader.GetString(10), reader.IsDBNull(11) ? null : reader.GetBoolean(11), reader.IsDBNull(12) ? null : reader.GetString(12)));
            }
        }
        var photos = new Dictionary<Guid, List<Guid>>();
        await using (var command = new NpgsqlCommand("SELECT return_id, id FROM return_photos WHERE tenant_id = @tenant AND return_id = ANY(@ids) ORDER BY created_at, id", connection, transaction))
        {
            command.Parameters.AddWithValue("tenant", tenantId.Value);
            command.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = items.Select(i => i.Id).ToArray() });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var id = reader.GetGuid(0);
                if (!photos.TryGetValue(id, out var list)) photos[id] = list = [];
                list.Add(reader.GetGuid(1));
            }
        }
        return items.Select(i => i with { Items = lines.GetValueOrDefault(i.Id) ?? [], PhotoIds = photos.GetValueOrDefault(i.Id) ?? [] }).ToList();
    }

    public async Task<ReturnResult> ApproveAsync(TenantId tenantId, UserId actorId, Guid returnId, string? note, IReadOnlyList<int>? lines, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, actorId, cancellationToken);
        var locked = await LockAsync(connection, transaction, tenantId, returnId, cancellationToken);
        if (locked is null) return ReturnResult.NotFound;
        if (locked.Value.Status != "requested") return ReturnResult.Conflict;
        // Row by row (plan step 5.56): the chosen lines are approved, the others rejected; a number that is not a line of this return is refused. Null = every line.
        int lineCount;
        await using (var count = new NpgsqlCommand("SELECT count(*) FROM return_items WHERE tenant_id = @tenant AND return_id = @id", connection, transaction))
        {
            count.Parameters.AddWithValue("tenant", tenantId.Value);
            count.Parameters.AddWithValue("id", returnId);
            lineCount = Convert.ToInt32(await count.ExecuteScalarAsync(cancellationToken));
        }
        if (lines is not null && lines.Any(n => n < 1 || n > lineCount)) return ReturnResult.InvalidData;
        await using (var decide = new NpgsqlCommand("""
            UPDATE return_items SET decision = CASE WHEN @all OR line_no = ANY(@lines) THEN 'approved' ELSE 'rejected' END
            WHERE tenant_id = @tenant AND return_id = @id
            """, connection, transaction))
        {
            decide.Parameters.AddWithValue("tenant", tenantId.Value);
            decide.Parameters.AddWithValue("id", returnId);
            decide.Parameters.AddWithValue("all", lines is null);
            decide.Parameters.Add(new NpgsqlParameter("lines", NpgsqlDbType.Array | NpgsqlDbType.Integer) { Value = (lines ?? []).ToArray() });
            await decide.ExecuteNonQueryAsync(cancellationToken);
        }
        // The credit is what the approved lines are worth.
        await using (var total = new NpgsqlCommand("UPDATE returns SET total = (SELECT coalesce(sum(line_total), 0) FROM return_items WHERE tenant_id = @tenant AND return_id = @id AND decision = 'approved') WHERE tenant_id = @tenant AND id = @id", connection, transaction))
        {
            total.Parameters.AddWithValue("tenant", tenantId.Value);
            total.Parameters.AddWithValue("id", returnId);
            await total.ExecuteNonQueryAsync(cancellationToken);
        }
        if (locked.Value.Warehouse is { } warehouse)
        {
            // Goods that go back are added to the sellable stock where that warehouse already counts the product; where it does not, nothing is added (stock_applied false) so a product nobody
            // counted does not become limited by a small return.
            await using (var stock = new NpgsqlCommand("""
                WITH applied AS (
                    UPDATE warehouse_stock s SET quantity = s.quantity + i.quantity, updated_at = now(), updated_by = @actor
                    FROM return_items i
                    WHERE i.tenant_id = @tenant AND i.return_id = @id AND i.destination = 'warehouse' AND i.decision = 'approved'
                      AND s.tenant_id = @tenant AND s.warehouse_id = @warehouse AND s.product_id = i.product_id
                    RETURNING i.id)
                UPDATE return_items i SET stock_applied = (i.id IN (SELECT id FROM applied))
                WHERE i.tenant_id = @tenant AND i.return_id = @id AND i.destination = 'warehouse' AND i.decision = 'approved'
                """, connection, transaction))
            {
                stock.Parameters.AddWithValue("tenant", tenantId.Value);
                stock.Parameters.AddWithValue("actor", actorId.Value);
                stock.Parameters.AddWithValue("id", returnId);
                stock.Parameters.AddWithValue("warehouse", warehouse);
                await stock.ExecuteNonQueryAsync(cancellationToken);
            }
            // Damaged and expired goods are held apart in the same warehouse (never in the stock that can be sold).
            await using var damaged = new NpgsqlCommand("""
                WITH moved AS (
                    UPDATE return_items SET stock_applied = true
                    WHERE tenant_id = @tenant AND return_id = @id AND destination = 'damaged' AND decision = 'approved'
                    RETURNING product_id, quantity)
                INSERT INTO warehouse_damaged_stock (tenant_id, warehouse_id, product_id, quantity, updated_by)
                SELECT @tenant, @warehouse, product_id, sum(quantity), @actor FROM moved GROUP BY product_id
                ON CONFLICT (tenant_id, warehouse_id, product_id) DO UPDATE SET quantity = warehouse_damaged_stock.quantity + EXCLUDED.quantity, updated_at = now(), updated_by = EXCLUDED.updated_by
                """, connection, transaction);
            damaged.Parameters.AddWithValue("tenant", tenantId.Value);
            damaged.Parameters.AddWithValue("actor", actorId.Value);
            damaged.Parameters.AddWithValue("id", returnId);
            damaged.Parameters.AddWithValue("warehouse", warehouse);
            await damaged.ExecuteNonQueryAsync(cancellationToken);
        }
        await DecideAsync(connection, transaction, tenantId, actorId, returnId, "approved", note, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ReturnResult.Success;
    }

    public async Task<ReturnResult> RejectAsync(TenantId tenantId, UserId actorId, Guid returnId, string reason, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, actorId, cancellationToken);
        var locked = await LockAsync(connection, transaction, tenantId, returnId, cancellationToken);
        if (locked is null) return ReturnResult.NotFound;
        if (locked.Value.Status != "requested") return ReturnResult.Conflict;
        await DecideAsync(connection, transaction, tenantId, actorId, returnId, "rejected", reason, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ReturnResult.Success;
    }

    public async Task<ReturnResult> CancelAsync(TenantId tenantId, UserId actorId, Guid returnId, string reason, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, actorId, cancellationToken);
        var locked = await LockAsync(connection, transaction, tenantId, returnId, cancellationToken);
        if (locked is null) return ReturnResult.NotFound;
        if (locked.Value.Author != actorId.Value) return ReturnResult.Forbidden;
        if (locked.Value.Status != "requested") return ReturnResult.Conflict;
        await using (var update = new NpgsqlCommand("UPDATE returns SET status = 'cancelled', decided_by = @actor, decided_at = now(), decision_note = @reason WHERE tenant_id = @tenant AND id = @id", connection, transaction))
        {
            update.Parameters.AddWithValue("tenant", tenantId.Value);
            update.Parameters.AddWithValue("actor", actorId.Value);
            update.Parameters.AddWithValue("id", returnId);
            update.Parameters.AddWithValue("reason", reason);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
        await AuditAsync(connection, transaction, tenantId, actorId, "return.cancelled", returnId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ReturnResult.Success;
    }

    private static async Task<(string Status, Guid Author, Guid? Warehouse)?> LockAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, TenantId tenantId, Guid returnId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT status, created_by, warehouse_id FROM returns WHERE tenant_id = @tenant AND id = @id FOR UPDATE", connection, transaction);
        command.Parameters.AddWithValue("tenant", tenantId.Value);
        command.Parameters.AddWithValue("id", returnId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? (reader.GetString(0), reader.GetGuid(1), reader.IsDBNull(2) ? null : reader.GetGuid(2)) : null;
    }

    // Marks the decision, writes the audit row and tells the author (unless the author decided it themselves).
    private static async Task DecideAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, TenantId tenantId, UserId actorId, Guid returnId, string status, string? note,
        CancellationToken cancellationToken)
    {
        await using (var update = new NpgsqlCommand("UPDATE returns SET status = @status, decided_by = @actor, decided_at = now(), decision_note = @note WHERE tenant_id = @tenant AND id = @id", connection, transaction))
        {
            update.Parameters.AddWithValue("tenant", tenantId.Value);
            update.Parameters.AddWithValue("actor", actorId.Value);
            update.Parameters.AddWithValue("id", returnId);
            update.Parameters.AddWithValue("status", status);
            update.Parameters.Add(new NpgsqlParameter("note", NpgsqlDbType.Text) { Value = (object?)note ?? DBNull.Value });
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
        await AuditAsync(connection, transaction, tenantId, actorId, status == "approved" ? "return.approved" : "return.rejected", returnId, cancellationToken);
        await using var notify = new NpgsqlCommand("""
            INSERT INTO notifications (tenant_id, user_id, kind, entity_id, actor_id)
            SELECT tenant_id, created_by, 'return.decided', id::text, @actor FROM returns WHERE tenant_id = @tenant AND id = @id AND created_by <> @actor
            """, connection, transaction);
        notify.Parameters.AddWithValue("tenant", tenantId.Value);
        notify.Parameters.AddWithValue("actor", actorId.Value);
        notify.Parameters.AddWithValue("id", returnId);
        await notify.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<ReturnResult> AddPhotoAsync(TenantId tenantId, UserId author, Guid returnId, string contentType, byte[] data, int maxPhotos, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, author, cancellationToken);
        var locked = await LockAsync(connection, transaction, tenantId, returnId, cancellationToken);
        if (locked is null) return ReturnResult.NotFound;
        if (locked.Value.Author != author.Value) return ReturnResult.Forbidden;
        if (locked.Value.Status != "requested") return ReturnResult.Conflict;
        await using (var count = new NpgsqlCommand("SELECT count(*) FROM return_photos WHERE tenant_id = @tenant AND return_id = @id", connection, transaction))
        {
            count.Parameters.AddWithValue("tenant", tenantId.Value);
            count.Parameters.AddWithValue("id", returnId);
            if (Convert.ToInt32(await count.ExecuteScalarAsync(cancellationToken)) >= maxPhotos) return ReturnResult.TooManyPhotos;
        }
        await using (var insert = new NpgsqlCommand("INSERT INTO return_photos (tenant_id, return_id, content_type, data) VALUES (@tenant, @id, @type, @data)", connection, transaction))
        {
            insert.Parameters.AddWithValue("tenant", tenantId.Value);
            insert.Parameters.AddWithValue("id", returnId);
            insert.Parameters.AddWithValue("type", contentType);
            insert.Parameters.Add(new NpgsqlParameter("data", NpgsqlDbType.Bytea) { Value = data });
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        await AuditAsync(connection, transaction, tenantId, author, "return.photo_added", returnId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ReturnResult.Success;
    }

    public async Task<ReturnPhoto?> GetPhotoAsync(TenantId tenantId, UserId actorId, ReturnView view, Guid? regionId, Guid returnId, Guid photoId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, null, cancellationToken);
        await using var command = new NpgsqlCommand($"SELECT p.content_type, p.data {Joins} JOIN return_photos p ON p.tenant_id = r.tenant_id AND p.return_id = r.id WHERE r.tenant_id = @tenant AND r.id = @id AND p.id = @photo AND {ViewFilter(view)}", connection, transaction);
        AddFilters(command, tenantId, actorId, regionId);
        command.Parameters.AddWithValue("id", returnId);
        command.Parameters.AddWithValue("photo", photoId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? new ReturnPhoto(reader.GetString(0), reader.GetFieldValue<byte[]>(1)) : null;
    }

    public async Task<IReadOnlyList<DamagedGoodsDto>> DamagedAsync(TenantId tenantId, UserId actorId, ReturnView view, Guid? regionId, DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, null, cancellationToken);
        await using var command = new NpgsqlCommand($"""
            SELECT i.product_id, i.product_code, i.product_name, i.unit, i.reason, sum(i.quantity), sum(i.line_total), r.currency
            FROM returns r
            JOIN customers c ON c.tenant_id = r.tenant_id AND c.id = r.customer_id
            JOIN return_items i ON i.tenant_id = r.tenant_id AND i.return_id = r.id
            WHERE r.tenant_id = @tenant AND r.status = 'approved' AND i.destination = 'damaged' AND i.decision IS DISTINCT FROM 'rejected' AND {ViewFilter(view)}
              AND (r.decided_at AT TIME ZONE 'Asia/Baghdad')::date BETWEEN @from AND @to
            GROUP BY i.product_id, i.product_code, i.product_name, i.unit, i.reason, r.currency
            ORDER BY i.product_code COLLATE "C", i.reason, r.currency
            """, connection, transaction);
        AddFilters(command, tenantId, actorId, regionId);
        command.Parameters.Add(new NpgsqlParameter("from", NpgsqlDbType.Date) { Value = from });
        command.Parameters.Add(new NpgsqlParameter("to", NpgsqlDbType.Date) { Value = to });
        var items = new List<DamagedGoodsDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            items.Add(new DamagedGoodsDto(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetDecimal(5), reader.GetDecimal(6), reader.GetString(7)));
        return items;
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
