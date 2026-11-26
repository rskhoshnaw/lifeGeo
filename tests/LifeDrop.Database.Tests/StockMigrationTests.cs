using Npgsql;
using Xunit;

namespace LifeDrop.Database.Tests;

// Migration 0078 (plan step 5.53): the old single stock figure moves into the warehouses without losing anything. Run as the superuser, like the real setup (setup-db.sh), against every
// earlier migration in an isolated schema with data that has the old figures: the figure is copied to legacy_stock_backup; a product nobody counted gets it (plus what its open orders took)
// in the first active company warehouse by code; a product already counted keeps its warehouse stock; a company without a warehouse keeps the figure only in the backup; running it again
// changes nothing.
public class StockMigrationTests
{
    [SkippableFact]
    public async Task The_old_stock_figure_moves_into_the_first_company_warehouse_once_and_loses_nothing()
    {
        var url = Environment.GetEnvironmentVariable("TEST_SUPERUSER_DATABASE_URL");
        Skip.If(url is null, "TEST_SUPERUSER_DATABASE_URL is not set");
        var builder = new NpgsqlConnectionStringBuilder(url);
        if (builder.Database is null || !builder.Database.StartsWith("lifedrop_test_", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Refusing to test a database outside lifedrop_test_*.");

        var schema = "stockmig_test_" + Guid.NewGuid().ToString("N");
        await using var connection = new NpgsqlConnection(url);
        await connection.OpenAsync();
        await Run(connection, $"CREATE SCHEMA {schema}");
        try
        {
            await Run(connection, $"SET search_path TO {schema}, public");
            var files = Directory.GetFiles(AppContext.BaseDirectory, "00??_*.sql").Select(Path.GetFileName).OrderBy(f => f, StringComparer.Ordinal).ToList();
            var last = files.Single(f => f!.StartsWith("0078_", StringComparison.Ordinal));
            await Run(connection, string.Concat(files.Where(f => f != last).Select(f => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, f!)))));

            Guid t1 = Guid.NewGuid(), t2 = Guid.NewGuid(), user1 = Guid.NewGuid(), user2 = Guid.NewGuid(), customer = Guid.NewGuid();
            Guid wB = Guid.NewGuid(), wA = Guid.NewGuid(), wOff = Guid.NewGuid();
            Guid plain = Guid.NewGuid(), none = Guid.NewGuid(), counted = Guid.NewGuid(), zero = Guid.NewGuid(), open = Guid.NewGuid(), lonely = Guid.NewGuid();
            Guid order = Guid.NewGuid();
            await Run(connection, $"""
                INSERT INTO tenants(id,name,code) VALUES ('{t1}','One','one'),('{t2}','Two','two');
                INSERT INTO users(id,tenant_id,email) VALUES ('{user1}','{t1}','u1@a.test'),('{user2}','{t2}','u2@a.test');
                INSERT INTO customers(id,tenant_id,type,code,name) VALUES ('{customer}','{t1}','Pharmacy','C1','Shifa');
                -- company one: W-B is the second by code, W-A the first; an inactive W-0 that sorts first must be skipped; company two has no warehouse
                INSERT INTO warehouses(id,tenant_id,code,name,status) VALUES ('{wB}','{t1}','W-B','Second','active'),('{wA}','{t1}','W-A','First','active'),('{wOff}','{t1}','W-0','Closed','inactive');
                INSERT INTO products(id,tenant_id,code,name,unit,currency,base_price,stock_qty) VALUES
                    ('{plain}','{t1}','PLAIN','Plain','box','IQD',1,20),
                    ('{none}','{t1}','NONE','No figure','box','IQD',1,NULL),
                    ('{counted}','{t1}','COUNTED','Counted already','box','IQD',1,50),
                    ('{zero}','{t1}','ZERO','Old zero','box','IQD',1,0),
                    ('{open}','{t1}','OPEN','Has an open order','box','IQD',1,10),
                    ('{lonely}','{t2}','LONELY','No warehouse','box','IQD',1,4);
                INSERT INTO warehouse_stock(tenant_id,warehouse_id,product_id,quantity) VALUES ('{t1}','{wB}','{counted}',3);
                INSERT INTO orders(id,tenant_id,number,customer_id,warehouse_id,created_by,status,currency,total,client_ref) VALUES ('{order}','{t1}',1,'{customer}','{wB}','{user1}','placed','IQD',0,gen_random_uuid());
                INSERT INTO order_items(tenant_id,order_id,line_no,product_id,product_code,product_name,unit,quantity,list_price,unit_price,line_total,legacy_stock_taken)
                    VALUES ('{t1}','{order}',1,'{open}','OPEN','Has an open order','box',5,1,1,5,true);
                """);

            var migration = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, last!));
            await Run(connection, migration);

            async Task<string> Q(string sql) => Convert.ToString(await new NpgsqlCommand(sql, connection).ExecuteScalarAsync()) ?? "";
            // Moved into W-A (the first active company warehouse by code): the figure, and for OPEN the 10 left plus the 5 its open order took
            Assert.Equal("W-A:20", await Q($"SELECT w.code || ':' || s.quantity::int FROM warehouse_stock s JOIN warehouses w ON w.id = s.warehouse_id WHERE s.product_id = '{plain}'"));
            Assert.Equal("W-A:15", await Q($"SELECT w.code || ':' || s.quantity::int FROM warehouse_stock s JOIN warehouses w ON w.id = s.warehouse_id WHERE s.product_id = '{open}'"));
            Assert.Equal("W-A:0", await Q($"SELECT w.code || ':' || s.quantity::int FROM warehouse_stock s JOIN warehouses w ON w.id = s.warehouse_id WHERE s.product_id = '{zero}'"));
            // A product already counted keeps what the warehouses hold (50 is ignored, 3 stays in W-B); a product with no figure gets nothing
            Assert.Equal("W-B:3", await Q($"SELECT string_agg(w.code || ':' || s.quantity::int, ',') FROM warehouse_stock s JOIN warehouses w ON w.id = s.warehouse_id WHERE s.product_id = '{counted}'"));
            Assert.Equal("0", await Q($"SELECT count(*) FROM warehouse_stock WHERE product_id = '{none}'"));
            // Company two has no warehouse: nothing moved, the figure waits in the backup
            Assert.Equal("0", await Q($"SELECT count(*) FROM warehouse_stock WHERE tenant_id = '{t2}'"));
            Assert.Equal("4|", await Q($"SELECT b.stock_qty::int || '|' || coalesce(b.moved_to_warehouse_id::text, '') FROM legacy_stock_backup b WHERE b.product_id = '{lonely}'"));
            // Every figure is in the backup (five products had one), with what was moved
            Assert.Equal("5", await Q("SELECT count(*) FROM legacy_stock_backup"));
            Assert.Equal("20|0|20", await Q($"SELECT b.stock_qty::int || '|' || b.open_orders_qty::int || '|' || b.moved_quantity::int FROM legacy_stock_backup b WHERE b.product_id = '{plain}'"));
            Assert.Equal("10|5|15", await Q($"SELECT b.stock_qty::int || '|' || b.open_orders_qty::int || '|' || b.moved_quantity::int FROM legacy_stock_backup b WHERE b.product_id = '{open}'"));
            // New order lines never take from the old figure any more
            Assert.Equal("false", await Q("SELECT column_default FROM information_schema.columns WHERE table_name = 'order_items' AND column_name = 'legacy_stock_taken' AND table_schema = current_schema()"));

            // Running it again changes nothing
            var before = await Q("SELECT string_agg(product_id || ':' || quantity, ',' ORDER BY product_id) FROM warehouse_stock") + "/" + await Q("SELECT count(*) FROM legacy_stock_backup");
            await Run(connection, migration);
            Assert.Equal(before, await Q("SELECT string_agg(product_id || ':' || quantity, ',' ORDER BY product_id) FROM warehouse_stock") + "/" + await Q("SELECT count(*) FROM legacy_stock_backup"));
        }
        finally
        {
            await Run(connection, "SET search_path TO public");
            await Run(connection, $"DROP SCHEMA IF EXISTS {schema} CASCADE");
        }
    }

    private static async Task Run(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
