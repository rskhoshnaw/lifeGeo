using LifeDrop.Application.Customers;
using LifeDrop.Application.Roles;
using LifeDrop.Domain.Common;
using LifeDrop.Infrastructure.Customers;
using Npgsql;
using Xunit;

namespace LifeDrop.Database.Tests;

// The store behind editing and archiving customers (plan step 4.11) against the first migrations plus 0043 in an isolated schema: type, code and name change only a customer
// inside the actor's scope and company, a code of another customer is a conflict, archive and restore write their audit row.
public class CustomerArchiveStoreTests
{
    [Fact]
    public async Task Edits_and_archives_only_inside_scope_and_company_and_audits_each_change()
    {
        var url = Environment.GetEnvironmentVariable("TEST_DATABASE_URL")
            ?? throw new InvalidOperationException("TEST_DATABASE_URL must name an isolated lifedrop_test_* database.");
        var builder = new NpgsqlConnectionStringBuilder(url);
        if (builder.Database is null || !builder.Database.StartsWith("lifedrop_test_", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Refusing to test a database outside lifedrop_test_*.");

        var schema = "custarchive_test_" + Guid.NewGuid().ToString("N");
        await using var setup = new NpgsqlConnection(url);
        await setup.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE SCHEMA {schema}", setup))
            await create.ExecuteNonQueryAsync();

        try
        {
            builder.SearchPath = $"{schema},public";
            await using var dataSource = NpgsqlDataSource.Create(builder.ConnectionString);
            await using var connection = await dataSource.OpenConnectionAsync();
            foreach (var file in new[] { "0001_role_assignment.sql", "0002_login.sql", "0003_sessions_devices.sql", "0004_customers.sql", "0007_regions_teams.sql", "0008_role_data_scope.sql", "0043_customer_archive.sql", "0047_customer_doctor_fields.sql" })
                await using (var migrate = new NpgsqlCommand(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, file)), connection))
                    await migrate.ExecuteNonQueryAsync();

            // The store tells the people responsible about an edit; only the table it writes to is needed here (the full 0022 also reseeds roles).
            await using (var notifications = new NpgsqlCommand("CREATE TABLE notifications (id uuid PRIMARY KEY DEFAULT gen_random_uuid(), tenant_id uuid NOT NULL, user_id uuid NOT NULL, kind text NOT NULL, entity_id text NOT NULL, actor_id uuid NOT NULL)", connection))
                await notifications.ExecuteNonQueryAsync();

            var tenant = Guid.NewGuid();
            var other = Guid.NewGuid();
            var actor = Guid.NewGuid();
            var c1 = Guid.NewGuid();
            var c2 = Guid.NewGuid();
            var foreign = Guid.NewGuid();
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
            await InTenant(connection, tenant, $"INSERT INTO customers(id,tenant_id,type,code,name) VALUES ('{c1}','{tenant}','pharmacy','A-1','One'),('{c2}','{tenant}','pharmacy','A-2','Two')");
            await InTenant(connection, other, $"INSERT INTO customers(id,tenant_id,type,code,name) VALUES ('{foreign}','{other}','pharmacy','A-1','Foreign')");

            var store = new PostgresCustomerStore(dataSource);
            var t = new TenantId(tenant);
            var a = new UserId(actor);
            var whole = AccessScope.WholeTenant(a);
            var own = new AccessScope(DataScope.Own, new UserId(Guid.NewGuid()), null);

            // Edit: all three fields change with an audit row; outside the scope or the company it is not found; another customer's code is a conflict.
            Assert.Equal(CustomerUpdateResult.Success, await store.EditAsync(t, a, new CustomerId(c1), whole, "clinic", "A-9", "Renamed", CancellationToken.None));
            Assert.Equal("clinic|A-9|Renamed", await InTenant(connection, tenant, $"SELECT type || '|' || code || '|' || name FROM customers WHERE id = '{c1}'"));
            Assert.Equal(CustomerUpdateResult.NotFound, await store.EditAsync(t, a, new CustomerId(c1), own, "x", "X-1", "x", CancellationToken.None));
            Assert.Equal(CustomerUpdateResult.NotFound, await store.EditAsync(t, a, new CustomerId(foreign), whole, "x", "X-2", "x", CancellationToken.None));
            Assert.Equal(CustomerUpdateResult.Conflict, await store.EditAsync(t, a, new CustomerId(c1), whole, "clinic", "A-2", "Renamed", CancellationToken.None));
            Assert.Equal("A-9", await InTenant(connection, tenant, $"SELECT code FROM customers WHERE id = '{c1}'"));

            // Doctor details (plan step 4.27): stored and cleared with an audit row; outside the scope or company it is not found; the class is limited by the table.
            Assert.True(await store.SetDoctorAsync(t, a, new CustomerId(c1), whole, new CustomerDoctorRequest("Azadi Complex", "orthopedics", true, false, "A+"), CancellationToken.None));
            Assert.Equal("Azadi Complex|orthopedics|true|false|A+", await InTenant(connection, tenant, $"SELECT concat_ws('|', clinic_name, specialty, cooperates::text, is_dealer::text, customer_class) FROM customers WHERE id = '{c1}'"));
            Assert.False(await store.SetDoctorAsync(t, a, new CustomerId(c1), own, new CustomerDoctorRequest(null, null, null, null, null), CancellationToken.None));
            Assert.False(await store.SetDoctorAsync(t, a, new CustomerId(foreign), whole, new CustomerDoctorRequest(null, null, null, null, null), CancellationToken.None));
            await Assert.ThrowsAsync<PostgresException>(() => store.SetDoctorAsync(t, a, new CustomerId(c1), whole, new CustomerDoctorRequest(null, null, null, null, "D"), CancellationToken.None));
            Assert.True(await store.SetDoctorAsync(t, a, new CustomerId(c1), whole, new CustomerDoctorRequest(null, null, null, null, null), CancellationToken.None));
            Assert.Equal("0", await InTenant(connection, tenant, $"SELECT count(*) FROM customers WHERE id = '{c1}' AND (clinic_name IS NOT NULL OR specialty IS NOT NULL OR cooperates IS NOT NULL OR customer_class IS NOT NULL)"));

            // Archive and restore, each audited; the normal list no longer shows an archived customer.
            Assert.True(await store.SetArchivedAsync(t, a, new CustomerId(c2), whole, true, CancellationToken.None));
            Assert.Equal("1", await InTenant(connection, tenant, $"SELECT count(*) FROM customers WHERE id = '{c2}' AND archived_at IS NOT NULL"));
            Assert.False(await store.SetArchivedAsync(t, a, new CustomerId(c2), own, false, CancellationToken.None));
            Assert.False(await store.SetArchivedAsync(t, a, new CustomerId(foreign), whole, true, CancellationToken.None));
            Assert.True(await store.SetArchivedAsync(t, a, new CustomerId(c2), whole, false, CancellationToken.None));
            Assert.Equal("0", await InTenant(connection, tenant, $"SELECT count(*) FROM customers WHERE id = '{c2}' AND archived_at IS NOT NULL"));
            Assert.Equal("customer.archived,customer.restored", await InTenant(connection, tenant, $"SELECT string_agg(action, ',' ORDER BY action COLLATE \"C\") FROM audit_logs WHERE entity_id = '{c2}'"));
            Assert.Equal("customer.doctor_changed,customer.doctor_changed,customer.edited", await InTenant(connection, tenant, $"SELECT string_agg(action, ',' ORDER BY action COLLATE \"C\") FROM audit_logs WHERE entity_id = '{c1}'"));
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE", setup);
            await drop.ExecuteNonQueryAsync();
        }
    }

    // customers has row level security: every statement runs in a transaction that names the company, as the API does.
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
