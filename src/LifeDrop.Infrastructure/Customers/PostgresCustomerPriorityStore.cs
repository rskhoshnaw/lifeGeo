using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LifeDrop.Application.Customers;
using LifeDrop.Application.Roles;
using LifeDrop.Domain.Common;
using Npgsql;

namespace LifeDrop.Infrastructure.Customers;

public sealed class PostgresCustomerPriorityStore(NpgsqlDataSource dataSource) : ICustomerPriorityStore
{
    public async Task<int> SetAsync(TenantId tenantId, UserId actorId, AccessScope scope, string priority, IReadOnlyList<Guid>? customerIds, Guid? placeId,
        CancellationToken cancellationToken)
    {
        var scopeFilter = scope.Scope switch
        {
            DataScope.Tenant => "TRUE",
            DataScope.Region when scope.RegionId is not null => "(c.owner_user_id = @actor OR c.region_id = @scopeRegion)",
            _ => "c.owner_user_id = @actor"
        };
        var sql = $"""
            WITH RECURSIVE sub AS (
                SELECT p.id FROM places p WHERE p.tenant_id = @tenant AND p.id = @place
                UNION ALL
                SELECT ch.id FROM places ch JOIN sub ON ch.parent_id = sub.id WHERE ch.tenant_id = @tenant
            ),
            changed AS (
                UPDATE customers c SET priority = @priority, priority_set_by = @actor, priority_set_at = now()
                WHERE c.tenant_id = @tenant AND c.priority <> @priority AND {scopeFilter}
                  AND ((@ids::uuid[] IS NOT NULL AND c.id = ANY(@ids)) OR (@place::uuid IS NOT NULL AND c.place_id IN (SELECT id FROM sub)))
                RETURNING c.id
            ),
            logged AS (
                INSERT INTO audit_logs (id, tenant_id, actor_id, action, entity_id, occurred_at)
                SELECT gen_random_uuid(), @tenant, @actor, 'customer.priority_set', changed.id::text, now() FROM changed
                RETURNING 1
            )
            SELECT count(*) FROM changed
            """;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var set = new NpgsqlCommand("SELECT set_config('app.current_tenant', @tenant, true)", connection, transaction))
        {
            set.Parameters.AddWithValue("tenant", tenantId.Value.ToString());
            await set.ExecuteNonQueryAsync(cancellationToken);
        }
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("tenant", tenantId.Value);
        command.Parameters.AddWithValue("actor", actorId.Value);
        command.Parameters.AddWithValue("priority", priority);
        command.Parameters.Add(new NpgsqlParameter("scopeRegion", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = (object?)scope.RegionId ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("place", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = (object?)placeId ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("ids", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid) { Value = (object?)customerIds?.ToArray() ?? DBNull.Value });
        var updated = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        return updated;
    }
}
