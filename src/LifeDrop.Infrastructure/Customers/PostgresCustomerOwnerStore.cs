using LifeDrop.Application.Customers;
using LifeDrop.Application.Roles;
using LifeDrop.Domain.Common;
using Npgsql;

namespace LifeDrop.Infrastructure.Customers;

// Gives customers to another user (plan step 5.28). One UPDATE limited to the caller's scope, its audit rows, one transaction.
public sealed class PostgresCustomerOwnerStore(NpgsqlDataSource dataSource) : ICustomerOwnerStore
{
    public async Task<NewOwnerInfo?> FindOwnerAsync(TenantId tenantId, Guid userId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await SetTenantAsync(connection, transaction, tenantId, cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT u.id, (SELECT t.region_id FROM team_members tm JOIN teams t ON t.tenant_id = tm.tenant_id AND t.id = tm.team_id
                          WHERE tm.tenant_id = u.tenant_id AND tm.user_id = u.id LIMIT 1)
            FROM users u WHERE u.tenant_id = @tenant AND u.id = @user AND u.is_active
            """, connection, transaction);
        command.Parameters.AddWithValue("tenant", tenantId.Value);
        command.Parameters.AddWithValue("user", userId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new NewOwnerInfo(reader.GetGuid(0), reader.IsDBNull(1) ? null : reader.GetGuid(1));
    }

    public async Task<int> SetAsync(TenantId tenantId, UserId actorId, AccessScope scope, AccessScope ownerScope, NewOwnerInfo owner, IReadOnlyList<Guid>? customerIds, Guid? fromUserId,
        CancellationToken cancellationToken)
    {
        var scopeFilter = (scope.Scope switch
        {
            DataScope.Tenant => "TRUE",
            DataScope.Region when scope.RegionId is not null => "(c.owner_user_id = @actor OR c.region_id = @scopeRegion)",
            _ => "c.owner_user_id = @actor"
        }) + scope.DoctorFilter("c.type") + ownerScope.DoctorFilter("c.type");
        var sql = $"""
            WITH changed AS (
                UPDATE customers c SET owner_user_id = @owner, region_id = COALESCE(@ownerRegion, c.region_id)
                WHERE c.tenant_id = @tenant AND c.archived_at IS NULL AND c.owner_user_id IS DISTINCT FROM @owner AND {scopeFilter}
                  AND ((@ids::uuid[] IS NOT NULL AND c.id = ANY(@ids)) OR (@from::uuid IS NOT NULL AND c.owner_user_id = @from))
                RETURNING c.id
            ),
            logged AS (
                INSERT INTO audit_logs (id, tenant_id, actor_id, action, entity_id, occurred_at)
                SELECT gen_random_uuid(), @tenant, @actor, 'customer.owner_changed', changed.id::text, now() FROM changed
                RETURNING 1
            )
            SELECT count(*) FROM changed
            """;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await SetTenantAsync(connection, transaction, tenantId, cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("tenant", tenantId.Value);
        command.Parameters.AddWithValue("actor", actorId.Value);
        command.Parameters.AddWithValue("owner", owner.UserId);
        command.Parameters.Add(new NpgsqlParameter("ownerRegion", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = (object?)owner.RegionId ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("scopeRegion", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = (object?)scope.RegionId ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("from", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = (object?)fromUserId ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("ids", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid) { Value = (object?)customerIds?.ToArray() ?? DBNull.Value });
        var updated = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        return updated;
    }

    private static async Task SetTenantAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, TenantId tenantId, CancellationToken cancellationToken)
    {
        await using var set = new NpgsqlCommand("SELECT set_config('app.current_tenant', @tenant, true)", connection, transaction);
        set.Parameters.AddWithValue("tenant", tenantId.Value.ToString());
        await set.ExecuteNonQueryAsync(cancellationToken);
    }
}
