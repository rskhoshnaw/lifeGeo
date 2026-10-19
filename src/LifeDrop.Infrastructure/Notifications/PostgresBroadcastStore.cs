using LifeDrop.Application.Notifications;
using LifeDrop.Domain.Common;
using Npgsql;
using NpgsqlTypes;

namespace LifeDrop.Infrastructure.Notifications;

// Writes the notifications of one manager message and its audit row in one transaction, with the tenant context set for RLS (plan step 4.16).
public sealed class PostgresBroadcastStore(NpgsqlDataSource dataSource) : IBroadcastStore
{
    public async Task<int> SendAsync(TenantId tenantId, UserId actorId, MessageAudience audience, string message, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var setTenant = new NpgsqlCommand("SELECT set_config('app.current_tenant', @tenant, true)", connection, transaction))
        {
            setTenant.Parameters.AddWithValue("tenant", tenantId.Value.ToString());
            await setTenant.ExecuteNonQueryAsync(cancellationToken);
        }

        int recipients;
        await using (var insert = new NpgsqlCommand("""
            WITH target AS (
                SELECT u.id FROM users u
                WHERE u.tenant_id = @tenant AND u.is_active
                  AND (@users::uuid[] IS NULL OR u.id = ANY(@users))
                  AND (@role::text IS NULL OR EXISTS (SELECT 1 FROM user_roles ur JOIN roles r ON r.tenant_id = ur.tenant_id AND r.id = ur.role_id
                                                      WHERE ur.tenant_id = u.tenant_id AND ur.user_id = u.id AND r.name = @role))
                  AND (@team::uuid IS NULL OR EXISTS (SELECT 1 FROM team_members m WHERE m.user_id = u.id AND m.team_id = @team))
                  AND (@region::uuid IS NULL OR EXISTS (SELECT 1 FROM team_members m JOIN teams t ON t.id = m.team_id WHERE m.user_id = u.id AND t.region_id = @region))
                  AND (@onlyRegion::uuid IS NULL OR EXISTS (SELECT 1 FROM team_members m JOIN teams t ON t.id = m.team_id WHERE m.user_id = u.id AND t.region_id = @onlyRegion))
            ), made AS (
                INSERT INTO notifications (tenant_id, user_id, kind, entity_id, actor_id)
                SELECT @tenant, id, 'manager.message', @message, @actor FROM target
                RETURNING 1
            )
            SELECT count(*)::int FROM made
            """, connection, transaction))
        {
            insert.Parameters.AddWithValue("tenant", tenantId.Value);
            insert.Parameters.AddWithValue("actor", actorId.Value);
            insert.Parameters.AddWithValue("message", message);
            insert.Parameters.Add(new NpgsqlParameter("users", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = audience.UserIds is null ? DBNull.Value : audience.UserIds.ToArray() });
            insert.Parameters.Add(new NpgsqlParameter("role", NpgsqlDbType.Text) { Value = (object?)audience.Role ?? DBNull.Value });
            insert.Parameters.Add(new NpgsqlParameter("team", NpgsqlDbType.Uuid) { Value = (object?)audience.TeamId ?? DBNull.Value });
            insert.Parameters.Add(new NpgsqlParameter("region", NpgsqlDbType.Uuid) { Value = (object?)audience.RegionId ?? DBNull.Value });
            insert.Parameters.Add(new NpgsqlParameter("onlyRegion", NpgsqlDbType.Uuid) { Value = (object?)audience.OnlyRegionId ?? DBNull.Value });
            recipients = (int)(await insert.ExecuteScalarAsync(cancellationToken))!;
        }

        await using (var audit = new NpgsqlCommand("""
            INSERT INTO audit_logs (id, tenant_id, actor_id, action, entity_id, occurred_at)
            VALUES (gen_random_uuid(), @tenant, @actor, 'notification.broadcast', @recipients, now())
            """, connection, transaction))
        {
            audit.Parameters.AddWithValue("tenant", tenantId.Value);
            audit.Parameters.AddWithValue("actor", actorId.Value);
            audit.Parameters.AddWithValue("recipients", recipients.ToString(System.Globalization.CultureInfo.InvariantCulture));
            await audit.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return recipients;
    }
}
