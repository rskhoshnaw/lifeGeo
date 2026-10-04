using System.Security.Cryptography;
using System.Text;
using LifeDrop.Application.Auth;
using LifeDrop.Domain.Common;
using Npgsql;
using NpgsqlTypes;

namespace LifeDrop.Infrastructure.Auth;

public sealed class PostgresSessionStore(NpgsqlDataSource dataSource) : ISessionStore
{
    private static readonly TimeSpan RefreshLifetime = TimeSpan.FromDays(30);

    public async Task<SessionTokens> IssueAsync(LoginIdentity identity, Guid? deviceId,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        if (!await IsEligibleAsync(connection, transaction, identity.TenantId.Value,
            identity.UserId.Value, deviceId, cancellationToken))
            throw new InvalidOperationException("User or device is not eligible for a session.");
        var familyId = Guid.NewGuid();
        var tokenId = Guid.NewGuid();
        var raw = NewToken();
        var expires = DateTime.UtcNow.Add(RefreshLifetime);
        await InsertAsync(connection, transaction, tokenId, familyId, identity.TenantId.Value,
            identity.UserId.Value, deviceId, raw, expires, cancellationToken);
        await AuditAsync(connection, transaction, identity.TenantId.Value, identity.UserId.Value,
            "auth.session_issued", familyId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new SessionTokens(new SessionIdentity(identity.TenantId, identity.UserId,
            familyId, deviceId), raw, expires);
    }

    public async Task<SessionTokens?> RotateAsync(RefreshRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.RefreshToken) || request.RefreshToken.Length > 1024)
            return null;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        Guid tokenId, familyId, tenantId, userId;
        Guid? deviceId;
        DateTime expires;
        DateTime? revoked;
        Guid? replacedBy;
        await using (var lookup = new NpgsqlCommand("""
            SELECT id,family_id,tenant_id,user_id,device_id,expires_at,revoked_at,replaced_by_id
            FROM refresh_tokens WHERE token_hash=@hash FOR UPDATE
            """, connection, transaction))
        {
            lookup.Parameters.AddWithValue("hash", TokenHash(request.RefreshToken));
            await using var reader = await lookup.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return null;
            tokenId = reader.GetGuid(0);
            familyId = reader.GetGuid(1);
            tenantId = reader.GetGuid(2);
            userId = reader.GetGuid(3);
            deviceId = reader.IsDBNull(4) ? null : reader.GetGuid(4);
            expires = reader.GetDateTime(5);
            revoked = reader.IsDBNull(6) ? null : reader.GetDateTime(6);
            replacedBy = reader.IsDBNull(7) ? null : reader.GetGuid(7);
        }
        if (revoked.HasValue)
        {
            if (replacedBy.HasValue)
            {
                await RevokeFamilyRowsAsync(connection, transaction, familyId, cancellationToken);
                await AuditAsync(connection, transaction, tenantId, userId,
                    "auth.refresh_replay", familyId, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
            return null;
        }
        if (expires <= DateTime.UtcNow ||
            !await IsEligibleAsync(connection, transaction, tenantId, userId, deviceId,
                cancellationToken) ||
            !await MatchesDeviceAsync(connection, transaction, tenantId, userId, deviceId,
                request, cancellationToken))
            return null;

        var nextId = Guid.NewGuid();
        var nextRaw = NewToken();
        var nextExpires = DateTime.UtcNow.Add(RefreshLifetime);
        await InsertAsync(connection, transaction, nextId, familyId, tenantId, userId,
            deviceId, nextRaw, nextExpires, cancellationToken);
        await using (var rotate = new NpgsqlCommand("""
            UPDATE refresh_tokens SET revoked_at=now(),replaced_by_id=@next
            WHERE id=@old AND revoked_at IS NULL
            """, connection, transaction))
        {
            rotate.Parameters.AddWithValue("next", nextId);
            rotate.Parameters.AddWithValue("old", tokenId);
            if (await rotate.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new InvalidOperationException("Concurrent refresh detected.");
        }
        await AuditAsync(connection, transaction, tenantId, userId,
            "auth.refresh_rotated", familyId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new SessionTokens(new SessionIdentity(new TenantId(tenantId),
            new UserId(userId), familyId, deviceId), nextRaw, nextExpires);
    }

    public async Task<bool> IsAccessActiveAsync(SessionIdentity identity,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        if (!await IsEligibleAsync(connection, transaction, identity.TenantId.Value,
            identity.UserId.Value, identity.DeviceId, cancellationToken)) return false;
        await using var command = new NpgsqlCommand("""
            SELECT EXISTS (SELECT 1 FROM refresh_tokens
              WHERE family_id=@family AND tenant_id=@tenant AND user_id=@user
                AND revoked_at IS NULL AND expires_at>now())
            """, connection, transaction);
        command.Parameters.AddWithValue("family", identity.FamilyId);
        command.Parameters.AddWithValue("tenant", identity.TenantId.Value);
        command.Parameters.AddWithValue("user", identity.UserId.Value);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken) ?? false);
    }

    public async Task RevokeFamilyAsync(SessionIdentity identity,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            UPDATE refresh_tokens SET revoked_at=now()
            WHERE family_id=@family AND tenant_id=@tenant AND user_id=@user
              AND revoked_at IS NULL
            """, connection, transaction);
        command.Parameters.AddWithValue("family", identity.FamilyId);
        command.Parameters.AddWithValue("tenant", identity.TenantId.Value);
        command.Parameters.AddWithValue("user", identity.UserId.Value);
        if (await command.ExecuteNonQueryAsync(cancellationToken) > 0)
            await AuditAsync(connection, transaction, identity.TenantId.Value,
                identity.UserId.Value, "auth.session_revoked", identity.FamilyId,
                cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task<bool> IsEligibleAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, Guid tenant, Guid user, Guid? device,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT EXISTS (
              SELECT 1 FROM users u JOIN tenants t ON t.id=u.tenant_id
              WHERE u.tenant_id=@tenant AND u.id=@user AND u.is_active AND t.is_active
                AND (
                  (@device IS NULL AND NOT EXISTS (
                    SELECT 1 FROM user_roles ur JOIN roles r
                      ON r.tenant_id=ur.tenant_id AND r.id=ur.role_id
                    WHERE ur.tenant_id=u.tenant_id AND ur.user_id=u.id
                      AND r.requires_device_binding))
                  OR (@device IS NOT NULL AND EXISTS (
                    SELECT 1 FROM devices d WHERE d.tenant_id=u.tenant_id
                      AND d.user_id=u.id AND d.id=@device AND d.status='approved'))
                ))
            """, connection, transaction);
        command.Parameters.AddWithValue("tenant", tenant);
        command.Parameters.AddWithValue("user", user);
        command.Parameters.Add(new NpgsqlParameter("device", NpgsqlDbType.Uuid)
            { Value = (object?)device ?? DBNull.Value });
        return (bool)(await command.ExecuteScalarAsync(cancellationToken) ?? false);
    }

    private static async Task<bool> MatchesDeviceAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, Guid tenant, Guid user, Guid? boundDevice,
        RefreshRequest request, CancellationToken cancellationToken)
    {
        if (!boundDevice.HasValue)
            return !request.DeviceId.HasValue && string.IsNullOrEmpty(request.DeviceSecret);
        if (request.DeviceId != boundDevice || string.IsNullOrWhiteSpace(request.DeviceSecret))
            return false;
        byte[] supplied;
        try { supplied = Convert.FromBase64String(request.DeviceSecret); }
        catch (FormatException) { return false; }
        if (supplied.Length != 32) return false;
        await using var command = new NpgsqlCommand("""
            SELECT secret_hash FROM devices
            WHERE tenant_id=@tenant AND user_id=@user AND id=@device AND status='approved'
            """, connection, transaction);
        command.Parameters.AddWithValue("tenant", tenant);
        command.Parameters.AddWithValue("user", user);
        command.Parameters.AddWithValue("device", boundDevice.Value);
        var stored = await command.ExecuteScalarAsync(cancellationToken) as byte[];
        return stored is { Length: 32 } && CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(supplied), stored);
    }

    private static async Task InsertAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, Guid id, Guid family, Guid tenant, Guid user,
        Guid? device, string raw, DateTime expires, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO refresh_tokens(id,family_id,tenant_id,user_id,device_id,token_hash,expires_at)
            VALUES (@id,@family,@tenant,@user,@device,@hash,@expires)
            """, connection, transaction);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("family", family);
        command.Parameters.AddWithValue("tenant", tenant);
        command.Parameters.AddWithValue("user", user);
        command.Parameters.Add(new NpgsqlParameter("device", NpgsqlDbType.Uuid)
            { Value = (object?)device ?? DBNull.Value });
        command.Parameters.AddWithValue("hash", TokenHash(raw));
        command.Parameters.AddWithValue("expires", expires);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task RevokeFamilyRowsAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, Guid family, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            UPDATE refresh_tokens SET revoked_at=now()
            WHERE family_id=@family AND revoked_at IS NULL
            """, connection, transaction);
        command.Parameters.AddWithValue("family", family);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task AuditAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, Guid tenant, Guid user, string action, Guid family,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO audit_logs(id,tenant_id,actor_id,action,entity_id,occurred_at)
            VALUES (gen_random_uuid(),@tenant,@user,@action,@family::text,now())
            """, connection, transaction);
        command.Parameters.AddWithValue("tenant", tenant);
        command.Parameters.AddWithValue("user", user);
        command.Parameters.AddWithValue("action", action);
        command.Parameters.AddWithValue("family", family);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
    private static byte[] TokenHash(string raw) => SHA256.HashData(Encoding.UTF8.GetBytes(raw));
}
