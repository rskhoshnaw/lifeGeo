using System.Security.Cryptography;
using LifeDrop.Application.Devices;
using LifeDrop.Domain.Common;
using Npgsql;

namespace LifeDrop.Infrastructure.Devices;

public sealed class PostgresDeviceAccessStore(NpgsqlDataSource dataSource) : IDeviceAccessStore
{
    public async Task<RegisteredDevice?> RegisterAsync(TenantId tenantId, UserId userId,
        RegisterDeviceRequest request, CancellationToken cancellationToken)
    {
        var deviceId = Guid.NewGuid();
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var secretHash = SHA256.HashData(Convert.FromBase64String(secret));
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        const string sql = """
            WITH created AS (
              INSERT INTO devices(id,tenant_id,user_id,device_label,platform,install_id,app_version,secret_hash,model,os_version)
              SELECT @id,u.tenant_id,u.id,@label,@platform,@install_id,@app_version,@secret_hash,@model,@os
              FROM users u JOIN tenants t ON t.id=u.tenant_id
              WHERE u.tenant_id=@tenant AND u.id=@user AND u.is_active AND t.is_active
              ON CONFLICT DO NOTHING RETURNING id,tenant_id,user_id
            ), audited AS (
              INSERT INTO audit_logs(id,tenant_id,actor_id,action,entity_id,occurred_at)
              SELECT gen_random_uuid(),tenant_id,user_id,'device.registered',id::text,now()
              FROM created RETURNING id
            ) SELECT count(*) FROM audited
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("id", deviceId);
        command.Parameters.AddWithValue("tenant", tenantId.Value);
        command.Parameters.AddWithValue("user", userId.Value);
        command.Parameters.AddWithValue("label", request.DeviceLabel.Trim());
        command.Parameters.AddWithValue("platform", request.Platform.ToLowerInvariant());
        command.Parameters.AddWithValue("install_id", request.InstallId.Trim());
        command.Parameters.AddWithValue("app_version", request.AppVersion.Trim());
        command.Parameters.AddWithValue("secret_hash", secretHash);
        command.Parameters.Add(new NpgsqlParameter("model", NpgsqlTypes.NpgsqlDbType.Text) { Value = string.IsNullOrWhiteSpace(request.Model) ? DBNull.Value : request.Model.Trim()[..Math.Min(100, request.Model.Trim().Length)] });
        command.Parameters.Add(new NpgsqlParameter("os", NpgsqlTypes.NpgsqlDbType.Text) { Value = string.IsNullOrWhiteSpace(request.OsVersion) ? DBNull.Value : request.OsVersion.Trim()[..Math.Min(40, request.OsVersion.Trim().Length)] });
        var inserted = (long)(await command.ExecuteScalarAsync(cancellationToken) ?? 0L);
        if (inserted != 1) return null;
        await transaction.CommitAsync(cancellationToken);
        return new RegisteredDevice(deviceId, secret);
    }

    public async Task<bool> IsApprovedAsync(TenantId tenantId, UserId userId, Guid deviceId,
        string deviceSecret, CancellationToken cancellationToken)
    {
        if (deviceId == Guid.Empty || string.IsNullOrWhiteSpace(deviceSecret)) return false;
        byte[] supplied;
        try { supplied = Convert.FromBase64String(deviceSecret); }
        catch (FormatException) { return false; }
        if (supplied.Length != 32) return false;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT secret_hash FROM devices
            WHERE tenant_id=@tenant AND user_id=@user AND id=@device AND status='approved'
            """, connection);
        command.Parameters.AddWithValue("tenant", tenantId.Value);
        command.Parameters.AddWithValue("user", userId.Value);
        command.Parameters.AddWithValue("device", deviceId);
        var stored = await command.ExecuteScalarAsync(cancellationToken) as byte[];
        return stored is { Length: 32 } && CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(supplied), stored);
    }

    public async Task RecordDeniedAsync(TenantId tenantId, UserId userId,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            INSERT INTO audit_logs(id,tenant_id,actor_id,action,entity_id,occurred_at)
            VALUES (gen_random_uuid(),@tenant,@user,'auth.device_rejected',@user::text,now())
            """, connection);
        command.Parameters.AddWithValue("tenant", tenantId.Value);
        command.Parameters.AddWithValue("user", userId.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public Task<DeviceChangeResult> ApproveAsync(TenantId tenantId, UserId actorId,
        Guid deviceId, CancellationToken cancellationToken) =>
        ChangeAsync(tenantId, actorId, deviceId, block: false, cancellationToken);

    public Task<DeviceChangeResult> BlockAsync(TenantId tenantId, UserId actorId,
        Guid deviceId, CancellationToken cancellationToken) =>
        ChangeAsync(tenantId, actorId, deviceId, block: true, cancellationToken);

    public async Task<DeviceChangeResult> SetLabelAsync(TenantId tenantId, UserId actorId, Guid deviceId, string? label, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        if (!await MayManageAsync(connection, transaction, tenantId, actorId, cancellationToken)) return DeviceChangeResult.Forbidden;
        await using (var update = new NpgsqlCommand("UPDATE devices SET admin_label=@label WHERE tenant_id=@tenant AND id=@device AND deleted_at IS NULL RETURNING id", connection, transaction))
        {
            update.Parameters.AddWithValue("tenant", tenantId.Value);
            update.Parameters.AddWithValue("device", deviceId);
            update.Parameters.Add(new NpgsqlParameter("label", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)label ?? DBNull.Value });
            if (await update.ExecuteScalarAsync(cancellationToken) is null) return DeviceChangeResult.NotFound;
        }
        await AuditAsync(connection, transaction, tenantId, actorId, "device.label_changed", deviceId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return DeviceChangeResult.Changed;
    }

    public async Task<DeviceChangeResult> DeleteAsync(TenantId tenantId, UserId actorId, Guid deviceId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        if (!await MayManageAsync(connection, transaction, tenantId, actorId, cancellationToken)) return DeviceChangeResult.Forbidden;
        await using (var update = new NpgsqlCommand("""
            UPDATE devices SET status='blocked', blocked_at=COALESCE(blocked_at, now()), blocked_by=COALESCE(blocked_by, @actor), deleted_at=now(),
                install_id = install_id || '#deleted-' || id::text
            WHERE tenant_id=@tenant AND id=@device AND deleted_at IS NULL RETURNING id
            """, connection, transaction))
        {
            update.Parameters.AddWithValue("tenant", tenantId.Value);
            update.Parameters.AddWithValue("actor", actorId.Value);
            update.Parameters.AddWithValue("device", deviceId);
            if (await update.ExecuteScalarAsync(cancellationToken) is null) return DeviceChangeResult.NotFound;
        }
        await using (var revoke = new NpgsqlCommand("UPDATE refresh_tokens SET revoked_at=now() WHERE tenant_id=@tenant AND device_id=@device AND revoked_at IS NULL", connection, transaction))
        {
            revoke.Parameters.AddWithValue("tenant", tenantId.Value);
            revoke.Parameters.AddWithValue("device", deviceId);
            await revoke.ExecuteNonQueryAsync(cancellationToken);
        }
        await AuditAsync(connection, transaction, tenantId, actorId, "device.deleted", deviceId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return DeviceChangeResult.Changed;
    }

    private static async Task<bool> MayManageAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, TenantId tenantId, UserId actorId, CancellationToken cancellationToken)
    {
        await using var permission = new NpgsqlCommand("""
            SELECT EXISTS (
              SELECT 1 FROM users a JOIN tenants t ON t.id=a.tenant_id
              JOIN user_roles ur ON ur.tenant_id=a.tenant_id AND ur.user_id=a.id
              JOIN role_permissions rp ON rp.tenant_id=ur.tenant_id AND rp.role_id=ur.role_id
              WHERE a.tenant_id=@tenant AND a.id=@actor AND a.is_active AND t.is_active
                AND rp.permission_key IN ('device.approve','devices.manage','system.admin'))
            """, connection, transaction);
        permission.Parameters.AddWithValue("tenant", tenantId.Value);
        permission.Parameters.AddWithValue("actor", actorId.Value);
        return (bool)(await permission.ExecuteScalarAsync(cancellationToken) ?? false);
    }

    private static async Task AuditAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, TenantId tenantId, UserId actorId, string action, Guid deviceId, CancellationToken cancellationToken)
    {
        await using var audit = new NpgsqlCommand("INSERT INTO audit_logs(id,tenant_id,actor_id,action,entity_id,occurred_at) VALUES (gen_random_uuid(),@tenant,@actor,@action,@device::text,now())", connection, transaction);
        audit.Parameters.AddWithValue("tenant", tenantId.Value);
        audit.Parameters.AddWithValue("actor", actorId.Value);
        audit.Parameters.AddWithValue("action", action);
        audit.Parameters.AddWithValue("device", deviceId);
        await audit.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<DeviceChangeResult> ChangeAsync(TenantId tenantId, UserId actorId,
        Guid deviceId, bool block, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var permission = new NpgsqlCommand("""
            SELECT EXISTS (
              SELECT 1 FROM users a JOIN tenants t ON t.id=a.tenant_id
              JOIN user_roles ur ON ur.tenant_id=a.tenant_id AND ur.user_id=a.id
              JOIN role_permissions rp ON rp.tenant_id=ur.tenant_id AND rp.role_id=ur.role_id
              WHERE a.tenant_id=@tenant AND a.id=@actor AND a.is_active AND t.is_active
                AND rp.permission_key IN ('device.approve','devices.manage','system.admin'))
            """, connection, transaction))
        {
            permission.Parameters.AddWithValue("tenant", tenantId.Value);
            permission.Parameters.AddWithValue("actor", actorId.Value);
            if (!(bool)(await permission.ExecuteScalarAsync(cancellationToken) ?? false))
                return DeviceChangeResult.Forbidden;
        }
        var sql = block
            ? "UPDATE devices SET status='blocked',blocked_at=now(),blocked_by=@actor WHERE tenant_id=@tenant AND id=@device AND deleted_at IS NULL AND status IN ('pending','approved') RETURNING id"
            : "UPDATE devices SET status='approved',approved_at=now(),approved_by=@actor WHERE tenant_id=@tenant AND id=@device AND deleted_at IS NULL AND status='pending' RETURNING id";
        await using (var update = new NpgsqlCommand(sql, connection, transaction))
        {
            update.Parameters.AddWithValue("tenant", tenantId.Value);
            update.Parameters.AddWithValue("actor", actorId.Value);
            update.Parameters.AddWithValue("device", deviceId);
            if (await update.ExecuteScalarAsync(cancellationToken) is null)
                return DeviceChangeResult.NotFound;
        }
        if (block)
        {
            await using var revoke = new NpgsqlCommand("""
                UPDATE refresh_tokens SET revoked_at=now()
                WHERE tenant_id=@tenant AND device_id=@device AND revoked_at IS NULL
                """, connection, transaction);
            revoke.Parameters.AddWithValue("tenant", tenantId.Value);
            revoke.Parameters.AddWithValue("device", deviceId);
            await revoke.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var audit = new NpgsqlCommand("""
            INSERT INTO audit_logs(id,tenant_id,actor_id,action,entity_id,occurred_at)
            VALUES (gen_random_uuid(),@tenant,@actor,@action,@device::text,now())
            """, connection, transaction))
        {
            audit.Parameters.AddWithValue("tenant", tenantId.Value);
            audit.Parameters.AddWithValue("actor", actorId.Value);
            audit.Parameters.AddWithValue("action", block ? "device.blocked" : "device.approved");
            audit.Parameters.AddWithValue("device", deviceId);
            await audit.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return DeviceChangeResult.Changed;
    }
}
