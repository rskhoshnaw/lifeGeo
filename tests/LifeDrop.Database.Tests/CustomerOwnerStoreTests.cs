using LifeDrop.Application.Customers;
using LifeDrop.Application.Roles;
using LifeDrop.Domain.Common;
using LifeDrop.Infrastructure.Customers;
using Npgsql;
using Xunit;

namespace LifeDrop.Database.Tests;

// Plan step 5.28 against every migration in an isolated schema: the list shows who added a customer and its owner, filters by them, by the day added (Iraq time),
// specialty, class, position and dealer; and a manager gives customers to another user (ticked ones or everything a user owns), inside the caller's scope only.
public class CustomerOwnerStoreTests
{
    [Fact]
    public async Task Lists_filters_and_hands_over_customers_inside_scope_with_audit()
    {
        var url = Environment.GetEnvironmentVariable("TEST_DATABASE_URL")
            ?? throw new InvalidOperationException("TEST_DATABASE_URL must name an isolated lifedrop_test_* database.");
        var builder = new NpgsqlConnectionStringBuilder(url);
        if (builder.Database is null || !builder.Database.StartsWith("lifedrop_test_", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Refusing to test a database outside lifedrop_test_*.");

        var schema = "custowner_test_" + Guid.NewGuid().ToString("N");
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

            Guid tenant = Guid.NewGuid(), other = Guid.NewGuid(), manager = Guid.NewGuid(), ann = Guid.NewGuid(), bob = Guid.NewGuid(), gone = Guid.NewGuid();
            Guid regionA = Guid.NewGuid(), regionB = Guid.NewGuid(), teamA = Guid.NewGuid();
            Guid c1 = Guid.NewGuid(), c2 = Guid.NewGuid(), c3 = Guid.NewGuid(), c4 = Guid.NewGuid(), c5 = Guid.NewGuid(), foreign = Guid.NewGuid();
            await using (var seed = new NpgsqlCommand("""
                INSERT INTO tenants(id,name,code) VALUES (@tenant,'A','a'),(@other,'B','b');
                INSERT INTO users(id,tenant_id,email,is_active) VALUES (@manager,@tenant,'manager@x.com',true),(@ann,@tenant,'ann@x.com',true),(@bob,@tenant,'bob@x.com',true),(@gone,@tenant,'gone@x.com',false);
                """, connection))
            {
                seed.Parameters.AddWithValue("tenant", tenant);
                seed.Parameters.AddWithValue("other", other);
                seed.Parameters.AddWithValue("manager", manager);
                seed.Parameters.AddWithValue("ann", ann);
                seed.Parameters.AddWithValue("bob", bob);
                seed.Parameters.AddWithValue("gone", gone);
                await seed.ExecuteNonQueryAsync();
            }
            await InTenant(connection, tenant, $"""
                INSERT INTO regions(id,tenant_id,name) VALUES ('{regionA}','{tenant}','A'),('{regionB}','{tenant}','B');
                INSERT INTO teams(id,tenant_id,name,region_id) VALUES ('{teamA}','{tenant}','Team A','{regionA}');
                INSERT INTO team_members(team_id,tenant_id,user_id) VALUES ('{teamA}','{tenant}','{bob}');
                INSERT INTO customers(id,tenant_id,type,code,name,owner_user_id,created_by,created_at,specialty,customer_class,is_dealer,region_id,geom,status) VALUES
                  ('{c1}','{tenant}','Doctor','O-1','Dr One','{ann}','{ann}','2026-11-01 21:30:00+00','orthopedics','A',true,'{regionB}',ST_SetSRID(ST_MakePoint(44,36),4326)::geography,'located'),
                  ('{c2}','{tenant}','Doctor','O-2','Dr Two','{ann}','{manager}','2026-11-03 10:00:00+00','neurology','B',NULL,'{regionB}',NULL,'no_location'),
                  ('{c3}','{tenant}','Pharmacy','O-3','Shifa','{ann}','{ann}','2026-11-05 10:00:00+00',NULL,NULL,NULL,'{regionB}',NULL,'no_location'),
                  ('{c4}','{tenant}','Doctor','O-4','Dr Four','{bob}','{bob}','2026-11-06 10:00:00+00','orthopedics','A',false,'{regionA}',NULL,'no_location'),
                  ('{c5}','{tenant}','Doctor','O-5','Dr Archived','{ann}','{ann}','2026-11-02 10:00:00+00',NULL,NULL,NULL,'{regionB}',NULL,'no_location');
                UPDATE customers SET archived_at = now() WHERE id = '{c5}';
                """);
            await InTenant(connection, other, $"INSERT INTO customers(id,tenant_id,type,code,name) VALUES ('{foreign}','{other}','Doctor','O-9','Foreign')");

            var store = new PostgresCustomerStore(dataSource);
            var owners = new PostgresCustomerOwnerStore(dataSource);
            var t = new TenantId(tenant);
            var whole = AccessScope.WholeTenant(new UserId(manager));
            async Task<string> Codes(CustomerFilter filter) =>
                string.Join(",", (await store.FilterAsync(t, whole, 50, null, null, null, filter, CancellationToken.None)).Select(c => c.Code).OrderBy(c => c, StringComparer.Ordinal));

            // The list says who added the customer and who owns it now.
            var listed = (await store.FilterAsync(t, whole, 50, null, null, null, new CustomerFilter("Doctor", null, null, OwnerId: ann, CreatedBy: manager), CancellationToken.None)).Single();
            Assert.Equal(("O-2", ann, "ann@x.com", manager, "manager@x.com"), (listed.Code, listed.OwnerUserId, listed.OwnerEmail, listed.CreatedBy, listed.CreatedByEmail));

            Assert.Equal("O-1,O-2,O-3", await Codes(new CustomerFilter(null, null, null, OwnerId: ann)));                           // archived O-5 never shows
            Assert.Equal("O-1,O-3", await Codes(new CustomerFilter(null, null, null, CreatedBy: ann)));
            Assert.Equal("O-1,O-2", await Codes(new CustomerFilter(null, null, null, CreatedTo: new DateOnly(2026, 11, 4))));       // 2026-11-01 21:30 UTC is already 2026-11-02 in Iraq
            Assert.Equal("O-3,O-4", await Codes(new CustomerFilter(null, null, null, CreatedFrom: new DateOnly(2026, 11, 5))));
            Assert.Equal("O-2", await Codes(new CustomerFilter(null, null, null, CreatedFrom: new DateOnly(2026, 11, 3), CreatedTo: new DateOnly(2026, 11, 3))));
            Assert.Equal("", await Codes(new CustomerFilter(null, null, null, CreatedTo: new DateOnly(2026, 11, 1))));              // the Iraq day, not the UTC day
            Assert.Equal("O-1,O-4", await Codes(new CustomerFilter(null, null, null, Specialty: "orthopedics")));
            Assert.Equal("O-1,O-4", await Codes(new CustomerFilter(null, null, null, CustomerClass: "A")));
            Assert.Equal("O-1", await Codes(new CustomerFilter(null, null, null, HasLocation: true)));
            Assert.Equal("O-2,O-3,O-4", await Codes(new CustomerFilter(null, null, null, HasLocation: false)));
            Assert.Equal("O-1", await Codes(new CustomerFilter(null, null, null, Dealer: true)));
            Assert.Equal("O-2,O-3,O-4", await Codes(new CustomerFilter(null, null, null, Dealer: false)));                         // not set counts as "no"
            Assert.Equal("O-1", await Codes(new CustomerFilter("Doctor", null, null, OwnerId: ann, CreatedFrom: new DateOnly(2026, 11, 1), CreatedTo: new DateOnly(2026, 11, 2), HasLocation: true)));   // combined

            // Hand over: Bob is in region A, active; he may hold doctors. O-3 is a pharmacy and O-9 is another company's.
            var bobInfo = new NewOwnerInfo(bob, regionA);
            Assert.Equal(bobInfo, await owners.FindOwnerAsync(t, bob, CancellationToken.None));
            Assert.Null(await owners.FindOwnerAsync(t, gone, CancellationToken.None));                                               // inactive
            Assert.Null(await owners.FindOwnerAsync(t, Guid.NewGuid(), CancellationToken.None));
            var bobCanSee = new AccessScope(DataScope.Own, new UserId(bob), regionA);
            var noDoctors = new AccessScope(DataScope.Own, new UserId(bob), regionA, HideDoctors: true);

            // A user who may not hold doctors only gets the other types; the foreign customer and the archived one never move.
            Assert.Equal(1, await owners.SetAsync(t, new UserId(manager), whole, noDoctors, bobInfo, [c1, c2, c3, c5, foreign], null, CancellationToken.None));
            Assert.Equal("O-3", await Codes(new CustomerFilter(null, null, null, OwnerId: bob, CreatedBy: ann)));
            Assert.Equal(regionA.ToString(), await InTenant(connection, tenant, $"SELECT region_id::text FROM customers WHERE id = '{c3}'"));   // the customer takes the new owner's region
            Assert.Equal("1", await InTenant(connection, tenant, $"SELECT count(*) FROM audit_logs WHERE action = 'customer.owner_changed' AND entity_id = '{c3}' AND actor_id = '{manager}'"));

            // Everything a user owns, in one call (c1 and c2; c5 is archived and stays).
            Assert.Equal(2, await owners.SetAsync(t, new UserId(manager), whole, bobCanSee, bobInfo, null, ann, CancellationToken.None));
            Assert.Equal("O-1,O-2,O-3,O-4", await Codes(new CustomerFilter(null, null, null, OwnerId: bob)));
            Assert.Equal(ann.ToString(), await InTenant(connection, tenant, $"SELECT owner_user_id::text FROM customers WHERE id = '{c5}'"));
            Assert.Equal(ann.ToString(), await InTenant(connection, tenant, $"SELECT created_by::text FROM customers WHERE id = '{c1}'"));      // who added it does not change
            Assert.Equal(0, await owners.SetAsync(t, new UserId(manager), whole, bobCanSee, bobInfo, null, ann, CancellationToken.None));         // nothing left: repeating changes nothing and audits nothing

            // A caller who only sees own customers moves only those; one who sees a region only that region's and own.
            var annInfo = new NewOwnerInfo(ann, null);
            var onlyOwn = new AccessScope(DataScope.Own, new UserId(manager), null);
            Assert.Equal(0, await owners.SetAsync(t, new UserId(manager), onlyOwn, bobCanSee, annInfo, [c1, c4], null, CancellationToken.None));
            var regionScope = new AccessScope(DataScope.Region, new UserId(manager), regionA);
            Assert.Equal(4, await owners.SetAsync(t, new UserId(manager), regionScope, bobCanSee, annInfo, [c1, c2, c3, c4], null, CancellationToken.None));   // all four are in region A now
            Assert.Equal("O-1,O-2,O-3,O-4", await Codes(new CustomerFilter(null, null, null, OwnerId: ann)));
            Assert.Equal(regionA.ToString(), await InTenant(connection, tenant, $"SELECT region_id::text FROM customers WHERE id = '{c1}'"));   // Ann has no team: the customer keeps its region
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE", setup);
            await drop.ExecuteNonQueryAsync();
        }
    }

    // customers have row level security in the real schema; here only the company is named, as the API does for every statement.
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
