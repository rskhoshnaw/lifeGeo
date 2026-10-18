using LifeDrop.Application.Roles;
using LifeDrop.Application.Users;
using LifeDrop.Domain.Common;
using Npgsql;

namespace LifeDrop.Infrastructure.Users;

// users, roles, devices and refresh_tokens have no RLS, so every query filters by tenant explicitly;
// teams/team_members are filtered by RLS through app.current_tenant.
public sealed class PostgresUserDirectoryStore(NpgsqlDataSource dataSource) : IUserDirectoryStore
{
    public async Task<CurrentUserDto?> GetCurrentAsync(TenantId tenantId, UserId userId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT u.email,
                   COALESCE((SELECT array_agg(r.name ORDER BY r.name) FROM user_roles ur JOIN roles r ON r.tenant_id = ur.tenant_id AND r.id = ur.role_id
                             WHERE ur.tenant_id = u.tenant_id AND ur.user_id = u.id), '{}'),
                   COALESCE((SELECT array_agg(DISTINCT rp.permission_key ORDER BY rp.permission_key) FROM user_roles ur
                             JOIN role_permissions rp ON rp.tenant_id = ur.tenant_id AND rp.role_id = ur.role_id
                             WHERE ur.tenant_id = u.tenant_id AND ur.user_id = u.id), '{}')
            FROM users u JOIN tenants t ON t.id = u.tenant_id
            WHERE u.tenant_id = @tenant AND u.id = @user AND u.is_active AND t.is_active
            """, connection);
        command.Parameters.AddWithValue("tenant", tenantId.Value);
        command.Parameters.AddWithValue("user", userId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new CurrentUserDto(userId.Value, reader.IsDBNull(0) ? null : reader.GetString(0),
            reader.GetFieldValue<string[]>(1), reader.GetFieldValue<string[]>(2));
    }

    public async Task<IReadOnlyList<UserSummaryDto>> ListUsersAsync(TenantId tenantId, AccessScope scope, bool includeInactive, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginTenantAsync(connection, tenantId, cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT u.id, u.email,
                   COALESCE((SELECT array_agg(r.name ORDER BY r.name) FROM user_roles ur JOIN roles r ON r.tenant_id = ur.tenant_id AND r.id = ur.role_id
                             WHERE ur.tenant_id = u.tenant_id AND ur.user_id = u.id), '{}'),
                   t.id, t.name, t.region_id,
                   (SELECT count(DISTINCT rt.family_id)::int FROM refresh_tokens rt
                    WHERE rt.tenant_id = u.tenant_id AND rt.user_id = u.id AND rt.revoked_at IS NULL AND rt.expires_at > now()),
                   u.is_active
            FROM users u
            LEFT JOIN team_members m ON m.user_id = u.id
            LEFT JOIN teams t ON t.id = m.team_id
            WHERE u.tenant_id = @tenant AND (u.is_active OR @all)
              AND (@scope = 2 OR u.id = @actor OR (@scope = 1 AND @region::uuid IS NOT NULL AND t.region_id = @region))
            ORDER BY u.email COLLATE "C" NULLS LAST, u.id
            LIMIT 500
            """, connection, transaction);
        command.Parameters.AddWithValue("tenant", tenantId.Value);
        command.Parameters.AddWithValue("all", includeInactive);
        command.Parameters.AddWithValue("scope", (int)scope.Scope);
        command.Parameters.AddWithValue("actor", scope.ActorId.Value);
        command.Parameters.AddWithValue("region", NpgsqlTypes.NpgsqlDbType.Uuid, (object?)scope.RegionId ?? DBNull.Value);
        var items = new List<UserSummaryDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            items.Add(new UserSummaryDto(reader.GetGuid(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetFieldValue<string[]>(2),
                reader.IsDBNull(3) ? null : reader.GetGuid(3), reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetGuid(5), reader.GetInt32(6), reader.GetBoolean(7)));
        return items;
    }

    public async Task<IReadOnlyList<RoleDto>> ListRolesAsync(TenantId tenantId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT id, name, data_scope FROM roles WHERE tenant_id = @tenant ORDER BY name COLLATE \"C\"", connection);
        command.Parameters.AddWithValue("tenant", tenantId.Value);
        var items = new List<RoleDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            items.Add(new RoleDto(reader.GetGuid(0), reader.GetString(1), reader.GetString(2)));
        return items;
    }

    public async Task<IReadOnlyList<DeviceSummaryDto>> ListDevicesAsync(TenantId tenantId, string? status, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT d.id, d.user_id, u.email, d.device_label, d.platform, d.app_version, d.status, d.registered_at
            FROM devices d JOIN users u ON u.tenant_id = d.tenant_id AND u.id = d.user_id
            WHERE d.tenant_id = @tenant AND (@status::text IS NULL OR d.status = @status)
            ORDER BY d.registered_at DESC, d.id
            LIMIT 500
            """, connection);
        command.Parameters.AddWithValue("tenant", tenantId.Value);
        command.Parameters.AddWithValue("status", NpgsqlTypes.NpgsqlDbType.Text, (object?)status ?? DBNull.Value);
        var items = new List<DeviceSummaryDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            items.Add(new DeviceSummaryDto(reader.GetGuid(0), reader.GetGuid(1), reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetFieldValue<DateTimeOffset>(7)));
        return items;
    }

    private static async Task<NpgsqlTransaction> BeginTenantAsync(NpgsqlConnection connection, TenantId tenantId, CancellationToken cancellationToken)
    {
        var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var setTenant = new NpgsqlCommand("SELECT set_config('app.current_tenant', @tenant, true)", connection, transaction);
        setTenant.Parameters.AddWithValue("tenant", tenantId.Value.ToString());
        await setTenant.ExecuteNonQueryAsync(cancellationToken);
        return transaction;
    }
}
