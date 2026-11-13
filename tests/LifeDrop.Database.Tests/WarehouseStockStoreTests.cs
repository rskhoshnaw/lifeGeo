using LifeDrop.Application.Orders;
using LifeDrop.Domain.Common;
using LifeDrop.Infrastructure.Orders;
using Npgsql;
using Xunit;

namespace LifeDrop.Database.Tests;

// One warehouse per city with its own stock (owner request 2026-11-13) against every migration in an isolated schema: the managers' list has every active product of every
// active company warehouse (counted or not), a warehouse worker's list and changes are limited to the warehouses they work in, wholesaler warehouses and inactive products have no
// stock, and a warehouse keeps its city, address and phone.
public class WarehouseStockStoreTests
{
    [Fact]
    public async Task Stock_is_per_warehouse_and_a_worker_only_reaches_their_own()
    {
        var url = Environment.GetEnvironmentVariable("TEST_DATABASE_URL")
            ?? throw new InvalidOperationException("TEST_DATABASE_URL must name an isolated lifedrop_test_* database.");
        var builder = new NpgsqlConnectionStringBuilder(url);
        if (builder.Database is null || !builder.Database.StartsWith("lifedrop_test_", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Refusing to test a database outside lifedrop_test_*.");

        var schema = "stock_test_" + Guid.NewGuid().ToString("N");
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

            Guid tenant = Guid.NewGuid(), officer = Guid.NewGuid(), manager = Guid.NewGuid();
            Guid erbil = Guid.NewGuid(), basra = Guid.NewGuid(), area = Guid.NewGuid();
            Guid w1 = Guid.NewGuid(), w2 = Guid.NewGuid(), wholesaler = Guid.NewGuid();
            Guid a = Guid.NewGuid(), b = Guid.NewGuid(), old = Guid.NewGuid();
            await using (var seed = new NpgsqlCommand("""
                INSERT INTO tenants(id,name,code) VALUES (@tenant,'A','a');
                INSERT INTO users(id,tenant_id,email) VALUES (@officer,@tenant,'officer@a.test'),(@manager,@tenant,'manager@a.test');
                """, connection))
            {
                seed.Parameters.AddWithValue("tenant", tenant);
                seed.Parameters.AddWithValue("officer", officer);
                seed.Parameters.AddWithValue("manager", manager);
                await seed.ExecuteNonQueryAsync();
            }
            await InTenant(connection, tenant, $"""
                INSERT INTO places(id,tenant_id,parent_id,kind,name) VALUES ('{erbil}','{tenant}',NULL,'city','Erbil'),('{basra}','{tenant}',NULL,'city','Basra'),('{area}','{tenant}','{erbil}','neighbourhood','Ankawa');
                INSERT INTO warehouses(id,tenant_id,code,name,kind,city_id) VALUES ('{w1}','{tenant}','W1','Erbil warehouse','company','{erbil}'),('{w2}','{tenant}','W2','Basra warehouse','company','{basra}'),('{wholesaler}','{tenant}','M1','A wholesaler','wholesaler',NULL);
                INSERT INTO warehouse_staff(tenant_id,warehouse_id,user_id) VALUES ('{tenant}','{w1}','{officer}');
                INSERT INTO products(id,tenant_id,code,name,unit,currency,base_price,status) VALUES
                    ('{a}','{tenant}','A','Cough syrup','box','IQD',1000,'active'),('{b}','{tenant}','B','Vitamin C','box','IQD',500,'active'),('{old}','{tenant}','C','Old product','box','IQD',1,'inactive');
                """);

            var store = new PostgresOrderStore(dataSource);
            var t = new TenantId(tenant);
            var mgr = new UserId(manager);

            // The managers: every active product in both company warehouses, nothing counted yet; the wholesaler and the inactive product are left out.
            var start = await store.ListStockAsync(t, null, CancellationToken.None);
            Assert.Equal(new[] { "W1/A", "W1/B", "W2/A", "W2/B" }, start.Select(r => $"{r.WarehouseCode}/{r.ProductCode}").ToArray());
            Assert.All(start, r => Assert.False(r.Counted));
            Assert.Equal(new[] { "Erbil", "Erbil", "Basra", "Basra" }, start.Select(r => r.CityName).ToArray());

            Assert.Equal(OrderResult.Success, await store.SetStockAsync(t, mgr, w1, a, 12.5m, null, CancellationToken.None));
            Assert.Equal(OrderResult.Success, await store.SetStockAsync(t, mgr, w1, a, 10m, null, CancellationToken.None));   // a second count replaces the first
            var counted = (await store.ListStockAsync(t, null, CancellationToken.None)).Single(r => r.WarehouseCode == "W1" && r.ProductCode == "A");
            Assert.Equal((10m, true), (counted.Quantity, counted.Counted));

            // The worker of W1: sees and changes W1 only.
            var own = await store.ListStockAsync(t, officer, CancellationToken.None);
            Assert.Equal(new[] { "W1/A", "W1/B" }, own.Select(r => $"{r.WarehouseCode}/{r.ProductCode}").ToArray());
            Assert.Equal(OrderResult.Success, await store.SetStockAsync(t, new UserId(officer), w1, b, 3m, officer, CancellationToken.None));
            Assert.Equal(OrderResult.NotFound, await store.SetStockAsync(t, new UserId(officer), w2, a, 1m, officer, CancellationToken.None));   // not their warehouse
            Assert.Equal(OrderResult.NotFound, await store.SetStockAsync(t, mgr, wholesaler, a, 1m, null, CancellationToken.None));            // a wholesaler has no stock here
            Assert.Equal(OrderResult.NotFound, await store.SetStockAsync(t, mgr, w1, old, 1m, null, CancellationToken.None));                  // inactive product
            Assert.Equal(OrderResult.NotFound, await store.SetStockAsync(t, mgr, Guid.NewGuid(), a, 1m, null, CancellationToken.None));
            Assert.Equal("2", await InTenant(connection, tenant, "SELECT count(*) FROM warehouse_stock"));
            Assert.Equal("3", await InTenant(connection, tenant, "SELECT count(*) FROM audit_logs WHERE action = 'warehouse.stock_set'"));

            // Handing an order to a driver takes what was prepared off the counted stock of that warehouse (never below zero; uncounted products are left alone).
            Guid driver = Guid.NewGuid(), customer = Guid.NewGuid(), order = Guid.NewGuid();
            await using (var seedDriver = new NpgsqlCommand("INSERT INTO users(id,tenant_id,email) VALUES (@d,@t,'driver@a.test')", connection))
            {
                seedDriver.Parameters.AddWithValue("d", driver);
                seedDriver.Parameters.AddWithValue("t", tenant);
                await seedDriver.ExecuteNonQueryAsync();
            }
            await InTenant(connection, tenant, $"""
                SELECT seed_default_roles('{tenant}');
                INSERT INTO user_roles(tenant_id,user_id,role_id) SELECT '{tenant}','{driver}',id FROM roles WHERE tenant_id = '{tenant}' AND name = 'Driver';
                INSERT INTO customers(id,tenant_id,type,code,name) VALUES ('{customer}','{tenant}','Pharmacy','H-1','Shifa');
                INSERT INTO orders(id,tenant_id,number,customer_id,warehouse_id,created_by,status,currency,total,client_ref) VALUES ('{order}','{tenant}',1,'{customer}','{w1}','{manager}','ready','IQD',0,gen_random_uuid());
                INSERT INTO order_items(tenant_id,order_id,line_no,product_id,product_code,product_name,unit,quantity,prepared_quantity,shortage_reason,list_price,unit_price,line_total) VALUES
                    ('{tenant}','{order}',1,'{a}','A','Cough syrup','box',4,3,'short',1000,1000,0),
                    ('{tenant}','{order}',2,'{b}','B','Vitamin C','box',9,NULL,NULL,500,500,0);
                """);
            Assert.Equal(OrderResult.Success, await store.HandOverAsync(t, new UserId(officer), order, driver, CancellationToken.None));
            var after = (await store.ListStockAsync(t, null, CancellationToken.None)).Where(r => r.WarehouseCode == "W1").ToDictionary(r => r.ProductCode, r => r.Quantity);
            Assert.Equal((7m, 0m), (after["A"], after["B"]));   // A: 10 - 3 prepared; B: 3 - 9 ordered, not below zero
            Assert.Equal("0", await InTenant(connection, tenant, $"SELECT count(*) FROM warehouse_stock WHERE warehouse_id = '{w2}'"));   // the other warehouse is untouched

            // Quantities cannot be negative, whatever the caller does.
            await Assert.ThrowsAsync<PostgresException>(() => store.SetStockAsync(t, mgr, w1, a, -1m, null, CancellationToken.None));

            // Cities: only a place of kind city counts.
            Assert.True(await store.CityExistsAsync(t, erbil, CancellationToken.None));
            Assert.False(await store.CityExistsAsync(t, area, CancellationToken.None));
            Assert.False(await store.CityExistsAsync(t, Guid.NewGuid(), CancellationToken.None));

            // A warehouse keeps its city, address and phone through create, list and update.
            var created = await store.CreateWarehouseAsync(t, mgr, new SaveWarehouseRequest("W3", "Mosul warehouse", CityId: basra, Address: "Industrial street", Phone: "0750 000 0000"), CancellationToken.None);
            var listed = (await store.ListWarehousesAsync(t, true, CancellationToken.None)).Single(w => w.Id == created);
            Assert.Equal(("Basra", "Industrial street", "0750 000 0000"), (listed.CityName, listed.Address, listed.Phone));
            Assert.True(await store.UpdateWarehouseAsync(t, mgr, created!.Value, new SaveWarehouseRequest("W3", "Mosul warehouse", "inactive", CityId: erbil), CancellationToken.None));
            var changed = (await store.ListWarehousesAsync(t, true, CancellationToken.None)).Single(w => w.Id == created);
            Assert.Equal(("Erbil", "inactive", null), (changed.CityName, changed.Status, changed.Address));
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP SCHEMA {schema} CASCADE", setup);
            await drop.ExecuteNonQueryAsync();
        }
    }

    // Every statement runs in a transaction that names the company, as the API does (row level security).
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
