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
}
