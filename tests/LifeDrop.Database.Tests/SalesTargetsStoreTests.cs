using LifeDrop.Application.Roles;
using LifeDrop.Application.Targets;
using LifeDrop.Domain.Common;
using LifeDrop.Infrastructure.Targets;
using Npgsql;
using Xunit;

namespace LifeDrop.Database.Tests;

// The sales targets of plan step 5.5 against every migration in an isolated schema: a target is set, changed and cleared with its audit row, and the report counts what was really
// delivered (the quantity delivered, in the Iraq month of the "delivered" step, for the user who placed the order) next to it, inside the reader's scope.
public class SalesTargetsStoreTests
{
    [Fact]
    public async Task Sets_clears_and_reports_targets_against_the_delivered_quantity()
    {
        var url = Environment.GetEnvironmentVariable("TEST_DATABASE_URL")
            ?? throw new InvalidOperationException("TEST_DATABASE_URL must name an isolated lifedrop_test_* database.");
        var builder = new NpgsqlConnectionStringBuilder(url);
        if (builder.Database is null || !builder.Database.StartsWith("lifedrop_test_", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Refusing to test a database outside lifedrop_test_*.");

        var schema = "targets_test_" + Guid.NewGuid().ToString("N");
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

            Guid tenant = Guid.NewGuid(), u1 = Guid.NewGuid(), u2 = Guid.NewGuid(), driver = Guid.NewGuid(), warehouse = Guid.NewGuid(), customer = Guid.NewGuid();
            Guid pa = Guid.NewGuid(), pb = Guid.NewGuid(), o1 = Guid.NewGuid(), o2 = Guid.NewGuid(), o3 = Guid.NewGuid(), o4 = Guid.NewGuid();
            await using (var seed = new NpgsqlCommand("""
                INSERT INTO tenants(id,name,code) VALUES (@tenant,'A','a');
                INSERT INTO users(id,tenant_id,email) VALUES (@u1,@tenant,'rep1@a.test'),(@u2,@tenant,'rep2@a.test'),(@driver,@tenant,'driver@a.test');
                """, connection))
            {
                seed.Parameters.AddWithValue("tenant", tenant);
                seed.Parameters.AddWithValue("u1", u1);
                seed.Parameters.AddWithValue("u2", u2);
                seed.Parameters.AddWithValue("driver", driver);
                await seed.ExecuteNonQueryAsync();
            }
            await InTenant(connection, tenant, $"""
                INSERT INTO warehouses(id,tenant_id,code,name) VALUES ('{warehouse}','{tenant}','W1','Main');
                INSERT INTO products(id,tenant_id,code,name,unit,currency,base_price) VALUES ('{pa}','{tenant}','A','Cough syrup','box','IQD',1000),('{pb}','{tenant}','B','Vitamin','box','IQD',500);
                INSERT INTO customers(id,tenant_id,type,code,name) VALUES ('{customer}','{tenant}','Pharmacy','T-1','Shifa');
                INSERT INTO orders(id,tenant_id,number,customer_id,warehouse_id,created_by,status,currency,total,client_ref,driver_id,receiver_name) VALUES
                    ('{o1}','{tenant}',1,'{customer}','{warehouse}','{u1}','delivered','IQD',100,gen_random_uuid(),'{driver}','Ali'),
                    ('{o2}','{tenant}',2,'{customer}','{warehouse}','{u1}','delivered','IQD',100,gen_random_uuid(),'{driver}','Ali'),
                    ('{o3}','{tenant}',3,'{customer}','{warehouse}','{u2}','delivered','IQD',100,gen_random_uuid(),'{driver}','Ali'),
                    ('{o4}','{tenant}',4,'{customer}','{warehouse}','{u2}','placed','IQD',100,gen_random_uuid(),NULL,NULL);
                INSERT INTO order_items(tenant_id,order_id,line_no,product_id,product_code,product_name,unit,quantity,list_price,unit_price,line_total,delivered_quantity,delivery_reason) VALUES
                    ('{tenant}','{o1}',1,'{pa}','A','Cough syrup','box',10,1000,1000,10000,8,'short of stock'),
                    ('{tenant}','{o1}',2,'{pb}','B','Vitamin','box',5,500,500,2500,5,NULL),
                    ('{tenant}','{o2}',1,'{pa}','A','Cough syrup','box',20,1000,1000,20000,20,NULL),
                    ('{tenant}','{o3}',1,'{pa}','A','Cough syrup','box',3,1000,1000,3000,3,NULL),
                    ('{tenant}','{o4}',1,'{pa}','A','Cough syrup','box',99,1000,1000,99000,NULL,NULL);
                INSERT INTO order_status_history(tenant_id,order_id,status,actor_id,occurred_at) VALUES
                    ('{tenant}','{o1}','delivered','{driver}','2026-10-12 09:00:00+00'),
                    ('{tenant}','{o2}','delivered','{driver}','2026-09-30 21:30:00+00'),
                    ('{tenant}','{o3}','delivered','{driver}','2026-10-31 21:30:00+00');
                """);

            var store = new PostgresTargetStore(dataSource);
            var t = new TenantId(tenant);
            var actor = new UserId(u2);
            var october = new DateOnly(2026, 10, 1);
            var from = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.FromHours(3));
            var whole = AccessScope.WholeTenant(actor);
            Task<IReadOnlyList<TargetRowDto>> Report(AccessScope scope) => store.ListAsync(t, scope, october, from, from.AddMonths(1), CancellationToken.None);
            static string Show(IEnumerable<TargetRowDto> rows) => string.Join(" | ", rows.Select(r => $"{r.UserEmail} {r.ProductCode} {r.Target?.ToString("0.###") ?? "-"} {r.Actual:0.###}"));

            Assert.Equal(TargetResult.Success, await store.SetAsync(t, actor, u1, pa, october, 100, CancellationToken.None));
            Assert.Equal(TargetResult.Success, await store.SetAsync(t, actor, u1, pb, october, 50, CancellationToken.None));
            Assert.Equal(TargetResult.Success, await store.SetAsync(t, actor, u1, pa, october, 120, CancellationToken.None));     // changed
            Assert.Equal(TargetResult.Success, await store.SetAsync(t, actor, u2, pa, october, 10, CancellationToken.None));
            Assert.Equal(TargetResult.Success, await store.SetAsync(t, actor, u1, pb, october, null, CancellationToken.None));     // cleared
            Assert.Equal(TargetResult.Success, await store.SetAsync(t, actor, u1, pb, october, null, CancellationToken.None));     // nothing left to clear: still fine
            Assert.Equal(TargetResult.NotFound, await store.SetAsync(t, actor, u1, Guid.NewGuid(), october, 5, CancellationToken.None));
            Assert.Equal(TargetResult.NotFound, await store.SetAsync(t, actor, Guid.NewGuid(), pa, october, 5, CancellationToken.None));

            // What was delivered in October (Iraq): the 8 of the first order, the 5 vitamins, and the 20 whose delivery step was 00:30 on the 1st in Iraq; the November delivery
            // and the order that was only placed are not counted.
            Assert.Equal("rep1@a.test A 120 28 | rep1@a.test B - 5 | rep2@a.test A 10 0", Show(await Report(whole)));

            // A reader limited to their own records sees their own rows only.
            Assert.Equal("rep2@a.test A 10 0", Show(await Report(new AccessScope(DataScope.Own, actor, null))));

            Assert.Equal("target.cleared,target.set,target.set,target.set,target.set",
                await InTenant(connection, tenant, "SELECT string_agg(action, ',' ORDER BY action COLLATE \"C\") FROM audit_logs WHERE action LIKE 'target.%'"));
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
