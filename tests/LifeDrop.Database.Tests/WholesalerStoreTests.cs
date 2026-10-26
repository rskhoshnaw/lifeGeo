using LifeDrop.Application.Orders;
using LifeDrop.Domain.Common;
using LifeDrop.Infrastructure.Orders;
using Npgsql;
using Xunit;

namespace LifeDrop.Database.Tests;

// The wholesaler officer's store of plan step 5.6 against every migration in an isolated schema: an officer sees only the orders of the wholesalers they work with (not the company
// warehouse, not another wholesaler), with the customer's phone, address and place and the product lines; "dispatched" works once, leaves its history and audit rows, and a company
// order or a stranger's order is not found.
public class WholesalerStoreTests
{
    [Fact]
    public async Task The_officer_sees_and_dispatches_only_the_orders_of_their_own_wholesalers()
    {
        var url = Environment.GetEnvironmentVariable("TEST_DATABASE_URL")
            ?? throw new InvalidOperationException("TEST_DATABASE_URL must name an isolated lifedrop_test_* database.");
        var builder = new NpgsqlConnectionStringBuilder(url);
        if (builder.Database is null || !builder.Database.StartsWith("lifedrop_test_", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Refusing to test a database outside lifedrop_test_*.");

        var schema = "wholesaler_test_" + Guid.NewGuid().ToString("N");
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

            Guid tenant = Guid.NewGuid(), officer = Guid.NewGuid(), stranger = Guid.NewGuid(), rep = Guid.NewGuid();
            Guid wholesaler = Guid.NewGuid(), otherWholesaler = Guid.NewGuid(), company = Guid.NewGuid();
            Guid city = Guid.NewGuid(), area = Guid.NewGuid(), customer = Guid.NewGuid(), product = Guid.NewGuid();
            Guid oWait = Guid.NewGuid(), oOther = Guid.NewGuid(), oCompany = Guid.NewGuid();
            await using (var seed = new NpgsqlCommand("""
                INSERT INTO tenants(id,name,code) VALUES (@tenant,'A','a');
                INSERT INTO users(id,tenant_id,email) VALUES (@officer,@tenant,'officer@a.test'),(@stranger,@tenant,'stranger@a.test'),(@rep,@tenant,'rep@a.test');
                """, connection))
            {
                seed.Parameters.AddWithValue("tenant", tenant);
                seed.Parameters.AddWithValue("officer", officer);
                seed.Parameters.AddWithValue("stranger", stranger);
                seed.Parameters.AddWithValue("rep", rep);
                await seed.ExecuteNonQueryAsync();
            }
            await InTenant(connection, tenant, $"""
                INSERT INTO places(id,tenant_id,parent_id,kind,name) VALUES ('{city}','{tenant}',NULL,'city','Erbil'),('{area}','{tenant}','{city}','neighbourhood','Ankawa');
                INSERT INTO warehouses(id,tenant_id,code,name,kind) VALUES ('{wholesaler}','{tenant}','M1','Al-Rafidain Wholesaler','wholesaler'),('{otherWholesaler}','{tenant}','M2','Other Wholesaler','wholesaler'),('{company}','{tenant}','W1','Main','company');
                INSERT INTO warehouse_staff(tenant_id,warehouse_id,user_id) VALUES ('{tenant}','{wholesaler}','{officer}'),('{tenant}','{company}','{officer}'),('{tenant}','{otherWholesaler}','{stranger}');
                INSERT INTO products(id,tenant_id,code,name,unit,currency,base_price) VALUES ('{product}','{tenant}','A','Cough syrup','box','IQD',1000);
                INSERT INTO customers(id,tenant_id,type,code,name,phone,phone2,address,contact_name,place_id) VALUES ('{customer}','{tenant}','Pharmacy','H-1','Shifa Pharmacy','0750 111 2233','0770 444 5566','Near the main road','Dr. Ali','{area}');
                INSERT INTO orders(id,tenant_id,number,customer_id,warehouse_id,created_by,status,currency,total,client_ref,note) VALUES
                    ('{oWait}','{tenant}',1,'{customer}','{wholesaler}','{rep}','placed','IQD',20000,gen_random_uuid(),'Urgent'),
                    ('{oOther}','{tenant}',2,'{customer}','{otherWholesaler}','{rep}','placed','IQD',1000,gen_random_uuid(),NULL),
                    ('{oCompany}','{tenant}',3,'{customer}','{company}','{rep}','placed','IQD',1000,gen_random_uuid(),NULL);
                INSERT INTO order_items(tenant_id,order_id,line_no,product_id,product_code,product_name,unit,quantity,list_price,unit_price,line_total) VALUES
                    ('{tenant}','{oWait}',1,'{product}','A','Cough syrup','box',20,1000,1000,20000),
                    ('{tenant}','{oOther}',1,'{product}','A','Cough syrup','box',1,1000,1000,1000),
                    ('{tenant}','{oCompany}',1,'{product}','A','Cough syrup','box',1,1000,1000,1000);
                """);

            var store = new PostgresWholesalerStore(dataSource);
            var t = new TenantId(tenant);
            var me = new UserId(officer);
            var wholesalers = await store.ListWholesalersAsync(t, me, CancellationToken.None);
            Assert.Equal(new[] { ("M1", 1) }, wholesalers.Select(w => (w.Code, w.OpenOrders)).ToArray());   // the company warehouse and the other wholesaler are not theirs
            Assert.Empty(await store.ListWholesalersAsync(t, new UserId(rep), CancellationToken.None));

            var waiting = await store.ListOrdersAsync(t, me, null, "placed", 100, CancellationToken.None);
            var order = Assert.Single(waiting);
            Assert.Equal(("O-000001", "Al-Rafidain Wholesaler", "Shifa Pharmacy", "0750 111 2233", "Near the main road", "Erbil / Ankawa", "Dr. Ali", "rep@a.test", "Urgent"),
                (order.Number, order.WholesalerName, order.CustomerName, order.Phone, order.Address, order.Place, order.ContactName, order.VisitorEmail, order.Note));
            Assert.Equal(("0770 444 5566", 20000m), (order.Phone2, order.Total));
            var line = Assert.Single(order.Lines);
            Assert.Equal(("A", "Cough syrup", 20m, 20000m), (line.ProductCode, line.ProductName, line.Quantity, line.LineTotal));
            Assert.Single(await store.ListOrdersAsync(t, me, wholesaler, "placed", 100, CancellationToken.None));
            Assert.Empty(await store.ListOrdersAsync(t, me, otherWholesaler, "placed", 100, CancellationToken.None));

            Assert.Equal(OrderResult.NotFound, await store.DispatchAsync(t, new UserId(stranger), oWait, null, CancellationToken.None));   // not their wholesaler
            Assert.Equal(OrderResult.NotFound, await store.DispatchAsync(t, me, oCompany, null, CancellationToken.None));                 // a company warehouse order
            Assert.Equal(OrderResult.NotFound, await store.DispatchAsync(t, me, oOther, null, CancellationToken.None));                   // another wholesaler's order
            Assert.Equal(OrderResult.Success, await store.DispatchAsync(t, me, oWait, "told Ali", CancellationToken.None));
            Assert.Equal(OrderResult.Conflict, await store.DispatchAsync(t, me, oWait, null, CancellationToken.None));                    // only once

            Assert.Empty(await store.ListOrdersAsync(t, me, null, "placed", 100, CancellationToken.None));
            var sent = Assert.Single(await store.ListOrdersAsync(t, me, null, "dispatched", 100, CancellationToken.None));
            Assert.Equal(("dispatched", "officer@a.test"), (sent.Status, sent.DispatchedByEmail));
            Assert.NotNull(sent.DispatchedAt);
            Assert.Equal("dispatched|told Ali", await InTenant(connection, tenant, $"SELECT status || '|' || note FROM order_status_history WHERE order_id = '{oWait}' AND status = 'dispatched'"));
            Assert.Equal("1", await InTenant(connection, tenant, "SELECT count(*) FROM audit_logs WHERE action = 'order.dispatched'"));
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
