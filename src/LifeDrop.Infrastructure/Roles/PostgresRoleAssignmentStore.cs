using LifeDrop.Application.Roles;
using LifeDrop.Domain.Common;
using Npgsql;

namespace LifeDrop.Infrastructure.Roles;

// All lookups and the insert run in one transaction. The database's composite
// foreign keys provide a second tenant boundary for any other write path.
public sealed class PostgresRoleAssignmentStore(NpgsqlDataSource dataSource) : IRoleAssignmentStore
{
    public async Task<AssignRoleResult> AssignAsync(TenantId tenantId, UserId actorId,
        UserId userId, RoleId roleId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        const string sql = """
            WITH eligible AS (
                SELECT u.id AS user_id, r.id AS role_id
                FROM users u JOIN roles r ON r.tenant_id = u.tenant_id
                JOIN users a ON a.tenant_id = u.tenant_id AND a.id = @actor
                JOIN tenants t ON t.id = u.tenant_id
                WHERE u.tenant_id = @tenant AND u.id = @user
                  AND r.id = @role AND u.is_active AND a.is_active AND t.is_active
                  AND EXISTS (
                      SELECT 1 FROM user_roles ar
                      JOIN role_permissions p ON p.tenant_id = ar.tenant_id AND p.role_id = ar.role_id
                      WHERE ar.tenant_id = @tenant AND ar.user_id = @actor
                        AND p.permission_key IN ('user.manageroles', 'system.admin')
                  )
            ), inserted AS (
                INSERT INTO user_roles (tenant_id, user_id, role_id)
                SELECT @tenant, user_id, role_id FROM eligible
                ON CONFLICT DO NOTHING RETURNING tenant_id, user_id, role_id
            ), audited AS (
                INSERT INTO audit_logs (id, tenant_id, actor_id, action, entity_id, occurred_at)
                SELECT gen_random_uuid(), tenant_id, @actor, 'user.role_assigned',
                       user_id::text, now() FROM inserted
                RETURNING id
            )
            SELECT count(*) FROM audited;
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("tenant", tenantId.Value);
        command.Parameters.AddWithValue("actor", actorId.Value);
        command.Parameters.AddWithValue("user", userId.Value);
        command.Parameters.AddWithValue("role", roleId.Value);
        var inserted = (long)(await command.ExecuteScalarAsync(cancellationToken) ?? 0L);
        if (inserted == 1)
        {
            await transaction.CommitAsync(cancellationToken);
            return AssignRoleResult.Assigned;
        }

        // Foreign-tenant identifiers are deliberately indistinguishable from missing ones.
        var actorExists = await ExistsAsync(connection, transaction,
            """
            SELECT EXISTS(
                SELECT 1 FROM users a JOIN tenants t ON t.id=a.tenant_id
                JOIN user_roles ar ON ar.tenant_id=a.tenant_id AND ar.user_id=a.id
                JOIN role_permissions p ON p.tenant_id=ar.tenant_id AND p.role_id=ar.role_id
                WHERE a.tenant_id=@tenant AND a.id=@id AND a.is_active AND t.is_active
                  AND p.permission_key IN ('user.manageroles', 'system.admin'))
            """,
            tenantId.Value, actorId.Value, cancellationToken);
        if (!actorExists) return AssignRoleResult.Forbidden;
        var userExists = await ExistsAsync(connection, transaction,
            "SELECT EXISTS(SELECT 1 FROM users u JOIN tenants t ON t.id=u.tenant_id WHERE u.tenant_id=@tenant AND u.id=@id AND u.is_active AND t.is_active)",
            tenantId.Value, userId.Value, cancellationToken);
        if (!userExists) return AssignRoleResult.UserNotFound;
        var roleExists = await ExistsAsync(connection, transaction,
            "SELECT EXISTS(SELECT 1 FROM roles WHERE tenant_id=@tenant AND id=@id)",
            tenantId.Value, roleId.Value, cancellationToken);
        if (!roleExists) return AssignRoleResult.RoleNotFound;
        return AssignRoleResult.AlreadyAssigned;
    }

    private static async Task<bool> ExistsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        string sql, Guid tenantId, Guid id, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("id", id);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken) ?? false);
    }
}
