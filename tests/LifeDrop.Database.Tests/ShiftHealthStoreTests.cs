using LifeDrop.Application.Roles;
using LifeDrop.Application.Tracking;
using LifeDrop.Domain.Common;
using LifeDrop.Infrastructure.Tracking;
using Npgsql;
using Xunit;

namespace LifeDrop.Database.Tests;

// The state of the phone during a shift (plan step 5.17) against every migration in an isolated schema: a report without any position is accepted when it carries the health, the
// newest report replaces the older one, values outside the allowed ones are dropped, a shift that ended takes no more reports, and the live map keeps a user live while the phone reports.
public class ShiftHealthStoreTests
{
    [Fact]
    public async Task Reports_replace_the_state_keep_the_user_live_and_stop_with_the_shift()
    {
        var url = Environment.GetEnvironmentVariable("TEST_DATABASE_URL")
            ?? throw new InvalidOperationException("TEST_DATABASE_URL must name an isolated lifedrop_test_* database.");
        var builder = new NpgsqlConnectionStringBuilder(url);
        if (builder.Database is null || !builder.Database.StartsWith("lifedrop_test_", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Refusing to test a database outside lifedrop_test_*.");

        var schema = "shifthealth_test_" + Guid.NewGuid().ToString("N");
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

            Guid t = Guid.NewGuid(), u = Guid.NewGuid(), device = Guid.NewGuid(), shift = Guid.NewGuid();
            await using (var seed = new NpgsqlCommand("""
                INSERT INTO tenants(id,name,code) VALUES (@t,'T','t1');
                INSERT INTO users(id,tenant_id,email) VALUES (@u,@t,'rep@t.test');
                INSERT INTO devices (id, tenant_id, user_id, device_label, platform, install_id, app_version, secret_hash, status)
                  VALUES (@d, @t, @u, 'Phone', 'android', 'install-1', '1.0', '\x00', 'approved');
                SELECT set_config('app.current_tenant', @t::text, false);
                INSERT INTO shifts (id, tenant_id, user_id, device_id, started_at) VALUES (@s, @t, @u, @d, now() - interval '1 hour');
                """, connection))
            {
                seed.Parameters.AddWithValue("t", t);
                seed.Parameters.AddWithValue("u", u);
                seed.Parameters.AddWithValue("d", device);
                seed.Parameters.AddWithValue("s", shift);
                await seed.ExecuteNonQueryAsync();
            }

            var tenant = new TenantId(t);
            var user = new UserId(u);
            var tracking = new ShiftTracking(new PostgresShiftStore(dataSource), TimeProvider.System);
            var live = new PostgresLiveMapStore(dataSource);

            // Nothing to report: refused. A report without positions but with the health: accepted, nothing stored as a point.
            Assert.Equal(ShiftResult.InvalidData, (await tracking.UploadAsync(tenant, user, device, shift, new UploadPointsRequest([]), CancellationToken.None)).Result);
            var first = new ShiftHealthRequest("1.0.5", "android", "14", "Pixel 8", "always", true, true, true, true, null, 80, false);
            var (result, response) = await tracking.UploadAsync(tenant, user, device, shift, new UploadPointsRequest([], first), CancellationToken.None);
            Assert.Equal(ShiftResult.Success, result);
            Assert.Equal((0, 0, 0), (response!.Accepted, response.Duplicates, response.Rejected));
            Assert.Equal(0L, await ScalarAsync(connection, "SELECT count(*) FROM tracking_points"));

            // The live map: live with no position at all, and the state of the phone is there.
            var scope = AccessScope.WholeTenant(user);
            var row = Assert.Single(await live.ListOpenShiftsAsync(tenant, scope, CancellationToken.None));
            Assert.NotNull(row.SeenAt);
            Assert.Null(row.LastPointAt);
            Assert.Equal(("always", "1.0.5", "android", 80), (row.Health!.LocationPermission, row.Health.AppVersion, row.Health.Platform, row.Health.BatteryPercent));
            Assert.True(LiveMap.ToEntry(row, true, DateTimeOffset.UtcNow).IsLive);
            Assert.False(LiveMap.ToEntry(row, true, DateTimeOffset.UtcNow.AddMinutes(30)).IsLive);

            // The newest report replaces the older; wrong values are dropped (platform, refresh) or become unknown (permission), a battery of 150 % is dropped.
            var second = new ShiftHealthRequest("1.0.6", "windows", "14", null, "root", false, true, false, false, "sometimes", 150, true);
            Assert.Equal(ShiftResult.Success, (await tracking.UploadAsync(tenant, user, device, shift, new UploadPointsRequest(null, second), CancellationToken.None)).Result);
            Assert.Equal(1L, await ScalarAsync(connection, "SELECT count(*) FROM shift_health"));
            Assert.Equal(1L, await ScalarAsync(connection, "SELECT count(*) FROM shift_health WHERE app_version = '1.0.6' AND platform IS NULL AND location_permission = 'unknown' AND background_refresh IS NULL AND battery_percent IS NULL AND notifications = false"));

            // A report whose seen_at is old no longer keeps the user live.
            await using (var age = new NpgsqlCommand("UPDATE shift_health SET seen_at = now() - interval '30 minutes'", connection))
                await age.ExecuteNonQueryAsync();
            var stale = Assert.Single(await live.ListOpenShiftsAsync(tenant, scope, CancellationToken.None));
            Assert.False(LiveMap.ToEntry(stale, true, DateTimeOffset.UtcNow).IsLive);

            // The shift ends: a later report changes nothing.
            await using (var end = new NpgsqlCommand("UPDATE shifts SET ended_at = now()", connection))
                await end.ExecuteNonQueryAsync();
            Assert.Equal(ShiftResult.Success, (await tracking.UploadAsync(tenant, user, device, shift, new UploadPointsRequest([], first), CancellationToken.None)).Result);
            Assert.Equal(1L, await ScalarAsync(connection, "SELECT count(*) FROM shift_health WHERE app_version = '1.0.6'"));
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
