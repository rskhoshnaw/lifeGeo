using LifeDrop.Application.Users;
using LifeDrop.Domain.Common;
using LifeDrop.Infrastructure.Auth;
using Npgsql;

namespace LifeDrop.Infrastructure.Users;

// The new account, its first role and the audit row are written in one transaction. The unique index on (tenant, lower(email)) decides a duplicate, so two
// managers adding the same e-mail at once cannot both win. A role of another company is reported like a missing one.
public sealed class PostgresUserCreationStore(NpgsqlDataSource dataSource) : IUserCreationStore
{
    public async Task<(CreateUserResult Result, Guid? UserId)> CreateAsync(TenantId tenantId, UserId actorId, string email, string password, RoleId roleId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var role = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM roles WHERE tenant_id = @tenant AND id = @role)", connection, transaction))
        {
            role.Parameters.AddWithValue("tenant", tenantId.Value);
            role.Parameters.AddWithValue("role", roleId.Value);
            if (!(bool)(await role.ExecuteScalarAsync(cancellationToken) ?? false)) return (CreateUserResult.RoleNotFound, null);
        }

        var userId = Guid.NewGuid();
        try
        {
            await using var insert = new NpgsqlCommand("""
                INSERT INTO users (id, tenant_id, email, password_hash) VALUES (@user, @tenant, @email, @hash);
                INSERT INTO user_roles (tenant_id, user_id, role_id) VALUES (@tenant, @user, @role);
                INSERT INTO audit_logs (id, tenant_id, actor_id, action, entity_id, occurred_at) VALUES (gen_random_uuid(), @tenant, @actor, 'user.created', @user::text, now());
                """, connection, transaction);
            insert.Parameters.AddWithValue("user", userId);
            insert.Parameters.AddWithValue("tenant", tenantId.Value);
            insert.Parameters.AddWithValue("email", email);
            insert.Parameters.AddWithValue("hash", PasswordHash.Create(password));
            insert.Parameters.AddWithValue("role", roleId.Value);
            insert.Parameters.AddWithValue("actor", actorId.Value);
            await insert.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return (CreateUserResult.Created, userId);
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return (CreateUserResult.EmailTaken, null);
        }
    }
}
