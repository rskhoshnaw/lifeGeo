using LifeDrop.Application.Orders;
using LifeDrop.Domain.Common;
using Npgsql;

namespace LifeDrop.Infrastructure.Orders;

public sealed class PostgresWholesalerStore(NpgsqlDataSource dataSource) : IWholesalerStore
{
    private static async Task<NpgsqlTransaction> BeginAsync(NpgsqlConnection connection, TenantId tenantId, UserId actorId, CancellationToken cancellationToken)
    {
        var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var set = new NpgsqlCommand("SELECT set_config('app.current_tenant', @tenant, true), set_config('app.current_actor', @actor, true)", connection, transaction);
        set.Parameters.AddWithValue("tenant", tenantId.Value.ToString());
        set.Parameters.AddWithValue("actor", actorId.Value.ToString());
        await set.ExecuteNonQueryAsync(cancellationToken);
        return transaction;
    }

    // The wholesaler orders the actor may handle: orders of the wholesaler warehouses the actor works in.
    private const string Mine = """
        w.kind = 'wholesaler' AND EXISTS (SELECT 1 FROM warehouse_staff s WHERE s.tenant_id = o.tenant_id AND s.warehouse_id = o.warehouse_id AND s.user_id = @actor)
        """;

    public async Task<IReadOnlyList<WholesalerDto>> ListWholesalersAsync(TenantId tenantId, UserId actorId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, actorId, cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT w.id, w.code, w.name,
                   (SELECT count(*)::int FROM orders o WHERE o.tenant_id = w.tenant_id AND o.warehouse_id = w.id AND o.status = 'placed')
            FROM warehouses w
            WHERE w.tenant_id = @tenant AND w.kind = 'wholesaler' AND w.status = 'active'
              AND EXISTS (SELECT 1 FROM warehouse_staff s WHERE s.tenant_id = w.tenant_id AND s.warehouse_id = w.id AND s.user_id = @actor)
            ORDER BY w.name COLLATE "C", w.id
            """, connection, transaction);
        command.Parameters.AddWithValue("tenant", tenantId.Value);
        command.Parameters.AddWithValue("actor", actorId.Value);
        var items = new List<WholesalerDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) items.Add(new WholesalerDto(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3)));
        return items;
    }

    public async Task<IReadOnlyList<WholesalerOrderDto>> ListOrdersAsync(TenantId tenantId, UserId actorId, Guid? warehouseId, string status, int limit, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, actorId, cancellationToken);
        var orders = new List<WholesalerOrderDto>();
        await using (var command = new NpgsqlCommand($"""
            SELECT o.id, o.number, o.created_at, o.status, o.warehouse_id, w.name, c.id, c.code, c.name, c.contact_name, c.phone, c.phone2, c.address,
                   (SELECT string_agg(a.name, ' / ' ORDER BY a.depth DESC)
                    FROM (WITH RECURSIVE chain(id, parent_id, name, depth) AS (
                              SELECT p.id, p.parent_id, p.name, 0 FROM places p WHERE p.id = c.place_id
                              UNION ALL
                              SELECT p.id, p.parent_id, p.name, k.depth + 1 FROM places p JOIN chain k ON p.id = k.parent_id)
                          SELECT * FROM chain) a),
                   o.currency, o.total, o.note, u.email, o.dispatched_at, du.email
            FROM orders o
            JOIN warehouses w ON w.tenant_id = o.tenant_id AND w.id = o.warehouse_id
            JOIN customers c ON c.tenant_id = o.tenant_id AND c.id = o.customer_id
            LEFT JOIN users u ON u.tenant_id = o.tenant_id AND u.id = o.created_by
            LEFT JOIN users du ON du.tenant_id = o.tenant_id AND du.id = o.dispatched_by
            WHERE o.tenant_id = @tenant AND o.status = @status AND (@warehouse::uuid IS NULL OR o.warehouse_id = @warehouse) AND {Mine}
            ORDER BY o.created_at DESC, o.id DESC
            LIMIT @limit
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("tenant", tenantId.Value);
            command.Parameters.AddWithValue("actor", actorId.Value);
            command.Parameters.AddWithValue("status", status);
            command.Parameters.Add(new NpgsqlParameter("warehouse", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = (object?)warehouseId ?? DBNull.Value });
            command.Parameters.AddWithValue("limit", limit);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                string? Text(int i) => reader.IsDBNull(i) ? null : reader.GetString(i);
                orders.Add(new WholesalerOrderDto(reader.GetGuid(0), OrderNumber(reader.GetInt64(1)), reader.GetFieldValue<DateTimeOffset>(2), reader.GetString(3), reader.GetGuid(4), reader.GetString(5),
                    reader.GetGuid(6), reader.GetString(7), reader.GetString(8), Text(9), Text(10), Text(11), Text(12), Text(13), reader.GetString(14), reader.GetDecimal(15), Text(16), Text(17),
                    reader.IsDBNull(18) ? null : reader.GetFieldValue<DateTimeOffset>(18), Text(19), Array.Empty<WholesalerLineDto>()));
            }
        }
        if (orders.Count == 0) return orders;

        var lines = new Dictionary<Guid, List<WholesalerLineDto>>();
        await using (var items = new NpgsqlCommand("""
            SELECT i.order_id, i.product_code, i.product_name, i.unit, i.quantity, i.unit_price, i.line_total
            FROM order_items i
            WHERE i.tenant_id = @tenant AND i.order_id = ANY(@orders)
            ORDER BY i.order_id, i.line_no
            """, connection, transaction))
        {
            items.Parameters.AddWithValue("tenant", tenantId.Value);
            items.Parameters.AddWithValue("orders", orders.Select(o => o.Id).ToArray());
            await using var reader = await items.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var id = reader.GetGuid(0);
                if (!lines.TryGetValue(id, out var list)) lines[id] = list = [];
                list.Add(new WholesalerLineDto(reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetDecimal(4), reader.GetDecimal(5), reader.GetDecimal(6)));
            }
        }
        return orders.Select(o => lines.TryGetValue(o.Id, out var list) ? o with { Lines = list } : o).ToList();
    }

    public async Task<OrderResult> DispatchAsync(TenantId tenantId, UserId actorId, Guid orderId, string? note, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, actorId, cancellationToken);
        string? status;
        await using (var lookup = new NpgsqlCommand($"""
            SELECT o.status FROM orders o JOIN warehouses w ON w.tenant_id = o.tenant_id AND w.id = o.warehouse_id
            WHERE o.tenant_id = @tenant AND o.id = @id AND {Mine}
            FOR UPDATE OF o
            """, connection, transaction))
        {
            lookup.Parameters.AddWithValue("tenant", tenantId.Value);
            lookup.Parameters.AddWithValue("id", orderId);
            lookup.Parameters.AddWithValue("actor", actorId.Value);
            status = await lookup.ExecuteScalarAsync(cancellationToken) as string;
        }
        if (status is null) return OrderResult.NotFound;
        if (status != "placed") return OrderResult.Conflict;

        await using (var update = new NpgsqlCommand("UPDATE orders SET status = 'dispatched', dispatched_at = now(), dispatched_by = @actor WHERE tenant_id = @tenant AND id = @id", connection, transaction))
        {
            update.Parameters.AddWithValue("tenant", tenantId.Value);
            update.Parameters.AddWithValue("id", orderId);
            update.Parameters.AddWithValue("actor", actorId.Value);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var history = new NpgsqlCommand("INSERT INTO order_status_history (tenant_id, order_id, status, actor_id, note) VALUES (@tenant, @id, 'dispatched', @actor, @note)", connection, transaction))
        {
            history.Parameters.AddWithValue("tenant", tenantId.Value);
            history.Parameters.AddWithValue("id", orderId);
            history.Parameters.AddWithValue("actor", actorId.Value);
            history.Parameters.Add(new NpgsqlParameter("note", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)note ?? DBNull.Value });
            await history.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var audit = new NpgsqlCommand("""
            INSERT INTO audit_logs (id, tenant_id, actor_id, action, entity_id, occurred_at) VALUES (gen_random_uuid(), @tenant, @actor, 'order.dispatched', @entity, now())
            """, connection, transaction))
        {
            audit.Parameters.AddWithValue("tenant", tenantId.Value);
            audit.Parameters.AddWithValue("actor", actorId.Value);
            audit.Parameters.AddWithValue("entity", orderId.ToString());
            await audit.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return OrderResult.Success;
    }

    private static string OrderNumber(long number) => PostgresOrderStore.FormatNumber(number);
}
