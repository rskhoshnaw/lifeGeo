using LifeDrop.Application.Users;
using LifeDrop.Domain.Common;
using LifeDrop.Infrastructure.Auth;
using Npgsql;

namespace LifeDrop.Infrastructure.Users;

// users, refresh_tokens and audit_logs have no RLS, so every statement filters by tenant explicitly. The change, the closing of the sessions and the audit row
// are one transaction; a user of another company is reported as not found.
public sealed class PostgresUserAccountStore(NpgsqlDataSource dataSource) : IUserAccountStore
{
    public Task<ManageUserResult> ChangeEmailAsync(TenantId tenantId, UserId actorId, UserId targetId, string email, CancellationToken cancellationToken) =>
        ChangeAsync(tenantId, actorId, targetId, "user.email_changed", "UPDATE users SET email = @email WHERE tenant_id = @tenant AND id = @target",
            command => command.Parameters.AddWithValue("email", email), endSessions: false, cancellationToken);

    public Task<ManageUserResult> SetPasswordAsync(TenantId tenantId, UserId actorId, UserId targetId, string password, CancellationToken cancellationToken) =>
        ChangeAsync(tenantId, actorId, targetId, "user.password_changed", "UPDATE users SET password_hash = @hash WHERE tenant_id = @tenant AND id = @target",
            command => command.Parameters.AddWithValue("hash", PasswordHash.Create(password)), endSessions: true, cancellationToken);

    public Task<ManageUserResult> SetActiveAsync(TenantId tenantId, UserId actorId, UserId targetId, bool active, CancellationToken cancellationToken) =>
        ChangeAsync(tenantId, actorId, targetId, active ? "user.activated" : "user.deactivated", "UPDATE users SET is_active = @active WHERE tenant_id = @tenant AND id = @target",
            command => command.Parameters.AddWithValue("active", active), endSessions: !active, cancellationToken);

    public async Task<ManageUserResult> ChangeOwnPasswordAsync(TenantId tenantId, UserId userId, Guid keepFamilyId, string currentPassword, string newPassword, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        string? hash;
        await using (var read = new NpgsqlCommand("SELECT password_hash FROM users WHERE tenant_id = @tenant AND id = @user AND is_active FOR UPDATE", connection, transaction))
        {
            read.Parameters.AddWithValue("tenant", tenantId.Value);
            read.Parameters.AddWithValue("user", userId.Value);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return ManageUserResult.NotFound;
            hash = reader.IsDBNull(0) ? null : reader.GetString(0);
        }
        if (!PasswordHash.Verify(currentPassword, hash)) return ManageUserResult.WrongPassword;
        await using (var update = new NpgsqlCommand("UPDATE users SET password_hash = @hash WHERE tenant_id = @tenant AND id = @user", connection, transaction))
        {
            update.Parameters.AddWithValue("hash", PasswordHash.Create(newPassword));
            update.Parameters.AddWithValue("tenant", tenantId.Value);
            update.Parameters.AddWithValue("user", userId.Value);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var revoke = new NpgsqlCommand("UPDATE refresh_tokens SET revoked_at = now() WHERE tenant_id = @tenant AND user_id = @user AND revoked_at IS NULL AND family_id <> @keep", connection, transaction))
        {
            revoke.Parameters.AddWithValue("tenant", tenantId.Value);
            revoke.Parameters.AddWithValue("user", userId.Value);
            revoke.Parameters.AddWithValue("keep", keepFamilyId);
            await revoke.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var audit = new NpgsqlCommand("INSERT INTO audit_logs (id, tenant_id, actor_id, action, entity_id, occurred_at) VALUES (gen_random_uuid(), @tenant, @user, 'user.password_changed_self', @user::text, now())", connection, transaction))
        {
            audit.Parameters.AddWithValue("tenant", tenantId.Value);
            audit.Parameters.AddWithValue("user", userId.Value);
            await audit.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return ManageUserResult.Done;
    }

    public Task<ManageUserResult> SetCityAsync(TenantId tenantId, UserId actorId, UserId targetId, string? city, CancellationToken cancellationToken) =>
        ChangeAsync(tenantId, actorId, targetId, "user.city_changed", "UPDATE users SET city = @city WHERE tenant_id = @tenant AND id = @target",
            command => command.Parameters.Add(new NpgsqlParameter("city", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)city ?? DBNull.Value }), endSessions: false, cancellationToken);

    public Task<ManageUserResult> RemoveRoleAsync(TenantId tenantId, UserId actorId, UserId targetId, Guid roleId, CancellationToken cancellationToken) =>
        ChangeAsync(tenantId, actorId, targetId, "user.role_removed", "DELETE FROM user_roles WHERE tenant_id = @tenant AND user_id = @target AND role_id = @role",
            command => command.Parameters.AddWithValue("role", roleId), endSessions: false, cancellationToken);

    private async Task<ManageUserResult> ChangeAsync(TenantId tenantId, UserId actorId, UserId targetId, string action, string updateSql,
        Action<NpgsqlCommand> addParameters, bool endSessions, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await using (var update = new NpgsqlCommand(updateSql, connection, transaction))
            {
                update.Parameters.AddWithValue("tenant", tenantId.Value);
                update.Parameters.AddWithValue("target", targetId.Value);
                addParameters(update);
                if (await update.ExecuteNonQueryAsync(cancellationToken) != 1) return ManageUserResult.NotFound;
            }
            if (endSessions)
            {
                await using var revoke = new NpgsqlCommand("UPDATE refresh_tokens SET revoked_at = now() WHERE tenant_id = @tenant AND user_id = @target AND revoked_at IS NULL", connection, transaction);
                revoke.Parameters.AddWithValue("tenant", tenantId.Value);
                revoke.Parameters.AddWithValue("target", targetId.Value);
                await revoke.ExecuteNonQueryAsync(cancellationToken);
            }
            await using var audit = new NpgsqlCommand("INSERT INTO audit_logs (id, tenant_id, actor_id, action, entity_id, occurred_at) VALUES (gen_random_uuid(), @tenant, @actor, @action, @target::text, now())", connection, transaction);
            audit.Parameters.AddWithValue("tenant", tenantId.Value);
            audit.Parameters.AddWithValue("actor", actorId.Value);
            audit.Parameters.AddWithValue("action", action);
            audit.Parameters.AddWithValue("target", targetId.Value);
            await audit.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return ManageUserResult.Done;
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return ManageUserResult.Conflict;
        }
    }
}
