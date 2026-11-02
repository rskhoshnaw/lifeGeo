using LifeDrop.Application.ClientErrors;
using LifeDrop.Application.Roles;
using LifeDrop.Domain.Common;
using LifeDrop.Infrastructure.ClientErrors;
using Npgsql;
using Xunit;

namespace LifeDrop.Database.Tests;

// Connection-error reports of plan step 5.10 against every migration in an isolated schema: the report lands in the tenant of the company code, is audited when the typed e-mail is a user of
// that company, is invisible to another company, a bad stage is refused by the database, and the 40-day cleanup removes only old rows of the one company.
public class ClientErrorStoreTests
{
    [Fact]
    public async Task Reports_are_kept_per_company_audited_for_known_users_and_purged_after_forty_days()
    {
        var url = Environment.GetEnvironmentVariable("TEST_DATABASE_URL")
            ?? throw new InvalidOperationException("TEST_DATABASE_URL must name an isolated lifedrop_test_* database.");
        var builder = new NpgsqlConnectionStringBuilder(url);
        if (builder.Database is null || !builder.Database.StartsWith("lifedrop_test_", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Refusing to test a database outside lifedrop_test_*.");

        var schema = "clienterr_test_" + Guid.NewGuid().ToString("N");
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

            Guid a = Guid.NewGuid(), b = Guid.NewGuid(), userA = Guid.NewGuid(), manager = Guid.NewGuid();
            await using (var seed = new NpgsqlCommand("""
                INSERT INTO tenants(id,name,code) VALUES (@a,'A','lifedrops'),(@b,'B','other');
                INSERT INTO users(id,tenant_id,email) VALUES (@userA,@a,'Panar@A.test'),(@manager,@a,'manager@a.test');
                """, connection))
            {
                seed.Parameters.AddWithValue("a", a);
                seed.Parameters.AddWithValue("b", b);
                seed.Parameters.AddWithValue("userA", userA);
                seed.Parameters.AddWithValue("manager", manager);
                await seed.ExecuteNonQueryAsync();
            }

            var store = new PostgresClientErrorStore(dataSource);
            var clock = new Clock();
            var tenantA = new TenantId(a);
            var tenantB = new TenantId(b);

            // The company code (any case) names the tenant; an unknown code names none.
            Assert.Equal(tenantA, await store.FindTenantAsync("lifedrops", CancellationToken.None));
            Assert.Null(await store.FindTenantAsync("lifedrop", CancellationToken.None));

            var service = new ClientErrorReports(store, new Permissions(), clock);
            var item = new ClientErrorItem(clock.GetUtcNow().AddMinutes(-3), "company_lookup", "dns", "HttpRequestException", "No such host is known (https://lifedrop.lifegeotracking.com/company-config/lifedrop)",
                "Lifedrop", "panar@a.test", "device-1", "1.0.0", "android");
            var unknownUser = item with { EnteredEmail = "nobody@a.test", Stage = "login", ErrorKind = "timeout" };
            var (result, stored) = await service.ReportAsync(new ReportClientErrorsRequest("LifeDrops", [item, unknownUser]), CancellationToken.None);
            Assert.Equal((ClientErrorResult.Success, 2), (result, stored));

            // The manager of company A sees both reports, newest first, text cleaned; company B sees nothing.
            var (listed, rows) = await service.ListAsync(tenantA, new UserId(manager), new ClientErrorQuery(null, null, null, null, null, null), CancellationToken.None);
            Assert.Equal(ClientErrorResult.Success, listed);
            Assert.Equal(2, rows.Count);
            var lookup = rows.Single(r => r.Stage == "company_lookup");
            Assert.Equal(("dns", "Lifedrop", "panar@a.test", "device-1"), (lookup.ErrorKind, lookup.EnteredCompanyCode, lookup.EnteredEmail, lookup.DeviceId));
            Assert.DoesNotContain("https", lookup.Message);
            Assert.Single((await service.ListAsync(tenantA, new UserId(manager), new ClientErrorQuery("login", null, null, null, null, null), CancellationToken.None)).Items);
            Assert.Empty(await store.ListAsync(tenantB, new ClientErrorQuery(null, null, null, null, null, null), 50, CancellationToken.None));

            // Audit: once, for the report whose e-mail belongs to a user of the company (case-insensitive); none for the unknown e-mail.
            Assert.Equal(1, await CountAsync(connection, a, "SELECT count(*) FROM audit_logs WHERE action = 'client_error.reported' AND actor_id = @user", ("user", userA)));
            Assert.Equal(1, await CountAsync(connection, a, "SELECT count(*) FROM audit_logs WHERE action = 'client_error.reported'"));
            Assert.Equal(lookup.Id.ToString(), (string?)await ScalarAsync(connection, a, "SELECT entity_id FROM audit_logs WHERE action = 'client_error.reported'"));

            // The database refuses an unknown stage and an over-long message (the service never sends either).
            await using (var bad = new NpgsqlCommand("INSERT INTO client_error_reports (tenant_id, occurred_at, stage, error_kind, error_type, message) VALUES (@a, now(), 'bogus', 'dns', 't', 'm')", connection))
            {
                bad.Parameters.AddWithValue("a", a);
                await Assert.ThrowsAsync<PostgresException>(() => InTenantAsync(connection, a, bad));
            }

            // 40-day retention: an old row of A goes, a fresh one and the old row of B stay.
            await InsertAsync(connection, a, DateTimeOffset.UtcNow.AddDays(-41));
            await InsertAsync(connection, b, DateTimeOffset.UtcNow.AddDays(-41));
            var purged = await new ClientErrorRetention(store, new RealClock()).PurgeAsync(tenantA, CancellationToken.None);
            Assert.Equal(1, purged);
            Assert.Equal(2, await CountAsync(connection, a, "SELECT count(*) FROM client_error_reports"));
            Assert.Equal(1, await CountAsync(connection, b, "SELECT count(*) FROM client_error_reports"));
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE", setup);
            await drop.ExecuteNonQueryAsync();
        }
    }

    private static async Task InsertAsync(NpgsqlConnection connection, Guid tenant, DateTimeOffset reportedAt)
    {
        await using var insert = new NpgsqlCommand("INSERT INTO client_error_reports (tenant_id, reported_at, occurred_at, stage, error_kind, error_type, message) VALUES (@t, @at, @at, 'login', 'dns', 't', 'm')", connection);
        insert.Parameters.AddWithValue("t", tenant);
        insert.Parameters.AddWithValue("at", reportedAt);
        await InTenantAsync(connection, tenant, insert);
    }

    // Runs one command inside a transaction that has the company set (row-level security).
    private static async Task<int> InTenantAsync(NpgsqlConnection connection, Guid tenant, NpgsqlCommand command)
    {
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var set = new NpgsqlCommand("SELECT set_config('app.current_tenant', @t, true)", connection, transaction))
        {
            set.Parameters.AddWithValue("t", tenant.ToString());
            await set.ExecuteNonQueryAsync();
        }
        command.Connection = connection;
        command.Transaction = transaction;
        var rows = await command.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
        return rows;
    }

    private static async Task<object?> ScalarAsync(NpgsqlConnection connection, Guid tenant, string sql, params (string Name, object Value)[] parameters)
    {
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var set = new NpgsqlCommand("SELECT set_config('app.current_tenant', @t, true)", connection, transaction))
        {
            set.Parameters.AddWithValue("t", tenant.ToString());
            await set.ExecuteNonQueryAsync();
        }
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        var value1 = await command.ExecuteScalarAsync();
        await transaction.CommitAsync();
        return value1;
    }

    private static async Task<long> CountAsync(NpgsqlConnection connection, Guid tenant, string sql, params (string Name, object Value)[] parameters) =>
        (long)(await ScalarAsync(connection, tenant, sql, parameters))!;

    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow;
    }

    private sealed class RealClock : TimeProvider;

    private sealed class Permissions : IPermissionStore
    {
        public Task<bool> HasPermissionAsync(TenantId tenantId, UserId userId, string permissionKey, CancellationToken cancellationToken) => Task.FromResult(permissionKey == PermissionKeys.ClientErrorsRead);
    }
}
