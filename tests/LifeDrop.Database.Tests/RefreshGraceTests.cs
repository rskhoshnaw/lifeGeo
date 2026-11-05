using LifeDrop.Application.Auth;
using LifeDrop.Domain.Common;
using LifeDrop.Infrastructure.Auth;
using Npgsql;
using Xunit;

namespace LifeDrop.Database.Tests;

// Plan step 5.21: a refresh token sent again soon after it was used (the answer was lost, two tabs, a reload during the call) gets a new token of the same session instead of ending it;
// a replay after the grace window still ends the whole session; with grace 0 every replay ends it.
public class RefreshGraceTests
{
    [Fact]
    public async Task Replay_inside_the_grace_window_is_a_retry_and_later_replay_ends_the_session()
    {
        var url = Environment.GetEnvironmentVariable("TEST_DATABASE_URL")
            ?? throw new InvalidOperationException("TEST_DATABASE_URL must name an isolated lifedrop_test_* database.");
        var builder = new NpgsqlConnectionStringBuilder(url);
        if (builder.Database is null || !builder.Database.StartsWith("lifedrop_test_", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Refusing to test a database outside lifedrop_test_*.");

        var schema = "refreshgrace_test_" + Guid.NewGuid().ToString("N");
        await using var setup = new NpgsqlConnection(url);
        await setup.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE SCHEMA {schema}", setup))
            await create.ExecuteNonQueryAsync();

        try
        {
            builder.SearchPath = $"{schema},public";
            await using var dataSource = NpgsqlDataSource.Create(builder.ConnectionString);
            await using var connection = await dataSource.OpenConnectionAsync();
            var all = string.Concat(Directory.GetFiles(AppContext.BaseDirectory, "00??_*.sql")
                .Select(Path.GetFileName).OrderBy(f => f, StringComparer.Ordinal)
                .Select(f => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, f!))));
            await using (var migrate = new NpgsqlCommand($"SET search_path TO {schema}, public; {all}", connection))
                await migrate.ExecuteNonQueryAsync();

            Guid t = Guid.NewGuid(), u = Guid.NewGuid();
            await using (var seed = new NpgsqlCommand("""
                INSERT INTO tenants(id,name,code) VALUES (@t,'T','t1');
                INSERT INTO users(id,tenant_id,email) VALUES (@u,@t,'tv@t.test');
                """, connection))
            {
                seed.Parameters.AddWithValue("t", t);
                seed.Parameters.AddWithValue("u", u);
                await seed.ExecuteNonQueryAsync();
            }

            var identity = new LoginIdentity(new TenantId(t), new UserId(u), false);
            var store = new PostgresSessionStore(dataSource, TimeSpan.FromMinutes(2));

            // Inside the window: the first token is used, its answer "got lost", and it is sent again.
            var issued = await store.IssueAsync(identity, null, CancellationToken.None);
            var first = await store.RotateAsync(new RefreshRequest(issued.RefreshToken), CancellationToken.None);
            Assert.NotNull(first);
            var retry = await store.RotateAsync(new RefreshRequest(issued.RefreshToken), CancellationToken.None);
            Assert.NotNull(retry);
            Assert.Equal(first!.Identity.FamilyId, retry!.Identity.FamilyId);
            Assert.NotEqual(first.RefreshToken, retry.RefreshToken);
            // Both new tokens work, once each.
            Assert.NotNull(await store.RotateAsync(new RefreshRequest(retry.RefreshToken), CancellationToken.None));
            Assert.NotNull(await store.RotateAsync(new RefreshRequest(first.RefreshToken), CancellationToken.None));

            // After the window: a replay is theft and the whole session ends.
            var other = await store.IssueAsync(identity, null, CancellationToken.None);
            var moved = await store.RotateAsync(new RefreshRequest(other.RefreshToken), CancellationToken.None);
            Assert.NotNull(moved);
            await using (var age = new NpgsqlCommand("UPDATE refresh_tokens SET revoked_at = now() - interval '10 minutes' WHERE replaced_by_id IS NOT NULL", connection))
                await age.ExecuteNonQueryAsync();
            Assert.Null(await store.RotateAsync(new RefreshRequest(other.RefreshToken), CancellationToken.None));
            Assert.Null(await store.RotateAsync(new RefreshRequest(moved!.RefreshToken), CancellationToken.None));   // the family is revoked

            // Grace 0: every replay ends the session.
            var strict = new PostgresSessionStore(dataSource, TimeSpan.Zero);
            var s1 = await strict.IssueAsync(identity, null, CancellationToken.None);
            Assert.NotNull(await strict.RotateAsync(new RefreshRequest(s1.RefreshToken), CancellationToken.None));
            Assert.Null(await strict.RotateAsync(new RefreshRequest(s1.RefreshToken), CancellationToken.None));
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE", setup);
            await drop.ExecuteNonQueryAsync();
        }
    }
}
