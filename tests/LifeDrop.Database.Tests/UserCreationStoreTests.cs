using LifeDrop.Application.Users;
using LifeDrop.Domain.Common;
using LifeDrop.Infrastructure.Auth;
using LifeDrop.Infrastructure.Users;
using Npgsql;
using Xunit;

namespace LifeDrop.Database.Tests;

// The store behind POST /users (plan step 4.9) against the first migrations in an isolated schema: user, first role and audit row together, a duplicate e-mail
// of the same company refused (also with other letter case), the same e-mail free in another company, and a role of another company reported as missing.
public class UserCreationStoreTests
{
    [Fact]
    public async Task Creates_the_user_with_role_and_audit_and_refuses_duplicates_and_foreign_roles()
    {
        var url = Environment.GetEnvironmentVariable("TEST_DATABASE_URL")
            ?? throw new InvalidOperationException("TEST_DATABASE_URL must name an isolated lifedrop_test_* database.");
        var builder = new NpgsqlConnectionStringBuilder(url);
        if (builder.Database is null || !builder.Database.StartsWith("lifedrop_test_", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Refusing to test a database outside lifedrop_test_*.");

        var schema = "usercreate_test_" + Guid.NewGuid().ToString("N");
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
            var repRole = Guid.NewGuid();
            var foreignRole = Guid.NewGuid();
            await using (var seed = new NpgsqlCommand("""
                INSERT INTO tenants(id,name,code) VALUES (@tenant,'A','a'),(@other,'B','b');
                INSERT INTO users(id,tenant_id) VALUES (@actor,@tenant),(@otherActor,@other);
                INSERT INTO roles(id,tenant_id,name) VALUES (@repRole,@tenant,'SalesRep'),(@foreignRole,@other,'Foreign');
                """, connection))
            {
                seed.Parameters.AddWithValue("tenant", tenant);
                seed.Parameters.AddWithValue("other", other);
                seed.Parameters.AddWithValue("actor", actor);
                seed.Parameters.AddWithValue("otherActor", otherActor);
                seed.Parameters.AddWithValue("repRole", repRole);
                seed.Parameters.AddWithValue("foreignRole", foreignRole);
                await seed.ExecuteNonQueryAsync();
            }

            var store = new PostgresUserCreationStore(dataSource);
            var (created, userId) = await store.CreateAsync(new TenantId(tenant), new UserId(actor), "rep@example.test", "a-long-password-1", new RoleId(repRole), CancellationToken.None);
            Assert.Equal(CreateUserResult.Created, created);
            Assert.NotNull(userId);

            Assert.Equal(("rep@example.test", true), await Row<(string, bool)>(connection, "SELECT email, left(password_hash, 14) = 'pbkdf2-sha256$' FROM users WHERE id = @id", userId!.Value, r => (r.GetString(0), r.GetBoolean(1))));
            Assert.Equal("SalesRep", await Row(connection, "SELECT r.name FROM user_roles ur JOIN roles r ON r.tenant_id = ur.tenant_id AND r.id = ur.role_id WHERE ur.user_id = @id", userId.Value, r => r.GetString(0)));
            Assert.Equal(("user.created", actor), await Row(connection, "SELECT action, actor_id FROM audit_logs WHERE entity_id = @id::text", userId.Value, r => (r.GetString(0), r.GetGuid(1))));
            Assert.True(PasswordHash.Verify("a-long-password-1", await Row(connection, "SELECT password_hash FROM users WHERE id = @id", userId.Value, r => r.GetString(0))));

            // The same e-mail again (other letter case; the service lower-cases, the unique index is on lower(email) as well) in the same company is refused and writes nothing more.
            var (taken, none) = await store.CreateAsync(new TenantId(tenant), new UserId(actor), "Rep@Example.Test", "another-long-password", new RoleId(repRole), CancellationToken.None);
            Assert.Equal((CreateUserResult.EmailTaken, (Guid?)null), (taken, none));
            Assert.Equal(2L, await Scalar(connection, "SELECT count(*) FROM users", tenant));   // the actor and the new rep

            // A role of another company is reported as missing; nothing is written.
            var (missing, _) = await store.CreateAsync(new TenantId(tenant), new UserId(actor), "x@example.test", "a-long-password-1", new RoleId(foreignRole), CancellationToken.None);
            Assert.Equal(CreateUserResult.RoleNotFound, missing);
            Assert.Equal(2L, await Scalar(connection, "SELECT count(*) FROM users", tenant));
            Assert.Equal(1L, await Scalar(connection, "SELECT count(*) FROM audit_logs", tenant));

            // The same e-mail is free in another company, with that company's own role.
            var (otherCompany, _) = await store.CreateAsync(new TenantId(other), new UserId(otherActor), "rep@example.test", "a-long-password-1", new RoleId(foreignRole), CancellationToken.None);
            Assert.Equal(CreateUserResult.Created, otherCompany);
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE", setup);
            await drop.ExecuteNonQueryAsync();
        }
    }

    private static async Task<T> Row<T>(NpgsqlConnection connection, string sql, Guid id, Func<NpgsqlDataReader, T> read)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return read(reader);
    }

    private static async Task<long> Scalar(NpgsqlConnection connection, string sql, Guid tenant)
    {
        await using var command = new NpgsqlCommand(sql + " WHERE tenant_id = @tenant", connection);
        command.Parameters.AddWithValue("tenant", tenant);
        return (long)(await command.ExecuteScalarAsync() ?? 0L);
    }
}
