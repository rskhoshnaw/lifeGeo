using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LifeDrop.Application.Catalog;
using LifeDrop.Application.Orders;
using LifeDrop.Application.Roles;
using LifeDrop.Domain.Common;
using Npgsql;

namespace LifeDrop.Infrastructure.Orders;

public sealed class PostgresOrderStore(NpgsqlDataSource dataSource) : IOrderStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string FormatNumber(long number) => $"O-{number:D6}";

    public async Task<IReadOnlyList<WarehouseDto>> ListWarehousesAsync(TenantId tenantId, bool includeInactive, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, null, cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT id, code, name, status FROM warehouses WHERE tenant_id = @tenant AND (@all OR status = 'active') ORDER BY code COLLATE "C"
            """, connection, transaction);
        command.Parameters.AddWithValue("tenant", tenantId.Value);
        command.Parameters.AddWithValue("all", includeInactive);
        var items = new List<WarehouseDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) items.Add(new WarehouseDto(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        return items;
    }

    public async Task<Guid?> CreateWarehouseAsync(TenantId tenantId, UserId actorId, SaveWarehouseRequest warehouse, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, actorId, cancellationToken);
        var id = Guid.NewGuid();
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO warehouses (id, tenant_id, code, name, status, created_by) VALUES (@id, @tenant, @code, @name, @status, @actor)
            ON CONFLICT (tenant_id, code) DO NOTHING
            """, connection, transaction))
        {
            insert.Parameters.AddWithValue("id", id);
            insert.Parameters.AddWithValue("tenant", tenantId.Value);
            insert.Parameters.AddWithValue("code", warehouse.Code!);
            insert.Parameters.AddWithValue("name", warehouse.Name!);
            insert.Parameters.AddWithValue("status", warehouse.Status ?? "active");
            insert.Parameters.AddWithValue("actor", actorId.Value);
            if (await insert.ExecuteNonQueryAsync(cancellationToken) == 0) return null;
        }
        await AuditAsync(connection, transaction, tenantId, actorId, "warehouse.created", id, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return id;
    }

    public async Task<bool> UpdateWarehouseAsync(TenantId tenantId, UserId actorId, Guid warehouseId, SaveWarehouseRequest warehouse, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, actorId, cancellationToken);
        await using (var update = new NpgsqlCommand("""
            UPDATE warehouses SET name = @name, status = @status, updated_at = now() WHERE tenant_id = @tenant AND id = @id
            """, connection, transaction))
        {
            update.Parameters.AddWithValue("tenant", tenantId.Value);
            update.Parameters.AddWithValue("id", warehouseId);
            update.Parameters.AddWithValue("name", warehouse.Name!);
            update.Parameters.AddWithValue("status", warehouse.Status ?? "active");
            if (await update.ExecuteNonQueryAsync(cancellationToken) == 0) return false;
        }
        await AuditAsync(connection, transaction, tenantId, actorId, "warehouse.updated", warehouseId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<(OrderResult Result, CreatedOrderDto? Order)> CreateAsync(TenantId tenantId, UserId actorId, NewOrder order, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, actorId, cancellationToken);

        // A retry of the same send gives back the order that exists.
        await using (var again = new NpgsqlCommand("""
            SELECT id, number, currency, total, created_by, customer_id FROM orders WHERE tenant_id = @tenant AND client_ref = @ref
            """, connection, transaction))
        {
            again.Parameters.AddWithValue("tenant", tenantId.Value);
            again.Parameters.AddWithValue("ref", order.ClientRef);
            await using var reader = await again.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                return reader.GetGuid(4) == actorId.Value && reader.GetGuid(5) == order.CustomerId
                    ? (OrderResult.Success, new CreatedOrderDto(reader.GetGuid(0), FormatNumber(reader.GetInt64(1)), reader.GetString(2), reader.GetDecimal(3), true))
                    : (OrderResult.Conflict, null);
            }
        }

        string? warehouseStatus;
        await using (var warehouse = new NpgsqlCommand("SELECT status FROM warehouses WHERE tenant_id = @tenant AND id = @id", connection, transaction))
        {
            warehouse.Parameters.AddWithValue("tenant", tenantId.Value);
            warehouse.Parameters.AddWithValue("id", order.WarehouseId);
            warehouseStatus = await warehouse.ExecuteScalarAsync(cancellationToken) as string;
        }
        if (warehouseStatus is null) return (OrderResult.NotFound, null);
        if (warehouseStatus != "active") return (OrderResult.InvalidData, null);

        long number;
        await using (var counter = new NpgsqlCommand("""
            INSERT INTO order_counters (tenant_id, last_number) VALUES (@tenant, 1)
            ON CONFLICT (tenant_id) DO UPDATE SET last_number = order_counters.last_number + 1
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
                INSERT INTO orders (id, tenant_id, number, customer_id, visit_id, warehouse_id, created_by, currency, total, note, client_ref)
                VALUES (@id, @tenant, @number, @customer, @visit, @warehouse, @actor, @currency, @total, @note, @ref)
                """, connection, transaction))
            {
                insert.Parameters.AddWithValue("id", id);
                insert.Parameters.AddWithValue("tenant", tenantId.Value);
                insert.Parameters.AddWithValue("number", number);
                insert.Parameters.AddWithValue("customer", order.CustomerId);
                insert.Parameters.Add(new NpgsqlParameter("visit", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = (object?)order.VisitId ?? DBNull.Value });
                insert.Parameters.AddWithValue("warehouse", order.WarehouseId);
                insert.Parameters.AddWithValue("actor", actorId.Value);
                insert.Parameters.AddWithValue("currency", order.Currency);
                insert.Parameters.AddWithValue("total", order.Total);
                insert.Parameters.Add(new NpgsqlParameter("note", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)order.Note ?? DBNull.Value });
                insert.Parameters.AddWithValue("ref", order.ClientRef);
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }
            var lineNo = 0;
            foreach (var line in order.Lines)
            {
                lineNo++;
                await using var item = new NpgsqlCommand("""
                    INSERT INTO order_items (tenant_id, order_id, line_no, product_id, product_code, product_name, unit, quantity, list_price, unit_price, line_total, discounts)
                    VALUES (@tenant, @order, @line, @product, @code, @name, @unit, @quantity, @list, @price, @total, @discounts::jsonb)
                    """, connection, transaction);
                item.Parameters.AddWithValue("tenant", tenantId.Value);
                item.Parameters.AddWithValue("order", id);
                item.Parameters.AddWithValue("line", lineNo);
                item.Parameters.AddWithValue("product", line.ProductId);
                item.Parameters.AddWithValue("code", line.ProductCode);
                item.Parameters.AddWithValue("name", line.ProductName);
                item.Parameters.AddWithValue("unit", line.Unit);
                item.Parameters.AddWithValue("quantity", line.Quantity);
                item.Parameters.AddWithValue("list", line.ListPrice);
                item.Parameters.AddWithValue("price", line.UnitPrice);
                item.Parameters.AddWithValue("total", line.LineTotal);
                item.Parameters.AddWithValue("discounts", JsonSerializer.Serialize(line.Discounts, Json));
                await item.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            // The same ClientRef arrived twice at the same moment; the sender retries and gets the order that won.
            return (OrderResult.Conflict, null);
        }
        await AuditAsync(connection, transaction, tenantId, actorId, "order.created", id, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (OrderResult.Success, new CreatedOrderDto(id, FormatNumber(number), order.Currency, order.Total, false));
    }

    // Tenant: all; region: the customers of the region, the ones the user owns and the orders the user placed; own: the user's own orders and customers.
    private static string ScopeFilter(AccessScope scope) => scope.Scope switch
    {
        DataScope.Tenant => "TRUE",
        DataScope.Region when scope.RegionId is not null => "(o.created_by = @actor OR c.owner_user_id = @actor OR c.region_id = @scopeRegion)",
        _ => "(o.created_by = @actor OR c.owner_user_id = @actor)"
    };

    private static void AddScope(NpgsqlCommand command, AccessScope scope)
    {
        command.Parameters.AddWithValue("actor", scope.ActorId.Value);
        command.Parameters.Add(new NpgsqlParameter("scopeRegion", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = (object?)scope.RegionId ?? DBNull.Value });
    }

    private const string OrderColumns = """
        o.id, o.number, o.customer_id, c.code, c.name, o.visit_id, o.warehouse_id, w.name, o.created_by, u.email, o.status, o.currency, o.total,
        o.note, o.cancel_reason, o.created_at
        """;

    private const string OrderJoins = """
        FROM orders o
        JOIN customers c ON c.tenant_id = o.tenant_id AND c.id = o.customer_id
        JOIN warehouses w ON w.tenant_id = o.tenant_id AND w.id = o.warehouse_id
        LEFT JOIN users u ON u.tenant_id = o.tenant_id AND u.id = o.created_by
        """;

    private static OrderDto ReadOrder(NpgsqlDataReader reader) => new(reader.GetGuid(0), FormatNumber(reader.GetInt64(1)), reader.GetGuid(2), reader.GetString(3), reader.GetString(4),
        reader.IsDBNull(5) ? null : reader.GetGuid(5), reader.GetGuid(6), reader.GetString(7), reader.GetGuid(8), reader.IsDBNull(9) ? null : reader.GetString(9),
        reader.GetString(10), reader.GetString(11), reader.GetDecimal(12), reader.IsDBNull(13) ? null : reader.GetString(13), reader.IsDBNull(14) ? null : reader.GetString(14),
        reader.GetFieldValue<DateTimeOffset>(15), Array.Empty<OrderLineDto>());

    public async Task<OrderDto?> GetAsync(TenantId tenantId, Guid orderId, AccessScope scope, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, null, cancellationToken);
        OrderDto? order;
        await using (var command = new NpgsqlCommand($"SELECT {OrderColumns} {OrderJoins} WHERE o.tenant_id = @tenant AND o.id = @id AND {ScopeFilter(scope)}", connection, transaction))
        {
            command.Parameters.AddWithValue("tenant", tenantId.Value);
            command.Parameters.AddWithValue("id", orderId);
            AddScope(command, scope);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            order = await reader.ReadAsync(cancellationToken) ? ReadOrder(reader) : null;
        }
        if (order is null) return null;

        var lines = new List<OrderLineDto>();
        await using (var items = new NpgsqlCommand("""
            SELECT line_no, product_id, product_code, product_name, unit, quantity, list_price, unit_price, line_total, discounts::text
            FROM order_items WHERE tenant_id = @tenant AND order_id = @id ORDER BY line_no
            """, connection, transaction))
        {
            items.Parameters.AddWithValue("tenant", tenantId.Value);
            items.Parameters.AddWithValue("id", orderId);
            await using var reader = await items.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                lines.Add(new OrderLineDto(reader.GetInt32(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetDecimal(5),
                    reader.GetDecimal(6), reader.GetDecimal(7), reader.GetDecimal(8),
                    JsonSerializer.Deserialize<List<AppliedDiscountDto>>(reader.GetString(9), Json) ?? new List<AppliedDiscountDto>()));
        }
        return order with { Items = lines };
    }

    public async Task<IReadOnlyList<OrderDto>> ListAsync(TenantId tenantId, AccessScope scope, Guid? customerId, string? status, int limit, DateTimeOffset? before, Guid? beforeId,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, null, cancellationToken);
        await using var command = new NpgsqlCommand($"""
            SELECT {OrderColumns} {OrderJoins}
            WHERE o.tenant_id = @tenant AND {ScopeFilter(scope)}
              AND (@customer::uuid IS NULL OR o.customer_id = @customer)
              AND (@status::text IS NULL OR o.status = @status)
              AND (@before::timestamptz IS NULL OR (o.created_at, o.id) < (@before, @beforeId))
            ORDER BY o.created_at DESC, o.id DESC
            LIMIT @limit
            """, connection, transaction);
        command.Parameters.AddWithValue("tenant", tenantId.Value);
        AddScope(command, scope);
        command.Parameters.Add(new NpgsqlParameter("customer", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = (object?)customerId ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("status", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)status ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("before", NpgsqlTypes.NpgsqlDbType.TimestampTz) { Value = (object?)before ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("beforeId", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = (object?)beforeId ?? DBNull.Value });
        command.Parameters.AddWithValue("limit", limit);
        var items = new List<OrderDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) items.Add(ReadOrder(reader));
        return items;
    }

    public async Task<OrderResult> CancelAsync(TenantId tenantId, UserId actorId, Guid orderId, AccessScope scope, bool isManager, string reason, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, actorId, cancellationToken);
        string status;
        Guid createdBy;
        await using (var read = new NpgsqlCommand($"""
            SELECT o.status, o.created_by FROM orders o JOIN customers c ON c.tenant_id = o.tenant_id AND c.id = o.customer_id
            WHERE o.tenant_id = @tenant AND o.id = @id AND {ScopeFilter(scope)} FOR UPDATE OF o
            """, connection, transaction))
        {
            read.Parameters.AddWithValue("tenant", tenantId.Value);
            read.Parameters.AddWithValue("id", orderId);
            AddScope(read, scope);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return OrderResult.NotFound;
            status = reader.GetString(0);
            createdBy = reader.GetGuid(1);
        }
        if (!isManager && createdBy != actorId.Value) return OrderResult.Forbidden;
        if (status != "placed") return OrderResult.Conflict;
        await using (var update = new NpgsqlCommand("""
            UPDATE orders SET status = 'cancelled', cancel_reason = @reason, cancelled_by = @actor, cancelled_at = now() WHERE tenant_id = @tenant AND id = @id
            """, connection, transaction))
        {
            update.Parameters.AddWithValue("reason", reason);
            update.Parameters.AddWithValue("actor", actorId.Value);
            update.Parameters.AddWithValue("tenant", tenantId.Value);
            update.Parameters.AddWithValue("id", orderId);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
        await AuditAsync(connection, transaction, tenantId, actorId, "order.cancelled", orderId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return OrderResult.Success;
    }

    public async Task<bool> HasPlacedOrderForVisitAsync(TenantId tenantId, Guid visitId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, null, cancellationToken);
        await using var command = new NpgsqlCommand("SELECT 1 FROM orders WHERE tenant_id = @tenant AND visit_id = @visit AND status = 'placed' LIMIT 1", connection, transaction);
        command.Parameters.AddWithValue("tenant", tenantId.Value);
        command.Parameters.AddWithValue("visit", visitId);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
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

    private static async Task AuditAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, TenantId tenantId, UserId actorId, string action, Guid entity,
        CancellationToken cancellationToken)
    {
        await using var audit = new NpgsqlCommand("""
            INSERT INTO audit_logs (id, tenant_id, actor_id, action, entity_id, occurred_at)
            VALUES (gen_random_uuid(), @tenant, @actor, @action, @entity, now())
            """, connection, transaction);
        audit.Parameters.AddWithValue("tenant", tenantId.Value);
        audit.Parameters.AddWithValue("actor", actorId.Value);
        audit.Parameters.AddWithValue("action", action);
        audit.Parameters.AddWithValue("entity", entity.ToString());
        await audit.ExecuteNonQueryAsync(cancellationToken);
    }
}
