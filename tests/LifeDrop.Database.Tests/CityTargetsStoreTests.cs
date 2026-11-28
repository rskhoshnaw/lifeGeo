using LifeDrop.Application.Targets;
using LifeDrop.Domain.Common;
using LifeDrop.Infrastructure.Targets;
using Npgsql;
using Xunit;

namespace LifeDrop.Database.Tests;

// The sales targets of a city (plan step 5.76) against every migration in an isolated schema: a target is set, changed and cleared with its audit row, only a city (a place of kind city) of the
// company can have one, and the report counts what was delivered to the customers of the city (a customer in a neighbourhood belongs to the city above) next to it, whoever placed the orders.
public class CityTargetsStoreTests
{
    [Fact]
    public async Task Sets_clears_and_reports_city_targets_against_the_delivered_quantity_of_the_customers_of_each_city()
    {
        var url = Environment.GetEnvironmentVariable("TEST_DATABASE_URL")
            ?? throw new InvalidOperationException("TEST_DATABASE_URL must name an isolated lifedrop_test_* database.");
        var builder = new NpgsqlConnectionStringBuilder(url);
        if (builder.Database is null || !builder.Database.StartsWith("lifedrop_test_", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Refusing to test a database outside lifedrop_test_*.");

        var schema = "city_targets_test_" + Guid.NewGuid().ToString("N");
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

            Guid tenant = Guid.NewGuid(), u1 = Guid.NewGuid(), u2 = Guid.NewGuid(), driver = Guid.NewGuid(), warehouse = Guid.NewGuid();
            Guid erbil = Guid.NewGuid(), hood = Guid.NewGuid(), basra = Guid.NewGuid(), cErbil = Guid.NewGuid(), cHood = Guid.NewGuid(), cBasra = Guid.NewGuid(), cNone = Guid.NewGuid();
            Guid pa = Guid.NewGuid(), pb = Guid.NewGuid(), o1 = Guid.NewGuid(), o2 = Guid.NewGuid(), o3 = Guid.NewGuid(), o4 = Guid.NewGuid(), o5 = Guid.NewGuid(), o6 = Guid.NewGuid();
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
                INSERT INTO places(id,tenant_id,kind,name) VALUES ('{erbil}','{tenant}','city','Erbil'),('{basra}','{tenant}','city','Basra');
                INSERT INTO places(id,tenant_id,parent_id,kind,name) VALUES ('{hood}','{tenant}','{erbil}','neighbourhood','Hood');
                INSERT INTO warehouses(id,tenant_id,code,name) VALUES ('{warehouse}','{tenant}','W1','Main');
                INSERT INTO products(id,tenant_id,code,name,unit,currency,base_price) VALUES ('{pa}','{tenant}','A','Cough syrup','box','IQD',1000),('{pb}','{tenant}','B','Vitamin','box','IQD',500);
                INSERT INTO customers(id,tenant_id,type,code,name,place_id) VALUES
                    ('{cErbil}','{tenant}','Pharmacy','C1','In Erbil','{erbil}'), ('{cHood}','{tenant}','Pharmacy','C2','In the hood','{hood}'),
                    ('{cBasra}','{tenant}','Pharmacy','C3','In Basra','{basra}'), ('{cNone}','{tenant}','Pharmacy','C4','No place',NULL);
                INSERT INTO orders(id,tenant_id,number,customer_id,warehouse_id,created_by,status,currency,total,client_ref,driver_id,receiver_name) VALUES
                    ('{o1}','{tenant}',1,'{cErbil}','{warehouse}','{u1}','delivered','IQD',100,gen_random_uuid(),'{driver}','Ali'),
                    ('{o2}','{tenant}',2,'{cHood}','{warehouse}','{u2}','delivered','IQD',100,gen_random_uuid(),'{driver}','Ali'),
                    ('{o3}','{tenant}',3,'{cBasra}','{warehouse}','{u1}','delivered','IQD',100,gen_random_uuid(),'{driver}','Ali'),
                    ('{o4}','{tenant}',4,'{cNone}','{warehouse}','{u1}','delivered','IQD',100,gen_random_uuid(),'{driver}','Ali'),
                    ('{o5}','{tenant}',5,'{cErbil}','{warehouse}','{u1}','delivered','IQD',100,gen_random_uuid(),'{driver}','Ali'),
                    ('{o6}','{tenant}',6,'{cErbil}','{warehouse}','{u1}','placed','IQD',100,gen_random_uuid(),NULL,NULL);
                INSERT INTO order_items(tenant_id,order_id,line_no,product_id,product_code,product_name,unit,quantity,list_price,unit_price,line_total,delivered_quantity,delivery_reason) VALUES
                    ('{tenant}','{o1}',1,'{pa}','A','Cough syrup','box',10,1000,1000,10000,8,'short of stock'),
                    ('{tenant}','{o1}',2,'{pb}','B','Vitamin','box',5,500,500,2500,5,NULL),
                    ('{tenant}','{o2}',1,'{pa}','A','Cough syrup','box',20,1000,1000,20000,20,NULL),
                    ('{tenant}','{o3}',1,'{pa}','A','Cough syrup','box',7,1000,1000,7000,7,NULL),
                    ('{tenant}','{o4}',1,'{pa}','A','Cough syrup','box',50,1000,1000,50000,50,NULL),
                    ('{tenant}','{o5}',1,'{pa}','A','Cough syrup','box',99,1000,1000,99000,99,NULL),
                    ('{tenant}','{o6}',1,'{pa}','A','Cough syrup','box',77,1000,1000,77000,NULL,NULL);
                INSERT INTO order_status_history(tenant_id,order_id,status,actor_id,occurred_at) VALUES
                    ('{tenant}','{o1}','delivered','{driver}','2026-10-12 09:00:00+00'),
                    ('{tenant}','{o2}','delivered','{driver}','2026-09-30 21:30:00+00'),
                    ('{tenant}','{o3}','delivered','{driver}','2026-10-20 09:00:00+00'),
                    ('{tenant}','{o4}','delivered','{driver}','2026-10-20 09:00:00+00'),
                    ('{tenant}','{o5}','delivered','{driver}','2026-10-31 21:30:00+00');
                """);

            var store = new PostgresTargetStore(dataSource);
            var t = new TenantId(tenant);
            var actor = new UserId(u1);
            var october = new DateOnly(2026, 10, 1);
            var from = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.FromHours(3));
            Task<IReadOnlyList<CityTargetRowDto>> Report() => store.ListCitiesAsync(t, october, from, from.AddMonths(1), CancellationToken.None);
            static string Show(IEnumerable<CityTargetRowDto> rows) => string.Join(" | ", rows.Select(r => $"{r.CityName} {r.ProductCode} {r.Target?.ToString("0.###") ?? "-"} {r.Actual:0.###}"));

            Assert.Equal(TargetResult.Success, await store.SetCityAsync(t, actor, erbil, pa, october, 100, CancellationToken.None));
            Assert.Equal(TargetResult.Success, await store.SetCityAsync(t, actor, erbil, pb, october, 50, CancellationToken.None));
            Assert.Equal(TargetResult.Success, await store.SetCityAsync(t, actor, erbil, pa, october, 120, CancellationToken.None));       // changed
            Assert.Equal(TargetResult.Success, await store.SetCityAsync(t, actor, basra, pa, october, 10, CancellationToken.None));
            Assert.Equal(TargetResult.Success, await store.SetCityAsync(t, actor, erbil, pb, october, null, CancellationToken.None));       // cleared
            Assert.Equal(TargetResult.Success, await store.SetCityAsync(t, actor, erbil, pb, october, null, CancellationToken.None));       // nothing left to clear: still fine
            Assert.Equal(TargetResult.NotFound, await store.SetCityAsync(t, actor, hood, pa, october, 5, CancellationToken.None));           // a neighbourhood is not a city
            Assert.Equal(TargetResult.NotFound, await store.SetCityAsync(t, actor, Guid.NewGuid(), pa, october, 5, CancellationToken.None));
            Assert.Equal(TargetResult.NotFound, await store.SetCityAsync(t, actor, erbil, Guid.NewGuid(), october, 5, CancellationToken.None));

            // Delivered in October (Iraq) to the customers of each city, whoever placed the orders: Erbil = the 8 of the first order + the 20 of the customer in the hood (delivery step at 00:30 on the 1st
            // in Iraq, placed by another visitor) and its 5 vitamins; the 99 delivered on 1 November (Iraq), the order that was only placed and the customer without a city are not counted.
            Assert.Equal("Basra A 10 7 | Erbil A 120 28 | Erbil B - 5", Show(await Report()));

            Assert.Equal("city_target.cleared,city_target.set,city_target.set,city_target.set,city_target.set",
                await InTenant(connection, tenant, "SELECT string_agg(action, ',' ORDER BY action COLLATE \"C\") FROM audit_logs WHERE action LIKE 'city_target.%'"));
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
