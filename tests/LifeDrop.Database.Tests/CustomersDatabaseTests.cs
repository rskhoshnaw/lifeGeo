using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LifeDrop.Application.Customers;
using LifeDrop.Domain.Common;
using LifeDrop.Infrastructure.Customers;
using Npgsql;
using Xunit;

namespace LifeDrop.Database.Tests;

public class CustomersDatabaseTests
{
    private async Task<string> SetupIsolatedSchemaAsync(string url)
    {
        var builder = new NpgsqlConnectionStringBuilder(url);
        if (builder.Database is null || !builder.Database.StartsWith("lifedrop_test_", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Refusing to test a database outside lifedrop_test_*.");

        var schema = "customer_test_" + Guid.NewGuid().ToString("N");
        await using var setup = new NpgsqlConnection(url);
        await setup.OpenAsync();
        
        await using (var create = new NpgsqlCommand($"CREATE SCHEMA {schema}", setup))
            await create.ExecuteNonQueryAsync();

        builder.SearchPath = schema;
        var connectionString = builder.ConnectionString;

        // Run migrations
        await using var migrationConnection = new NpgsqlConnection(connectionString);
        await migrationConnection.OpenAsync();

        var m1 = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "0001_role_assignment.sql"));
        var m2 = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "0002_login.sql"));
        var m3 = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "0003_sessions_devices.sql"));
        var m4 = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "0004_customers.sql"));

        await using (var cmd = new NpgsqlCommand(m1 + m2 + m3 + m4, migrationConnection))
        {
            // Set the search path explicitly for the migration batch
            cmd.CommandText = $"SET search_path TO {schema}, public; {cmd.CommandText}";
            await cmd.ExecuteNonQueryAsync();
        }

        return connectionString;
    }

    [SkippableFact]
    public async Task PostgresCustomerStore_EnforcesIsolationAndConstraints()
    {
        var url = Environment.GetEnvironmentVariable("TEST_DATABASE_URL");
        if (string.IsNullOrWhiteSpace(url)) { throw new SkipException("TEST_DATABASE_URL not set"); }

        var connectionString = await SetupIsolatedSchemaAsync(url);
        
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var store = new PostgresCustomerStore(dataSource);

        var tenantId1 = TenantId.New();
        var tenantId2 = TenantId.New();
        var actorId1 = UserId.New();
        var actorId2 = UserId.New();
        
        // Setup tenants and users
        await using (var conn = await dataSource.OpenConnectionAsync())
        {
            await using var cmd = new NpgsqlCommand("""
                INSERT INTO tenants (id, name, code) VALUES (@t1, 'T1', 'T1'), (@t2, 'T2', 'T2');
                INSERT INTO users (id, tenant_id) VALUES (@a1, @t1), (@a2, @t2);
                """, conn);
            cmd.Parameters.AddWithValue("t1", tenantId1.Value);
            cmd.Parameters.AddWithValue("t2", tenantId2.Value);
            cmd.Parameters.AddWithValue("a1", actorId1.Value);
            cmd.Parameters.AddWithValue("a2", actorId2.Value);
            await cmd.ExecuteNonQueryAsync();
        }

        // Test 1: Create
        var req = new CreateCustomerRequest("Pharmacy", "P001", "Central Pharmacy");
        var custId = CustomerId.New();
        var createResult = await store.CreateAsync(tenantId1, actorId1, custId, req, CancellationToken.None);
        Assert.Equal(CreateCustomerResult.Created, createResult);

        // Test 2: Duplicate Code returns Conflict in same tenant
        var dupResult = await store.CreateAsync(tenantId1, actorId1, CustomerId.New(), req, CancellationToken.None);
        Assert.Equal(CreateCustomerResult.DuplicateCode, dupResult);

        // Test 3: Same Code allowed in different tenant
        var tenant2CustId = CustomerId.New();
        var diffTenantResult = await store.CreateAsync(tenantId2, actorId2, tenant2CustId, req, CancellationToken.None);
        Assert.Equal(CreateCustomerResult.Created, diffTenantResult);

        // Test 4: Get Customer (visibility scoped by tenant)
        var fetched = await store.GetByIdAsync(tenantId1, custId, LifeDrop.Application.Roles.AccessScope.WholeTenant(UserId.New()), CancellationToken.None);
        Assert.NotNull(fetched);
        Assert.Equal("Central Pharmacy", fetched.Name);

        // Test 5: Cross-Tenant Invisibility (Tenant 2 cannot see Tenant 1's customer)
        var crossFetched = await store.GetByIdAsync(tenantId2, custId, LifeDrop.Application.Roles.AccessScope.WholeTenant(UserId.New()), CancellationToken.None);
        Assert.Null(crossFetched);
        
        // Verify audit log
        await using (var conn = await dataSource.OpenConnectionAsync())
        {
            await using var cmd = new NpgsqlCommand("SELECT count(*) FROM audit_logs WHERE action = 'customer.created'", conn);
            var audits = (long)(await cmd.ExecuteScalarAsync() ?? 0L);
            Assert.Equal(2, audits);
        }
    }

    [SkippableFact]
    public async Task PostgresCustomerStore_List_EnforcesPaginationAndIsolation()
    {
        var url = Environment.GetEnvironmentVariable("TEST_DATABASE_URL");
        if (string.IsNullOrWhiteSpace(url)) { throw new SkipException("TEST_DATABASE_URL not set"); }

        var connectionString = await SetupIsolatedSchemaAsync(url);
        
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var store = new PostgresCustomerStore(dataSource);

        var tenantId1 = TenantId.New();
        var tenantId2 = TenantId.New();
        var actorId1 = UserId.New();
        var actorId2 = UserId.New();
        
        await using (var conn = await dataSource.OpenConnectionAsync())
        {
            await using var cmd = new NpgsqlCommand("""
                INSERT INTO tenants (id, name, code) VALUES (@t1, 'T1', 'T1'), (@t2, 'T2', 'T2');
                INSERT INTO users (id, tenant_id) VALUES (@a1, @t1), (@a2, @t2);
                """, conn);
            cmd.Parameters.AddWithValue("t1", tenantId1.Value);
            cmd.Parameters.AddWithValue("t2", tenantId2.Value);
            cmd.Parameters.AddWithValue("a1", actorId1.Value);
            cmd.Parameters.AddWithValue("a2", actorId2.Value);
            await cmd.ExecuteNonQueryAsync();
        }

        // Create 3 customers for tenant 1, 1 for tenant 2
        var req1 = new CreateCustomerRequest("P", "C1", "A");
        var req2 = new CreateCustomerRequest("P", "C2", "B");
        var req3 = new CreateCustomerRequest("P", "C3", "C");

        await store.CreateAsync(tenantId1, actorId1, CustomerId.New(), req1, CancellationToken.None);
        await store.CreateAsync(tenantId1, actorId1, CustomerId.New(), req2, CancellationToken.None);
        await store.CreateAsync(tenantId1, actorId1, CustomerId.New(), req3, CancellationToken.None);

        await store.CreateAsync(tenantId2, actorId2, CustomerId.New(), req1, CancellationToken.None);

        // Test 1: List all for tenant 1 (should return 3)
        var allT1 = await store.ListAsync(tenantId1, LifeDrop.Application.Roles.AccessScope.WholeTenant(UserId.New()), 10, null, null, CancellationToken.None);
        Assert.Equal(3, allT1.Count);

        // Test 2: Pagination (Limit 2)
        var page1 = await store.ListAsync(tenantId1, LifeDrop.Application.Roles.AccessScope.WholeTenant(UserId.New()), 2, null, null, CancellationToken.None);
        Assert.Equal(2, page1.Count);

        // Test 3: Next page via cursor
        var lastItem = page1[page1.Count - 1];
        var page2 = await store.ListAsync(tenantId1, LifeDrop.Application.Roles.AccessScope.WholeTenant(UserId.New()), 2, lastItem.CreatedAt, lastItem.Id, CancellationToken.None);
        Assert.Single(page2);

        // Verify pages concatenate exactly to the full ordered result
        var combined = new System.Collections.Generic.List<CustomerDto>(page1);
        combined.AddRange(page2);
        
        for (int i = 0; i < allT1.Count; i++)
        {
            Assert.Equal(allT1[i].Id, combined[i].Id);
        }

        // Test 4: Tenant isolation (Tenant 2 only sees 1)
        var allT2 = await store.ListAsync(tenantId2, LifeDrop.Application.Roles.AccessScope.WholeTenant(UserId.New()), 10, null, null, CancellationToken.None);
        Assert.Single(allT2);
        Assert.Equal("C1", allT2[0].Code);
    }
}







