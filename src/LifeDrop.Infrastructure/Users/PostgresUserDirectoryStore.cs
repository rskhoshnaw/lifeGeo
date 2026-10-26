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
                             WHERE ur.tenant_id = u.tenant_id AND ur.user_id = u.id
                               -- the "doctors only" restriction counts only when every role that lets the user write customers carries it (a user with SalesRep and ScientificRep is not restricted)
                               AND NOT (rp.permission_key = 'customers.doctors.only' AND EXISTS (
                                   SELECT 1 FROM user_roles ur2 JOIN role_permissions rw ON rw.tenant_id = ur2.tenant_id AND rw.role_id = ur2.role_id AND rw.permission_key = 'customers.write'
                                   WHERE ur2.tenant_id = u.tenant_id AND ur2.user_id = u.id
                                     AND NOT EXISTS (SELECT 1 FROM role_permissions ro WHERE ro.tenant_id = ur2.tenant_id AND ro.role_id = ur2.role_id AND ro.permission_key = 'customers.doctors.only')))), '{}')
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
                   u.is_active, u.city,
                   (SELECT d.platform FROM devices d WHERE d.tenant_id = u.tenant_id AND d.user_id = u.id ORDER BY d.registered_at DESC LIMIT 1)
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
                reader.IsDBNull(5) ? null : reader.GetGuid(5), reader.GetInt32(6), reader.GetBoolean(7),
                reader.IsDBNull(8) ? null : reader.GetString(8), reader.IsDBNull(9) ? null : reader.GetString(9)));
        return items;
    }

    public async Task<IReadOnlyList<RoleDto>> ListRolesAsync(TenantId tenantId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            SELECT r.id, r.name, r.data_scope,
                   COALESCE((SELECT array_agg(rp.permission_key ORDER BY rp.permission_key COLLATE "C") FROM role_permissions rp WHERE rp.tenant_id = r.tenant_id AND rp.role_id = r.id), '{}'),
                   (SELECT count(*)::int FROM user_roles ur WHERE ur.tenant_id = r.tenant_id AND ur.role_id = r.id),
                   r.requires_device_binding
            FROM roles r WHERE r.tenant_id = @tenant ORDER BY r.name COLLATE "C"
            """, connection);
        command.Parameters.AddWithValue("tenant", tenantId.Value);
        var items = new List<RoleDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            items.Add(new RoleDto(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetFieldValue<string[]>(3), reader.GetInt32(4),
                RoleDefaults.Names.Contains(reader.GetString(1)), reader.GetBoolean(5)));
        return items;
    }

    public async Task<IReadOnlyList<DeviceSummaryDto>> ListDevicesAsync(TenantId tenantId, string? status, string? platform, string? search, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT d.id, d.user_id, u.email, d.device_label, d.platform, d.app_version, d.status, d.registered_at, d.admin_label, d.model, d.os_version
            FROM devices d JOIN users u ON u.tenant_id = d.tenant_id AND u.id = d.user_id
            WHERE d.tenant_id = @tenant AND d.deleted_at IS NULL AND (@status::text IS NULL OR d.status = @status) AND (@platform::text IS NULL OR d.platform = @platform)
              AND (@q::text IS NULL OR d.device_label ILIKE @q ESCAPE '\' OR d.admin_label ILIKE @q ESCAPE '\' OR d.model ILIKE @q ESCAPE '\' OR u.email ILIKE @q ESCAPE '\')
            ORDER BY d.registered_at DESC, d.id
            LIMIT 500
            """, connection);
        command.Parameters.AddWithValue("tenant", tenantId.Value);
        command.Parameters.AddWithValue("status", NpgsqlTypes.NpgsqlDbType.Text, (object?)status ?? DBNull.Value);
        command.Parameters.AddWithValue("platform", NpgsqlTypes.NpgsqlDbType.Text, (object?)platform ?? DBNull.Value);
        command.Parameters.AddWithValue("q", NpgsqlTypes.NpgsqlDbType.Text, search is null ? DBNull.Value : "%" + search.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%");
        var items = new List<DeviceSummaryDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            items.Add(new DeviceSummaryDto(reader.GetGuid(0), reader.GetGuid(1), reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetFieldValue<DateTimeOffset>(7),
                reader.IsDBNull(8) ? null : reader.GetString(8), reader.IsDBNull(9) ? null : reader.GetString(9), reader.IsDBNull(10) ? null : reader.GetString(10)));
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
