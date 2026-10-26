using LifeDrop.Application.Customers;
using LifeDrop.Application.Roles;
using LifeDrop.Domain.Common;
using LifeDrop.Infrastructure.Customers;
using Npgsql;
using Xunit;

namespace LifeDrop.Database.Tests;

// The customer list filters of plan step 5.2 (type, place with everything below it, cooperates, with or without a search text) against the first migrations
// in an isolated schema; the places table is created here in its minimal form so the test does not depend on the later role migrations.
public class CustomerFilterStoreTests
{
    [Fact]
    public async Task Filters_by_type_place_below_and_cooperates_inside_the_company()
    {
        var url = Environment.GetEnvironmentVariable("TEST_DATABASE_URL")
            ?? throw new InvalidOperationException("TEST_DATABASE_URL must name an isolated lifedrop_test_* database.");
        var builder = new NpgsqlConnectionStringBuilder(url);
        if (builder.Database is null || !builder.Database.StartsWith("lifedrop_test_", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Refusing to test a database outside lifedrop_test_*.");

        var schema = "custfilter_test_" + Guid.NewGuid().ToString("N");
        await using var setup = new NpgsqlConnection(url);
        await setup.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE SCHEMA {schema}", setup))
            await create.ExecuteNonQueryAsync();

        try
        {
            builder.SearchPath = $"{schema},public";
            await using var dataSource = NpgsqlDataSource.Create(builder.ConnectionString);
            await using var connection = await dataSource.OpenConnectionAsync();
            foreach (var file in new[] { "0001_role_assignment.sql", "0002_login.sql", "0003_sessions_devices.sql", "0004_customers.sql", "0007_regions_teams.sql", "0008_role_data_scope.sql", "0047_customer_doctor_fields.sql" })
                await using (var migrate = new NpgsqlCommand(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, file)), connection))
                    await migrate.ExecuteNonQueryAsync();
            await using (var places = new NpgsqlCommand("""
                CREATE TABLE places (id uuid PRIMARY KEY, tenant_id uuid NOT NULL REFERENCES tenants(id), parent_id uuid, kind text NOT NULL, name text NOT NULL, UNIQUE (tenant_id, id));
                ALTER TABLE customers ADD COLUMN place_id uuid;
                """, connection))
                await places.ExecuteNonQueryAsync();

            Guid tenant = Guid.NewGuid(), other = Guid.NewGuid(), actor = Guid.NewGuid();
            Guid erbil = Guid.NewGuid(), ankawa = Guid.NewGuid(), street = Guid.NewGuid(), sulaymaniyah = Guid.NewGuid();
            Guid c1 = Guid.NewGuid(), c2 = Guid.NewGuid(), c3 = Guid.NewGuid(), c4 = Guid.NewGuid(), foreign = Guid.NewGuid();
            await using (var seed = new NpgsqlCommand("""
                INSERT INTO tenants(id,name,code) VALUES (@tenant,'A','a'),(@other,'B','b');
                INSERT INTO users(id,tenant_id) VALUES (@actor,@tenant);
                """, connection))
            {
                seed.Parameters.AddWithValue("tenant", tenant);
                seed.Parameters.AddWithValue("other", other);
                seed.Parameters.AddWithValue("actor", actor);
                await seed.ExecuteNonQueryAsync();
            }
            await InTenant(connection, tenant, $"""
                INSERT INTO places(id,tenant_id,parent_id,kind,name) VALUES ('{erbil}','{tenant}',NULL,'city','Erbil'),('{ankawa}','{tenant}','{erbil}','neighbourhood','Ankawa'),('{street}','{tenant}','{ankawa}','street','Main'),('{sulaymaniyah}','{tenant}',NULL,'city','Sulaymaniyah');
                INSERT INTO customers(id,tenant_id,type,code,name,place_id,cooperates) VALUES
                  ('{c1}','{tenant}','Pharmacy','F-1','Shifa Pharmacy','{street}',NULL),
                  ('{c2}','{tenant}','Doctor','F-2','Dr. Hana','{ankawa}',true),
                  ('{c3}','{tenant}','Doctor','F-3','Dr. Omar','{sulaymaniyah}',false),
                  ('{c4}','{tenant}','Doctor','F-4','Dr. Sara','{erbil}',false)
                """);
            await InTenant(connection, other, $"INSERT INTO customers(id,tenant_id,type,code,name) VALUES ('{foreign}','{other}','Doctor','F-9','Foreign doctor')");

            var store = new PostgresCustomerStore(dataSource);
            var t = new TenantId(tenant);
            var whole = AccessScope.WholeTenant(new UserId(actor));
            async Task<string> Codes(string? search, CustomerFilter filter)
            {
                var items = await store.FilterAsync(t, whole, 50, null, null, search, filter, CancellationToken.None);
                return string.Join(",", items.Select(c => c.Code).OrderBy(c => c, StringComparer.Ordinal));
            }

            Assert.Equal("F-2,F-3,F-4", await Codes(null, new CustomerFilter("doctor", null, null)));                 // type, any case; the other company's doctor never shows
            Assert.Equal("F-1,F-2,F-4", await Codes(null, new CustomerFilter(null, erbil, null)));                    // a city includes its neighbourhoods and streets
            Assert.Equal("F-1,F-2", await Codes(null, new CustomerFilter(null, ankawa, null)));                       // a neighbourhood includes its streets, not its parent
            Assert.Equal("F-1", await Codes(null, new CustomerFilter(null, street, null)));
            Assert.Equal("F-2", await Codes(null, new CustomerFilter(null, null, true)));
            Assert.Equal("F-3,F-4", await Codes(null, new CustomerFilter(null, null, false)));                        // false means "no", not "not set"
            Assert.Equal("F-4", await Codes(null, new CustomerFilter("Doctor", erbil, false)));                       // the filters combine
            Assert.Equal("F-2", await Codes("hana", new CustomerFilter(null, erbil, null)));                          // with a search text
            Assert.Equal("", await Codes("shifa", new CustomerFilter("Doctor", null, null)));
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE", setup);
            await drop.ExecuteNonQueryAsync();
        }
    }

    // customers and places have row level security in the real schema; here only the company is named, as the API does for every statement.
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
