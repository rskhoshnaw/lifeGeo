using LifeDrop.Application.Roles;
using LifeDrop.Domain.Common;
using Npgsql;

namespace LifeDrop.Infrastructure.Roles;

// roles, role_permissions and user_roles have no RLS, so every statement filters by tenant explicitly. Each call is one transaction with its audit row.
public sealed class PostgresRoleDefinitionStore(NpgsqlDataSource dataSource) : IRoleDefinitionStore
{
    public async Task<(RoleResult Result, Guid? Id)> CreateAsync(TenantId tenantId, UserId actorId, SaveRoleRequest role, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var id = Guid.NewGuid();
        try
        {
            await using (var insert = new NpgsqlCommand("INSERT INTO roles (id, tenant_id, name, data_scope, requires_device_binding) VALUES (@id, @tenant, @name, @scope, @binding)", connection, transaction))
            {
                insert.Parameters.AddWithValue("id", id);
                insert.Parameters.AddWithValue("tenant", tenantId.Value);
                insert.Parameters.AddWithValue("name", role.Name!);
                insert.Parameters.AddWithValue("scope", role.DataScope!);
                insert.Parameters.AddWithValue("binding", role.RequiresDeviceBinding ?? false);
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return (RoleResult.Conflict, null);
        }
        await WritePermissionsAsync(connection, transaction, tenantId, id, role.Permissions!, cancellationToken);
        await AuditAsync(connection, transaction, tenantId, actorId, "role.created", id, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (RoleResult.Success, id);
    }

    public async Task<RoleResult> UpdateAsync(TenantId tenantId, UserId actorId, Guid roleId, SaveRoleRequest role, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        string? current;
        await using (var read = new NpgsqlCommand("SELECT name FROM roles WHERE tenant_id = @tenant AND id = @id FOR UPDATE", connection, transaction))
        {
            read.Parameters.AddWithValue("tenant", tenantId.Value);
            read.Parameters.AddWithValue("id", roleId);
            current = (string?)await read.ExecuteScalarAsync(cancellationToken);
        }
        if (current is null) return RoleResult.NotFound;
        if (RoleDefaults.Names.Contains(current) && current != role.Name) return RoleResult.InvalidData;
        try
        {
            await using var update = new NpgsqlCommand("UPDATE roles SET name = @name, data_scope = @scope, requires_device_binding = @binding WHERE tenant_id = @tenant AND id = @id", connection, transaction);
            update.Parameters.AddWithValue("id", roleId);
            update.Parameters.AddWithValue("tenant", tenantId.Value);
            update.Parameters.AddWithValue("name", role.Name!);
            update.Parameters.AddWithValue("scope", role.DataScope!);
            update.Parameters.AddWithValue("binding", role.RequiresDeviceBinding ?? false);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return RoleResult.Conflict;
        }
        await using (var clear = new NpgsqlCommand("DELETE FROM role_permissions WHERE tenant_id = @tenant AND role_id = @id", connection, transaction))
        {
            clear.Parameters.AddWithValue("tenant", tenantId.Value);
            clear.Parameters.AddWithValue("id", roleId);
            await clear.ExecuteNonQueryAsync(cancellationToken);
        }
        await WritePermissionsAsync(connection, transaction, tenantId, roleId, role.Permissions!, cancellationToken);
        if (!await SomeoneCanManageRolesAsync(connection, transaction, tenantId, cancellationToken)) return RoleResult.Conflict;   // the transaction is rolled back on dispose
        await AuditAsync(connection, transaction, tenantId, actorId, "role.updated", roleId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return RoleResult.Success;
    }

    public async Task<RoleResult> DeleteAsync(TenantId tenantId, UserId actorId, Guid roleId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        string? current;
        await using (var read = new NpgsqlCommand("SELECT name FROM roles WHERE tenant_id = @tenant AND id = @id FOR UPDATE", connection, transaction))
        {
            read.Parameters.AddWithValue("tenant", tenantId.Value);
            read.Parameters.AddWithValue("id", roleId);
            current = (string?)await read.ExecuteScalarAsync(cancellationToken);
        }
        if (current is null) return RoleResult.NotFound;
        if (RoleDefaults.Names.Contains(current)) return RoleResult.Conflict;
        await using (var used = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM user_roles WHERE tenant_id = @tenant AND role_id = @id)", connection, transaction))
        {
            used.Parameters.AddWithValue("tenant", tenantId.Value);
            used.Parameters.AddWithValue("id", roleId);
            if ((bool)(await used.ExecuteScalarAsync(cancellationToken))!) return RoleResult.Conflict;
        }
        foreach (var sql in new[] { "DELETE FROM role_permissions WHERE tenant_id = @tenant AND role_id = @id", "DELETE FROM roles WHERE tenant_id = @tenant AND id = @id" })
        {
            await using var delete = new NpgsqlCommand(sql, connection, transaction);
            delete.Parameters.AddWithValue("tenant", tenantId.Value);
            delete.Parameters.AddWithValue("id", roleId);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }
        await AuditAsync(connection, transaction, tenantId, actorId, "role.deleted", roleId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return RoleResult.Success;
    }

    private static async Task WritePermissionsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, TenantId tenantId, Guid roleId, IReadOnlyList<string> keys, CancellationToken cancellationToken)
    {
        if (keys.Count == 0) return;
        await using var insert = new NpgsqlCommand("INSERT INTO role_permissions (tenant_id, role_id, permission_key) SELECT @tenant, @id, unnest(@keys::text[])", connection, transaction);
        insert.Parameters.AddWithValue("tenant", tenantId.Value);
        insert.Parameters.AddWithValue("id", roleId);
        insert.Parameters.AddWithValue("keys", keys.ToArray());
        await insert.ExecuteNonQueryAsync(cancellationToken);
    }

    // At least one active user must still be able to manage roles, or nobody could undo a mistake.
    private static async Task<bool> SomeoneCanManageRolesAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, TenantId tenantId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT EXISTS (SELECT 1 FROM users u JOIN user_roles ur ON ur.tenant_id = u.tenant_id AND ur.user_id = u.id
                           JOIN role_permissions rp ON rp.tenant_id = ur.tenant_id AND rp.role_id = ur.role_id
                           WHERE u.tenant_id = @tenant AND u.is_active AND rp.permission_key IN ('user.manageroles', 'system.admin'))
            """, connection, transaction);
        command.Parameters.AddWithValue("tenant", tenantId.Value);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private static async Task AuditAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, TenantId tenantId, UserId actorId, string action, Guid entity, CancellationToken cancellationToken)
    {
        await using var audit = new NpgsqlCommand("INSERT INTO audit_logs (id, tenant_id, actor_id, action, entity_id, occurred_at) VALUES (gen_random_uuid(), @tenant, @actor, @action, @entity::text, now())", connection, transaction);
        audit.Parameters.AddWithValue("tenant", tenantId.Value);
        audit.Parameters.AddWithValue("actor", actorId.Value);
        audit.Parameters.AddWithValue("action", action);
        audit.Parameters.AddWithValue("entity", entity);
        await audit.ExecuteNonQueryAsync(cancellationToken);
    }
}
