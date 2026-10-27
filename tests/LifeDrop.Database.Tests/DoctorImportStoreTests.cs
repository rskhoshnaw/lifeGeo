using LifeDrop.Application.Customers;
using LifeDrop.Domain.Common;
using LifeDrop.Infrastructure.Customers;
using Npgsql;
using Xunit;

namespace LifeDrop.Database.Tests;

// The doctor list import of plan step 5.9 against every migration in an isolated schema: codes D-0001..., places found or added once, the visitor as owner, a position only when both
// coordinates are given, a repeated file adds nothing, and a dry run leaves nothing behind.
public class DoctorImportStoreTests
{
    [Fact]
    public async Task The_import_creates_doctors_with_places_codes_and_owner_and_skips_repeats()
    {
        var url = Environment.GetEnvironmentVariable("TEST_DATABASE_URL")
            ?? throw new InvalidOperationException("TEST_DATABASE_URL must name an isolated lifedrop_test_* database.");
        var builder = new NpgsqlConnectionStringBuilder(url);
        if (builder.Database is null || !builder.Database.StartsWith("lifedrop_test_", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Refusing to test a database outside lifedrop_test_*.");

        var schema = "docimport_test_" + Guid.NewGuid().ToString("N");
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

            Guid tenant = Guid.NewGuid(), manager = Guid.NewGuid(), rep = Guid.NewGuid();
            await using (var seed = new NpgsqlCommand("""
                INSERT INTO tenants(id,name,code) VALUES (@tenant,'A','a');
                INSERT INTO users(id,tenant_id,email) VALUES (@manager,@tenant,'manager@a.test'),(@rep,@tenant,'Rep@A.test');
                """, connection))
            {
                seed.Parameters.AddWithValue("tenant", tenant);
                seed.Parameters.AddWithValue("manager", manager);
                seed.Parameters.AddWithValue("rep", rep);
                await seed.ExecuteNonQueryAsync();
            }
            // An older doctor code D-0007 and a manually made place "Erbil" that the file spells with Arabic letters differently is not needed: the same spelling is enough here.
            await InTenant(connection, tenant, $"""
                INSERT INTO customers(id,tenant_id,type,code,name,status,owner_user_id) VALUES (gen_random_uuid(),'{tenant}','Doctor','D-0007','Dr. Old','no_location','{manager}');
                INSERT INTO places(id,tenant_id,parent_id,kind,name) VALUES (gen_random_uuid(),'{tenant}',NULL,'city','Erbil');
                """);

            var store = new PostgresDoctorImportStore(dataSource);
            var t = new TenantId(tenant);
            var users = await store.FindUsersAsync(t, ["rep@a.test", "nobody@a.test"], CancellationToken.None);
            Assert.Equal(rep, Assert.Single(users).Value.UserId);   // found by lower-case e-mail

            DoctorToImport Row(int n, DoctorImportRow data, Guid? owner = null) => new(n, data, owner, null);
            var file = new List<DoctorToImport>
            {
                Row(1, new DoctorImportRow("Dr. Ali", ClinicName: "Noor", Specialty: "neurology", Cooperates: true, Dealer: false, CustomerClass: "A", Phone: "0750 111 2233", City: "Erbil",
                    Neighbourhood: "Ankawa", Street: "Main", Latitude: 36.2201, Longitude: 43.9924, Notes: "first"), rep),
                Row(2, new DoctorImportRow("Dr. Zana", City: "Erbil", Neighbourhood: "Ankawa")),
                Row(3, new DoctorImportRow("Dr. Nora")),
            };

            var dry = await store.ImportAsync(t, new UserId(manager), file, dryRun: true, CancellationToken.None);
            Assert.Equal((3, 0), dry);
            Assert.Equal("1", await InTenant(connection, tenant, "SELECT count(*) FROM customers"));   // the dry run left nothing
            Assert.Equal("1", await InTenant(connection, tenant, "SELECT count(*) FROM places"));

            Assert.Equal((3, 0), await store.ImportAsync(t, new UserId(manager), file, dryRun: false, CancellationToken.None));
            Assert.Equal("D-0008,D-0009,D-0010", await InTenant(connection, tenant, "SELECT string_agg(code, ',' ORDER BY code) FROM customers WHERE code <> 'D-0007'"));
            Assert.Equal("3", await InTenant(connection, tenant, "SELECT count(*) FROM places"));   // Erbil existed; Ankawa and Main were added once for two doctors
            Assert.Equal($"Doctor|{rep}|located|neurology|A|true|false|Erbil / Ankawa / Main|manual", await InTenant(connection, tenant, """
                SELECT c.type || '|' || c.owner_user_id || '|' || c.status || '|' || c.specialty || '|' || c.customer_class || '|' || c.cooperates || '|' || c.is_dealer || '|' ||
                       (SELECT string_agg(a.name, ' / ' ORDER BY a.depth DESC) FROM (WITH RECURSIVE chain(id, parent_id, name, depth) AS (
                            SELECT p.id, p.parent_id, p.name, 0 FROM places p WHERE p.id = c.place_id
                            UNION ALL SELECT p.id, p.parent_id, p.name, k.depth + 1 FROM places p JOIN chain k ON p.id = k.parent_id) SELECT * FROM chain) a) || '|' || c.location_source
                FROM customers c WHERE c.name = 'Dr. Ali'
                """));
            Assert.Equal($"no_location|{manager}", await InTenant(connection, tenant, "SELECT status || '|' || owner_user_id FROM customers WHERE name = 'Dr. Nora'"));   // no visitor: the caller owns it
            Assert.Equal("3", await InTenant(connection, tenant, "SELECT count(*) FROM audit_logs WHERE action = 'customer.created'"));
            Assert.Equal("1", await InTenant(connection, tenant, "SELECT count(*) FROM audit_logs WHERE action = 'customers.doctors_imported'"));

            // The same file again: the doctors (same name and phone) are skipped.
            Assert.Equal((0, 3), await store.ImportAsync(t, new UserId(manager), file, dryRun: false, CancellationToken.None));
            Assert.Equal("4", await InTenant(connection, tenant, "SELECT count(*) FROM customers"));
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
