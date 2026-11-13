using LifeDrop.Application.Catalog;
using LifeDrop.Application.Orders;
using LifeDrop.Application.Roles;
using LifeDrop.Domain.Common;
using LifeDrop.Infrastructure.Catalog;
using LifeDrop.Infrastructure.Orders;
using Npgsql;
using Xunit;

namespace LifeDrop.Database.Tests;

// The total stock of a product is the sum of its warehouses (owner request 2026-11-13) against every migration in an isolated schema: a product counted in warehouses reports their
// sum and what the open orders will take; an order for more than the warehouse has free is placed with a warning and never touches the old single figure (which still rules the
// products that are not counted); cancelling gives the old figure back only for lines that took it; a failed delivery, or a short one, gives the goods back to the warehouse once.
public class StockFollowsWarehousesStoreTests
{
    [Fact]
    public async Task Warehouse_stock_is_the_total_orders_warn_and_failed_deliveries_give_back()
    {
        var url = Environment.GetEnvironmentVariable("TEST_DATABASE_URL")
            ?? throw new InvalidOperationException("TEST_DATABASE_URL must name an isolated lifedrop_test_* database.");
        var builder = new NpgsqlConnectionStringBuilder(url);
        if (builder.Database is null || !builder.Database.StartsWith("lifedrop_test_", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Refusing to test a database outside lifedrop_test_*.");

        var schema = "stockflow_test_" + Guid.NewGuid().ToString("N");
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

            Guid tenant = Guid.NewGuid(), manager = Guid.NewGuid(), rep = Guid.NewGuid(), officer = Guid.NewGuid(), driver = Guid.NewGuid();
            Guid w1 = Guid.NewGuid(), w2 = Guid.NewGuid(), customer = Guid.NewGuid(), a = Guid.NewGuid(), b = Guid.NewGuid(), c = Guid.NewGuid();
            await using (var seed = new NpgsqlCommand("""
                INSERT INTO tenants(id,name,code) VALUES (@tenant,'A','a');
                INSERT INTO users(id,tenant_id,email) VALUES (@manager,@tenant,'m@a.test'),(@rep,@tenant,'rep@a.test'),(@officer,@tenant,'o@a.test'),(@driver,@tenant,'d@a.test');
                """, connection))
            {
                seed.Parameters.AddWithValue("tenant", tenant);
                seed.Parameters.AddWithValue("manager", manager);
                seed.Parameters.AddWithValue("rep", rep);
                seed.Parameters.AddWithValue("officer", officer);
                seed.Parameters.AddWithValue("driver", driver);
                await seed.ExecuteNonQueryAsync();
            }
            await InTenant(connection, tenant, $"""
                SELECT seed_default_roles('{tenant}');
                INSERT INTO user_roles(tenant_id,user_id,role_id) SELECT '{tenant}','{driver}',id FROM roles WHERE tenant_id = '{tenant}' AND name = 'Driver';
                INSERT INTO customers(id,tenant_id,type,code,name) VALUES ('{customer}','{tenant}','Pharmacy','H-1','Shifa');
                INSERT INTO warehouses(id,tenant_id,code,name) VALUES ('{w1}','{tenant}','W1','Erbil'),('{w2}','{tenant}','W2','Basra');
                INSERT INTO warehouse_staff(tenant_id,warehouse_id,user_id) VALUES ('{tenant}','{w1}','{officer}');
                INSERT INTO products(id,tenant_id,code,name,unit,currency,base_price,stock_qty) VALUES
                    ('{a}','{tenant}','A','Counted','box','IQD',1000,100),('{b}','{tenant}','B','Old figure','box','IQD',500,3),('{c}','{tenant}','C','Untracked','box','IQD',100,NULL);
                INSERT INTO warehouse_stock(tenant_id,warehouse_id,product_id,quantity) VALUES ('{tenant}','{w1}','{a}',10),('{tenant}','{w2}','{a}',5);
                """);

            var orders = new PostgresOrderStore(dataSource);
            var products = new PostgresProductStore(dataSource);
            var t = new TenantId(tenant);
            var repId = new UserId(rep);
            async Task<ProductDto> Product(Guid id) => (await products.ListAsync(t, true, null, CancellationToken.None)).Single(p => p.Id == id);
            async Task<string> Scalar(string sql) => await InTenant(connection, tenant, sql);
            NewOrder Order(Guid warehouse, params (Guid Product, string Code, decimal Quantity)[] lines) => new(customer, warehouse, null, Guid.NewGuid(), "IQD", 0, null,
                lines.Select(l => new PricedLine(l.Product, l.Code, l.Code, "box", l.Quantity, 1, 1, 0, [])).ToList());

            // The total of a counted product is the sum of its warehouses; the others keep the old figure.
            var start = await Product(a);
            Assert.Equal((15m, 0m, true), (start.StockQty, start.ReservedQty, start.InStock));
            Assert.Equal((3m, (decimal?)null), ((await Product(b)).StockQty, (await Product(b)).ReservedQty));
            Assert.Null((await Product(c)).StockQty);

            // Orders: 4 of 10 free is fine; 7 more is more than the 6 left free: placed anyway, with a warning, and the old figure of A (100) is never touched.
            var (r1, first) = await orders.CreateAsync(t, repId, Order(w1, (a, "A", 4m)), CancellationToken.None);
            Assert.Equal((OrderResult.Success, null), (r1, first!.StockWarnings));
            var (r2, second) = await orders.CreateAsync(t, repId, Order(w1, (a, "A", 7m)), CancellationToken.None);
            Assert.Equal(OrderResult.Success, r2);
            Assert.Equal(new[] { "A" }, second!.StockWarnings!.ToArray());
            Assert.Equal("100", await Scalar($"SELECT stock_qty::int FROM products WHERE id = '{a}'"));
            Assert.Equal("false|false", await Scalar($"SELECT string_agg(legacy_stock_taken::text, '|' ORDER BY line_no) FROM order_items WHERE order_id IN ('{first.Id}', '{second.Id}') GROUP BY product_id"));
            var reserved = await Product(a);
            Assert.Equal((15m, 11m, true), (reserved.StockQty, reserved.ReservedQty, reserved.InStock));   // 15 - 11 = 4 free
            var row = (await orders.ListStockAsync(t, null, CancellationToken.None)).Single(r => r.WarehouseCode == "W1" && r.ProductCode == "A");
            Assert.Equal((10m, 11m), (row.Quantity, row.Reserved));

            // A product that is not counted keeps the old figure: taken by the order, given back when it is cancelled; the counted one gives nothing back.
            var (rb, orderB) = await orders.CreateAsync(t, repId, Order(w1, (b, "B", 2m)), CancellationToken.None);
            Assert.Equal(OrderResult.Success, rb);
            Assert.Equal(1m, (await Product(b)).StockQty);
            var (rOut, _) = await orders.CreateAsync(t, repId, Order(w1, (b, "B", 2m)), CancellationToken.None);
            Assert.Equal(OrderResult.OutOfStock, rOut);
            var scope = new AccessScope(DataScope.Tenant, repId, null);
            Assert.Equal(OrderResult.Success, await orders.CancelAsync(t, repId, orderB!.Id, scope, true, "changed mind", CancellationToken.None));
            Assert.Equal(3m, (await Product(b)).StockQty);
            Assert.Equal(OrderResult.Success, await orders.CancelAsync(t, repId, second.Id, scope, true, "changed mind", CancellationToken.None));
            Assert.Equal("100", await Scalar($"SELECT stock_qty::int FROM products WHERE id = '{a}'"));   // not given back: it was never taken

            // Hand over the 4 of the first order: they leave W1 (10 -> 6); the delivery fails: they come back (6 -> 10), once.
            var staff = new UserId(officer);
            await InTenant(connection, tenant, $"UPDATE orders SET status = 'ready' WHERE id = '{first.Id}'");
            Assert.Equal(OrderResult.Success, await orders.HandOverAsync(t, staff, first.Id, driver, CancellationToken.None));
            async Task<decimal> W1Stock() => (await orders.ListStockAsync(t, null, CancellationToken.None)).Single(r => r.WarehouseCode == "W1" && r.ProductCode == "A").Quantity;
            Assert.Equal(6m, await W1Stock());
            Assert.Equal(OrderResult.Success, await orders.FailDeliveryAsync(t, new UserId(driver), first.Id, "nobody there", null, CancellationToken.None));
            Assert.Equal(10m, await W1Stock());
            Assert.Equal("false", await Scalar($"SELECT stock_left_warehouse::text FROM orders WHERE id = '{first.Id}'"));

            // A short delivery gives back only the difference: 5 handed over, 3 delivered, 2 come back.
            var (r3, third) = await orders.CreateAsync(t, repId, Order(w1, (a, "A", 5m)), CancellationToken.None);
            Assert.Equal(OrderResult.Success, r3);
            await InTenant(connection, tenant, $"UPDATE orders SET status = 'ready' WHERE id = '{third!.Id}'");
            Assert.Equal(OrderResult.Success, await orders.HandOverAsync(t, staff, third.Id, driver, CancellationToken.None));
            Assert.Equal(5m, await W1Stock());
            await InTenant(connection, tenant, $"UPDATE orders SET status = 'on_the_way' WHERE id = '{third.Id}'");
            Assert.Equal(OrderResult.Success, await orders.DeliverAsync(t, new UserId(driver), third.Id, "Dr. Ali", null, [new DeliveredLineRequest(1, 3m, "customer wanted less")], null, CancellationToken.None));
            Assert.Equal(7m, await W1Stock());

            // An order handed over before the stock followed the warehouses (flag false) adds nothing back.
            var (r4, old) = await orders.CreateAsync(t, repId, Order(w1, (a, "A", 1m)), CancellationToken.None);
            Assert.Equal(OrderResult.Success, r4);
            await InTenant(connection, tenant, $"UPDATE orders SET status = 'handed_over', driver_id = '{driver}' WHERE id = '{old!.Id}'");
            Assert.Equal(OrderResult.Success, await orders.FailDeliveryAsync(t, new UserId(driver), old.Id, "nobody there", null, CancellationToken.None));
            Assert.Equal(7m, await W1Stock());
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP SCHEMA {schema} CASCADE", setup);
            await drop.ExecuteNonQueryAsync();
        }
    }

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
