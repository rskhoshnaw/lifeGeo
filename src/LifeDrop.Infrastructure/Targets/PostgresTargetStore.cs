using LifeDrop.Application.Roles;
using LifeDrop.Application.Targets;
using LifeDrop.Domain.Common;
using Npgsql;
using NpgsqlTypes;

namespace LifeDrop.Infrastructure.Targets;

public sealed class PostgresTargetStore(NpgsqlDataSource dataSource) : ITargetStore
{
    private static async Task<NpgsqlTransaction> BeginAsync(NpgsqlConnection connection, TenantId tenantId, CancellationToken cancellationToken)
    {
        var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var set = new NpgsqlCommand("SELECT set_config('app.current_tenant', @tenant, true)", connection, transaction);
        set.Parameters.AddWithValue("tenant", tenantId.Value.ToString());
        await set.ExecuteNonQueryAsync(cancellationToken);
        return transaction;
    }

    public async Task<TargetResult> SetAsync(TenantId tenantId, UserId actorId, Guid userId, Guid productId, DateOnly month, decimal? quantity, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, cancellationToken);
        await using (var exists = new NpgsqlCommand("""
            SELECT (SELECT count(*) FROM users WHERE tenant_id = @tenant AND id = @user) = 1
               AND (SELECT count(*) FROM products WHERE tenant_id = @tenant AND id = @product) = 1
            """, connection, transaction))
        {
            exists.Parameters.AddWithValue("tenant", tenantId.Value);
            exists.Parameters.AddWithValue("user", userId);
            exists.Parameters.AddWithValue("product", productId);
            if (await exists.ExecuteScalarAsync(cancellationToken) is not true) return TargetResult.NotFound;
        }

        var changed = false;
        if (quantity is { } value)
        {
            await using var upsert = new NpgsqlCommand("""
                INSERT INTO sales_targets (tenant_id, user_id, product_id, month, quantity, set_by)
                VALUES (@tenant, @user, @product, @month, @quantity, @actor)
                ON CONFLICT (tenant_id, user_id, product_id, month) DO UPDATE SET quantity = EXCLUDED.quantity, set_by = EXCLUDED.set_by, updated_at = now()
                """, connection, transaction);
            upsert.Parameters.AddWithValue("tenant", tenantId.Value);
            upsert.Parameters.AddWithValue("user", userId);
            upsert.Parameters.AddWithValue("product", productId);
            upsert.Parameters.Add(new NpgsqlParameter("month", NpgsqlDbType.Date) { Value = month.ToDateTime(TimeOnly.MinValue) });
            upsert.Parameters.AddWithValue("quantity", value);
            upsert.Parameters.AddWithValue("actor", actorId.Value);
            changed = await upsert.ExecuteNonQueryAsync(cancellationToken) > 0;
        }
        else
        {
            await using var delete = new NpgsqlCommand("DELETE FROM sales_targets WHERE tenant_id = @tenant AND user_id = @user AND product_id = @product AND month = @month", connection, transaction);
            delete.Parameters.AddWithValue("tenant", tenantId.Value);
            delete.Parameters.AddWithValue("user", userId);
            delete.Parameters.AddWithValue("product", productId);
            delete.Parameters.Add(new NpgsqlParameter("month", NpgsqlDbType.Date) { Value = month.ToDateTime(TimeOnly.MinValue) });
            changed = await delete.ExecuteNonQueryAsync(cancellationToken) > 0;
        }

        if (changed)
        {
            await using var audit = new NpgsqlCommand("""
                INSERT INTO audit_logs (id, tenant_id, actor_id, action, entity_id, occurred_at)
                VALUES (gen_random_uuid(), @tenant, @actor, @action, @entity, now())
                """, connection, transaction);
            audit.Parameters.AddWithValue("tenant", tenantId.Value);
            audit.Parameters.AddWithValue("actor", actorId.Value);
            audit.Parameters.AddWithValue("action", quantity is null ? "target.cleared" : "target.set");
            audit.Parameters.AddWithValue("entity", $"{userId}:{productId}:{month:yyyy-MM}");
            await audit.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return TargetResult.Success;
    }

    public async Task<IReadOnlyList<TargetRowDto>> ListAsync(TenantId tenantId, AccessScope scope, DateOnly month, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, cancellationToken);
        // Scope 2 = tenant, 1 = region (the actor and the members of teams in the region), 0 = own (DataScope values), as the route history lists its users.
        // Actual: what was delivered in the month (the last "delivered" step of the order decides the month), counted for the user who placed the order.
        await using var command = new NpgsqlCommand("""
            WITH visible AS (
                SELECT u.id, u.email
                FROM users u
                WHERE u.tenant_id = @tenant AND u.is_active
                  AND (@scope = 2 OR u.id = @actor OR (@scope = 1 AND @region::uuid IS NOT NULL AND EXISTS (
                        SELECT 1 FROM team_members m JOIN teams t ON t.id = m.team_id WHERE m.user_id = u.id AND t.region_id = @region)))
            ), delivered AS (
                SELECT o.id, o.created_by
                FROM orders o
                JOIN LATERAL (SELECT max(h.occurred_at) AS at FROM order_status_history h WHERE h.tenant_id = o.tenant_id AND h.order_id = o.id AND h.status = 'delivered') d ON true
                WHERE o.tenant_id = @tenant AND o.status = 'delivered' AND d.at >= @from AND d.at < @to
            ), actual AS (
                SELECT d.created_by AS user_id, i.product_id, sum(COALESCE(i.delivered_quantity, i.quantity)) AS quantity
                FROM delivered d
                JOIN order_items i ON i.tenant_id = @tenant AND i.order_id = d.id
                GROUP BY d.created_by, i.product_id
            ), target AS (
                SELECT t.user_id, t.product_id, t.quantity FROM sales_targets t WHERE t.tenant_id = @tenant AND t.month = @month
            )
            SELECT v.id, v.email, p.id, p.code, p.name, p.unit, t.quantity, COALESCE(a.quantity, 0)
            FROM visible v
            JOIN (SELECT user_id, product_id FROM target UNION SELECT user_id, product_id FROM actual) k ON k.user_id = v.id
            JOIN products p ON p.tenant_id = @tenant AND p.id = k.product_id
            LEFT JOIN target t ON t.user_id = k.user_id AND t.product_id = k.product_id
            LEFT JOIN actual a ON a.user_id = k.user_id AND a.product_id = k.product_id
            ORDER BY v.email COLLATE "C" NULLS LAST, v.id, p.code COLLATE "C"
            """, connection, transaction);
        command.Parameters.AddWithValue("tenant", tenantId.Value);
        command.Parameters.AddWithValue("scope", (int)scope.Scope);
        command.Parameters.AddWithValue("actor", scope.ActorId.Value);
        command.Parameters.Add(new NpgsqlParameter("region", NpgsqlDbType.Uuid) { Value = (object?)scope.RegionId ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("month", NpgsqlDbType.Date) { Value = month.ToDateTime(TimeOnly.MinValue) });
        command.Parameters.Add(new NpgsqlParameter("from", NpgsqlDbType.TimestampTz) { Value = from.ToUniversalTime() });
        command.Parameters.Add(new NpgsqlParameter("to", NpgsqlDbType.TimestampTz) { Value = to.ToUniversalTime() });
        var rows = new List<TargetRowDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            rows.Add(new TargetRowDto(reader.GetGuid(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetGuid(2), reader.GetString(3), reader.GetString(4), reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetDecimal(6), reader.GetDecimal(7)));
        return rows;
    }
}
