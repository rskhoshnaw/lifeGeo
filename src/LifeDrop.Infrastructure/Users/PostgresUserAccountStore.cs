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
