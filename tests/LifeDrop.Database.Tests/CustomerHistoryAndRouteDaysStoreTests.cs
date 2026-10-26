using LifeDrop.Application.Customers;
using LifeDrop.Application.Tracking;
using LifeDrop.Domain.Common;
using LifeDrop.Infrastructure.Customers;
using LifeDrop.Infrastructure.Tracking;
using Npgsql;
using Xunit;

namespace LifeDrop.Database.Tests;

// The two queries of plan step 5.4 against every migration in an isolated schema: the visit history of a customer (result, accepted check-in and check-out, the report) and the days of a
// month on which a user had a route (Iraq days; a shift at 00:30 Iraq time on the 1st belongs to that day, one at 00:30 on the next month's 1st does not).
public class CustomerHistoryAndRouteDaysStoreTests
{
    [Fact]
    public async Task Lists_the_visits_of_one_customer_and_the_route_days_of_one_month()
    {
        var url = Environment.GetEnvironmentVariable("TEST_DATABASE_URL")
            ?? throw new InvalidOperationException("TEST_DATABASE_URL must name an isolated lifedrop_test_* database.");
        var builder = new NpgsqlConnectionStringBuilder(url);
        if (builder.Database is null || !builder.Database.StartsWith("lifedrop_test_", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Refusing to test a database outside lifedrop_test_*.");

        var schema = "custhistory_test_" + Guid.NewGuid().ToString("N");
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

            Guid tenant = Guid.NewGuid(), user = Guid.NewGuid(), device = Guid.NewGuid(), customer = Guid.NewGuid(), otherCustomer = Guid.NewGuid();
            Guid done = Guid.NewGuid(), failed = Guid.NewGuid(), elsewhere = Guid.NewGuid();
            await using (var seed = new NpgsqlCommand("""
                INSERT INTO tenants(id,name,code) VALUES (@tenant,'A','a');
                INSERT INTO users(id,tenant_id,email) VALUES (@user,@tenant,'rep@a.test');
                """, connection))
            {
                seed.Parameters.AddWithValue("tenant", tenant);
                seed.Parameters.AddWithValue("user", user);
                await seed.ExecuteNonQueryAsync();
            }
            await InTenant(connection, tenant, $"""
                INSERT INTO devices(id,tenant_id,user_id,device_label,platform,install_id,app_version,secret_hash,status)
                VALUES ('{device}','{tenant}','{user}','phone','android','install-1','1.0','\x01'::bytea,'approved');
                INSERT INTO customers(id,tenant_id,type,code,name) VALUES ('{customer}','{tenant}','Pharmacy','H-1','Shifa'),('{otherCustomer}','{tenant}','Pharmacy','H-2','Other');
                INSERT INTO visits(id,tenant_id,user_id,customer_id,status,schedule_date,exception_reason) VALUES
                    ('{done}','{tenant}','{user}','{customer}','Completed','2026-10-05',NULL),
                    ('{failed}','{tenant}','{user}','{customer}','Failed','2026-10-06','Closed for holiday'),
                    ('{elsewhere}','{tenant}','{user}','{otherCustomer}','Completed','2026-10-07',NULL);
                INSERT INTO visit_reports(visit_id,tenant_id,outcome,note,submitted_by) VALUES ('{done}','{tenant}','order_intent','Wants more of the cough syrup','{user}');
                INSERT INTO visit_location_events(id,tenant_id,visit_id,user_id,kind,accepted,rejection_reason,geom,accuracy_m,captured_at) VALUES
                    (gen_random_uuid(),'{tenant}','{done}','{user}','check_in',false,'outside_geofence',ST_SetSRID(ST_MakePoint(44.1,36.2),4326)::geography,8,'2026-10-05 05:50:00+00'),
                    (gen_random_uuid(),'{tenant}','{done}','{user}','check_in',true,NULL,ST_SetSRID(ST_MakePoint(44.0,36.19),4326)::geography,8,'2026-10-05 06:00:00+00'),
                    (gen_random_uuid(),'{tenant}','{done}','{user}','check_out',true,NULL,ST_SetSRID(ST_MakePoint(44.0,36.19),4326)::geography,8,'2026-10-05 06:20:00+00');
                INSERT INTO shifts(id,tenant_id,user_id,device_id,started_at,ended_at) VALUES
                    ('{Guid.NewGuid()}','{tenant}','{user}','{device}','2026-09-30 21:30:00+00','2026-09-30 22:30:00+00'),
                    ('{Guid.NewGuid()}','{tenant}','{user}','{device}','2026-10-10 04:00:00+00','2026-10-10 06:00:00+00'),
                    ('{Guid.NewGuid()}','{tenant}','{user}','{device}','2026-10-31 21:30:00+00','2026-10-31 22:30:00+00');
                """);
            var shiftA = Guid.NewGuid();
            var shiftB = Guid.NewGuid();
            await InTenant(connection, tenant, $"""
                INSERT INTO shifts(id,tenant_id,user_id,device_id,started_at,ended_at) VALUES
                    ('{shiftA}','{tenant}','{user}','{device}','2026-10-04 03:00:00+00','2026-10-04 04:00:00+00'),
                    ('{shiftB}','{tenant}','{user}','{device}','2026-10-04 08:00:00+00','2026-10-04 09:00:00+00');
                INSERT INTO tracking_points(tenant_id,shift_id,user_id,device_id,client_point_id,geom,accuracy_m,captured_at) VALUES
                    ('{tenant}','{shiftA}','{user}','{device}',gen_random_uuid(),ST_SetSRID(ST_MakePoint(44.0,36.19),4326)::geography,8,'2026-10-04 03:10:00+00'),
                    ('{tenant}','{shiftA}','{user}','{device}',gen_random_uuid(),ST_SetSRID(ST_MakePoint(44.0,36.19),4326)::geography,8,'2026-10-04 03:20:00+00');
                """);

            var history = new PostgresCustomerHistoryStore(dataSource);
            var visits = await history.ListVisitsAsync(new TenantId(tenant), customer, 30, CancellationToken.None);
            Assert.Equal(new[] { failed, done }, visits.Select(v => v.Id).ToArray());            // newest first, the other customer's visit is not here
            var first = visits[1];
            Assert.Equal(("Completed", new DateOnly(2026, 10, 5), "rep@a.test", "order_intent", "Wants more of the cough syrup"), (first.Status, first.Date, first.UserEmail, first.Outcome, first.Note));
            Assert.Equal(new DateTimeOffset(2026, 10, 5, 6, 0, 0, TimeSpan.Zero), first.CheckInAt);   // the refused attempt does not count
            Assert.Equal(new DateTimeOffset(2026, 10, 5, 6, 20, 0, TimeSpan.Zero), first.CheckOutAt);
            Assert.Equal(("Failed", "Closed for holiday", null), (visits[0].Status, visits[0].Reason, visits[0].Outcome));
            Assert.Single(await history.ListVisitsAsync(new TenantId(tenant), customer, 1, CancellationToken.None));
            Assert.Empty(await history.ListOrderLinesAsync(new TenantId(tenant), Array.Empty<Guid>(), CancellationToken.None));

            var routes = new PostgresRouteHistoryStore(dataSource);
            var from = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.FromHours(3));
            var days = await routes.ListDaysAsync(new TenantId(tenant), new UserId(user), from, from.AddMonths(1), CancellationToken.None);
            Assert.Equal(new[] { (new DateOnly(2026, 10, 1), 1, 0), (new DateOnly(2026, 10, 4), 2, 2), (new DateOnly(2026, 10, 10), 1, 0) }, days.Select(d => (d.Date, d.Shifts, d.Points)).ToArray());
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE", setup);
            await drop.ExecuteNonQueryAsync();
        }
    }

    // The tables have row level security: every statement runs in a transaction that names the company, as the API does.
    private static async Task<string> InTenant(NpgsqlConnection connection, Guid tenant, string sql)
    {
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var set = new NpgsqlCommand("SELECT set_config('app.current_tenant', @tenant, true)", connection, transaction))
        {
            set.Parameters.AddWithValue("tenant", tenant.ToString());
            await set.ExecuteNonQueryAsync();
        }
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        var value = Convert.ToString(await command.ExecuteScalarAsync()) ?? "";
        await transaction.CommitAsync();
        return value;
    }
}
