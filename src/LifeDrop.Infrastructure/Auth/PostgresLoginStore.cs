using System.Security.Cryptography;
using System.Text;
using LifeDrop.Application.Auth;
using LifeDrop.Domain.Common;
using Npgsql;

namespace LifeDrop.Infrastructure.Auth;

public sealed class PostgresLoginStore(NpgsqlDataSource dataSource) : ILoginStore
{
    private static readonly string DummyHash = PasswordHash.Create("unused-dummy-password");

    public async Task<LoginIdentity?> AuthenticateAsync(LoginRequest request,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        const string lookupSql = """
            SELECT t.id, u.id, u.password_hash,
              EXISTS (
                SELECT 1 FROM user_roles ur JOIN roles r
                  ON r.tenant_id=ur.tenant_id AND r.id=ur.role_id
                WHERE ur.tenant_id=t.id AND ur.user_id=u.id AND r.requires_device_binding
              )
            FROM tenants t
            LEFT JOIN users u ON u.tenant_id=t.id AND lower(u.email)=@email AND u.is_active
            WHERE t.code=@tenant_code AND t.is_active
            """;
        Guid? tenantId = null;
        Guid? userId = null;
        string? storedHash = null;
        var requiresDeviceBinding = false;
        await using (var lookup = new NpgsqlCommand(lookupSql, connection, transaction))
        {
            lookup.Parameters.AddWithValue("tenant_code", request.TenantCode);
            lookup.Parameters.AddWithValue("email", request.Email);
            await using var reader = await lookup.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                tenantId = reader.GetGuid(0);
                if (!reader.IsDBNull(1)) userId = reader.GetGuid(1);
                if (!reader.IsDBNull(2)) storedHash = reader.GetString(2);
                requiresDeviceBinding = reader.GetBoolean(3);
            }
        }

        var valid = PasswordHash.Verify(request.Password, storedHash ?? DummyHash);
        var authenticated = tenantId.HasValue && userId.HasValue && storedHash is not null && valid;
        var emailDigest = SHA256.HashData(Encoding.UTF8.GetBytes(request.Email));
        const string auditSql = """
            INSERT INTO auth_login_audit
                (id, tenant_code, email_sha256, tenant_id, user_id, succeeded)
            VALUES (gen_random_uuid(), @tenant_code, @email_digest, @tenant_id, @user_id, @succeeded)
            """;
        await using (var audit = new NpgsqlCommand(auditSql, connection, transaction))
        {
            audit.Parameters.AddWithValue("tenant_code", request.TenantCode);
            audit.Parameters.AddWithValue("email_digest", emailDigest);
            audit.Parameters.AddWithValue("tenant_id", (object?)tenantId ?? DBNull.Value);
            audit.Parameters.AddWithValue("user_id", authenticated ? userId!.Value : DBNull.Value);
            audit.Parameters.AddWithValue("succeeded", authenticated);
            await audit.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return authenticated
            ? new LoginIdentity(new TenantId(tenantId!.Value), new UserId(userId!.Value),
                requiresDeviceBinding)
            : null;
    }
}
