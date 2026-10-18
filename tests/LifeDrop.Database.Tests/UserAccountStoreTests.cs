using LifeDrop.Application.Users;
using LifeDrop.Domain.Common;
using LifeDrop.Infrastructure.Auth;
using LifeDrop.Infrastructure.Users;
using Npgsql;
using Xunit;

namespace LifeDrop.Database.Tests;

// The store behind the account actions (plan step 4.10) against the first migrations in an isolated schema: e-mail, password and lock/unlock change only a user of
// the actor's own company, write their audit row, end the sessions when the password changes or the account is locked, and refuse a duplicate e-mail.
public class UserAccountStoreTests
{
    [Fact]
    public async Task Creates_the_user_with_role_and_audit_and_refuses_duplicates_and_foreign_roles()
    {
        var url = Environment.GetEnvironmentVariable("TEST_DATABASE_URL")
            ?? throw new InvalidOperationException("TEST_DATABASE_URL must name an isolated lifedrop_test_* database.");
        var builder = new NpgsqlConnectionStringBuilder(url);
        if (builder.Database is null || !builder.Database.StartsWith("lifedrop_test_", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Refusing to test a database outside lifedrop_test_*.");

        var schema = "useraccount_test_" + Guid.NewGuid().ToString("N");
        await using var setup = new NpgsqlConnection(url);
        await setup.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE SCHEMA {schema}", setup))
            await create.ExecuteNonQueryAsync();

        try
        {
            builder.SearchPath = $"{schema},public";
            await using var dataSource = NpgsqlDataSource.Create(builder.ConnectionString);
            await using var connection = await dataSource.OpenConnectionAsync();
            foreach (var file in new[] { "0001_role_assignment.sql", "0002_login.sql", "0003_sessions_devices.sql" })
                await using (var migrate = new NpgsqlCommand(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, file)), connection))
                    await migrate.ExecuteNonQueryAsync();

            var tenant = Guid.NewGuid();
            var other = Guid.NewGuid();
            var actor = Guid.NewGuid();
            var otherActor = Guid.NewGuid();
            var rep = Guid.NewGuid();
            await using (var seed = new NpgsqlCommand("""
                INSERT INTO tenants(id,name,code) VALUES (@tenant,'A','a'),(@other,'B','b');
                INSERT INTO users(id,tenant_id,email) VALUES (@actor,@tenant,'boss@example.test'),(@rep,@tenant,'rep@example.test'),(@otherActor,@other,'rep@example.test');
                INSERT INTO refresh_tokens(id,family_id,tenant_id,user_id,token_hash,expires_at) VALUES
                    (gen_random_uuid(),gen_random_uuid(),@tenant,@rep,'\x01',now()+interval '1 day'),
                    (gen_random_uuid(),gen_random_uuid(),@other,@otherActor,'\x02',now()+interval '1 day');
                """, connection))
            {
                seed.Parameters.AddWithValue("tenant", tenant);
                seed.Parameters.AddWithValue("other", other);
                seed.Parameters.AddWithValue("actor", actor);
                seed.Parameters.AddWithValue("otherActor", otherActor);
                seed.Parameters.AddWithValue("rep", rep);
                await seed.ExecuteNonQueryAsync();
            }

            var store = new PostgresUserAccountStore(dataSource);
            var t = new TenantId(tenant);
            var a = new UserId(actor);

            // E-mail: changed with an audit row; the other company's user of the same address is untouched; a taken address is a conflict.
            Assert.Equal(ManageUserResult.Done, await store.ChangeEmailAsync(t, a, new UserId(rep), "new@example.test", CancellationToken.None));
            Assert.Equal("new@example.test", await Text(connection, "SELECT email FROM users WHERE id = @id", rep));
            Assert.Equal("rep@example.test", await Text(connection, "SELECT email FROM users WHERE id = @id", otherActor));
            Assert.Equal(ManageUserResult.Conflict, await store.ChangeEmailAsync(t, a, new UserId(rep), "boss@example.test", CancellationToken.None));
            Assert.Equal(ManageUserResult.NotFound, await store.ChangeEmailAsync(t, a, new UserId(otherActor), "x@example.test", CancellationToken.None));
            Assert.Equal(ManageUserResult.NotFound, await store.ChangeEmailAsync(t, a, new UserId(Guid.NewGuid()), "y@example.test", CancellationToken.None));

            // Password: hashed, and the user's open sessions end; another company's sessions stay.
            Assert.Equal(ManageUserResult.Done, await store.SetPasswordAsync(t, a, new UserId(rep), "a-long-password-1", CancellationToken.None));
            Assert.True(PasswordHash.Verify("a-long-password-1", await Text(connection, "SELECT password_hash FROM users WHERE id = @id", rep)));
            Assert.Equal(0L, await Count(connection, "SELECT count(*) FROM refresh_tokens WHERE user_id = @id AND revoked_at IS NULL", rep));
            Assert.Equal(1L, await Count(connection, "SELECT count(*) FROM refresh_tokens WHERE user_id = @id AND revoked_at IS NULL", otherActor));

            // Lock and unlock.
            Assert.Equal(ManageUserResult.Done, await store.SetActiveAsync(t, a, new UserId(rep), false, CancellationToken.None));
            Assert.Equal(0L, await Count(connection, "SELECT count(*) FROM users WHERE id = @id AND is_active", rep));
            Assert.Equal(ManageUserResult.Done, await store.SetActiveAsync(t, a, new UserId(rep), true, CancellationToken.None));
            Assert.Equal(1L, await Count(connection, "SELECT count(*) FROM users WHERE id = @id AND is_active", rep));
            Assert.Equal(ManageUserResult.NotFound, await store.SetActiveAsync(t, a, new UserId(otherActor), false, CancellationToken.None));
            Assert.Equal(1L, await Count(connection, "SELECT count(*) FROM users WHERE id = @id AND is_active", otherActor));

            // One audit row per success and none for refusals.
            Assert.Equal(["user.activated", "user.deactivated", "user.email_changed", "user.password_changed"], await Actions(connection, rep));
            Assert.Equal(0L, await Count(connection, "SELECT count(*) FROM audit_logs WHERE entity_id = @id::text", otherActor));
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE", setup);
            await drop.ExecuteNonQueryAsync();
        }
    }

    private static async Task<string> Text(NpgsqlConnection connection, string sql, Guid id) => (string)(await Scalar(connection, sql, id))!;

    private static async Task<long> Count(NpgsqlConnection connection, string sql, Guid id) => (long)(await Scalar(connection, sql, id))!;

    private static async Task<object?> Scalar(NpgsqlConnection connection, string sql, Guid id)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", id);
        return await command.ExecuteScalarAsync();
    }

    private static async Task<string[]> Actions(NpgsqlConnection connection, Guid id)
    {
        await using var command = new NpgsqlCommand("SELECT action FROM audit_logs WHERE entity_id = @id::text ORDER BY action COLLATE \"C\"", connection);
        command.Parameters.AddWithValue("id", id);
        var list = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) list.Add(reader.GetString(0));
        return [.. list];
    }
}
