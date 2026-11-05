using LifeDrop.Domain.Common;
using LifeDrop.Infrastructure.Alerts;
using Npgsql;
using Xunit;

namespace LifeDrop.Database.Tests;

// Plan step 5.18: a shift with no contact at all (no position, no heartbeat) for 4 hours is closed by the scan, at the time of the last contact; a shift that still reports, or was silent less than 4 hours, stays open.
public class AutoCloseShiftTests
{
    [Fact]
    public async Task Scan_closes_only_shifts_silent_for_four_hours()
    {
        var url = Environment.GetEnvironmentVariable("TEST_DATABASE_URL")
            ?? throw new InvalidOperationException("TEST_DATABASE_URL must name an isolated lifedrop_test_* database.");
        var builder = new NpgsqlConnectionStringBuilder(url);
        if (builder.Database is null || !builder.Database.StartsWith("lifedrop_test_", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Refusing to test a database outside lifedrop_test_*.");

        var schema = "autoclose_test_" + Guid.NewGuid().ToString("N");
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

            Guid t = Guid.NewGuid(), u1 = Guid.NewGuid(), u2 = Guid.NewGuid(), u3 = Guid.NewGuid();
            Guid d1 = Guid.NewGuid(), d2 = Guid.NewGuid(), d3 = Guid.NewGuid();
            Guid sSilent = Guid.NewGuid(), sBeating = Guid.NewGuid(), sRecent = Guid.NewGuid();
            await using (var seed = new NpgsqlCommand("""
                INSERT INTO tenants(id,name,code) VALUES (@t,'T','t1');
                INSERT INTO users(id,tenant_id,email) VALUES (@u1,@t,'a@t.test'), (@u2,@t,'b@t.test'), (@u3,@t,'c@t.test');
                INSERT INTO devices (id, tenant_id, user_id, device_label, platform, install_id, app_version, secret_hash, status) VALUES
                  (@d1, @t, @u1, 'P1', 'android', 'i1', '1.0', '\x00', 'approved'),
                  (@d2, @t, @u2, 'P2', 'android', 'i2', '1.0', '\x00', 'approved'),
                  (@d3, @t, @u3, 'P3', 'android', 'i3', '1.0', '\x00', 'approved');
                SELECT set_config('app.current_tenant', @t::text, false);
                INSERT INTO shifts (id, tenant_id, user_id, device_id, started_at) VALUES
                  (@s1, @t, @u1, @d1, now() - interval '6 hours'),
                  (@s2, @t, @u2, @d2, now() - interval '6 hours'),
                  (@s3, @t, @u3, @d3, now() - interval '3 hours');
                INSERT INTO shift_health (shift_id, tenant_id, user_id, seen_at) VALUES (@s2, @t, @u2, now() - interval '1 minute');
                """, connection))
            {
                seed.Parameters.AddWithValue("t", t);
                seed.Parameters.AddWithValue("u1", u1); seed.Parameters.AddWithValue("u2", u2); seed.Parameters.AddWithValue("u3", u3);
                seed.Parameters.AddWithValue("d1", d1); seed.Parameters.AddWithValue("d2", d2); seed.Parameters.AddWithValue("d3", d3);
                seed.Parameters.AddWithValue("s1", sSilent); seed.Parameters.AddWithValue("s2", sBeating); seed.Parameters.AddWithValue("s3", sRecent);
                await seed.ExecuteNonQueryAsync();
            }

            var now = DateTimeOffset.UtcNow;
            await new PostgresAlertStore(dataSource).ScanAsync(new TenantId(t), now, DateOnly.FromDateTime(now.UtcDateTime), TimeOnly.FromDateTime(now.UtcDateTime), CancellationToken.None);

            Assert.Equal(1L, await ScalarAsync(connection, $"SELECT count(*) FROM shifts WHERE id = '{sSilent}' AND ended_at = started_at"));
            Assert.Equal(2L, await ScalarAsync(connection, $"SELECT count(*) FROM shifts WHERE id IN ('{sBeating}', '{sRecent}') AND ended_at IS NULL"));

            // Plan step 5.19: the user of the shift that is quiet for 3 hours (but not closed yet) gets one push message; the one that reports, and the one just closed, get none. A second scan adds none.
            Assert.Equal(1L, await ScalarAsync(connection, $"SELECT count(*) FROM notifications WHERE kind = 'tracking.stopped' AND user_id = '{u3}' AND entity_id = '{sRecent}'"));
            Assert.Equal(1L, await ScalarAsync(connection, "SELECT count(*) FROM notifications WHERE kind = 'tracking.stopped'"));
            await new PostgresAlertStore(dataSource).ScanAsync(new TenantId(t), now, DateOnly.FromDateTime(now.UtcDateTime), TimeOnly.FromDateTime(now.UtcDateTime), CancellationToken.None);
            Assert.Equal(1L, await ScalarAsync(connection, "SELECT count(*) FROM notifications WHERE kind = 'tracking.stopped'"));
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE", setup);
            await drop.ExecuteNonQueryAsync();
        }
    }

    private static async Task<long> ScalarAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }
}
