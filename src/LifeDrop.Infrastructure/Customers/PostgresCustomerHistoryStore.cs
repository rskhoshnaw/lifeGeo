using LifeDrop.Application.Customers;
using LifeDrop.Domain.Common;
using Npgsql;

namespace LifeDrop.Infrastructure.Customers;

public sealed class PostgresCustomerHistoryStore(NpgsqlDataSource dataSource) : ICustomerHistoryStore
{
    private static async Task<NpgsqlTransaction> BeginAsync(NpgsqlConnection connection, TenantId tenantId, CancellationToken cancellationToken)
    {
        var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var set = new NpgsqlCommand("SELECT set_config('app.current_tenant', @tenant, true)", connection, transaction);
        set.Parameters.AddWithValue("tenant", tenantId.Value.ToString());
        await set.ExecuteNonQueryAsync(cancellationToken);
        return transaction;
    }

    public async Task<IReadOnlyList<CustomerVisitHistoryDto>> ListVisitsAsync(TenantId tenantId, Guid customerId, int limit, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT v.id, v.schedule_date, v.status, v.visit_type, u.email,
                   (SELECT min(e.captured_at) FROM visit_location_events e WHERE e.tenant_id = v.tenant_id AND e.visit_id = v.id AND e.kind = 'check_in' AND e.accepted),
                   (SELECT max(e.captured_at) FROM visit_location_events e WHERE e.tenant_id = v.tenant_id AND e.visit_id = v.id AND e.kind = 'check_out' AND e.accepted),
                   r.outcome, r.note, v.exception_reason
            FROM visits v
            LEFT JOIN users u ON u.tenant_id = v.tenant_id AND u.id = v.user_id
            LEFT JOIN visit_reports r ON r.tenant_id = v.tenant_id AND r.visit_id = v.id
            WHERE v.tenant_id = @tenant AND v.customer_id = @customer
            ORDER BY v.schedule_date DESC, v.created_at DESC, v.id
            LIMIT @limit
            """, connection, transaction);
        command.Parameters.AddWithValue("tenant", tenantId.Value);
        command.Parameters.AddWithValue("customer", customerId);
        command.Parameters.AddWithValue("limit", limit);
        var visits = new List<CustomerVisitHistoryDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            visits.Add(new CustomerVisitHistoryDto(reader.GetGuid(0), DateOnly.FromDateTime(reader.GetDateTime(1)), reader.GetString(2), reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5), reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6),
                reader.IsDBNull(7) ? null : reader.GetString(7), reader.IsDBNull(8) ? null : reader.GetString(8), reader.IsDBNull(9) ? null : reader.GetString(9)));
        return visits;
    }

    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<CustomerOrderLineDto>>> ListOrderLinesAsync(TenantId tenantId, IReadOnlyCollection<Guid> orderIds, CancellationToken cancellationToken)
    {
        var result = new Dictionary<Guid, IReadOnlyList<CustomerOrderLineDto>>();
        if (orderIds.Count == 0) return result;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT i.order_id, i.product_name, i.quantity, i.delivered_quantity
            FROM order_items i
            WHERE i.tenant_id = @tenant AND i.order_id = ANY(@orders)
            ORDER BY i.order_id, i.line_no
            """, connection, transaction);
        command.Parameters.AddWithValue("tenant", tenantId.Value);
        command.Parameters.AddWithValue("orders", orderIds.ToArray());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var lines = new Dictionary<Guid, List<CustomerOrderLineDto>>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var id = reader.GetGuid(0);
            if (!lines.TryGetValue(id, out var list)) lines[id] = list = [];
            list.Add(new CustomerOrderLineDto(reader.GetString(1), reader.GetDecimal(2), reader.IsDBNull(3) ? null : reader.GetDecimal(3)));
        }
        foreach (var (id, list) in lines) result[id] = list;
        return result;
    }
}
