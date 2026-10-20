using LifeDrop.Application.Roles;
using LifeDrop.Domain.Common;
using Npgsql;

namespace LifeDrop.Infrastructure.Roles;

public sealed class PostgresPermissionStore(NpgsqlDataSource dataSource) : IPermissionStore
{
    public async Task<bool> HasPermissionAsync(TenantId tenantId, UserId userId, string permissionKey, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        const string sql = """
            SELECT EXISTS (
                SELECT 1 FROM user_roles ur
                JOIN role_permissions rp ON rp.tenant_id = ur.tenant_id AND rp.role_id = ur.role_id
                WHERE ur.tenant_id = @tenantId AND ur.user_id = @userId AND rp.permission_key = @permissionKey
            )
        """;
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("tenantId", tenantId.Value);
        command.Parameters.AddWithValue("userId", userId.Value);
        command.Parameters.AddWithValue("permissionKey", permissionKey);

        return (bool)(await command.ExecuteScalarAsync(cancellationToken) ?? false);
    }

    public async Task<AccessScope?> GetAccessScopeAsync(TenantId tenantId, UserId userId, string permissionKey, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        const string sql = """
            SELECT max(CASE r.data_scope WHEN 'tenant' THEN 2 WHEN 'region' THEN 1 ELSE 0 END),
                   NOT EXISTS (SELECT 1 FROM user_roles ur2 JOIN role_permissions rp2 ON rp2.tenant_id = ur2.tenant_id AND rp2.role_id = ur2.role_id
                               WHERE ur2.tenant_id = @tenantId AND ur2.user_id = @userId AND rp2.permission_key = 'customers.doctors.read')
            FROM user_roles ur
            JOIN roles r ON r.tenant_id = ur.tenant_id AND r.id = ur.role_id
            JOIN role_permissions rp ON rp.tenant_id = ur.tenant_id AND rp.role_id = ur.role_id
            WHERE ur.tenant_id = @tenantId AND ur.user_id = @userId AND rp.permission_key = @permissionKey
        """;
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("tenantId", tenantId.Value);
        command.Parameters.AddWithValue("userId", userId.Value);
        command.Parameters.AddWithValue("permissionKey", permissionKey);
        int rank;
        bool hideDoctors;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken) || reader.IsDBNull(0)) return null;
            rank = reader.GetInt32(0);
            hideDoctors = reader.GetBoolean(1);
        }

        var scope = (DataScope)rank;
        var (_, regionId) = scope == DataScope.Tenant ? (true, null) : await GetUserRegionAsync(tenantId, userId, cancellationToken);
        return new AccessScope(scope, userId, regionId, hideDoctors);
    }

    public async Task<(bool Exists, Guid? RegionId)> GetUserRegionAsync(TenantId tenantId, UserId userId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var setTenant = new NpgsqlCommand("SELECT set_config('app.current_tenant', @tenant, true)", connection, transaction))
        {
            setTenant.Parameters.AddWithValue("tenant", tenantId.Value.ToString());
            await setTenant.ExecuteNonQueryAsync(cancellationToken);
        }

        // users has no RLS, so the tenant filter is explicit; teams/team_members are tenant-filtered by RLS.
        const string sql = """
            SELECT u.id, t.region_id
            FROM users u
            LEFT JOIN team_members m ON m.user_id = u.id
            LEFT JOIN teams t ON t.id = m.team_id
            WHERE u.tenant_id = @tenantId AND u.id = @userId AND u.is_active
        """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("tenantId", tenantId.Value);
        command.Parameters.AddWithValue("userId", userId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return (false, null);
        return (true, reader.IsDBNull(1) ? null : reader.GetGuid(1));
    }
}
