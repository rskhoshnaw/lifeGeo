using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using LifeDrop.Application.Customers;
using Npgsql;
using Xunit;

namespace LifeDrop.Database.Tests;

public class CustomerApiIntegrationTests
{
    private class FakeSessionStore : LifeDrop.Application.Auth.ISessionStore
    {
        public Task<LifeDrop.Application.Auth.SessionTokens> IssueAsync(LifeDrop.Application.Auth.LoginIdentity identity, Guid? deviceId, System.Threading.CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<LifeDrop.Application.Auth.SessionTokens?> RotateAsync(LifeDrop.Application.Auth.RefreshRequest request, System.Threading.CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<bool> IsAccessActiveAsync(LifeDrop.Application.Auth.SessionIdentity identity, System.Threading.CancellationToken cancellationToken) => Task.FromResult(true);
        public Task RevokeFamilyAsync(LifeDrop.Application.Auth.SessionIdentity identity, System.Threading.CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<int> RevokeAllForUserAsync(LifeDrop.Domain.Common.TenantId tenantId, LifeDrop.Domain.Common.UserId actorId, LifeDrop.Domain.Common.UserId targetUserId, System.Threading.CancellationToken cancellationToken) => throw new NotImplementedException();
    }

    private class IsolatedSchema : IAsyncDisposable
    {
        public string ConnectionString { get; }
        public string SchemaName { get; }
        private readonly string _adminUrl;

        public IsolatedSchema(string connectionString, string schemaName, string adminUrl)
        {
            ConnectionString = connectionString;
            SchemaName = schemaName;
            _adminUrl = adminUrl;
        }

        public async ValueTask DisposeAsync()
        {
            await using var conn = new NpgsqlConnection(_adminUrl);
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand($"DROP SCHEMA IF EXISTS {SchemaName} CASCADE;", conn);
            try
            {
                await cmd.ExecuteNonQueryAsync();
            }
            catch (Exception ex)
            {
                throw new Exception($"FATAL: Failed to clean up exact generated schema {SchemaName}", ex);
            }
        }
    }

    private async Task<IsolatedSchema> SetupIsolatedSchemaAsync(string url, string overrideMigration = null)
    {
        var builder = new NpgsqlConnectionStringBuilder(url);
        var dbName = builder.Database;
        if (string.IsNullOrWhiteSpace(dbName) || !dbName.StartsWith("lifedrop_test_"))
            throw new Exception($"FATAL: Database name '{dbName}' must start with 'lifedrop_test_' for safety.");

        var schema = "customer_api_" + Guid.NewGuid().ToString("N");
        await using var setup = new NpgsqlConnection(url);
        await setup.OpenAsync();
        
        await using (var create = new NpgsqlCommand($"CREATE SCHEMA {schema}", setup))
            await create.ExecuteNonQueryAsync();

        builder.SearchPath = $"{schema},public"; // PostGIS types and functions live in public
        var connectionString = builder.ConnectionString;

        try
        {
            await using var migrationConnection = new NpgsqlConnection(connectionString);
            await migrationConnection.OpenAsync();

            string migrations;
            if (overrideMigration != null)
            {
                migrations = overrideMigration;
            }
            else
            {
                var m1 = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "0001_role_assignment.sql"));
                var m2 = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "0002_login.sql"));
                var m3 = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "0003_sessions_devices.sql"));
                var m4 = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "0004_customers.sql"));
                var m5 = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "0005_dev_admin_customer_permissions.sql"));
                var m6 = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "0006_visits.sql"));
                var m7 = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "0007_regions_teams.sql"));
                var m8 = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "0008_role_data_scope.sql"));
                var m9 = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "0009_default_roles.sql"));
                var m10 = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "0010_visit_planning.sql"));
                var m11 = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "0011_visit_cancel_permission.sql"));
                var m12 = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "0012_customer_location.sql"));
                var m13 = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "0013_visit_reports.sql"));
                var m14 = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "0014_visit_location_events.sql"));
                var m15 = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "0015_visit_status_history.sql"));
                migrations = m1 + m2 + m3 + m4 + m5 + m6 + m7 + m8 + m9 + m10 + m11 + m12 + m13 + m14 + m15;
            }

            await using (var cmd = new NpgsqlCommand(migrations, migrationConnection))
            {
                cmd.CommandText = $"SET search_path TO {schema}, public; {cmd.CommandText}";
                await cmd.ExecuteNonQueryAsync();
            }

            return new IsolatedSchema(connectionString, schema, url);
        }
        catch (Exception ex)
        {
            ex.Data["SchemaName"] = schema;
            Exception cleanupException = null;
            try
            {
                await using var drop = new NpgsqlCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE;", setup);
                await drop.ExecuteNonQueryAsync();
            }
            catch (Exception dropEx)
            {
                cleanupException = dropEx;
            }

            if (cleanupException != null)
                throw new AggregateException(ex, new Exception($"Failed to clean up schema {schema} after setup failed", cleanupException));
            
            throw; // Preserve original failure
        }
    }

    private async Task VerifyRoleAsync(string url, bool expectElevated)
    {
        await using var conn = new NpgsqlConnection(url);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT rolsuper, rolbypassrls FROM pg_roles WHERE rolname = current_user", conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            var isSuper = reader.GetBoolean(0);
            var isBypass = reader.GetBoolean(1);
            if (expectElevated && !isSuper && !isBypass)
                throw new Exception("Elevated test requires a SUPERUSER or BYPASSRLS role.");
            if (!expectElevated && (isSuper || isBypass))
                throw new Exception("Normal customer test requires a NON-superuser, NON-bypassrls role.");
        }
        else
        {
            throw new Exception("Could not verify role privileges.");
        }
    }

    private string GenerateToken(Guid tenantId, Guid userId, string signingKey, string issuer, string audience, Guid? sessionFamily = null, Guid? deviceId = null)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, userId.ToString()),
            new Claim("tenant_id", tenantId.ToString()),
            new Claim("session_family", (sessionFamily ?? Guid.NewGuid()).ToString())
        }.Concat(deviceId is null ? Array.Empty<Claim>() : new[] { new Claim("device_id", deviceId.Value.ToString()) }).ToArray();
        var token = new JwtSecurityToken(
            issuer: issuer,
            audience: audience,
            claims: claims,
            expires: DateTime.UtcNow.AddHours(1),
            signingCredentials: creds
        );
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private string GetRequiredEnvVar(string key)
    {
        var val = Environment.GetEnvironmentVariable(key);
        if (string.IsNullOrWhiteSpace(val)) throw new SkipException($"{key} not set. Skipping test.");
        return val;
    }

    [SkippableFact]
    public async Task CustomerApi_VerifiesPermissionsAndIsolation()
    {
        var url = GetRequiredEnvVar("TEST_DATABASE_URL");
        await VerifyRoleAsync(url, expectElevated: false);
        
        await using var schemaBlock = await SetupIsolatedSchemaAsync(url);
        
        await using var factory = new WebApplicationFactory<LifeDrop.API.Program>()
            .WithWebHostBuilder(webBuilder =>
            {
                webBuilder.UseSetting("ConnectionStrings:LifeDrop", schemaBlock.ConnectionString);
                TestHostSettings.UseTestJwt(webBuilder);
                webBuilder.ConfigureTestServices(services =>
                {
                    var descriptor = System.Linq.Enumerable.SingleOrDefault(services, d => d.ServiceType == typeof(LifeDrop.Application.Auth.ISessionStore));
                    if (descriptor != null) services.Remove(descriptor);
                    services.AddScoped<LifeDrop.Application.Auth.ISessionStore, FakeSessionStore>();
                });
            });

        var conf = factory.Services.GetRequiredService<IConfiguration>();
        var signingKey = conf["Jwt:SigningKey"];
        var issuer = conf["Jwt:Issuer"];
        var audience = conf["Jwt:Audience"];

        var client = factory.CreateClient();

        var tenantId1 = Guid.NewGuid();
        var tenantId2 = Guid.NewGuid();
        var adminId = Guid.NewGuid();
        var deniedUserId = Guid.NewGuid();
        var adminRole = Guid.NewGuid();
        var emptyRole = Guid.NewGuid();
        
        await using (var conn = new NpgsqlConnection(schemaBlock.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(@"
                INSERT INTO tenants (id, name, code) VALUES (@t1, 'T1', 'T1'), (@t2, 'T2', 'T2');
                INSERT INTO users (id, tenant_id) VALUES (@a1, @t1), (@u1, @t1);
                INSERT INTO roles (id, tenant_id, name) VALUES (@r1, @t1, 'DevAdmin'), (@r2, @t1, 'EmptyRole');
                INSERT INTO role_permissions (tenant_id, role_id, permission_key) VALUES (@t1, @r1, 'customers.read'), (@t1, @r1, 'customers.write');
                INSERT INTO user_roles (tenant_id, user_id, role_id) VALUES (@t1, @a1, @r1), (@t1, @u1, @r2);
            ", conn);
            cmd.Parameters.AddWithValue("t1", tenantId1);
            cmd.Parameters.AddWithValue("t2", tenantId2);
            cmd.Parameters.AddWithValue("a1", adminId);
            cmd.Parameters.AddWithValue("u1", deniedUserId);
            cmd.Parameters.AddWithValue("r1", adminRole);
            cmd.Parameters.AddWithValue("r2", emptyRole);
            await cmd.ExecuteNonQueryAsync();
        }

        var adminToken = GenerateToken(tenantId1, adminId, signingKey!, issuer!, audience!);
        var deniedToken = GenerateToken(tenantId1, deniedUserId, signingKey!, issuer!, audience!);

        var req = new CreateCustomerRequest("Clinic", "C001", "Test Clinic");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", deniedToken);
        var respDenied = await client.PostAsJsonAsync("/customers", req);
        Assert.Equal(HttpStatusCode.Forbidden, respDenied.StatusCode);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        var respAllowed = await client.PostAsJsonAsync("/customers", req);
        Assert.Equal(HttpStatusCode.Created, respAllowed.StatusCode);
        var location = respAllowed.Headers.Location;
        Assert.NotNull(location);
        var createdId = location!.ToString().Split('/')[2];

        var respDup = await client.PostAsJsonAsync("/customers", req);
        Assert.Equal(HttpStatusCode.Conflict, respDup.StatusCode);

        var respGet = await client.GetAsync($"/customers/{createdId}");
        Assert.Equal(HttpStatusCode.OK, respGet.StatusCode);
        var cust = await respGet.Content.ReadFromJsonAsync<CustomerDto>();
        Assert.Equal("Test Clinic", cust!.Name);

        var tenant2Admin = Guid.NewGuid();
        var t2Role = Guid.NewGuid();
        await using (var conn = new NpgsqlConnection(schemaBlock.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(@"
                INSERT INTO users (id, tenant_id) VALUES (@a2, @t2);
                INSERT INTO roles (id, tenant_id, name) VALUES (@r2, @t2, 'DevAdmin');
                INSERT INTO role_permissions (tenant_id, role_id, permission_key) VALUES (@t2, @r2, 'customers.read');
                INSERT INTO user_roles (tenant_id, user_id, role_id) VALUES (@t2, @a2, @r2);
            ", conn);
            cmd.Parameters.AddWithValue("t2", tenantId2);
            cmd.Parameters.AddWithValue("a2", tenant2Admin);
            cmd.Parameters.AddWithValue("r2", t2Role);
            await cmd.ExecuteNonQueryAsync();
        }
        var t2AdminToken = GenerateToken(tenantId2, tenant2Admin, signingKey!, issuer!, audience!);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", t2AdminToken);
        
        var respCrossGet = await client.GetAsync($"/customers/{createdId}");
        Assert.Equal(HttpStatusCode.NotFound, respCrossGet.StatusCode);

        // --- NEW GET /customers TEST BLOCK ---
        // 1. Unauthenticated -> 401
        client.DefaultRequestHeaders.Authorization = null;
        var listUnauth = await client.GetAsync("/customers");
        Assert.Equal(HttpStatusCode.Unauthorized, listUnauth.StatusCode);

        // 2. User without customers.read -> 403
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", deniedToken);
        var listDenied = await client.GetAsync("/customers");
        Assert.Equal(HttpStatusCode.Forbidden, listDenied.StatusCode);

        // 3. Authorized tenant-1 admin -> 200 OK + returns the created customer
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        var listT1 = await client.GetAsync("/customers");
        Assert.Equal(HttpStatusCode.OK, listT1.StatusCode);
        var t1Customers = await listT1.Content.ReadFromJsonAsync<System.Collections.Generic.List<CustomerDto>>();
        Assert.NotNull(t1Customers);
        Assert.Single(t1Customers);
        Assert.Equal(createdId, t1Customers[0].Id.ToString());

        // 4. Invalid limit=0 -> 400 Bad Request
        var listInvalid = await client.GetAsync("/customers?limit=0");
        Assert.Equal(HttpStatusCode.BadRequest, listInvalid.StatusCode);

        // 5. Next page using the returned item's createdAt and id -> 200 OK + empty list
        var cursorDate = Uri.EscapeDataString(t1Customers[0].CreatedAt.ToString("O"));
        var cursorIdStr = t1Customers[0].Id.ToString();
        var listNext = await client.GetAsync($"/customers?cursorCreatedAt={cursorDate}&cursorId={cursorIdStr}");
        Assert.Equal(HttpStatusCode.OK, listNext.StatusCode);
        var nextCustomers = await listNext.Content.ReadFromJsonAsync<System.Collections.Generic.List<CustomerDto>>();
        Assert.NotNull(nextCustomers);
        Assert.Empty(nextCustomers);

        // 6. Authorized tenant-2 admin -> 200 OK + empty list
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", t2AdminToken);
        var listT2 = await client.GetAsync("/customers");
        Assert.Equal(HttpStatusCode.OK, listT2.StatusCode);
        var t2Customers = await listT2.Content.ReadFromJsonAsync<System.Collections.Generic.List<CustomerDto>>();
        Assert.NotNull(t2Customers);
        Assert.Empty(t2Customers);
        // --- END NEW TEST BLOCK ---


        await using (var conn = new NpgsqlConnection(schemaBlock.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand("SELECT count(*) FROM audit_logs WHERE action = 'customer.created'", conn);
            var count = (long)(await cmd.ExecuteScalarAsync() ?? 0L);
            Assert.Equal(1, count);
        }
    }

    [SkippableFact]
    public async Task VisitApi_VerifiesPermissionsAndIsolation()
    {
        var url = GetRequiredEnvVar("TEST_DATABASE_URL");
        await VerifyRoleAsync(url, expectElevated: false);
        
        await using var schemaBlock = await SetupIsolatedSchemaAsync(url);
        
        await using var factory = new WebApplicationFactory<LifeDrop.API.Program>()
            .WithWebHostBuilder(webBuilder =>
            {
                webBuilder.UseSetting("ConnectionStrings:LifeDrop", schemaBlock.ConnectionString);
                TestHostSettings.UseTestJwt(webBuilder);
                webBuilder.ConfigureTestServices(services =>
                {
                    var descriptor = System.Linq.Enumerable.SingleOrDefault(services, d => d.ServiceType == typeof(LifeDrop.Application.Auth.ISessionStore));
                    if (descriptor != null) services.Remove(descriptor);
                    services.AddScoped<LifeDrop.Application.Auth.ISessionStore, FakeSessionStore>();
                });
            });

        var conf = factory.Services.GetRequiredService<IConfiguration>();
        var signingKey = conf["Jwt:SigningKey"];
        var issuer = conf["Jwt:Issuer"];
        var audience = conf["Jwt:Audience"];

        var client = factory.CreateClient();

        var tenantId1 = Guid.NewGuid();
        var tenantId2 = Guid.NewGuid();
        var adminId = Guid.NewGuid();
        var deniedUserId = Guid.NewGuid();
        var customerId = Guid.NewGuid();
        
        await using (var conn = new NpgsqlConnection(schemaBlock.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(@"
                INSERT INTO tenants (id, name, code) VALUES (@t1, 'T1', 'T1'), (@t2, 'T2', 'T2');
                INSERT INTO users (id, tenant_id) VALUES (@a1, @t1), (@u1, @t1);
                SELECT set_config('app.current_tenant', @t1::text, false);
                INSERT INTO customers (id, tenant_id, type, code, name) VALUES (@c1, @t1, 'Clinic', 'C001', 'Test Clinic');
                INSERT INTO roles (id, tenant_id, name) VALUES (@r1, @t1, 'DevAdmin'), (@r2, @t1, 'EmptyRole');
                INSERT INTO role_permissions (tenant_id, role_id, permission_key) VALUES (@t1, @r1, 'visits.write');
                INSERT INTO user_roles (tenant_id, user_id, role_id) VALUES (@t1, @a1, @r1), (@t1, @u1, @r2);
            ", conn);
            cmd.Parameters.AddWithValue("t1", tenantId1);
            cmd.Parameters.AddWithValue("t2", tenantId2);
            cmd.Parameters.AddWithValue("a1", adminId);
            cmd.Parameters.AddWithValue("u1", deniedUserId);
            cmd.Parameters.AddWithValue("c1", customerId);
            cmd.Parameters.AddWithValue("r1", Guid.NewGuid());
            cmd.Parameters.AddWithValue("r2", Guid.NewGuid());
            await cmd.ExecuteNonQueryAsync();
        }

        var adminToken = GenerateToken(tenantId1, adminId, signingKey!, issuer!, audience!);
        var deniedToken = GenerateToken(tenantId1, deniedUserId, signingKey!, issuer!, audience!);

        var req = new LifeDrop.Application.Visits.CreateVisitRequest(adminId, customerId, new DateOnly(2026, 10, 3));

        // 1. Missing permission
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", deniedToken);
        var respDenied = await client.PostAsJsonAsync("/visits", req);
        Assert.Equal(HttpStatusCode.Forbidden, respDenied.StatusCode);

        // 2. Success
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        var respAllowed = await client.PostAsJsonAsync("/visits", req);
        Assert.Equal(HttpStatusCode.Created, respAllowed.StatusCode);

        // 4. Test GET /visits for the scheduled date
        var respGet = await client.GetAsync("/visits?scheduleDate=2026-10-03");
        Assert.Equal(HttpStatusCode.OK, respGet.StatusCode);
        var visits = await respGet.Content.ReadFromJsonAsync<System.Collections.Generic.List<LifeDrop.Application.Visits.VisitDto>>();
        Assert.NotNull(visits);
        Assert.Single(visits);
        Assert.Equal(customerId, visits[0].CustomerId);
        
        // 5. Test GET /visits for a day with no visits
        var respGetEmpty = await client.GetAsync("/visits?scheduleDate=2026-10-04");
        Assert.Equal(HttpStatusCode.OK, respGetEmpty.StatusCode);
        var emptyVisits = await respGetEmpty.Content.ReadFromJsonAsync<System.Collections.Generic.List<LifeDrop.Application.Visits.VisitDto>>();
        Assert.NotNull(emptyVisits);
        Assert.Empty(emptyVisits);

        // 3. Cross-tenant rejection
        var t2AdminId = Guid.NewGuid();
        var t2AdminToken = GenerateToken(tenantId2, t2AdminId, signingKey!, issuer!, audience!);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", t2AdminToken);
        // Using T1's user and customer in T2 context -> NotFound
        var respCross = await client.PostAsJsonAsync("/visits", req);
        Assert.Equal(HttpStatusCode.Forbidden, respCross.StatusCode); // Wait, no visits.write in T2!

        // Give T2 admin visits.write to test NotFound properly
        await using (var conn = new NpgsqlConnection(schemaBlock.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(@"
                INSERT INTO users (id, tenant_id) VALUES (@u, @t);
                INSERT INTO roles (id, tenant_id, name) VALUES (@r, @t, 'T2Admin');
                INSERT INTO role_permissions (tenant_id, role_id, permission_key) VALUES (@t, @r, 'visits.write');
                INSERT INTO user_roles (tenant_id, user_id, role_id) VALUES (@t, @u, @r);
            ", conn);
            cmd.Parameters.AddWithValue("t", tenantId2);
            cmd.Parameters.AddWithValue("r", Guid.NewGuid());
            cmd.Parameters.AddWithValue("u", t2AdminId);
            await cmd.ExecuteNonQueryAsync();
        }

        var respCrossAuth = await client.PostAsJsonAsync("/visits", req);
        Assert.Equal(HttpStatusCode.NotFound, respCrossAuth.StatusCode);

        // 6. Cross-user read isolation (same tenant, different user)
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", deniedToken);
        var respGetDenied = await client.GetAsync("/visits?scheduleDate=2026-10-03");
        Assert.Equal(HttpStatusCode.OK, respGetDenied.StatusCode);
        var deniedVisits = await respGetDenied.Content.ReadFromJsonAsync<System.Collections.Generic.List<LifeDrop.Application.Visits.VisitDto>>();
        Assert.NotNull(deniedVisits);
        Assert.Empty(deniedVisits);

        // 7. Cross-tenant read isolation
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", t2AdminToken);
        var respGetT2 = await client.GetAsync("/visits?scheduleDate=2026-10-03");
        Assert.Equal(HttpStatusCode.OK, respGetT2.StatusCode);
        var t2Visits = await respGetT2.Content.ReadFromJsonAsync<System.Collections.Generic.List<LifeDrop.Application.Visits.VisitDto>>();
        Assert.NotNull(t2Visits);
        Assert.Empty(t2Visits);
    }

    [SkippableFact]
    public async Task VisitApi_EnRoute_EnforcesPermissionsAndTransitions()
    {
        var url = GetRequiredEnvVar("TEST_DATABASE_URL");
        await VerifyRoleAsync(url, expectElevated: false);
        
        await using var schemaBlock = await SetupIsolatedSchemaAsync(url);
        
        await using var factory = new WebApplicationFactory<LifeDrop.API.Program>()
            .WithWebHostBuilder(webBuilder =>
            {
                webBuilder.UseSetting("ConnectionStrings:LifeDrop", schemaBlock.ConnectionString);
                TestHostSettings.UseTestJwt(webBuilder);
                webBuilder.ConfigureTestServices(services =>
                {
                    var descriptor = System.Linq.Enumerable.SingleOrDefault(services, d => d.ServiceType == typeof(LifeDrop.Application.Auth.ISessionStore));
                    if (descriptor != null) services.Remove(descriptor);
                    services.AddScoped<LifeDrop.Application.Auth.ISessionStore, FakeSessionStore>();
                });
            });

        var conf = factory.Services.GetRequiredService<IConfiguration>();
        var signingKey = conf["Jwt:SigningKey"];
        var issuer = conf["Jwt:Issuer"];
        var audience = conf["Jwt:Audience"];

        var client = factory.CreateClient();

        var tenantId1 = Guid.NewGuid();
        var tenantId2 = Guid.NewGuid();
        var adminId = Guid.NewGuid();
        var anotherUser = Guid.NewGuid();
        var customerId = Guid.NewGuid();
        var visitId = Guid.NewGuid();
        
        await using (var conn = new NpgsqlConnection(schemaBlock.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(@"
                INSERT INTO tenants (id, name, code) VALUES (@t1, 'T1', 'T1'), (@t2, 'T2', 'T2');
                INSERT INTO users (id, tenant_id) VALUES (@a1, @t1), (@u1, @t1);
                SELECT set_config('app.current_tenant', @t1::text, false);
                INSERT INTO customers (id, tenant_id, type, code, name) VALUES (@c1, @t1, 'Clinic', 'C001', 'Test Clinic');
                INSERT INTO visits (id, tenant_id, user_id, customer_id, status, schedule_date, created_at) 
                VALUES (@v1, @t1, @a1, @c1, 'Planned', '2026-10-03', now());
            ", conn);
            cmd.Parameters.AddWithValue("t1", tenantId1);
            cmd.Parameters.AddWithValue("t2", tenantId2);
            cmd.Parameters.AddWithValue("a1", adminId);
            cmd.Parameters.AddWithValue("u1", anotherUser);
            cmd.Parameters.AddWithValue("c1", customerId);
            cmd.Parameters.AddWithValue("v1", visitId);
            await cmd.ExecuteNonQueryAsync();
        }

        var adminToken = GenerateToken(tenantId1, adminId, signingKey!, issuer!, audience!);
        var wrongUserToken = GenerateToken(tenantId1, anotherUser, signingKey!, issuer!, audience!);
        var t2Token = GenerateToken(tenantId2, adminId, signingKey!, issuer!, audience!);

        // 1. Wrong user (another employee)
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", wrongUserToken);
        var respWrong = await client.PostAsync($"/visits/{visitId}/en-route", null);
        Assert.Equal(HttpStatusCode.Forbidden, respWrong.StatusCode);

        // 2. Wrong tenant (t2 admin)
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", t2Token);
        var respT2 = await client.PostAsync($"/visits/{visitId}/en-route", null);
        Assert.Equal(HttpStatusCode.NotFound, respT2.StatusCode);

        // 3. Success
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        var respSuccess = await client.PostAsync($"/visits/{visitId}/en-route", null);
        Assert.Equal(HttpStatusCode.NoContent, respSuccess.StatusCode);

        // 4. Repeated transition (invalid state)
        var respRepeated = await client.PostAsync($"/visits/{visitId}/en-route", null);
        Assert.Equal(HttpStatusCode.BadRequest, respRepeated.StatusCode);

        // Verify it was persisted
        await using (var conn = new NpgsqlConnection(schemaBlock.ConnectionString))
        {
            await conn.OpenAsync();
            await using var setCmd = new NpgsqlCommand("SELECT set_config('app.current_tenant', @t1::text, false);", conn);
            setCmd.Parameters.AddWithValue("t1", tenantId1);
            await setCmd.ExecuteNonQueryAsync();

            await using var cmd = new NpgsqlCommand("SELECT status FROM visits WHERE id = @v1;", conn);
            cmd.Parameters.AddWithValue("v1", visitId);
            var status = (string)await cmd.ExecuteScalarAsync();
            Assert.Equal("EnRoute", status);
        }
        
        // 5. Test concurrency conflict (try to en-route again while it's already en-route or in a race)
        // Wait, the previous test made it EnRoute, let's create a new visit for the concurrency test!
        var concurrentVisitId = Guid.NewGuid();
        await using (var conn = new NpgsqlConnection(schemaBlock.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(@"
                SELECT set_config('app.current_tenant', @t1::text, false);
                INSERT INTO visits (id, tenant_id, user_id, customer_id, status, schedule_date, created_at) 
                VALUES (@v1, @t1, @a1, @c1, 'Planned', '2026-10-03', now());
            ", conn);
            cmd.Parameters.AddWithValue("t1", tenantId1);
            cmd.Parameters.AddWithValue("a1", adminId);
            cmd.Parameters.AddWithValue("c1", customerId);
            cmd.Parameters.AddWithValue("v1", concurrentVisitId);
            await cmd.ExecuteNonQueryAsync();
        }

        // Send two concurrent requests
        var req1 = client.PostAsync($"/visits/{concurrentVisitId}/en-route", null);
        var req2 = client.PostAsync($"/visits/{concurrentVisitId}/en-route", null);
        var results = await Task.WhenAll(req1, req2);

        var statuses = results.Select(r => r.StatusCode).ToList();
        
        // One should succeed, one should get a conflict (409) or bad request (400 depending on race timing, but 409 is our optimistic concurrency)
        Assert.Contains(HttpStatusCode.NoContent, statuses);
        Assert.True(statuses.Contains(HttpStatusCode.Conflict) || statuses.Contains(HttpStatusCode.BadRequest));
    }

    [SkippableFact]
    public async Task VisitApi_Arrived_EnforcesPermissionsAndTransitions()
    {
        var url = GetRequiredEnvVar("TEST_DATABASE_URL");
        await VerifyRoleAsync(url, expectElevated: false);
        
        await using var schemaBlock = await SetupIsolatedSchemaAsync(url);
        
        await using var factory = new WebApplicationFactory<LifeDrop.API.Program>()
            .WithWebHostBuilder(webBuilder =>
            {
                webBuilder.UseSetting("ConnectionStrings:LifeDrop", schemaBlock.ConnectionString);
                TestHostSettings.UseTestJwt(webBuilder);
                webBuilder.ConfigureTestServices(services =>
                {
                    var descriptor = System.Linq.Enumerable.SingleOrDefault(services, d => d.ServiceType == typeof(LifeDrop.Application.Auth.ISessionStore));
                    if (descriptor != null) services.Remove(descriptor);
                    services.AddScoped<LifeDrop.Application.Auth.ISessionStore, FakeSessionStore>();
                });
            });

        var conf = factory.Services.GetRequiredService<IConfiguration>();
        var signingKey = conf["Jwt:SigningKey"];
        var issuer = conf["Jwt:Issuer"];
        var audience = conf["Jwt:Audience"];

        var client = factory.CreateClient();

        var tenantId1 = Guid.NewGuid();
        var tenantId2 = Guid.NewGuid();
        var adminId = Guid.NewGuid();
        var anotherUser = Guid.NewGuid();
        var customerId = Guid.NewGuid();
        var visitId = Guid.NewGuid();
        
        await using (var conn = new NpgsqlConnection(schemaBlock.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(@"
                INSERT INTO tenants (id, name, code) VALUES (@t1, 'T1', 'T1'), (@t2, 'T2', 'T2');
                INSERT INTO users (id, tenant_id) VALUES (@a1, @t1), (@u1, @t1);
                SELECT set_config('app.current_tenant', @t1::text, false);
                INSERT INTO customers (id, tenant_id, type, code, name) VALUES (@c1, @t1, 'Clinic', 'C001', 'Test Clinic');
                INSERT INTO visits (id, tenant_id, user_id, customer_id, status, schedule_date, created_at) 
                VALUES (@v1, @t1, @a1, @c1, 'EnRoute', '2026-10-03', now());
            ", conn);
            cmd.Parameters.AddWithValue("t1", tenantId1);
            cmd.Parameters.AddWithValue("t2", tenantId2);
            cmd.Parameters.AddWithValue("a1", adminId);
            cmd.Parameters.AddWithValue("u1", anotherUser);
            cmd.Parameters.AddWithValue("c1", customerId);
            cmd.Parameters.AddWithValue("v1", visitId);
            await cmd.ExecuteNonQueryAsync();
        }

        var adminToken = GenerateToken(tenantId1, adminId, signingKey!, issuer!, audience!);
        var wrongUserToken = GenerateToken(tenantId1, anotherUser, signingKey!, issuer!, audience!);
        var t2Token = GenerateToken(tenantId2, adminId, signingKey!, issuer!, audience!);

        // 1. Wrong user (another employee)
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", wrongUserToken);
        var respWrong = await client.PostAsync($"/visits/{visitId}/arrive", null);
        Assert.Equal(HttpStatusCode.Forbidden, respWrong.StatusCode);

        // 2. Wrong tenant (t2 admin)
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", t2Token);
        var respT2 = await client.PostAsync($"/visits/{visitId}/arrive", null);
        Assert.Equal(HttpStatusCode.NotFound, respT2.StatusCode);

        // 3. Success
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        var respSuccess = await client.PostAsync($"/visits/{visitId}/arrive", null);
        Assert.Equal(HttpStatusCode.NoContent, respSuccess.StatusCode);

        // 4. Repeated transition (invalid state)
        var respRepeated = await client.PostAsync($"/visits/{visitId}/arrive", null);
        Assert.Equal(HttpStatusCode.BadRequest, respRepeated.StatusCode);

        // Verify it was persisted
        await using (var conn = new NpgsqlConnection(schemaBlock.ConnectionString))
        {
            await conn.OpenAsync();
            await using var setCmd = new NpgsqlCommand("SELECT set_config('app.current_tenant', @t1::text, false);", conn);
            setCmd.Parameters.AddWithValue("t1", tenantId1);
            await setCmd.ExecuteNonQueryAsync();

            await using var cmd = new NpgsqlCommand("SELECT status FROM visits WHERE id = @v1;", conn);
            cmd.Parameters.AddWithValue("v1", visitId);
            var status = (string)await cmd.ExecuteScalarAsync();
            Assert.Equal("Arrived", status);
        }
    }

    // Server-side check-in (plan step 1.7). Replaces the earlier status-only /start test.
    [SkippableFact]
    public async Task CheckIn_RequiresDeviceFreshGpsInsideGeofenceAndRecordsEveryAttempt()
    {
        var url = GetRequiredEnvVar("TEST_DATABASE_URL");
        await VerifyRoleAsync(url, expectElevated: false);

        await using var schemaBlock = await SetupIsolatedSchemaAsync(url);
        var now = new DateTimeOffset(2026, 10, 5, 9, 0, 0, TimeSpan.Zero);
        await using var factory = new WebApplicationFactory<LifeDrop.API.Program>()
            .WithWebHostBuilder(webBuilder =>
            {
                webBuilder.UseSetting("ConnectionStrings:LifeDrop", schemaBlock.ConnectionString);
                TestHostSettings.UseTestJwt(webBuilder);
                webBuilder.ConfigureTestServices(services =>
                {
                    var descriptor = System.Linq.Enumerable.SingleOrDefault(services, d => d.ServiceType == typeof(LifeDrop.Application.Auth.ISessionStore));
                    if (descriptor != null) services.Remove(descriptor);
                    services.AddScoped<LifeDrop.Application.Auth.ISessionStore, FakeSessionStore>();
                    services.AddSingleton<TimeProvider>(new FixedClock(now));
                });
            });
        var conf = factory.Services.GetRequiredService<IConfiguration>();
        var client = factory.CreateClient();

        Guid t1 = Guid.NewGuid(), t2 = Guid.NewGuid(), rep = Guid.NewGuid(), otherRep = Guid.NewGuid(), t2User = Guid.NewGuid(), device = Guid.NewGuid();
        Guid located = Guid.NewGuid(), unlocated = Guid.NewGuid(), vArrived = Guid.NewGuid(), vNoLocation = Guid.NewGuid(), vPlanned = Guid.NewGuid();
        await using (var conn = new NpgsqlConnection(schemaBlock.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(@"
                INSERT INTO tenants (id, name, code) VALUES (@t1, 'T1', 'T1'), (@t2, 'T2', 'T2');
                INSERT INTO users (id, tenant_id) VALUES (@r, @t1), (@o, @t1), (@u2, @t2);
                INSERT INTO devices (id, tenant_id, user_id, device_label, platform, install_id, app_version, secret_hash, status)
                VALUES (@d, @t1, @r, 'Phone', 'android', 'install-1', '1.0', '\x00', 'approved');
                SELECT set_config('app.current_tenant', @t1::text, false);
                INSERT INTO customers (id, tenant_id, type, code, name, geom, status) VALUES
                    (@cl, @t1, 'Pharmacy', 'C-L', 'Located', ST_SetSRID(ST_MakePoint(44.0092, 36.1911), 4326)::geography, 'located'),
                    (@cu, @t1, 'Pharmacy', 'C-U', 'Unlocated', NULL, 'no_location');
                INSERT INTO visits (id, tenant_id, user_id, customer_id, status, schedule_date) VALUES
                    (@va, @t1, @r, @cl, 'Arrived', '2026-10-05'), (@vn, @t1, @r, @cu, 'Arrived', '2026-10-05'), (@vp, @t1, @r, @cl, 'Planned', '2026-10-05');", conn);
            foreach (var (name, value) in new[] { ("t1", t1), ("t2", t2), ("r", rep), ("o", otherRep), ("u2", t2User), ("d", device),
                ("cl", located), ("cu", unlocated), ("va", vArrived), ("vn", vNoLocation), ("vp", vPlanned) })
                cmd.Parameters.AddWithValue(name, value);
            await cmd.ExecuteNonQueryAsync();
        }

        string Token(Guid tenant, Guid user, Guid? dev) => GenerateToken(tenant, user, conf["Jwt:SigningKey"]!, conf["Jwt:Issuer"]!, conf["Jwt:Audience"]!, deviceId: dev);
        async Task<HttpResponseMessage> CheckInAsync(string token, Guid visit, double lat, double lon, double accuracy = 8, int secondsAgo = 10, bool mocked = false, string? key = null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/visits/{visit}/start")
            {
                Content = JsonContent.Create(new { Latitude = lat, Longitude = lon, AccuracyMeters = accuracy, CapturedAt = now.AddSeconds(-secondsAgo), IsMocked = mocked })
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (key is not null) request.Headers.Add("Idempotency-Key", key);
            return await client.SendAsync(request);
        }
        const double inLat = 36.19115, inLon = 44.00925;   // about 6 m from the customer
        const double outLat = 36.1925, outLon = 44.0092;   // about 155 m away
        var repWithDevice = Token(t1, rep, device);

        // Identity, device, ownership, tenant and state
        await AssertProblemAsync(await CheckInAsync(Token(t1, rep, null), vArrived, inLat, inLon), HttpStatusCode.Forbidden, "DEVICE_REQUIRED");
        await AssertProblemAsync(await CheckInAsync(Token(t1, otherRep, Guid.NewGuid()), vArrived, inLat, inLon), HttpStatusCode.Forbidden, "FORBIDDEN");
        await AssertProblemAsync(await CheckInAsync(Token(t2, t2User, Guid.NewGuid()), vArrived, inLat, inLon), HttpStatusCode.NotFound, "NOT_FOUND");
        await AssertProblemAsync(await CheckInAsync(repWithDevice, vPlanned, inLat, inLon), HttpStatusCode.BadRequest, "INVALID_STATE");
        await AssertProblemAsync(await CheckInAsync(repWithDevice, vNoLocation, inLat, inLon), HttpStatusCode.Conflict, "CUSTOMER_LOCATION_MISSING");
        await AssertProblemAsync(await CheckInAsync(repWithDevice, vArrived, 0, 0), HttpStatusCode.BadRequest, "INVALID_DATA");

        // Positions the server refuses (each is recorded)
        await AssertProblemAsync(await CheckInAsync(repWithDevice, vArrived, outLat, outLon), HttpStatusCode.UnprocessableEntity, "OUTSIDE_GEOFENCE");
        await AssertProblemAsync(await CheckInAsync(repWithDevice, vArrived, inLat, inLon, accuracy: 80), HttpStatusCode.UnprocessableEntity, "GPS_LOW_ACCURACY");
        await AssertProblemAsync(await CheckInAsync(repWithDevice, vArrived, inLat, inLon, secondsAgo: 300), HttpStatusCode.UnprocessableEntity, "GPS_STALE");
        await AssertProblemAsync(await CheckInAsync(repWithDevice, vArrived, inLat, inLon, secondsAgo: -120), HttpStatusCode.UnprocessableEntity, "GPS_CLOCK_SKEW");
        await AssertProblemAsync(await CheckInAsync(repWithDevice, vArrived, inLat, inLon, mocked: true), HttpStatusCode.UnprocessableEntity, "GPS_MOCKED");

        // Accepted once; a retry with the same key is a no-op success; anything else is a state error
        Assert.Equal(HttpStatusCode.NoContent, (await CheckInAsync(repWithDevice, vArrived, inLat, inLon, key: "k-1")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await CheckInAsync(repWithDevice, vArrived, inLat, inLon, key: "k-1")).StatusCode);
        await AssertProblemAsync(await CheckInAsync(repWithDevice, vArrived, inLat, inLon, key: "k-2"), HttpStatusCode.BadRequest, "INVALID_STATE");
        await AssertProblemAsync(await CheckInAsync(repWithDevice, vArrived, inLat, inLon), HttpStatusCode.BadRequest, "INVALID_STATE");

        await using (var conn = new NpgsqlConnection(schemaBlock.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(@"
                SELECT set_config('app.current_tenant', @t1::text, false);
                SELECT (SELECT status FROM visits WHERE id = @va),
                       (SELECT string_agg(coalesce(rejection_reason, 'accepted'), ',' ORDER BY received_at, id) FROM visit_location_events WHERE visit_id = @va),
                       (SELECT distance_m < 20 AND device_id = @d FROM visit_location_events WHERE visit_id = @va AND accepted),
                       (SELECT string_agg(action || '=' || n, ',' ORDER BY action COLLATE ""C"") FROM (SELECT action, count(*) n FROM audit_logs WHERE tenant_id = @t1 GROUP BY action) a);", conn);
            cmd.Parameters.AddWithValue("t1", t1);
            cmd.Parameters.AddWithValue("va", vArrived);
            cmd.Parameters.AddWithValue("d", device);
            await using var reader = await cmd.ExecuteReaderAsync();
            await reader.NextResultAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal("InVisit", reader.GetString(0));
            Assert.Equal(new[] { "outside_geofence", "low_accuracy", "stale_fix", "clock_skew", "mock_location", "accepted" }, reader.GetString(1).Split(',').OrderBy(x => x == "accepted").ToArray());
            Assert.True(reader.GetBoolean(2));
            Assert.Equal("visit.check_in_rejected=5,visit.checked_in=1", reader.GetString(3)); // byte order, independent of the server locale
        }
    }

    // Server-side check-out (plan step 1.8, decision D2). Replaces the earlier status-only /complete test.
    [SkippableFact]
    public async Task CheckOut_RequiresReportDeviceAndGpsInsideGeofence()
    {
        var url = GetRequiredEnvVar("TEST_DATABASE_URL");
        await VerifyRoleAsync(url, expectElevated: false);

        await using var schemaBlock = await SetupIsolatedSchemaAsync(url);
        var now = new DateTimeOffset(2026, 10, 5, 11, 0, 0, TimeSpan.Zero);
        await using var factory = new WebApplicationFactory<LifeDrop.API.Program>()
            .WithWebHostBuilder(webBuilder =>
            {
                webBuilder.UseSetting("ConnectionStrings:LifeDrop", schemaBlock.ConnectionString);
                TestHostSettings.UseTestJwt(webBuilder);
                webBuilder.ConfigureTestServices(services =>
                {
                    var descriptor = System.Linq.Enumerable.SingleOrDefault(services, d => d.ServiceType == typeof(LifeDrop.Application.Auth.ISessionStore));
                    if (descriptor != null) services.Remove(descriptor);
                    services.AddScoped<LifeDrop.Application.Auth.ISessionStore, FakeSessionStore>();
                    services.AddSingleton<TimeProvider>(new FixedClock(now));
                });
            });
        var conf = factory.Services.GetRequiredService<IConfiguration>();
        var client = factory.CreateClient();

        Guid t1 = Guid.NewGuid(), rep = Guid.NewGuid(), otherRep = Guid.NewGuid(), device = Guid.NewGuid(), customer = Guid.NewGuid();
        Guid vReported = Guid.NewGuid(), vNoReport = Guid.NewGuid(), vArrived = Guid.NewGuid();
        await using (var conn = new NpgsqlConnection(schemaBlock.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(@"
                INSERT INTO tenants (id, name, code) VALUES (@t1, 'T1', 'T1');
                INSERT INTO users (id, tenant_id) VALUES (@r, @t1), (@o, @t1);
                INSERT INTO devices (id, tenant_id, user_id, device_label, platform, install_id, app_version, secret_hash, status)
                VALUES (@d, @t1, @r, 'Phone', 'android', 'install-1', '1.0', '\x00', 'approved');
                SELECT set_config('app.current_tenant', @t1::text, false);
                INSERT INTO customers (id, tenant_id, type, code, name, geom, geofence_radius_m, status)
                VALUES (@c, @t1, 'Pharmacy', 'C-L', 'Located', ST_SetSRID(ST_MakePoint(44.0092, 36.1911), 4326)::geography, 30, 'located');
                INSERT INTO visits (id, tenant_id, user_id, customer_id, status, schedule_date) VALUES
                    (@vr, @t1, @r, @c, 'InVisit', '2026-10-05'), (@vn, @t1, @r, @c, 'InVisit', '2026-10-05'), (@va, @t1, @r, @c, 'Arrived', '2026-10-05');
                INSERT INTO visit_reports (visit_id, tenant_id, outcome, note, submitted_by) VALUES (@vr, @t1, 'products_presented', 'Presented the range', @r);", conn);
            foreach (var (name, value) in new[] { ("t1", t1), ("r", rep), ("o", otherRep), ("d", device), ("c", customer), ("vr", vReported), ("vn", vNoReport), ("va", vArrived) })
                cmd.Parameters.AddWithValue(name, value);
            await cmd.ExecuteNonQueryAsync();
        }

        string Token(Guid user, Guid? dev) => GenerateToken(t1, user, conf["Jwt:SigningKey"]!, conf["Jwt:Issuer"]!, conf["Jwt:Audience"]!, deviceId: dev);
        async Task<HttpResponseMessage> CheckOutAsync(string token, Guid visit, double lat, double lon, bool mocked = false, string? key = null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/visits/{visit}/complete")
            {
                Content = JsonContent.Create(new { Latitude = lat, Longitude = lon, AccuracyMeters = 10, CapturedAt = now.AddSeconds(-5), IsMocked = mocked })
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (key is not null) request.Headers.Add("Idempotency-Key", key);
            return await client.SendAsync(request);
        }
        const double inLat = 36.1912, inLon = 44.0093;    // about 14 m away, inside the 30 m radius
        const double outLat = 36.1916, outLon = 44.0092;  // about 55 m away, outside it
        var repDevice = Token(rep, device);

        await AssertProblemAsync(await CheckOutAsync(Token(rep, null), vReported, inLat, inLon), HttpStatusCode.Forbidden, "DEVICE_REQUIRED");
        await AssertProblemAsync(await CheckOutAsync(Token(otherRep, Guid.NewGuid()), vReported, inLat, inLon), HttpStatusCode.Forbidden, "FORBIDDEN");
        await AssertProblemAsync(await CheckOutAsync(repDevice, vArrived, inLat, inLon), HttpStatusCode.BadRequest, "INVALID_STATE");
        await AssertProblemAsync(await CheckOutAsync(repDevice, vNoReport, inLat, inLon), HttpStatusCode.Conflict, "REPORT_REQUIRED");

        // Decision D2: leaving the customer's radius is not enough; check-out must be inside it
        await AssertProblemAsync(await CheckOutAsync(repDevice, vReported, outLat, outLon), HttpStatusCode.UnprocessableEntity, "OUTSIDE_GEOFENCE");
        await AssertProblemAsync(await CheckOutAsync(repDevice, vReported, inLat, inLon, mocked: true), HttpStatusCode.UnprocessableEntity, "GPS_MOCKED");

        Assert.Equal(HttpStatusCode.NoContent, (await CheckOutAsync(repDevice, vReported, inLat, inLon, key: "out-1")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await CheckOutAsync(repDevice, vReported, inLat, inLon, key: "out-1")).StatusCode);
        await AssertProblemAsync(await CheckOutAsync(repDevice, vReported, inLat, inLon), HttpStatusCode.BadRequest, "INVALID_STATE");

        // The report is frozen once the visit is completed
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", repDevice);
        await AssertProblemAsync(await client.PutAsJsonAsync($"/visits/{vReported}/report", new { Outcome = "other", Note = "Changed later" }), HttpStatusCode.BadRequest, "INVALID_STATE");

        await using (var conn = new NpgsqlConnection(schemaBlock.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(@"
                SELECT set_config('app.current_tenant', @t1::text, false);
                SELECT (SELECT string_agg(id::text || ':' || status, ',' ORDER BY id) FROM visits WHERE id IN (@vr, @vn)),
                       (SELECT string_agg(coalesce(rejection_reason, 'accepted'), ',' ORDER BY received_at, id) FROM visit_location_events WHERE visit_id = @vr AND kind = 'check_out'),
                       (SELECT string_agg(action || '=' || n, ',' ORDER BY action COLLATE ""C"") FROM (SELECT action, count(*) n FROM audit_logs WHERE tenant_id = @t1 GROUP BY action) a);", conn);
            cmd.Parameters.AddWithValue("t1", t1);
            cmd.Parameters.AddWithValue("vr", vReported);
            cmd.Parameters.AddWithValue("vn", vNoReport);
            await using var reader = await cmd.ExecuteReaderAsync();
            await reader.NextResultAsync();
            Assert.True(await reader.ReadAsync());
            var statuses = reader.GetString(0).Split(',').ToDictionary(x => Guid.Parse(x.Split(':')[0]), x => x.Split(':')[1]);
            Assert.Equal("Completed", statuses[vReported]);
            Assert.Equal("InVisit", statuses[vNoReport]);
            Assert.Equal("outside_geofence,mock_location,accepted", reader.GetString(1));
            Assert.Equal("visit.check_out_rejected=2,visit.checked_out=1", reader.GetString(2)); // byte order
        }
    }

    [SkippableFact]
    public async Task VisitApi_Failed_EnforcesPermissionsAndTransitions()
    {
        var url = GetRequiredEnvVar("TEST_DATABASE_URL");
        await VerifyRoleAsync(url, expectElevated: false);

        await using var schemaBlock = await SetupIsolatedSchemaAsync(url);

        await using var factory = new WebApplicationFactory<LifeDrop.API.Program>()
            .WithWebHostBuilder(webBuilder =>
            {
                webBuilder.UseSetting("ConnectionStrings:LifeDrop", schemaBlock.ConnectionString);
                TestHostSettings.UseTestJwt(webBuilder);
                webBuilder.ConfigureTestServices(services =>
                {
                    var descriptor = System.Linq.Enumerable.SingleOrDefault(services, d => d.ServiceType == typeof(LifeDrop.Application.Auth.ISessionStore));
                    if (descriptor != null) services.Remove(descriptor);
                    services.AddScoped<LifeDrop.Application.Auth.ISessionStore, FakeSessionStore>();
                });
            });

        var conf = factory.Services.GetRequiredService<IConfiguration>();
        var signingKey = conf["Jwt:SigningKey"];
        var issuer = conf["Jwt:Issuer"];
        var audience = conf["Jwt:Audience"];

        var client = factory.CreateClient();

        var tenantId1 = Guid.NewGuid();
        var tenantId2 = Guid.NewGuid();
        var assigneeId = Guid.NewGuid();
        var otherRepId = Guid.NewGuid();
        var noPermissionUserId = Guid.NewGuid();
        var t2RepId = Guid.NewGuid();
        var failRoleT1 = Guid.NewGuid();
        var failRoleT2 = Guid.NewGuid();
        var customerId = Guid.NewGuid();
        var visitId = Guid.NewGuid();
        var noPermissionVisitId = Guid.NewGuid();

        // Assignee and another rep both hold visits.fail; a third user owns a visit but lacks visits.fail.
        // A Tenant 2 user also holds visits.fail, so the cross-tenant case reaches the visit lookup.
        await using (var conn = new NpgsqlConnection(schemaBlock.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(@"
                INSERT INTO tenants (id, name, code) VALUES (@t1, 'T1', 'T1'), (@t2, 'T2', 'T2');
                INSERT INTO users (id, tenant_id) VALUES (@a1, @t1), (@o1, @t1), (@n1, @t1), (@x2, @t2);
                INSERT INTO roles (id, tenant_id, name) VALUES (@r1, @t1, 'FieldRep'), (@r2, @t2, 'FieldRep');
                INSERT INTO role_permissions (tenant_id, role_id, permission_key) VALUES (@t1, @r1, 'visits.fail'), (@t2, @r2, 'visits.fail');
                INSERT INTO user_roles (tenant_id, user_id, role_id) VALUES (@t1, @a1, @r1), (@t1, @o1, @r1), (@t2, @x2, @r2);
                SELECT set_config('app.current_tenant', @t1::text, false);
                INSERT INTO customers (id, tenant_id, type, code, name) VALUES (@c1, @t1, 'Clinic', 'C001', 'Test Clinic');
                INSERT INTO visits (id, tenant_id, user_id, customer_id, status, schedule_date, created_at)
                VALUES (@v1, @t1, @a1, @c1, 'Arrived', '2026-10-03', now()),
                       (@v2, @t1, @n1, @c1, 'Planned', '2026-10-03', now());
            ", conn);
            cmd.Parameters.AddWithValue("t1", tenantId1);
            cmd.Parameters.AddWithValue("t2", tenantId2);
            cmd.Parameters.AddWithValue("a1", assigneeId);
            cmd.Parameters.AddWithValue("o1", otherRepId);
            cmd.Parameters.AddWithValue("n1", noPermissionUserId);
            cmd.Parameters.AddWithValue("x2", t2RepId);
            cmd.Parameters.AddWithValue("r1", failRoleT1);
            cmd.Parameters.AddWithValue("r2", failRoleT2);
            cmd.Parameters.AddWithValue("c1", customerId);
            cmd.Parameters.AddWithValue("v1", visitId);
            cmd.Parameters.AddWithValue("v2", noPermissionVisitId);
            await cmd.ExecuteNonQueryAsync();
        }

        var assigneeToken = GenerateToken(tenantId1, assigneeId, signingKey!, issuer!, audience!);
        var otherRepToken = GenerateToken(tenantId1, otherRepId, signingKey!, issuer!, audience!);
        var noPermissionToken = GenerateToken(tenantId1, noPermissionUserId, signingKey!, issuer!, audience!);
        var t2Token = GenerateToken(tenantId2, t2RepId, signingKey!, issuer!, audience!);
        var validReason = new { Reason = "Pharmacy closed on arrival" };

        // 1. No token
        client.DefaultRequestHeaders.Authorization = null;
        var respAnonymous = await client.PostAsJsonAsync($"/visits/{visitId}/fail", validReason);
        Assert.Equal(HttpStatusCode.Unauthorized, respAnonymous.StatusCode);

        // 2. Owner of the visit but without visits.fail
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", noPermissionToken);
        var respNoPermission = await client.PostAsJsonAsync($"/visits/{noPermissionVisitId}/fail", validReason);
        Assert.Equal(HttpStatusCode.Forbidden, respNoPermission.StatusCode);

        // 3. Has visits.fail but is not the assigned user
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", otherRepToken);
        var respOtherOwner = await client.PostAsJsonAsync($"/visits/{visitId}/fail", validReason);
        Assert.Equal(HttpStatusCode.Forbidden, respOtherOwner.StatusCode);

        // 4. Has visits.fail in another tenant: the visit is not visible
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", t2Token);
        var respOtherTenant = await client.PostAsJsonAsync($"/visits/{visitId}/fail", validReason);
        Assert.Equal(HttpStatusCode.NotFound, respOtherTenant.StatusCode);

        // 5. Assignee with an empty reason
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", assigneeToken);
        var respEmptyReason = await client.PostAsJsonAsync($"/visits/{visitId}/fail", new { Reason = "   " });
        Assert.Equal(HttpStatusCode.BadRequest, respEmptyReason.StatusCode);

        // None of the rejected requests may change either visit
        Assert.Equal(("Arrived", (string?)null), await ReadVisitStatusAsync(schemaBlock.ConnectionString, tenantId1, visitId));
        Assert.Equal(("Planned", (string?)null), await ReadVisitStatusAsync(schemaBlock.ConnectionString, tenantId1, noPermissionVisitId));

        // 6. Success
        var respSuccess = await client.PostAsJsonAsync($"/visits/{visitId}/fail", validReason);
        Assert.Equal(HttpStatusCode.NoContent, respSuccess.StatusCode);

        // 7. Repeated request on a terminal visit
        var respRepeated = await client.PostAsJsonAsync($"/visits/{visitId}/fail", new { Reason = "Second attempt" });
        Assert.Equal(HttpStatusCode.BadRequest, respRepeated.StatusCode);

        // The first reason is persisted and the repeat did not overwrite it
        Assert.Equal(("Failed", (string?)validReason.Reason), await ReadVisitStatusAsync(schemaBlock.ConnectionString, tenantId1, visitId));
    }

    [SkippableFact]
    public async Task ApiErrors_ReturnProblemDetailsWithStableCodes()
    {
        var url = GetRequiredEnvVar("TEST_DATABASE_URL");
        await VerifyRoleAsync(url, expectElevated: false);

        await using var schemaBlock = await SetupIsolatedSchemaAsync(url);

        await using var factory = new WebApplicationFactory<LifeDrop.API.Program>()
            .WithWebHostBuilder(webBuilder =>
            {
                webBuilder.UseSetting("ConnectionStrings:LifeDrop", schemaBlock.ConnectionString);
                TestHostSettings.UseTestJwt(webBuilder);
                webBuilder.ConfigureTestServices(services =>
                {
                    var descriptor = System.Linq.Enumerable.SingleOrDefault(services, d => d.ServiceType == typeof(LifeDrop.Application.Auth.ISessionStore));
                    if (descriptor != null) services.Remove(descriptor);
                    services.AddScoped<LifeDrop.Application.Auth.ISessionStore, FakeSessionStore>();
                });
            });

        var conf = factory.Services.GetRequiredService<IConfiguration>();
        var client = factory.CreateClient();

        var tenantId = Guid.NewGuid();
        var adminId = Guid.NewGuid();
        var deniedUserId = Guid.NewGuid();
        var roleId = Guid.NewGuid();
        var customerId = Guid.NewGuid();
        var plannedVisitId = Guid.NewGuid();

        await using (var conn = new NpgsqlConnection(schemaBlock.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(@"
                INSERT INTO tenants (id, name, code) VALUES (@t1, 'T1', 'T1');
                INSERT INTO users (id, tenant_id) VALUES (@a1, @t1), (@d1, @t1);
                INSERT INTO roles (id, tenant_id, name) VALUES (@r1, @t1, 'Manager');
                INSERT INTO role_permissions (tenant_id, role_id, permission_key) VALUES (@t1, @r1, 'customers.read'), (@t1, @r1, 'customers.write');
                INSERT INTO user_roles (tenant_id, user_id, role_id) VALUES (@t1, @a1, @r1);
                SELECT set_config('app.current_tenant', @t1::text, false);
                INSERT INTO customers (id, tenant_id, type, code, name) VALUES (@c1, @t1, 'Clinic', 'C001', 'Test Clinic');
                INSERT INTO visits (id, tenant_id, user_id, customer_id, status, schedule_date, created_at)
                VALUES (@v1, @t1, @a1, @c1, 'Planned', '2026-10-03', now());
            ", conn);
            cmd.Parameters.AddWithValue("t1", tenantId);
            cmd.Parameters.AddWithValue("a1", adminId);
            cmd.Parameters.AddWithValue("d1", deniedUserId);
            cmd.Parameters.AddWithValue("r1", roleId);
            cmd.Parameters.AddWithValue("c1", customerId);
            cmd.Parameters.AddWithValue("v1", plannedVisitId);
            await cmd.ExecuteNonQueryAsync();
        }

        var adminToken = GenerateToken(tenantId, adminId, conf["Jwt:SigningKey"]!, conf["Jwt:Issuer"]!, conf["Jwt:Audience"]!);
        var deniedToken = GenerateToken(tenantId, deniedUserId, conf["Jwt:SigningKey"]!, conf["Jwt:Issuer"]!, conf["Jwt:Audience"]!);

        // 401: authentication challenge, no token
        client.DefaultRequestHeaders.Authorization = null;
        await AssertProblemAsync(await client.GetAsync("/customers"), HttpStatusCode.Unauthorized, "UNAUTHENTICATED");

        // 403: authenticated but missing customers.read
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", deniedToken);
        await AssertProblemAsync(await client.GetAsync("/customers"), HttpStatusCode.Forbidden, "FORBIDDEN");

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);

        // 400: invalid query input
        await AssertProblemAsync(await client.GetAsync("/customers?limit=0"), HttpStatusCode.BadRequest, "BAD_REQUEST");

        // 404: record not in the caller's scope
        await AssertProblemAsync(await client.GetAsync($"/customers/{Guid.NewGuid()}"), HttpStatusCode.NotFound, "NOT_FOUND");

        // 409: duplicate customer code
        await AssertProblemAsync(await client.PostAsJsonAsync("/customers", new CreateCustomerRequest("Clinic", "C001", "Other Clinic")),
            HttpStatusCode.Conflict, "DUPLICATE_CODE");

        // 400 with a specific code: visit state conflict (Planned cannot be marked arrived)
        await AssertProblemAsync(await client.PostAsync($"/visits/{plannedVisitId}/arrive", null), HttpStatusCode.BadRequest, "INVALID_STATE");

        // Success responses keep their normal JSON shape; an empty page is 200 with []
        var emptyPage = await client.GetAsync($"/customers?limit=1&cursorCreatedAt={Uri.EscapeDataString(new DateTimeOffset(2100, 1, 1, 0, 0, 0, TimeSpan.Zero).ToString("O"))}&cursorId={Guid.Empty}");
        Assert.Equal(HttpStatusCode.OK, emptyPage.StatusCode);
        Assert.Equal("application/json", emptyPage.Content.Headers.ContentType?.MediaType);
        Assert.Equal("[]", (await emptyPage.Content.ReadAsStringAsync()).Trim());
    }

    [SkippableFact]
    public async Task RegionTeamApi_EnforcesPermissionsTenantIsolationAndAudit()
    {
        var url = GetRequiredEnvVar("TEST_DATABASE_URL");
        await VerifyRoleAsync(url, expectElevated: false);

        await using var schemaBlock = await SetupIsolatedSchemaAsync(url);
        await using var factory = new WebApplicationFactory<LifeDrop.API.Program>()
            .WithWebHostBuilder(webBuilder =>
            {
                webBuilder.UseSetting("ConnectionStrings:LifeDrop", schemaBlock.ConnectionString);
                TestHostSettings.UseTestJwt(webBuilder);
                webBuilder.ConfigureTestServices(services =>
                {
                    var descriptor = System.Linq.Enumerable.SingleOrDefault(services, d => d.ServiceType == typeof(LifeDrop.Application.Auth.ISessionStore));
                    if (descriptor != null) services.Remove(descriptor);
                    services.AddScoped<LifeDrop.Application.Auth.ISessionStore, FakeSessionStore>();
                });
            });
        var conf = factory.Services.GetRequiredService<IConfiguration>();
        var client = factory.CreateClient();

        var t1 = Guid.NewGuid();
        var t2 = Guid.NewGuid();
        var salesManagerId = Guid.NewGuid();
        var supervisorId = Guid.NewGuid();
        var repId = Guid.NewGuid();
        var t2ManagerId = Guid.NewGuid();
        var t2UserId = Guid.NewGuid();

        // Sales manager: manage + read. Supervisor: read only. Rep: no region/team permissions.
        await using (var conn = new NpgsqlConnection(schemaBlock.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(@"
                INSERT INTO tenants (id, name, code) VALUES (@t1, 'T1', 'T1'), (@t2, 'T2', 'T2');
                INSERT INTO users (id, tenant_id) VALUES (@sm, @t1), (@sv, @t1), (@rep, @t1), (@m2, @t2), (@u2, @t2);
                INSERT INTO roles (id, tenant_id, name) VALUES ('11111111-0000-0000-0000-000000000001', @t1, 'SalesManager'),
                    ('11111111-0000-0000-0000-000000000002', @t1, 'Supervisor'), ('11111111-0000-0000-0000-000000000003', @t2, 'SalesManager');
                INSERT INTO role_permissions (tenant_id, role_id, permission_key) VALUES
                    (@t1, '11111111-0000-0000-0000-000000000001', 'regions.manage'), (@t1, '11111111-0000-0000-0000-000000000001', 'regions.read'),
                    (@t1, '11111111-0000-0000-0000-000000000001', 'teams.manage'), (@t1, '11111111-0000-0000-0000-000000000001', 'teams.read'),
                    (@t1, '11111111-0000-0000-0000-000000000002', 'regions.read'), (@t1, '11111111-0000-0000-0000-000000000002', 'teams.read'),
                    (@t2, '11111111-0000-0000-0000-000000000003', 'regions.manage'), (@t2, '11111111-0000-0000-0000-000000000003', 'regions.read'),
                    (@t2, '11111111-0000-0000-0000-000000000003', 'teams.manage'), (@t2, '11111111-0000-0000-0000-000000000003', 'teams.read');
                INSERT INTO user_roles (tenant_id, user_id, role_id) VALUES
                    (@t1, @sm, '11111111-0000-0000-0000-000000000001'), (@t1, @sv, '11111111-0000-0000-0000-000000000002'),
                    (@t2, @m2, '11111111-0000-0000-0000-000000000003');
            ", conn);
            cmd.Parameters.AddWithValue("t1", t1);
            cmd.Parameters.AddWithValue("t2", t2);
            cmd.Parameters.AddWithValue("sm", salesManagerId);
            cmd.Parameters.AddWithValue("sv", supervisorId);
            cmd.Parameters.AddWithValue("rep", repId);
            cmd.Parameters.AddWithValue("m2", t2ManagerId);
            cmd.Parameters.AddWithValue("u2", t2UserId);
            await cmd.ExecuteNonQueryAsync();
        }

        string Token(Guid tenant, Guid user) => GenerateToken(tenant, user, conf["Jwt:SigningKey"]!, conf["Jwt:Issuer"]!, conf["Jwt:Audience"]!);
        void As(Guid tenant, Guid user) => client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token(tenant, user));
        static async Task<Guid> CreatedIdAsync(HttpResponseMessage response)
        {
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            using var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonDocument>();
            return body!.RootElement.GetProperty("id").GetGuid();
        }

        // Authentication and permissions
        client.DefaultRequestHeaders.Authorization = null;
        await AssertProblemAsync(await client.PostAsJsonAsync("/regions", new { Name = "Erbil" }), HttpStatusCode.Unauthorized, "UNAUTHENTICATED");
        As(t1, supervisorId);
        await AssertProblemAsync(await client.PostAsJsonAsync("/regions", new { Name = "Erbil" }), HttpStatusCode.Forbidden, "FORBIDDEN");
        As(t1, repId);
        await AssertProblemAsync(await client.GetAsync("/regions"), HttpStatusCode.Forbidden, "FORBIDDEN");
        await AssertProblemAsync(await client.GetAsync("/teams"), HttpStatusCode.Forbidden, "FORBIDDEN");

        // Regions: create, validation, duplicate name within the tenant
        As(t1, salesManagerId);
        var erbil = await CreatedIdAsync(await client.PostAsJsonAsync("/regions", new { Name = " Erbil " }));
        var baghdad = await CreatedIdAsync(await client.PostAsJsonAsync("/regions", new { Name = "Baghdad" }));
        await AssertProblemAsync(await client.PostAsJsonAsync("/regions", new { Name = "   " }), HttpStatusCode.BadRequest, "INVALID_DATA");
        await AssertProblemAsync(await client.PostAsJsonAsync("/regions", new { Name = new string('x', 101) }), HttpStatusCode.BadRequest, "INVALID_DATA");
        await AssertProblemAsync(await client.PostAsJsonAsync("/regions", new { Name = "Erbil" }), HttpStatusCode.Conflict, "DUPLICATE_NAME");

        // The same region name is allowed in another tenant, and each tenant lists only its own regions
        As(t2, t2ManagerId);
        var t2Region = await CreatedIdAsync(await client.PostAsJsonAsync("/regions", new { Name = "Erbil" }));
        var t2Regions = await (await client.GetAsync("/regions")).Content.ReadFromJsonAsync<List<LifeDrop.Application.Regions.RegionDto>>();
        Assert.Equal(new[] { t2Region }, t2Regions!.Select(r => r.Id).ToArray());

        As(t1, supervisorId);
        var t1Regions = await (await client.GetAsync("/regions")).Content.ReadFromJsonAsync<List<LifeDrop.Application.Regions.RegionDto>>();
        Assert.Equal(new[] { "Baghdad", "Erbil" }, t1Regions!.Select(r => r.Name).ToArray());

        // Teams: only teams.manage creates; the region must belong to the caller's tenant
        await AssertProblemAsync(await client.PostAsJsonAsync("/teams", new { Name = "Team A", RegionId = erbil }), HttpStatusCode.Forbidden, "FORBIDDEN");
        As(t1, salesManagerId);
        await AssertProblemAsync(await client.PostAsJsonAsync("/teams", new { Name = "Team A", RegionId = t2Region }), HttpStatusCode.NotFound, "REGION_NOT_FOUND");
        var teamA = await CreatedIdAsync(await client.PostAsJsonAsync("/teams", new { Name = "Team A", RegionId = erbil }));
        var teamB = await CreatedIdAsync(await client.PostAsJsonAsync("/teams", new { Name = "Team B", RegionId = erbil }));
        await AssertProblemAsync(await client.PostAsJsonAsync("/teams", new { Name = "Team A", RegionId = baghdad }), HttpStatusCode.Conflict, "DUPLICATE_NAME");

        // Moving a team to another region needs regions.manage and stays inside the tenant
        As(t1, supervisorId);
        await AssertProblemAsync(await client.PutAsJsonAsync($"/teams/{teamA}/region", new { RegionId = baghdad }), HttpStatusCode.Forbidden, "FORBIDDEN");
        As(t2, t2ManagerId);
        await AssertProblemAsync(await client.PutAsJsonAsync($"/teams/{teamA}/region", new { RegionId = t2Region }), HttpStatusCode.NotFound, "NOT_FOUND");
        As(t1, salesManagerId);
        await AssertProblemAsync(await client.PutAsJsonAsync($"/teams/{teamA}/region", new { RegionId = t2Region }), HttpStatusCode.NotFound, "REGION_NOT_FOUND");
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync($"/teams/{teamA}/region", new { RegionId = baghdad })).StatusCode);

        // Members: a user belongs to one team; assigning again moves the user
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsync($"/teams/{teamA}/members/{repId}", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsync($"/teams/{teamB}/members/{repId}", null)).StatusCode);
        await AssertProblemAsync(await client.PutAsync($"/teams/{teamA}/members/{t2UserId}", null), HttpStatusCode.NotFound, "USER_NOT_FOUND");
        await AssertProblemAsync(await client.DeleteAsync($"/teams/{teamA}/members/{repId}"), HttpStatusCode.NotFound, "NOT_FOUND");

        // Database state: rep is only in Team B; Team A is in Baghdad
        await using (var conn = new NpgsqlConnection(schemaBlock.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(@"
                SELECT set_config('app.current_tenant', @t1::text, false);
                SELECT (SELECT array_agg(team_id) FROM team_members WHERE user_id = @rep),
                       (SELECT region_id FROM teams WHERE id = @ta);", conn);
            cmd.Parameters.AddWithValue("t1", t1);
            cmd.Parameters.AddWithValue("rep", repId);
            cmd.Parameters.AddWithValue("ta", teamA);
            await using var reader = await cmd.ExecuteReaderAsync();
            await reader.NextResultAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(new[] { teamB }, reader.GetFieldValue<Guid[]>(0));
            Assert.Equal(baghdad, reader.GetGuid(1));
        }

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/teams/{teamB}/members/{repId}")).StatusCode);

        As(t1, supervisorId);
        var teams = await (await client.GetAsync("/teams")).Content.ReadFromJsonAsync<List<LifeDrop.Application.Regions.TeamDto>>();
        Assert.Equal(new[] { (teamA, baghdad), (teamB, erbil) }, teams!.Select(t => (t.Id, t.RegionId)).ToArray());

        // Every successful change is audited in its own tenant; rejected requests leave no audit rows
        await using (var conn = new NpgsqlConnection(schemaBlock.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(@"
                SELECT tenant_id, action, count(*) FROM audit_logs
                WHERE action LIKE 'region.%' OR action LIKE 'team.%'
                GROUP BY tenant_id, action ORDER BY tenant_id = @t1 DESC, action;", conn);
            cmd.Parameters.AddWithValue("t1", t1);
            await using var reader = await cmd.ExecuteReaderAsync();
            var rows = new List<string>();
            while (await reader.ReadAsync())
                rows.Add($"{(reader.GetGuid(0) == t1 ? "t1" : "t2")}:{reader.GetString(1)}={reader.GetInt64(2)}");
            Assert.Equal(new[]
            {
                "t1:region.created=2", "t1:team.created=2", "t1:team.member_assigned=2",
                "t1:team.member_removed=1", "t1:team.region_changed=1", "t2:region.created=1"
            }, rows.ToArray());
        }
    }

    [SkippableFact]
    public async Task DataScope_LimitsCustomerReadsAndVisitAssignment()
    {
        var url = GetRequiredEnvVar("TEST_DATABASE_URL");
        await VerifyRoleAsync(url, expectElevated: false);

        await using var schemaBlock = await SetupIsolatedSchemaAsync(url);
        await using var factory = new WebApplicationFactory<LifeDrop.API.Program>()
            .WithWebHostBuilder(webBuilder =>
            {
                webBuilder.UseSetting("ConnectionStrings:LifeDrop", schemaBlock.ConnectionString);
                TestHostSettings.UseTestJwt(webBuilder);
                webBuilder.ConfigureTestServices(services =>
                {
                    var descriptor = System.Linq.Enumerable.SingleOrDefault(services, d => d.ServiceType == typeof(LifeDrop.Application.Auth.ISessionStore));
                    if (descriptor != null) services.Remove(descriptor);
                    services.AddScoped<LifeDrop.Application.Auth.ISessionStore, FakeSessionStore>();
                });
            });
        var conf = factory.Services.GetRequiredService<IConfiguration>();
        var client = factory.CreateClient();

        var t1 = Guid.NewGuid();
        Guid manager = Guid.NewGuid(), supervisorA = Guid.NewGuid(), repA = Guid.NewGuid(), repA2 = Guid.NewGuid(), repB = Guid.NewGuid();
        Guid erbil = Guid.NewGuid(), baghdad = Guid.NewGuid(), teamA = Guid.NewGuid(), teamB = Guid.NewGuid();
        Guid cErbil = Guid.NewGuid(), cBaghdad = Guid.NewGuid(), cRepOwned = Guid.NewGuid(), cSupervisorOwned = Guid.NewGuid();

        // Manager role has no explicit data_scope, so the migration default 'tenant' applies.
        // Supervisor: region scope, member of Team A (Erbil). Rep: own scope, member of Team A.
        await using (var conn = new NpgsqlConnection(schemaBlock.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(@"
                INSERT INTO tenants (id, name, code) VALUES (@t1, 'T1', 'T1');
                INSERT INTO users (id, tenant_id) VALUES (@m, @t1), (@sa, @t1), (@ra, @t1), (@ra2, @t1), (@rb, @t1);
                INSERT INTO roles (id, tenant_id, name) VALUES ('22222222-0000-0000-0000-000000000001', @t1, 'SalesManager');
                INSERT INTO roles (id, tenant_id, name, data_scope) VALUES
                    ('22222222-0000-0000-0000-000000000002', @t1, 'Supervisor', 'region'),
                    ('22222222-0000-0000-0000-000000000003', @t1, 'SalesRep', 'own');
                INSERT INTO role_permissions (tenant_id, role_id, permission_key)
                SELECT @t1, r.id, p.key FROM roles r, (VALUES ('customers.read'), ('visits.write')) AS p(key) WHERE r.tenant_id = @t1;
                INSERT INTO user_roles (tenant_id, user_id, role_id) VALUES
                    (@t1, @m, '22222222-0000-0000-0000-000000000001'),
                    (@t1, @sa, '22222222-0000-0000-0000-000000000002'),
                    (@t1, @ra, '22222222-0000-0000-0000-000000000003');
                SELECT set_config('app.current_tenant', @t1::text, false);
                INSERT INTO regions (id, tenant_id, name) VALUES (@er, @t1, 'Erbil'), (@bg, @t1, 'Baghdad');
                INSERT INTO teams (id, tenant_id, region_id, name) VALUES (@ta, @t1, @er, 'Team A'), (@tb, @t1, @bg, 'Team B');
                INSERT INTO team_members (tenant_id, user_id, team_id) VALUES (@t1, @sa, @ta), (@t1, @ra, @ta), (@t1, @ra2, @ta), (@t1, @rb, @tb);
                INSERT INTO customers (id, tenant_id, type, code, name, region_id, owner_user_id) VALUES
                    (@ce, @t1, 'Pharmacy', 'C-E', 'Erbil customer', @er, @m),
                    (@cb, @t1, 'Pharmacy', 'C-B', 'Baghdad customer', @bg, @m),
                    (@cr, @t1, 'Clinic', 'C-R', 'Rep customer', NULL, @ra),
                    (@cs, @t1, 'Clinic', 'C-S', 'Supervisor customer', @bg, @sa);
            ", conn);
            foreach (var (name, value) in new[] { ("t1", t1), ("m", manager), ("sa", supervisorA), ("ra", repA), ("ra2", repA2), ("rb", repB),
                ("er", erbil), ("bg", baghdad), ("ta", teamA), ("tb", teamB), ("ce", cErbil), ("cb", cBaghdad), ("cr", cRepOwned), ("cs", cSupervisorOwned) })
                cmd.Parameters.AddWithValue(name, value);
            await cmd.ExecuteNonQueryAsync();
        }

        void As(Guid user) => client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            GenerateToken(t1, user, conf["Jwt:SigningKey"]!, conf["Jwt:Issuer"]!, conf["Jwt:Audience"]!));
        async Task<Guid[]> ListAsync() => (await (await client.GetAsync("/customers?limit=100"))
            .Content.ReadFromJsonAsync<List<CustomerDto>>())!.Select(c => c.Id).OrderBy(id => id).ToArray();
        static Guid[] Sorted(params Guid[] ids) => ids.OrderBy(id => id).ToArray();
        Task<HttpResponseMessage> CreateVisitAsync(Guid assignee, Guid customer) =>
            client.PostAsJsonAsync("/visits", new { AssignedUserId = assignee, CustomerId = customer, ScheduleDate = "2026-10-05" });

        // Customer reads: tenant scope sees all; region scope sees its region plus owned; own scope sees owned only
        As(manager);
        Assert.Equal(Sorted(cErbil, cBaghdad, cRepOwned, cSupervisorOwned), await ListAsync());
        As(supervisorA);
        Assert.Equal(Sorted(cErbil, cSupervisorOwned), await ListAsync());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/customers/{cErbil}")).StatusCode);
        await AssertProblemAsync(await client.GetAsync($"/customers/{cBaghdad}"), HttpStatusCode.NotFound, "NOT_FOUND");
        As(repA);
        Assert.Equal(Sorted(cRepOwned), await ListAsync());
        await AssertProblemAsync(await client.GetAsync($"/customers/{cErbil}"), HttpStatusCode.NotFound, "NOT_FOUND");

        // Visit assignment, own scope: only for oneself and only for own customers
        Assert.Equal(HttpStatusCode.Created, (await CreateVisitAsync(repA, cRepOwned)).StatusCode);
        await AssertProblemAsync(await CreateVisitAsync(repA2, cRepOwned), HttpStatusCode.Forbidden, "FORBIDDEN");
        await AssertProblemAsync(await CreateVisitAsync(repA, cErbil), HttpStatusCode.NotFound, "CUSTOMER_NOT_FOUND");

        // Region scope: only to users of the same region and for customers visible in that scope
        As(supervisorA);
        Assert.Equal(HttpStatusCode.Created, (await CreateVisitAsync(repA2, cErbil)).StatusCode);
        await AssertProblemAsync(await CreateVisitAsync(repB, cErbil), HttpStatusCode.Forbidden, "FORBIDDEN");
        await AssertProblemAsync(await CreateVisitAsync(repA2, cBaghdad), HttpStatusCode.NotFound, "CUSTOMER_NOT_FOUND");
        await AssertProblemAsync(await CreateVisitAsync(Guid.NewGuid(), cErbil), HttpStatusCode.NotFound, "USER_NOT_FOUND");

        // Tenant scope: any user and any customer of the tenant
        As(manager);
        Assert.Equal(HttpStatusCode.Created, (await CreateVisitAsync(repB, cBaghdad)).StatusCode);

        // Only the three allowed visits were stored
        await using (var conn = new NpgsqlConnection(schemaBlock.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(@"
                SELECT set_config('app.current_tenant', @t1::text, false);
                SELECT count(*) FROM visits;", conn);
            cmd.Parameters.AddWithValue("t1", t1);
            await using var reader = await cmd.ExecuteReaderAsync();
            await reader.NextResultAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(3L, reader.GetInt64(0));
        }
    }

    [SkippableFact]
    public async Task DefaultRoles_SeedIsIdempotentAndEnforcedByApi()
    {
        var url = GetRequiredEnvVar("TEST_DATABASE_URL");
        await VerifyRoleAsync(url, expectElevated: false);

        await using var schemaBlock = await SetupIsolatedSchemaAsync(url);
        await using var factory = new WebApplicationFactory<LifeDrop.API.Program>()
            .WithWebHostBuilder(webBuilder =>
            {
                webBuilder.UseSetting("ConnectionStrings:LifeDrop", schemaBlock.ConnectionString);
                TestHostSettings.UseTestJwt(webBuilder);
                webBuilder.ConfigureTestServices(services =>
                {
                    var descriptor = System.Linq.Enumerable.SingleOrDefault(services, d => d.ServiceType == typeof(LifeDrop.Application.Auth.ISessionStore));
                    if (descriptor != null) services.Remove(descriptor);
                    services.AddScoped<LifeDrop.Application.Auth.ISessionStore, FakeSessionStore>();
                });
            });
        var conf = factory.Services.GetRequiredService<IConfiguration>();
        var client = factory.CreateClient();

        var t1 = Guid.NewGuid();
        Guid manager = Guid.NewGuid(), rep = Guid.NewGuid();
        Guid repCustomer = Guid.NewGuid(), otherCustomer = Guid.NewGuid();

        await using var conn = new NpgsqlConnection(schemaBlock.ConnectionString);
        await conn.OpenAsync();
        async Task ExecAsync(string sql)
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            foreach (var (name, value) in new[] { ("t1", t1), ("m", manager), ("r", rep), ("rc", repCustomer), ("oc", otherCustomer) })
                cmd.Parameters.AddWithValue(name, value);
            await cmd.ExecuteNonQueryAsync();
        }
        async Task<string[]> RowsAsync(string sql)
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("t1", t1);
            await using var reader = await cmd.ExecuteReaderAsync();
            var rows = new List<string>();
            while (await reader.ReadAsync()) rows.Add(reader.GetString(0));
            return rows.ToArray();
        }

        await ExecAsync("INSERT INTO tenants (id, name, code) VALUES (@t1, 'T1', 'T1'); SELECT seed_default_roles(@t1);");

        const string rolesSql = "SELECT name || ':' || data_scope || ':' || requires_device_binding FROM roles WHERE tenant_id = @t1 ORDER BY name";
        Assert.Equal(new[]
        {
            "CompanyManager:tenant:false", "Driver:own:true", "FinanceManager:tenant:false", "SalesManager:tenant:false",
            "SalesRep:own:true", "ScientificRep:own:true", "Supervisor:region:false", "WarehouseManager:own:true"
        }, await RowsAsync(rolesSql));
        Assert.Equal(new[] { "customers.location.write", "customers.read", "customers.write", "visits.create_unplanned", "visits.fail" }, await RowsAsync(
            "SELECT rp.permission_key FROM role_permissions rp JOIN roles r ON r.id = rp.role_id WHERE r.tenant_id = @t1 AND r.name = 'SalesRep' ORDER BY 1"));
        var grantsBefore = await RowsAsync("SELECT count(*)::text FROM role_permissions WHERE tenant_id = @t1");
        Assert.Equal(new[] { "36" }, grantsBefore);

        // Re-running adds nothing and keeps a tenant's own change to a default role
        await ExecAsync("UPDATE roles SET data_scope = 'region' WHERE tenant_id = @t1 AND name = 'Supervisor'; SELECT seed_default_roles(@t1); SELECT seed_default_roles(@t1);");
        Assert.Equal(grantsBefore, await RowsAsync("SELECT count(*)::text FROM role_permissions WHERE tenant_id = @t1"));
        Assert.Equal(8, (await RowsAsync(rolesSql)).Length);

        // The seeded roles work through the API: the sales manager manages regions, a rep cannot
        // and sees only own customers.
        await ExecAsync(@"
            INSERT INTO users (id, tenant_id) VALUES (@m, @t1), (@r, @t1);
            INSERT INTO user_roles (tenant_id, user_id, role_id)
            SELECT @t1, @m, id FROM roles WHERE tenant_id = @t1 AND name = 'SalesManager'
            UNION ALL SELECT @t1, @r, id FROM roles WHERE tenant_id = @t1 AND name = 'SalesRep';
            SELECT set_config('app.current_tenant', @t1::text, false);
            INSERT INTO customers (id, tenant_id, type, code, name, owner_user_id) VALUES
                (@rc, @t1, 'Pharmacy', 'C-R', 'Rep customer', @r), (@oc, @t1, 'Pharmacy', 'C-O', 'Other customer', @m);");

        void As(Guid user) => client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            GenerateToken(t1, user, conf["Jwt:SigningKey"]!, conf["Jwt:Issuer"]!, conf["Jwt:Audience"]!));
        As(manager);
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/regions", new { Name = "Erbil" })).StatusCode);
        Assert.Equal(2, (await (await client.GetAsync("/customers")).Content.ReadFromJsonAsync<List<CustomerDto>>())!.Count);
        As(rep);
        await AssertProblemAsync(await client.PostAsJsonAsync("/regions", new { Name = "Duhok" }), HttpStatusCode.Forbidden, "FORBIDDEN");
        Assert.Equal(new[] { repCustomer }, (await (await client.GetAsync("/customers")).Content.ReadFromJsonAsync<List<CustomerDto>>())!.Select(c => c.Id).ToArray());
    }

    [SkippableFact]
    public async Task KillSessions_RevokesTargetImmediatelyWithinScopeAndAudits()
    {
        var url = GetRequiredEnvVar("TEST_DATABASE_URL");
        await VerifyRoleAsync(url, expectElevated: false);

        await using var schemaBlock = await SetupIsolatedSchemaAsync(url);
        // Real session store: every request checks that its session family is still active.
        await using var factory = new WebApplicationFactory<LifeDrop.API.Program>()
            .WithWebHostBuilder(webBuilder =>
            {
                webBuilder.UseSetting("ConnectionStrings:LifeDrop", schemaBlock.ConnectionString);
                TestHostSettings.UseTestJwt(webBuilder);
            });
        var conf = factory.Services.GetRequiredService<IConfiguration>();
        var client = factory.CreateClient();

        var t1 = Guid.NewGuid();
        Guid manager = Guid.NewGuid(), supervisor = Guid.NewGuid(), plainUser = Guid.NewGuid(), repA = Guid.NewGuid(), repB = Guid.NewGuid();
        Guid erbil = Guid.NewGuid(), baghdad = Guid.NewGuid(), teamA = Guid.NewGuid(), teamB = Guid.NewGuid();
        var families = new Dictionary<Guid, Guid[]>
        {
            [manager] = new[] { Guid.NewGuid() }, [supervisor] = new[] { Guid.NewGuid() }, [plainUser] = new[] { Guid.NewGuid() },
            [repA] = new[] { Guid.NewGuid(), Guid.NewGuid() }, [repB] = new[] { Guid.NewGuid() }
        };

        await using (var conn = new NpgsqlConnection(schemaBlock.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(@"
                INSERT INTO tenants (id, name, code) VALUES (@t1, 'T1', 'T1');
                INSERT INTO users (id, tenant_id) VALUES (@m, @t1), (@sv, @t1), (@pu, @t1), (@ra, @t1), (@rb, @t1);
                INSERT INTO roles (id, tenant_id, name) VALUES ('33333333-0000-0000-0000-000000000001', @t1, 'Admin');
                INSERT INTO roles (id, tenant_id, name, data_scope) VALUES ('33333333-0000-0000-0000-000000000002', @t1, 'Supervisor', 'region');
                INSERT INTO role_permissions (tenant_id, role_id, permission_key) VALUES
                    (@t1, '33333333-0000-0000-0000-000000000001', 'sessions.kill'), (@t1, '33333333-0000-0000-0000-000000000002', 'sessions.kill');
                INSERT INTO user_roles (tenant_id, user_id, role_id) VALUES
                    (@t1, @m, '33333333-0000-0000-0000-000000000001'), (@t1, @sv, '33333333-0000-0000-0000-000000000002');
                SELECT set_config('app.current_tenant', @t1::text, false);
                INSERT INTO regions (id, tenant_id, name) VALUES (@er, @t1, 'Erbil'), (@bg, @t1, 'Baghdad');
                INSERT INTO teams (id, tenant_id, region_id, name) VALUES (@ta, @t1, @er, 'Team A'), (@tb, @t1, @bg, 'Team B');
                INSERT INTO team_members (tenant_id, user_id, team_id) VALUES (@t1, @sv, @ta), (@t1, @ra, @ta), (@t1, @rb, @tb);", conn);
            foreach (var (name, value) in new[] { ("t1", t1), ("m", manager), ("sv", supervisor), ("pu", plainUser), ("ra", repA), ("rb", repB),
                ("er", erbil), ("bg", baghdad), ("ta", teamA), ("tb", teamB) })
                cmd.Parameters.AddWithValue(name, value);
            await cmd.ExecuteNonQueryAsync();

            foreach (var (user, userFamilies) in families)
                foreach (var family in userFamilies)
                {
                    await using var token = new NpgsqlCommand(@"
                        INSERT INTO refresh_tokens (id, family_id, tenant_id, user_id, token_hash, expires_at)
                        VALUES (gen_random_uuid(), @f, @t1, @u, @h, now() + interval '1 day');", conn);
                    token.Parameters.AddWithValue("f", family);
                    token.Parameters.AddWithValue("t1", t1);
                    token.Parameters.AddWithValue("u", user);
                    token.Parameters.AddWithValue("h", Guid.NewGuid().ToByteArray());
                    await token.ExecuteNonQueryAsync();
                }
        }

        string Token(Guid user, int family = 0) => GenerateToken(t1, user, conf["Jwt:SigningKey"]!, conf["Jwt:Issuer"]!, conf["Jwt:Audience"]!, families[user][family]);
        async Task<HttpStatusCode> ProbeAsync(Guid user, int family = 0)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/visits?scheduleDate=2026-10-05");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token(user, family));
            return (await client.SendAsync(request)).StatusCode;
        }
        Task<HttpResponseMessage> KillAsync(Guid actor, Guid target)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/users/{target}/sessions/revoke");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token(actor));
            return client.SendAsync(request);
        }

        Assert.Equal(HttpStatusCode.OK, await ProbeAsync(repA));
        Assert.Equal(HttpStatusCode.OK, await ProbeAsync(repA, 1));

        // Without sessions.kill, and outside the actor's region, the request is refused
        await AssertProblemAsync(await KillAsync(plainUser, repA), HttpStatusCode.Forbidden, "FORBIDDEN");
        await AssertProblemAsync(await KillAsync(supervisor, repB), HttpStatusCode.Forbidden, "FORBIDDEN");
        await AssertProblemAsync(await KillAsync(manager, Guid.NewGuid()), HttpStatusCode.NotFound, "USER_NOT_FOUND");
        Assert.Equal(HttpStatusCode.OK, await ProbeAsync(repB));

        // Region scope: the supervisor ends every session of a rep in the same region, effective on the next request
        Assert.Equal(HttpStatusCode.NoContent, (await KillAsync(supervisor, repA)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, await ProbeAsync(repA));
        Assert.Equal(HttpStatusCode.Unauthorized, await ProbeAsync(repA, 1));

        // Tenant scope: any user of the tenant; other users keep their sessions
        Assert.Equal(HttpStatusCode.NoContent, (await KillAsync(manager, repB)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, await ProbeAsync(repB));
        Assert.Equal(HttpStatusCode.OK, await ProbeAsync(manager));
        Assert.Equal(HttpStatusCode.OK, await ProbeAsync(supervisor));

        await using (var conn = new NpgsqlConnection(schemaBlock.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(@"
                SELECT actor_id, entity_id FROM audit_logs WHERE tenant_id = @t1 AND action = 'auth.sessions_killed' ORDER BY occurred_at, entity_id;", conn);
            cmd.Parameters.AddWithValue("t1", t1);
            await using var reader = await cmd.ExecuteReaderAsync();
            var rows = new List<(Guid, string)>();
            while (await reader.ReadAsync()) rows.Add((reader.GetGuid(0), reader.GetString(1)));
            Assert.Equal(new[] { (supervisor, repA.ToString()), (manager, repB.ToString()) }, rows.ToArray());
        }
    }

    [SkippableFact]
    public async Task AuditApi_SearchesWithinScopeAndRowsCannotBeChanged()
    {
        var url = GetRequiredEnvVar("TEST_DATABASE_URL");
        await VerifyRoleAsync(url, expectElevated: false);

        await using var schemaBlock = await SetupIsolatedSchemaAsync(url);
        await using var factory = new WebApplicationFactory<LifeDrop.API.Program>()
            .WithWebHostBuilder(webBuilder =>
            {
                webBuilder.UseSetting("ConnectionStrings:LifeDrop", schemaBlock.ConnectionString);
                TestHostSettings.UseTestJwt(webBuilder);
                webBuilder.ConfigureTestServices(services =>
                {
                    var descriptor = System.Linq.Enumerable.SingleOrDefault(services, d => d.ServiceType == typeof(LifeDrop.Application.Auth.ISessionStore));
                    if (descriptor != null) services.Remove(descriptor);
                    services.AddScoped<LifeDrop.Application.Auth.ISessionStore, FakeSessionStore>();
                });
            });
        var conf = factory.Services.GetRequiredService<IConfiguration>();
        var client = factory.CreateClient();

        Guid t1 = Guid.NewGuid(), t2 = Guid.NewGuid();
        Guid auditor = Guid.NewGuid(), supervisor = Guid.NewGuid(), repA = Guid.NewGuid(), repB = Guid.NewGuid(), noPerm = Guid.NewGuid(), t2User = Guid.NewGuid();
        Guid a1 = Guid.NewGuid(), a2 = Guid.NewGuid(), a3 = Guid.NewGuid(), a4 = Guid.NewGuid();

        await using var conn = new NpgsqlConnection(schemaBlock.ConnectionString);
        await conn.OpenAsync();
        await using (var cmd = new NpgsqlCommand(@"
            INSERT INTO tenants (id, name, code) VALUES (@t1, 'T1', 'T1'), (@t2, 'T2', 'T2');
            INSERT INTO users (id, tenant_id) VALUES (@au, @t1), (@sv, @t1), (@ra, @t1), (@rb, @t1), (@np, @t1), (@u2, @t2);
            INSERT INTO roles (id, tenant_id, name) VALUES ('44444444-0000-0000-0000-000000000001', @t1, 'Auditor');
            INSERT INTO roles (id, tenant_id, name, data_scope) VALUES ('44444444-0000-0000-0000-000000000002', @t1, 'Supervisor', 'region');
            INSERT INTO role_permissions (tenant_id, role_id, permission_key) VALUES
                (@t1, '44444444-0000-0000-0000-000000000001', 'audit.read'), (@t1, '44444444-0000-0000-0000-000000000002', 'audit.read');
            INSERT INTO user_roles (tenant_id, user_id, role_id) VALUES
                (@t1, @au, '44444444-0000-0000-0000-000000000001'), (@t1, @sv, '44444444-0000-0000-0000-000000000002');
            SELECT set_config('app.current_tenant', @t1::text, false);
            INSERT INTO regions (id, tenant_id, name) VALUES ('44444444-0000-0000-0000-0000000000e1', @t1, 'Erbil'), ('44444444-0000-0000-0000-0000000000b1', @t1, 'Baghdad');
            INSERT INTO teams (id, tenant_id, region_id, name) VALUES
                ('44444444-0000-0000-0000-0000000000a1', @t1, '44444444-0000-0000-0000-0000000000e1', 'Team A'),
                ('44444444-0000-0000-0000-0000000000a2', @t1, '44444444-0000-0000-0000-0000000000b1', 'Team B');
            INSERT INTO team_members (tenant_id, user_id, team_id) VALUES
                (@t1, @sv, '44444444-0000-0000-0000-0000000000a1'), (@t1, @ra, '44444444-0000-0000-0000-0000000000a1'), (@t1, @rb, '44444444-0000-0000-0000-0000000000a2');
            INSERT INTO audit_logs (id, tenant_id, actor_id, action, entity_id, occurred_at) VALUES
                (@a1, @t1, @ra, 'customer.created', 'c1', '2026-10-01T08:00:00Z'),
                (@a2, @t1, @rb, 'customer.created', 'c2', '2026-10-01T09:00:00Z'),
                (@a3, @t1, @sv, 'team.created', 'team-1', '2026-10-01T10:00:00Z'),
                (@a4, @t1, @ra, 'device.approved', 'd1', '2026-10-01T11:00:00Z'),
                (gen_random_uuid(), @t2, @u2, 'customer.created', 'c9', '2026-10-01T12:00:00Z');", conn))
        {
            foreach (var (name, value) in new[] { ("t1", t1), ("t2", t2), ("au", auditor), ("sv", supervisor), ("ra", repA), ("rb", repB), ("np", noPerm), ("u2", t2User),
                ("a1", a1), ("a2", a2), ("a3", a3), ("a4", a4) })
                cmd.Parameters.AddWithValue(name, value);
            await cmd.ExecuteNonQueryAsync();
        }

        void As(Guid user) => client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            GenerateToken(t1, user, conf["Jwt:SigningKey"]!, conf["Jwt:Issuer"]!, conf["Jwt:Audience"]!));
        async Task<Guid[]> SearchAsync(string queryString)
        {
            var response = await client.GetAsync("/audit-logs" + queryString);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<List<LifeDrop.Application.Audit.AuditLogEntryDto>>())!.Select(e => e.Id).ToArray();
        }

        client.DefaultRequestHeaders.Authorization = null;
        await AssertProblemAsync(await client.GetAsync("/audit-logs"), HttpStatusCode.Unauthorized, "UNAUTHENTICATED");
        As(noPerm);
        await AssertProblemAsync(await client.GetAsync("/audit-logs"), HttpStatusCode.Forbidden, "FORBIDDEN");

        // Tenant scope: own tenant only, newest first, with filters
        As(auditor);
        Assert.Equal(new[] { a4, a3, a2, a1 }, await SearchAsync(""));
        Assert.Equal(new[] { a2, a1 }, await SearchAsync("?action=customer.created"));
        Assert.Equal(new[] { a4, a1 }, await SearchAsync($"?actorId={repA}"));
        Assert.Equal(new[] { a2 }, await SearchAsync("?entityId=c2"));
        Assert.Equal(new[] { a3, a2 }, await SearchAsync("?from=2026-10-01T09:00:00Z&to=2026-10-01T10:00:00Z"));

        // Keyset pagination
        Assert.Equal(new[] { a4, a3 }, await SearchAsync("?limit=2"));
        Assert.Equal(new[] { a2, a1 }, await SearchAsync($"?limit=2&cursorOccurredAt={Uri.EscapeDataString("2026-10-01T10:00:00Z")}&cursorId={a3}"));

        // Invalid queries
        await AssertProblemAsync(await client.GetAsync("/audit-logs?limit=0"), HttpStatusCode.BadRequest, "INVALID_DATA");
        await AssertProblemAsync(await client.GetAsync("/audit-logs?limit=101"), HttpStatusCode.BadRequest, "INVALID_DATA");
        await AssertProblemAsync(await client.GetAsync("/audit-logs?from=2026-10-02T00:00:00Z&to=2026-10-01T00:00:00Z"), HttpStatusCode.BadRequest, "INVALID_DATA");
        await AssertProblemAsync(await client.GetAsync($"/audit-logs?cursorId={a3}"), HttpStatusCode.BadRequest, "INVALID_DATA");

        // Region scope: entries by actors in the supervisor's region (supervisor and rep A), not rep B
        As(supervisor);
        Assert.Equal(new[] { a4, a3, a1 }, await SearchAsync(""));

        // No route changes audit rows, and the table itself rejects changes even for its owner
        As(auditor);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await client.DeleteAsync("/audit-logs")).StatusCode);
        foreach (var sql in new[] { "DELETE FROM audit_logs", "UPDATE audit_logs SET action = 'x'", "TRUNCATE audit_logs" })
        {
            await using var mutate = new NpgsqlCommand(sql, conn);
            var error = await Assert.ThrowsAsync<PostgresException>(() => mutate.ExecuteNonQueryAsync());
            Assert.Contains("append-only", error.MessageText);
        }
        As(auditor);
        Assert.Equal(4, (await SearchAsync("")).Length);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    [SkippableFact]
    public async Task VisitPlanning_StoresDetailsAndEnforcesUnplannedRules()
    {
        var url = GetRequiredEnvVar("TEST_DATABASE_URL");
        await VerifyRoleAsync(url, expectElevated: false);

        await using var schemaBlock = await SetupIsolatedSchemaAsync(url);
        await using var factory = new WebApplicationFactory<LifeDrop.API.Program>()
            .WithWebHostBuilder(webBuilder =>
            {
                webBuilder.UseSetting("ConnectionStrings:LifeDrop", schemaBlock.ConnectionString);
                TestHostSettings.UseTestJwt(webBuilder);
                webBuilder.ConfigureTestServices(services =>
                {
                    var descriptor = System.Linq.Enumerable.SingleOrDefault(services, d => d.ServiceType == typeof(LifeDrop.Application.Auth.ISessionStore));
                    if (descriptor != null) services.Remove(descriptor);
                    services.AddScoped<LifeDrop.Application.Auth.ISessionStore, FakeSessionStore>();
                    // 22:30 UTC on 4 Oct is already 5 Oct in Baghdad (UTC+3).
                    services.AddSingleton<TimeProvider>(new FixedClock(new DateTimeOffset(2026, 10, 4, 22, 30, 0, TimeSpan.Zero)));
                });
            });
        var conf = factory.Services.GetRequiredService<IConfiguration>();
        var client = factory.CreateClient();

        var t1 = Guid.NewGuid();
        Guid manager = Guid.NewGuid(), rep = Guid.NewGuid(), noPerm = Guid.NewGuid();
        Guid repCustomer = Guid.NewGuid(), otherCustomer = Guid.NewGuid();

        // Default roles from the seed: SalesManager (tenant, visits.write) and SalesRep (own, visits.create_unplanned).
        await using (var conn = new NpgsqlConnection(schemaBlock.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(@"
                INSERT INTO tenants (id, name, code) VALUES (@t1, 'T1', 'T1');
                SELECT seed_default_roles(@t1);
                INSERT INTO users (id, tenant_id) VALUES (@m, @t1), (@r, @t1), (@n, @t1);
                INSERT INTO user_roles (tenant_id, user_id, role_id)
                SELECT @t1, @m, id FROM roles WHERE tenant_id = @t1 AND name = 'SalesManager'
                UNION ALL SELECT @t1, @r, id FROM roles WHERE tenant_id = @t1 AND name = 'SalesRep';
                SELECT set_config('app.current_tenant', @t1::text, false);
                INSERT INTO customers (id, tenant_id, type, code, name, owner_user_id) VALUES
                    (@rc, @t1, 'Pharmacy', 'C-R', 'Rep customer', @r), (@oc, @t1, 'Pharmacy', 'C-O', 'Other customer', @m);", conn);
            foreach (var (name, value) in new[] { ("t1", t1), ("m", manager), ("r", rep), ("n", noPerm), ("rc", repCustomer), ("oc", otherCustomer) })
                cmd.Parameters.AddWithValue(name, value);
            await cmd.ExecuteNonQueryAsync();
        }

        void As(Guid user) => client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            GenerateToken(t1, user, conf["Jwt:SigningKey"]!, conf["Jwt:Issuer"]!, conf["Jwt:Audience"]!));
        async Task<List<LifeDrop.Application.Visits.VisitDto>> PlanAsync(string date) =>
            (await (await client.GetAsync($"/visits?scheduleDate={date}")).Content.ReadFromJsonAsync<List<LifeDrop.Application.Visits.VisitDto>>())!;

        // Planned visits with details; defaults when omitted; several visits for one customer on one day
        As(manager);
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/visits", new
            { AssignedUserId = rep, CustomerId = repCustomer, ScheduleDate = "2026-10-06", VisitType = "doctor", Priority = "high", PlannedTime = "09:30:00" })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/visits", new
            { AssignedUserId = rep, CustomerId = repCustomer, ScheduleDate = "2026-10-06" })).StatusCode);
        await AssertProblemAsync(await client.PostAsJsonAsync("/visits", new
            { AssignedUserId = rep, CustomerId = repCustomer, ScheduleDate = "2026-10-06", VisitType = "party" }), HttpStatusCode.BadRequest, "INVALID_DATA");
        await AssertProblemAsync(await client.PostAsJsonAsync("/visits", new
            { AssignedUserId = rep, CustomerId = repCustomer, ScheduleDate = "2026-10-06", Priority = "urgent" }), HttpStatusCode.BadRequest, "INVALID_DATA");

        As(rep);
        var plan = (await PlanAsync("2026-10-06")).OrderBy(v => v.PlannedTime is null).ToList();
        Assert.Equal(2, plan.Count);
        Assert.Equal(("doctor", "high", (TimeOnly?)new TimeOnly(9, 30), false), (plan[0].VisitType, plan[0].Priority, plan[0].PlannedTime, plan[0].IsUnplanned));
        Assert.Equal(("other", "normal", (TimeOnly?)null, false), (plan[1].VisitType, plan[1].Priority, plan[1].PlannedTime, plan[1].IsUnplanned));

        // Unplanned: requires permission and a reason; customer must be in the caller's scope; dated today in Baghdad
        await AssertProblemAsync(await client.PostAsJsonAsync("/visits/unplanned", new { CustomerId = repCustomer, Reason = "  " }), HttpStatusCode.BadRequest, "INVALID_DATA");
        await AssertProblemAsync(await client.PostAsJsonAsync("/visits/unplanned", new { CustomerId = otherCustomer, Reason = "Passing by" }), HttpStatusCode.NotFound, "CUSTOMER_NOT_FOUND");
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/visits/unplanned", new { CustomerId = repCustomer, Reason = "Customer called", VisitType = "pharmacy" })).StatusCode);
        As(noPerm);
        await AssertProblemAsync(await client.PostAsJsonAsync("/visits/unplanned", new { CustomerId = repCustomer, Reason = "x" }), HttpStatusCode.Forbidden, "FORBIDDEN");

        As(rep);
        Assert.Empty(await PlanAsync("2026-10-04"));
        var today = Assert.Single(await PlanAsync("2026-10-05"));
        Assert.Equal((true, "Customer called", "pharmacy", rep), (today.IsUnplanned, today.UnplannedReason, today.VisitType, today.UserId));
    }

    [SkippableFact]
    public async Task DailyPlan_DefaultsToIraqTodayOwnVisitsOnlyAndOrdered()
    {
        var url = GetRequiredEnvVar("TEST_DATABASE_URL");
        await VerifyRoleAsync(url, expectElevated: false);

        await using var schemaBlock = await SetupIsolatedSchemaAsync(url);
        await using var factory = new WebApplicationFactory<LifeDrop.API.Program>()
            .WithWebHostBuilder(webBuilder =>
            {
                webBuilder.UseSetting("ConnectionStrings:LifeDrop", schemaBlock.ConnectionString);
                TestHostSettings.UseTestJwt(webBuilder);
                webBuilder.ConfigureTestServices(services =>
                {
                    var descriptor = System.Linq.Enumerable.SingleOrDefault(services, d => d.ServiceType == typeof(LifeDrop.Application.Auth.ISessionStore));
                    if (descriptor != null) services.Remove(descriptor);
                    services.AddScoped<LifeDrop.Application.Auth.ISessionStore, FakeSessionStore>();
                    // 22:30 UTC on 4 Oct = 01:30 on 5 Oct in Baghdad.
                    services.AddSingleton<TimeProvider>(new FixedClock(new DateTimeOffset(2026, 10, 4, 22, 30, 0, TimeSpan.Zero)));
                });
            });
        var conf = factory.Services.GetRequiredService<IConfiguration>();
        var client = factory.CreateClient();

        var t1 = Guid.NewGuid();
        Guid manager = Guid.NewGuid(), rep = Guid.NewGuid(), otherRep = Guid.NewGuid(), customer = Guid.NewGuid();
        await using (var conn = new NpgsqlConnection(schemaBlock.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(@"
                INSERT INTO tenants (id, name, code) VALUES (@t1, 'T1', 'T1');
                SELECT seed_default_roles(@t1);
                INSERT INTO users (id, tenant_id) VALUES (@m, @t1), (@r, @t1), (@o, @t1);
                INSERT INTO user_roles (tenant_id, user_id, role_id) SELECT @t1, @m, id FROM roles WHERE tenant_id = @t1 AND name = 'SalesManager';
                SELECT set_config('app.current_tenant', @t1::text, false);
                INSERT INTO customers (id, tenant_id, type, code, name) VALUES (@c, @t1, 'Pharmacy', 'C1', 'Customer');", conn);
            foreach (var (name, value) in new[] { ("t1", t1), ("m", manager), ("r", rep), ("o", otherRep), ("c", customer) })
                cmd.Parameters.AddWithValue(name, value);
            await cmd.ExecuteNonQueryAsync();
        }

        void As(Guid user) => client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            GenerateToken(t1, user, conf["Jwt:SigningKey"]!, conf["Jwt:Issuer"]!, conf["Jwt:Audience"]!));
        async Task<Guid> CreateAsync(Guid assignee, string date, string priority, string? time)
        {
            var response = await client.PostAsJsonAsync("/visits", new { AssignedUserId = assignee, CustomerId = customer, ScheduleDate = date, Priority = priority, PlannedTime = time });
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            using var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonDocument>();
            return body!.RootElement.GetProperty("id").GetGuid();
        }

        As(manager);
        var lowUntimed = await CreateAsync(rep, "2026-10-05", "low", null);
        var highUntimed = await CreateAsync(rep, "2026-10-05", "high", null);
        var afternoon = await CreateAsync(rep, "2026-10-05", "normal", "14:00:00");
        var morning = await CreateAsync(rep, "2026-10-05", "normal", "08:00:00");
        await CreateAsync(otherRep, "2026-10-05", "high", "07:00:00");
        await CreateAsync(rep, "2026-10-04", "high", "07:00:00");

        // No date: today in Baghdad (5 Oct), only the caller's visits, timed first then by priority
        As(rep);
        var today = await client.GetAsync("/visits");
        Assert.Equal(HttpStatusCode.OK, today.StatusCode);
        var todayVisits = (await today.Content.ReadFromJsonAsync<List<LifeDrop.Application.Visits.VisitDto>>())!;
        Assert.Equal(new[] { morning, afternoon, highUntimed, lowUntimed }, todayVisits.Select(v => v.Id).ToArray());
        // The plan carries the customer's name and code for display (step 1.10)
        Assert.All(todayVisits, v => Assert.Equal(("Customer", "C1"), (v.CustomerName, v.CustomerCode)));

        // A day without visits is a successful empty list; a malformed date is rejected
        var empty = await client.GetAsync("/visits?scheduleDate=2026-10-07");
        Assert.Equal(HttpStatusCode.OK, empty.StatusCode);
        Assert.Equal("[]", (await empty.Content.ReadAsStringAsync()).Trim());
        await AssertProblemAsync(await client.GetAsync("/visits?scheduleDate=not-a-date"), HttpStatusCode.BadRequest, "BAD_REQUEST");
    }

    [SkippableFact]
    public async Task ManageVisit_ReassignAndCancelWithinScopeAndAudit()
    {
        var url = GetRequiredEnvVar("TEST_DATABASE_URL");
        await VerifyRoleAsync(url, expectElevated: false);

        await using var schemaBlock = await SetupIsolatedSchemaAsync(url);
        await using var factory = new WebApplicationFactory<LifeDrop.API.Program>()
            .WithWebHostBuilder(webBuilder =>
            {
                webBuilder.UseSetting("ConnectionStrings:LifeDrop", schemaBlock.ConnectionString);
                TestHostSettings.UseTestJwt(webBuilder);
                webBuilder.ConfigureTestServices(services =>
                {
                    var descriptor = System.Linq.Enumerable.SingleOrDefault(services, d => d.ServiceType == typeof(LifeDrop.Application.Auth.ISessionStore));
                    if (descriptor != null) services.Remove(descriptor);
                    services.AddScoped<LifeDrop.Application.Auth.ISessionStore, FakeSessionStore>();
                });
            });
        var conf = factory.Services.GetRequiredService<IConfiguration>();
        var client = factory.CreateClient();

        var t1 = Guid.NewGuid();
        Guid manager = Guid.NewGuid(), supervisor = Guid.NewGuid(), repA = Guid.NewGuid(), repA2 = Guid.NewGuid(), repB = Guid.NewGuid();
        Guid customer = Guid.NewGuid(), vA = Guid.NewGuid(), vB = Guid.NewGuid(), vC = Guid.NewGuid();

        // Default roles: SalesManager (tenant), Supervisor (region, Team A = Erbil), SalesRep (no visits.write / visits.cancel).
        await using (var conn = new NpgsqlConnection(schemaBlock.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(@"
                INSERT INTO tenants (id, name, code) VALUES (@t1, 'T1', 'T1');
                SELECT seed_default_roles(@t1);
                INSERT INTO users (id, tenant_id) VALUES (@m, @t1), (@sv, @t1), (@ra, @t1), (@ra2, @t1), (@rb, @t1);
                INSERT INTO user_roles (tenant_id, user_id, role_id)
                SELECT @t1, @m, id FROM roles WHERE tenant_id = @t1 AND name = 'SalesManager'
                UNION ALL SELECT @t1, @sv, id FROM roles WHERE tenant_id = @t1 AND name = 'Supervisor'
                UNION ALL SELECT @t1, @ra, id FROM roles WHERE tenant_id = @t1 AND name = 'SalesRep';
                SELECT set_config('app.current_tenant', @t1::text, false);
                INSERT INTO regions (id, tenant_id, name) VALUES ('55555555-0000-0000-0000-0000000000e1', @t1, 'Erbil'), ('55555555-0000-0000-0000-0000000000b1', @t1, 'Baghdad');
                INSERT INTO teams (id, tenant_id, region_id, name) VALUES
                    ('55555555-0000-0000-0000-0000000000a1', @t1, '55555555-0000-0000-0000-0000000000e1', 'Team A'),
                    ('55555555-0000-0000-0000-0000000000a2', @t1, '55555555-0000-0000-0000-0000000000b1', 'Team B');
                INSERT INTO team_members (tenant_id, user_id, team_id) VALUES
                    (@t1, @sv, '55555555-0000-0000-0000-0000000000a1'), (@t1, @ra, '55555555-0000-0000-0000-0000000000a1'),
                    (@t1, @ra2, '55555555-0000-0000-0000-0000000000a1'), (@t1, @rb, '55555555-0000-0000-0000-0000000000a2');
                INSERT INTO customers (id, tenant_id, type, code, name) VALUES (@c, @t1, 'Pharmacy', 'C1', 'Customer');
                INSERT INTO visits (id, tenant_id, user_id, customer_id, status, schedule_date) VALUES
                    (@va, @t1, @ra, @c, 'Planned', '2026-10-06'), (@vb, @t1, @rb, @c, 'Planned', '2026-10-06'), (@vc, @t1, @ra, @c, 'EnRoute', '2026-10-06');", conn);
            foreach (var (name, value) in new[] { ("t1", t1), ("m", manager), ("sv", supervisor), ("ra", repA), ("ra2", repA2), ("rb", repB),
                ("c", customer), ("va", vA), ("vb", vB), ("vc", vC) })
                cmd.Parameters.AddWithValue(name, value);
            await cmd.ExecuteNonQueryAsync();
        }

        void As(Guid user) => client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            GenerateToken(t1, user, conf["Jwt:SigningKey"]!, conf["Jwt:Issuer"]!, conf["Jwt:Audience"]!));
        Task<HttpResponseMessage> Reassign(Guid visit, Guid to) => client.PutAsJsonAsync($"/visits/{visit}/assignee", new { AssignedUserId = to });
        Task<HttpResponseMessage> Cancel(Guid visit, string reason) => client.PostAsJsonAsync($"/visits/{visit}/cancel", new { Reason = reason });

        // A rep has neither permission
        As(repA);
        await AssertProblemAsync(await Reassign(vA, repA2), HttpStatusCode.Forbidden, "FORBIDDEN");
        await AssertProblemAsync(await Cancel(vA, "No longer needed"), HttpStatusCode.Forbidden, "FORBIDDEN");

        // Supervisor (region scope): only visits and assignees in Erbil; only Planned visits can be reassigned
        As(supervisor);
        await AssertProblemAsync(await Reassign(vA, repB), HttpStatusCode.Forbidden, "FORBIDDEN");
        await AssertProblemAsync(await Reassign(vB, repA2), HttpStatusCode.NotFound, "NOT_FOUND");
        await AssertProblemAsync(await Reassign(vC, repA2), HttpStatusCode.BadRequest, "INVALID_STATE");
        await AssertProblemAsync(await Reassign(vA, Guid.NewGuid()), HttpStatusCode.NotFound, "USER_NOT_FOUND");
        await AssertProblemAsync(await Reassign(vA, repA), HttpStatusCode.BadRequest, "INVALID_DATA");
        Assert.Equal(HttpStatusCode.NoContent, (await Reassign(vA, repA2)).StatusCode);

        // Cancel needs a reason, works from an active state, and is final
        await AssertProblemAsync(await Cancel(vB, "Out of region"), HttpStatusCode.NotFound, "NOT_FOUND");
        await AssertProblemAsync(await Cancel(vC, "  "), HttpStatusCode.BadRequest, "INVALID_DATA");
        Assert.Equal(HttpStatusCode.NoContent, (await Cancel(vC, "Customer closed for holiday")).StatusCode);
        await AssertProblemAsync(await Cancel(vC, "Again"), HttpStatusCode.BadRequest, "INVALID_STATE");

        // Sales manager (tenant scope) reassigns across regions
        As(manager);
        Assert.Equal(HttpStatusCode.NoContent, (await Reassign(vB, repA)).StatusCode);

        // The new assignee sees the visit in their plan; the previous one no longer does
        As(repA2);
        Assert.Contains(vA, (await (await client.GetAsync("/visits?scheduleDate=2026-10-06")).Content.ReadFromJsonAsync<List<LifeDrop.Application.Visits.VisitDto>>())!.Select(v => v.Id));
        As(repA);
        var repAPlan = (await (await client.GetAsync("/visits?scheduleDate=2026-10-06")).Content.ReadFromJsonAsync<List<LifeDrop.Application.Visits.VisitDto>>())!;
        Assert.DoesNotContain(vA, repAPlan.Select(v => v.Id));
        Assert.Equal(("Cancelled", "Customer closed for holiday"), repAPlan.Where(v => v.Id == vC).Select(v => (v.Status, v.ExceptionReason)).Single());

        // Exactly the successful actions are audited, with the acting manager
        await using (var conn = new NpgsqlConnection(schemaBlock.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(@"
                SELECT action, actor_id, entity_id FROM audit_logs WHERE tenant_id = @t1 AND action LIKE 'visit.%' ORDER BY occurred_at, action;", conn);
            cmd.Parameters.AddWithValue("t1", t1);
            await using var reader = await cmd.ExecuteReaderAsync();
            var rows = new List<(string, Guid, string)>();
            while (await reader.ReadAsync()) rows.Add((reader.GetString(0), reader.GetGuid(1), reader.GetString(2)));
            Assert.Equal(new[]
            {
                ("visit.reassigned", supervisor, vA.ToString()),
                ("visit.cancelled", supervisor, vC.ToString()),
                ("visit.reassigned", manager, vB.ToString())
            }, rows.ToArray());
        }
    }

    [SkippableFact]
    public async Task VisitHistory_RecordsEveryTransitionWithActorAndIsReadableWithinScope()
    {
        var url = GetRequiredEnvVar("TEST_DATABASE_URL");
        await VerifyRoleAsync(url, expectElevated: false);

        await using var schemaBlock = await SetupIsolatedSchemaAsync(url);
        await using var factory = new WebApplicationFactory<LifeDrop.API.Program>()
            .WithWebHostBuilder(webBuilder =>
            {
                webBuilder.UseSetting("ConnectionStrings:LifeDrop", schemaBlock.ConnectionString);
                TestHostSettings.UseTestJwt(webBuilder);
                webBuilder.ConfigureTestServices(services =>
                {
                    var descriptor = System.Linq.Enumerable.SingleOrDefault(services, d => d.ServiceType == typeof(LifeDrop.Application.Auth.ISessionStore));
                    if (descriptor != null) services.Remove(descriptor);
                    services.AddScoped<LifeDrop.Application.Auth.ISessionStore, FakeSessionStore>();
                });
            });
        var conf = factory.Services.GetRequiredService<IConfiguration>();
        var client = factory.CreateClient();

        var t1 = Guid.NewGuid();
        Guid manager = Guid.NewGuid(), supervisor = Guid.NewGuid(), repA = Guid.NewGuid(), repB = Guid.NewGuid();
        Guid customer = Guid.NewGuid(), seeded = Guid.NewGuid();

        await using (var conn = new NpgsqlConnection(schemaBlock.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(@"
                INSERT INTO tenants (id, name, code) VALUES (@t1, 'T1', 'T1');
                SELECT seed_default_roles(@t1);
                INSERT INTO users (id, tenant_id) VALUES (@m, @t1), (@sv, @t1), (@ra, @t1), (@rb, @t1);
                INSERT INTO user_roles (tenant_id, user_id, role_id)
                SELECT @t1, @m, id FROM roles WHERE tenant_id = @t1 AND name = 'SalesManager'
                UNION ALL SELECT @t1, @sv, id FROM roles WHERE tenant_id = @t1 AND name = 'Supervisor'
                UNION ALL SELECT @t1, @ra, id FROM roles WHERE tenant_id = @t1 AND name = 'SalesRep'
                UNION ALL SELECT @t1, @rb, id FROM roles WHERE tenant_id = @t1 AND name = 'SalesRep';
                SELECT set_config('app.current_tenant', @t1::text, false);
                INSERT INTO regions (id, tenant_id, name) VALUES ('66666666-0000-0000-0000-0000000000e1', @t1, 'Erbil');
                INSERT INTO teams (id, tenant_id, region_id, name) VALUES ('66666666-0000-0000-0000-0000000000a1', @t1, '66666666-0000-0000-0000-0000000000e1', 'Team A');
                INSERT INTO team_members (tenant_id, user_id, team_id) VALUES
                    (@t1, @sv, '66666666-0000-0000-0000-0000000000a1'), (@t1, @ra, '66666666-0000-0000-0000-0000000000a1');
                INSERT INTO customers (id, tenant_id, type, code, name, region_id, owner_user_id) VALUES (@c, @t1, 'Pharmacy', 'C1', 'Customer', '66666666-0000-0000-0000-0000000000e1', @ra);
                INSERT INTO visits (id, tenant_id, user_id, customer_id, status, schedule_date) VALUES (@vs, @t1, @ra, @c, 'Planned', '2026-10-06');", conn);
            foreach (var (name, value) in new[] { ("t1", t1), ("m", manager), ("sv", supervisor), ("ra", repA), ("rb", repB), ("c", customer), ("vs", seeded) })
                cmd.Parameters.AddWithValue(name, value);
            await cmd.ExecuteNonQueryAsync();
        }

        void As(Guid user) => client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            GenerateToken(t1, user, conf["Jwt:SigningKey"]!, conf["Jwt:Issuer"]!, conf["Jwt:Audience"]!));
        async Task<Guid> CreatedId(HttpResponseMessage response)
        {
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("id").GetGuid();
        }
        async Task<List<LifeDrop.Application.Visits.VisitStatusChangeDto>> History(Guid visit)
        {
            var response = await client.GetAsync($"/visits/{visit}/history");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<List<LifeDrop.Application.Visits.VisitStatusChangeDto>>())!;
        }

        // Planned visit: manager creates, rep moves it, supervisor cancels
        As(manager);
        var planned = await CreatedId(await client.PostAsJsonAsync("/visits", new { AssignedUserId = repA, CustomerId = customer, ScheduleDate = "2026-10-06" }));
        As(repA);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync($"/visits/{planned}/en-route", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync($"/visits/{planned}/arrive", null)).StatusCode);
        // A refused transition leaves no trace
        await AssertProblemAsync(await client.PostAsync($"/visits/{planned}/arrive", null), HttpStatusCode.BadRequest, "INVALID_STATE");
        As(supervisor);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync($"/visits/{planned}/cancel", new { Reason = "Customer closed" })).StatusCode);

        // Unplanned visit: rep creates and fails it
        As(repA);
        var unplanned = await CreatedId(await client.PostAsJsonAsync("/visits/unplanned", new { CustomerId = customer, Reason = "Passing by" }));
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync($"/visits/{unplanned}/fail", new { Reason = "Pharmacist absent" })).StatusCode);

        // The assignee, the region supervisor and the manager read the history; another rep does not
        var expected = new (Guid?, string?, string, string?)[]
        {
            (manager, null, "Planned", null), (repA, "Planned", "EnRoute", null), (repA, "EnRoute", "Arrived", null),
            (supervisor, "Arrived", "Cancelled", "Customer closed")
        };
        foreach (var reader in new[] { repA, supervisor, manager })
        {
            As(reader);
            Assert.Equal(expected, (await History(planned)).Select(h => (h.ActorId, h.FromStatus, h.ToStatus, h.Reason)).ToArray());
        }
        As(repA);
        Assert.Equal(new (Guid?, string?, string, string?)[] { (repA, null, "Planned", null), (repA, "Planned", "Failed", "Pharmacist absent") },
            (await History(unplanned)).Select(h => (h.ActorId, h.FromStatus, h.ToStatus, h.Reason)).ToArray());
        // A row inserted without an actor (data import) is kept with no actor
        Assert.Equal(new (Guid?, string?, string, string?)[] { (null, null, "Planned", null) },
            (await History(seeded)).Select(h => (h.ActorId, h.FromStatus, h.ToStatus, h.Reason)).ToArray());
        As(repB);
        await AssertProblemAsync(await client.GetAsync($"/visits/{planned}/history"), HttpStatusCode.NotFound, "NOT_FOUND");
        await AssertProblemAsync(await client.GetAsync($"/visits/{Guid.NewGuid()}/history"), HttpStatusCode.NotFound, "NOT_FOUND");

        await using (var conn = new NpgsqlConnection(schemaBlock.ConnectionString))
        {
            await conn.OpenAsync();
            // Every successful action is in the audit log with its actor
            await using (var cmd = new NpgsqlCommand(@"
                SELECT string_agg(action || '=' || CASE actor_id WHEN @m THEN 'm' WHEN @sv THEN 'sv' WHEN @ra THEN 'ra' ELSE '?' END, ',' ORDER BY action COLLATE ""C"")
                FROM audit_logs WHERE tenant_id = @t1 AND action LIKE 'visit.%';", conn))
            {
                foreach (var (name, value) in new[] { ("t1", t1), ("m", manager), ("sv", supervisor), ("ra", repA) })
                    cmd.Parameters.AddWithValue(name, value);
                Assert.Equal("visit.arrived=ra,visit.cancelled=sv,visit.created=m,visit.created_unplanned=ra,visit.en_route=ra,visit.failed=ra",
                    (string?)await cmd.ExecuteScalarAsync());
            }

            await using (var setTenant = new NpgsqlCommand("SELECT set_config('app.current_tenant', @t1::text, false)", conn))
            {
                setTenant.Parameters.AddWithValue("t1", t1);
                await setTenant.ExecuteNonQueryAsync();
            }
            // A status change without a named actor is refused, and history rows cannot be changed
            await using (var cmd = new NpgsqlCommand("UPDATE visits SET status = 'EnRoute' WHERE id = @vs", conn))
            {
                cmd.Parameters.AddWithValue("vs", seeded);
                var error = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
                Assert.Contains("app.current_actor", error.MessageText);
            }
            await using (var cmd = new NpgsqlCommand("DELETE FROM visit_status_history", conn))
                Assert.Contains("append-only", (await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync())).MessageText);
        }
    }

    [SkippableFact]
    public async Task CustomerLocation_SetsLocationAndRadiusWithinScopeAndAudits()
    {
        var url = GetRequiredEnvVar("TEST_DATABASE_URL");
        await VerifyRoleAsync(url, expectElevated: false);

        await using var schemaBlock = await SetupIsolatedSchemaAsync(url);
        await using var factory = new WebApplicationFactory<LifeDrop.API.Program>()
            .WithWebHostBuilder(webBuilder =>
            {
                webBuilder.UseSetting("ConnectionStrings:LifeDrop", schemaBlock.ConnectionString);
                TestHostSettings.UseTestJwt(webBuilder);
                webBuilder.ConfigureTestServices(services =>
                {
                    var descriptor = System.Linq.Enumerable.SingleOrDefault(services, d => d.ServiceType == typeof(LifeDrop.Application.Auth.ISessionStore));
                    if (descriptor != null) services.Remove(descriptor);
                    services.AddScoped<LifeDrop.Application.Auth.ISessionStore, FakeSessionStore>();
                });
            });
        var conf = factory.Services.GetRequiredService<IConfiguration>();
        var client = factory.CreateClient();

        var t1 = Guid.NewGuid();
        Guid manager = Guid.NewGuid(), supervisor = Guid.NewGuid(), rep = Guid.NewGuid(), cRep = Guid.NewGuid(), cBaghdad = Guid.NewGuid();
        await using (var conn = new NpgsqlConnection(schemaBlock.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(@"
                INSERT INTO tenants (id, name, code) VALUES (@t1, 'T1', 'T1');
                SELECT seed_default_roles(@t1);
                INSERT INTO users (id, tenant_id) VALUES (@m, @t1), (@sv, @t1), (@r, @t1);
                INSERT INTO user_roles (tenant_id, user_id, role_id)
                SELECT @t1, @m, id FROM roles WHERE tenant_id = @t1 AND name = 'SalesManager'
                UNION ALL SELECT @t1, @sv, id FROM roles WHERE tenant_id = @t1 AND name = 'Supervisor'
                UNION ALL SELECT @t1, @r, id FROM roles WHERE tenant_id = @t1 AND name = 'SalesRep';
                SELECT set_config('app.current_tenant', @t1::text, false);
                INSERT INTO regions (id, tenant_id, name) VALUES ('66666666-0000-0000-0000-0000000000e1', @t1, 'Erbil'), ('66666666-0000-0000-0000-0000000000b1', @t1, 'Baghdad');
                INSERT INTO teams (id, tenant_id, region_id, name) VALUES ('66666666-0000-0000-0000-0000000000a1', @t1, '66666666-0000-0000-0000-0000000000e1', 'Team A');
                INSERT INTO team_members (tenant_id, user_id, team_id) VALUES (@t1, @sv, '66666666-0000-0000-0000-0000000000a1'), (@t1, @r, '66666666-0000-0000-0000-0000000000a1');
                INSERT INTO customers (id, tenant_id, type, code, name, region_id, owner_user_id) VALUES
                    (@cr, @t1, 'Pharmacy', 'C-R', 'Rep customer', '66666666-0000-0000-0000-0000000000e1', @r),
                    (@cb, @t1, 'Pharmacy', 'C-B', 'Baghdad customer', '66666666-0000-0000-0000-0000000000b1', @m);", conn);
            foreach (var (name, value) in new[] { ("t1", t1), ("m", manager), ("sv", supervisor), ("r", rep), ("cr", cRep), ("cb", cBaghdad) })
                cmd.Parameters.AddWithValue(name, value);
            await cmd.ExecuteNonQueryAsync();
        }

        void As(Guid user) => client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            GenerateToken(t1, user, conf["Jwt:SigningKey"]!, conf["Jwt:Issuer"]!, conf["Jwt:Audience"]!));
        async Task<CustomerDto> GetAsync(Guid id) => (await (await client.GetAsync($"/customers/{id}")).Content.ReadFromJsonAsync<CustomerDto>())!;
        Task<HttpResponseMessage> SetLocation(Guid id, object body) => client.PutAsJsonAsync($"/customers/{id}/location", body);
        Task<HttpResponseMessage> SetRadius(Guid id, int? radius) => client.PutAsJsonAsync($"/customers/{id}/geofence", new { RadiusMeters = radius });

        // Field rep: own customers only; GPS needs a good accuracy; invalid coordinates are rejected
        As(rep);
        var before = await GetAsync(cRep);
        Assert.Equal(("no_location", (double?)null, 20), (before.Status, before.Latitude, before.GeofenceRadiusMeters));
        Assert.Equal(HttpStatusCode.NoContent, (await SetLocation(cRep, new { Latitude = 36.1911, Longitude = 44.0092, Source = "gps", AccuracyMeters = 8 })).StatusCode);
        var located = await GetAsync(cRep);
        Assert.Equal(("located", "gps"), (located.Status, located.LocationSource));
        Assert.Equal(36.1911, located.Latitude!.Value, 6);
        Assert.Equal(44.0092, located.Longitude!.Value, 6);
        Assert.NotNull(located.LocationUpdatedAt);
        await AssertProblemAsync(await SetLocation(cBaghdad, new { Latitude = 33.31, Longitude = 44.36, Source = "manual" }), HttpStatusCode.NotFound, "NOT_FOUND");
        foreach (var invalid in new object[]
        {
            new { Latitude = 91.0, Longitude = 44.0, Source = "manual" },
            new { Latitude = 0.0, Longitude = 0.0, Source = "manual" },
            new { Latitude = 36.19, Longitude = 44.01, Source = "gps" },
            new { Latitude = 36.19, Longitude = 44.01, Source = "gps", AccuracyMeters = 150 },
            new { Latitude = 36.19, Longitude = 44.01, Source = "guess" }
        })
            await AssertProblemAsync(await SetLocation(cRep, invalid), HttpStatusCode.BadRequest, "INVALID_DATA");
        await AssertProblemAsync(await SetRadius(cRep, 50), HttpStatusCode.Forbidden, "FORBIDDEN");

        // Supervisor: radius within the region; null restores the default
        As(supervisor);
        Assert.Equal(HttpStatusCode.NoContent, (await SetRadius(cRep, 50)).StatusCode);
        Assert.Equal(50, (await GetAsync(cRep)).GeofenceRadiusMeters);
        await AssertProblemAsync(await SetRadius(cBaghdad, 50), HttpStatusCode.NotFound, "NOT_FOUND");
        await AssertProblemAsync(await SetRadius(cRep, 4), HttpStatusCode.BadRequest, "INVALID_DATA");
        Assert.Equal(HttpStatusCode.NoContent, (await SetRadius(cRep, null)).StatusCode);
        Assert.Equal(20, (await GetAsync(cRep)).GeofenceRadiusMeters);

        // Manager: any customer of the tenant, placed manually
        As(manager);
        Assert.Equal(HttpStatusCode.NoContent, (await SetLocation(cBaghdad, new { Latitude = 33.3152, Longitude = 44.3661, Source = "manual" })).StatusCode);
        Assert.Equal(("located", "manual"), ((await GetAsync(cBaghdad)).Status, (await GetAsync(cBaghdad)).LocationSource));

        await using (var conn = new NpgsqlConnection(schemaBlock.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(@"
                SELECT action, actor_id, entity_id FROM audit_logs WHERE tenant_id = @t1 AND action LIKE 'customer.%' ORDER BY occurred_at, action;", conn);
            cmd.Parameters.AddWithValue("t1", t1);
            await using var reader = await cmd.ExecuteReaderAsync();
            var rows = new List<(string, Guid, string)>();
            while (await reader.ReadAsync()) rows.Add((reader.GetString(0), reader.GetGuid(1), reader.GetString(2)));
            Assert.Equal(new[]
            {
                ("customer.location_changed", rep, cRep.ToString()),
                ("customer.geofence_changed", supervisor, cRep.ToString()),
                ("customer.geofence_changed", supervisor, cRep.ToString()),
                ("customer.location_changed", manager, cBaghdad.ToString())
            }, rows.ToArray());
        }
    }

    [SkippableFact]
    public async Task VisitReport_SavedByAssigneeOnlyInVisitAndAudited()
    {
        var url = GetRequiredEnvVar("TEST_DATABASE_URL");
        await VerifyRoleAsync(url, expectElevated: false);

        await using var schemaBlock = await SetupIsolatedSchemaAsync(url);
        await using var factory = new WebApplicationFactory<LifeDrop.API.Program>()
            .WithWebHostBuilder(webBuilder =>
            {
                webBuilder.UseSetting("ConnectionStrings:LifeDrop", schemaBlock.ConnectionString);
                TestHostSettings.UseTestJwt(webBuilder);
                webBuilder.ConfigureTestServices(services =>
                {
                    var descriptor = System.Linq.Enumerable.SingleOrDefault(services, d => d.ServiceType == typeof(LifeDrop.Application.Auth.ISessionStore));
                    if (descriptor != null) services.Remove(descriptor);
                    services.AddScoped<LifeDrop.Application.Auth.ISessionStore, FakeSessionStore>();
                });
            });
        var conf = factory.Services.GetRequiredService<IConfiguration>();
        var client = factory.CreateClient();

        var t1 = Guid.NewGuid();
        Guid manager = Guid.NewGuid(), rep = Guid.NewGuid(), otherRep = Guid.NewGuid(), customer = Guid.NewGuid();
        Guid inVisit = Guid.NewGuid(), arrived = Guid.NewGuid(), completed = Guid.NewGuid();
        await using (var conn = new NpgsqlConnection(schemaBlock.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(@"
                INSERT INTO tenants (id, name, code) VALUES (@t1, 'T1', 'T1');
                SELECT seed_default_roles(@t1);
                INSERT INTO users (id, tenant_id) VALUES (@m, @t1), (@r, @t1), (@o, @t1);
                INSERT INTO user_roles (tenant_id, user_id, role_id) SELECT @t1, @m, id FROM roles WHERE tenant_id = @t1 AND name = 'SalesManager';
                SELECT set_config('app.current_tenant', @t1::text, false);
                INSERT INTO customers (id, tenant_id, type, code, name) VALUES (@c, @t1, 'Pharmacy', 'C1', 'Customer');
                INSERT INTO visits (id, tenant_id, user_id, customer_id, status, schedule_date) VALUES
                    (@vi, @t1, @r, @c, 'InVisit', '2026-10-05'), (@va, @t1, @r, @c, 'Arrived', '2026-10-05'), (@vc, @t1, @r, @c, 'Completed', '2026-10-05');", conn);
            foreach (var (name, value) in new[] { ("t1", t1), ("m", manager), ("r", rep), ("o", otherRep), ("c", customer), ("vi", inVisit), ("va", arrived), ("vc", completed) })
                cmd.Parameters.AddWithValue(name, value);
            await cmd.ExecuteNonQueryAsync();
        }

        void As(Guid user) => client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            GenerateToken(t1, user, conf["Jwt:SigningKey"]!, conf["Jwt:Issuer"]!, conf["Jwt:Audience"]!));
        Task<HttpResponseMessage> Save(Guid visit, object body) => client.PutAsJsonAsync($"/visits/{visit}/report", body);

        // Only the assignee, only while InVisit
        As(otherRep);
        await AssertProblemAsync(await Save(inVisit, new { Outcome = "other", Note = "Not my visit" }), HttpStatusCode.Forbidden, "FORBIDDEN");
        As(rep);
        await AssertProblemAsync(await Save(arrived, new { Outcome = "other", Note = "Too early" }), HttpStatusCode.BadRequest, "INVALID_STATE");
        await AssertProblemAsync(await Save(completed, new { Outcome = "other", Note = "Too late" }), HttpStatusCode.BadRequest, "INVALID_STATE");
        await AssertProblemAsync(await Save(Guid.NewGuid(), new { Outcome = "other", Note = "Unknown visit" }), HttpStatusCode.NotFound, "NOT_FOUND");

        // Empty, malformed and out-of-list values are rejected by the API (PRD §5.3)
        foreach (var invalid in new object[]
        {
            new { },
            new { Outcome = "order_placed", Note = "No such outcome" },
            new { Outcome = "other", Note = " " },
            new { Outcome = "other", Note = "?!." },
            new { Outcome = "other", Note = "Valid note", ContractVersion = 2 }
        })
            await AssertProblemAsync(await Save(inVisit, invalid), HttpStatusCode.BadRequest, "INVALID_DATA");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsync($"/visits/{inVisit}/report",
            new StringContent("{not json", System.Text.Encoding.UTF8, "application/json"))).StatusCode);
        await AssertProblemAsync(await client.GetAsync($"/visits/{inVisit}/report"), HttpStatusCode.NotFound, "NOT_FOUND");

        // Save, then edit while still InVisit
        Assert.Equal(HttpStatusCode.NoContent, (await Save(inVisit, new { Outcome = "products_presented", Note = "Presented the new range" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Save(inVisit, new { Outcome = "order_intent", Note = "  Will order next week  ", ContractVersion = 1 })).StatusCode);
        var saved = (await (await client.GetAsync($"/visits/{inVisit}/report")).Content.ReadFromJsonAsync<LifeDrop.Application.Visits.VisitReportDto>())!;
        Assert.Equal(("order_intent", "Will order next week", 1, rep), (saved.Outcome, saved.Note, saved.ContractVersion, saved.SubmittedBy));

        // The manager (tenant scope) can read it; another rep cannot
        As(manager);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/visits/{inVisit}/report")).StatusCode);
        As(otherRep);
        await AssertProblemAsync(await client.GetAsync($"/visits/{inVisit}/report"), HttpStatusCode.NotFound, "NOT_FOUND");

        // Each successful save is audited; rejected ones are not
        await using (var conn = new NpgsqlConnection(schemaBlock.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand("SELECT count(*) FROM audit_logs WHERE tenant_id = @t1 AND action = 'visit.report_saved' AND actor_id = @r AND entity_id = @v;", conn);
            cmd.Parameters.AddWithValue("t1", t1);
            cmd.Parameters.AddWithValue("r", rep);
            cmd.Parameters.AddWithValue("v", inVisit.ToString());
            Assert.Equal(2L, (long)(await cmd.ExecuteScalarAsync())!);
        }
    }

    // Security baseline (plan step 0.9): every route requires an authenticated user unless it is on this
    // reviewed list of public routes. A new route without RequireAuthorization() fails here.
    [SkippableFact]
    public async Task EveryRouteRequiresAuthorizationExceptReviewedPublicRoutes()
    {
        var url = GetRequiredEnvVar("TEST_DATABASE_URL");
        await VerifyRoleAsync(url, expectElevated: false);

        await using var schemaBlock = await SetupIsolatedSchemaAsync(url);
        await using var factory = new WebApplicationFactory<LifeDrop.API.Program>()
            .WithWebHostBuilder(webBuilder =>
            {
                webBuilder.UseSetting("ConnectionStrings:LifeDrop", schemaBlock.ConnectionString);
                TestHostSettings.UseTestJwt(webBuilder);
            });
        _ = factory.CreateClient();

        var publicRoutes = new[]
        {
            "GET /", "POST /auth/login", "POST /auth/refresh", "POST /auth/devices/register",
            "GET /openapi/{documentName}.json" // Development only
        };

        var routes = factory.Services.GetRequiredService<Microsoft.AspNetCore.Routing.EndpointDataSource>().Endpoints
            .OfType<Microsoft.AspNetCore.Routing.RouteEndpoint>()
            .Select(e => (
                Name: $"{string.Join(",", e.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.HttpMethodMetadata>()?.HttpMethods ?? new[] { "ANY" })} /{e.RoutePattern.RawText?.TrimStart('/')}",
                Protected: e.Metadata.GetMetadata<Microsoft.AspNetCore.Authorization.IAuthorizeData>() is not null
                           && e.Metadata.GetMetadata<Microsoft.AspNetCore.Authorization.IAllowAnonymous>() is null))
            .ToList();

        Assert.True(routes.Count > 20, $"Expected the API's routes, found {routes.Count}.");
        var unprotected = routes.Where(r => !r.Protected).Select(r => r.Name).OrderBy(n => n).ToArray();
        Assert.Equal(publicRoutes.OrderBy(n => n).ToArray(), unprotected);
    }

    // Contract drift check (plan step 0.5b): the OpenAPI document generated by the running API
    // must equal the committed docs/api/openapi-v1.json. After an intended API change, regenerate with
    // UPDATE_OPENAPI_SNAPSHOT=1 and review the diff of that file in the PR.
    [SkippableFact]
    public async Task OpenApiDocument_MatchesCommittedContract()
    {
        var url = GetRequiredEnvVar("TEST_DATABASE_URL");
        await VerifyRoleAsync(url, expectElevated: false);

        await using var schemaBlock = await SetupIsolatedSchemaAsync(url);
        await using var factory = new WebApplicationFactory<LifeDrop.API.Program>()
            .WithWebHostBuilder(webBuilder =>
            {
                webBuilder.UseSetting("ConnectionStrings:LifeDrop", schemaBlock.ConnectionString);
                TestHostSettings.UseTestJwt(webBuilder);
            });
        var client = factory.CreateClient();

        var response = await client.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var actual = NormalizeOpenApi(await response.Content.ReadAsStringAsync());

        var snapshotPath = Path.Combine(FindRepositoryRoot(), "docs", "api", "openapi-v1.json");
        if (Environment.GetEnvironmentVariable("UPDATE_OPENAPI_SNAPSHOT") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(snapshotPath)!);
            await File.WriteAllTextAsync(snapshotPath, actual);
        }

        Assert.True(File.Exists(snapshotPath), $"Missing {snapshotPath}; run once with UPDATE_OPENAPI_SNAPSHOT=1.");
        var expected = (await File.ReadAllTextAsync(snapshotPath)).ReplaceLineEndings("\n");
        Assert.True(expected == actual,
            "The API contract changed. If intended, regenerate docs/api/openapi-v1.json with UPDATE_OPENAPI_SNAPSHOT=1 and review the diff.");
    }

    private static string NormalizeOpenApi(string json)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
        node.Remove("servers"); // depends on the test host address, not on the contract
        return node.ToJsonString(new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        }).ReplaceLineEndings("\n") + "\n";
    }

    private static string FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "LifeDrop.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root (LifeDrop.slnx) not found.");
    }

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode expectedStatus, string expectedCode)
    {
        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonDocument>();
        var root = body!.RootElement;
        Assert.Equal((int)expectedStatus, root.GetProperty("status").GetInt32());
        Assert.Equal(expectedCode, root.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("title").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("traceId").GetString()));
    }

    private static async Task<(string Status, string? Reason)> ReadVisitStatusAsync(string connectionString, Guid tenantId, Guid visitId)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using (var setCmd = new NpgsqlCommand("SELECT set_config('app.current_tenant', @t::text, false);", conn))
        {
            setCmd.Parameters.AddWithValue("t", tenantId);
            await setCmd.ExecuteNonQueryAsync();
        }

        await using var cmd = new NpgsqlCommand("SELECT status, exception_reason FROM visits WHERE id = @v;", conn);
        cmd.Parameters.AddWithValue("v", visitId);
        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), "Visit row not found.");
        return (reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1));
    }

    [SkippableFact]
    public async Task CustomerApi_HostsMaintainIndependentSecurityStates()
    {
        var normalUrl = GetRequiredEnvVar("TEST_DATABASE_URL");
        var superUrl = GetRequiredEnvVar("TEST_SUPERUSER_DATABASE_URL");
        
        await VerifyRoleAsync(normalUrl, expectElevated: false);
        await VerifyRoleAsync(superUrl, expectElevated: true);

        await using var schemaBlockNormal = await SetupIsolatedSchemaAsync(normalUrl);
        await using var schemaBlockSuper = await SetupIsolatedSchemaAsync(superUrl);
        
        // Ensure independence by creating them in both orders in separate assertions.
        // Pass 1: Normal then Super
        await ExecuteHostIndependenceAssertion(schemaBlockNormal.ConnectionString, schemaBlockSuper.ConnectionString, reverseOrder: false);
        // Pass 2: Super then Normal
        await ExecuteHostIndependenceAssertion(schemaBlockNormal.ConnectionString, schemaBlockSuper.ConnectionString, reverseOrder: true);
    }

    private async Task ExecuteHostIndependenceAssertion(string connectionStringNormal, string connectionStringSuper, bool reverseOrder)
    {
        WebApplicationFactory<LifeDrop.API.Program> factoryNormal = null!;
        WebApplicationFactory<LifeDrop.API.Program> factorySuper = null!;

        try
        {
            Action createNormal = () => {
                factoryNormal = new WebApplicationFactory<LifeDrop.API.Program>()
                    .WithWebHostBuilder(webBuilder =>
                    {
                        webBuilder.UseSetting("ConnectionStrings:LifeDrop", connectionStringNormal);
                        TestHostSettings.UseTestJwt(webBuilder);
                        webBuilder.ConfigureTestServices(services =>
                        {
                            var descriptor = System.Linq.Enumerable.SingleOrDefault(services, d => d.ServiceType == typeof(LifeDrop.Application.Auth.ISessionStore));
                            if (descriptor != null) services.Remove(descriptor);
                            services.AddScoped<LifeDrop.Application.Auth.ISessionStore, FakeSessionStore>();
                        });
                    });
            };

            Action createSuper = () => {
                factorySuper = new WebApplicationFactory<LifeDrop.API.Program>()
                    .WithWebHostBuilder(webBuilder =>
                    {
                        webBuilder.UseSetting("ConnectionStrings:LifeDrop", connectionStringSuper);
                        TestHostSettings.UseTestJwt(webBuilder);
                        webBuilder.ConfigureTestServices(services =>
                        {
                            var descriptor = System.Linq.Enumerable.SingleOrDefault(services, d => d.ServiceType == typeof(LifeDrop.Application.Auth.ISessionStore));
                            if (descriptor != null) services.Remove(descriptor);
                            services.AddScoped<LifeDrop.Application.Auth.ISessionStore, FakeSessionStore>();
                        });
                    });
            };

            if (reverseOrder) { createSuper(); createNormal(); }
            else { createNormal(); createSuper(); }

            var conf = factoryNormal.Services.GetRequiredService<IConfiguration>();
            var signingKey = conf["Jwt:SigningKey"];
            var issuer = conf["Jwt:Issuer"];
            var audience = conf["Jwt:Audience"];

            var clientNormal = factoryNormal.CreateClient();
            var clientSuper = factorySuper.CreateClient();

            var adminId = Guid.NewGuid();
            var tenantId = Guid.NewGuid();

            // Give adminId roles in normal DB
            await using (var conn = new NpgsqlConnection(connectionStringNormal))
            {
                await conn.OpenAsync();
                await using var cmd = new NpgsqlCommand(@"
                    INSERT INTO tenants (id, name, code) VALUES (@t1, 'T1', @t1Code) ON CONFLICT DO NOTHING;
                    INSERT INTO users (id, tenant_id) VALUES (@a1, @t1) ON CONFLICT DO NOTHING;
                    INSERT INTO roles (id, tenant_id, name) VALUES (@r1, @t1, 'DevAdmin') ON CONFLICT DO NOTHING;
                    INSERT INTO role_permissions (tenant_id, role_id, permission_key) VALUES (@t1, @r1, 'customers.read'), (@t1, @r1, 'customers.write') ON CONFLICT DO NOTHING;
                    INSERT INTO user_roles (tenant_id, user_id, role_id) VALUES (@t1, @a1, @r1) ON CONFLICT DO NOTHING;
                ", conn);
                cmd.Parameters.AddWithValue("t1", tenantId);
                cmd.Parameters.AddWithValue("t1Code", "T1_" + tenantId.ToString());
                cmd.Parameters.AddWithValue("a1", adminId);
                cmd.Parameters.AddWithValue("r1", Guid.NewGuid());
                await cmd.ExecuteNonQueryAsync();
            }

            var token = GenerateToken(tenantId, adminId, signingKey!, issuer!, audience!);
            var req = new CreateCustomerRequest("Clinic_" + Guid.NewGuid(), "C0" + Guid.NewGuid().ToString().Substring(0, 4), "Test Clinic");

            // Normal client should execute successfully
            clientNormal.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var respNormal = await clientNormal.PostAsJsonAsync("/customers", req);
            Assert.Equal(HttpStatusCode.Created, respNormal.StatusCode);

            // Superuser client should fail closed
            clientSuper.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var respSuper = await clientSuper.PostAsJsonAsync("/customers", req);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, respSuper.StatusCode);
        }
        finally
        {
            if (factoryNormal != null) await factoryNormal.DisposeAsync();
            if (factorySuper != null) await factorySuper.DisposeAsync();
        }
    }

    [SkippableFact]
    public async Task CustomerApi_Migration0005_GrantsOnlyToDevTenant()
    {
        var url = GetRequiredEnvVar("TEST_DATABASE_URL");
        await VerifyRoleAsync(url, expectElevated: false);
        
        await using var schemaBlock = await SetupIsolatedSchemaAsync(url);
        
        await using (var conn = new NpgsqlConnection(schemaBlock.ConnectionString))
        {
            await conn.OpenAsync();
            var devTenantId = Guid.NewGuid();
            var devRoleId = Guid.NewGuid();
            var otherTenantId = Guid.NewGuid();
            var otherRoleId = Guid.NewGuid();

            // Insert matching role 'DevAdmin' into two different tenants, but only one is 'dev'.
            await using (var cmd = new NpgsqlCommand(@"
                INSERT INTO tenants (id, name, code) VALUES (@d1, 'Dev', 'dev'), (@o1, 'Other', 'other');
                INSERT INTO roles (id, tenant_id, name) VALUES (@r1, @d1, 'DevAdmin'), (@r2, @o1, 'DevAdmin');
            ", conn))
            {
                cmd.Parameters.AddWithValue("d1", devTenantId);
                cmd.Parameters.AddWithValue("r1", devRoleId);
                cmd.Parameters.AddWithValue("o1", otherTenantId);
                cmd.Parameters.AddWithValue("r2", otherRoleId);
                await cmd.ExecuteNonQueryAsync();
            }

            // Re-apply migration 0005 script explicitly against the populated test tables.
            var m5 = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "0005_dev_admin_customer_permissions.sql"));
            await using (var cmd = new NpgsqlCommand(m5, conn))
            {
                await cmd.ExecuteNonQueryAsync();
            }

            // Verify
            await using (var cmd = new NpgsqlCommand(@"
                SELECT tenant_id, COUNT(*) 
                FROM role_permissions 
                WHERE permission_key IN ('customers.read', 'customers.write')
                GROUP BY tenant_id;
            ", conn))
            {
                await using var reader = await cmd.ExecuteReaderAsync();
                int rows = 0;
                while (await reader.ReadAsync())
                {
                    rows++;
                    var tid = reader.GetGuid(0);
                    var count = reader.GetInt64(1);
                    
                    if (tid == devTenantId)
                    {
                        Assert.Equal(2, count); // 'customers.read' and 'customers.write'
                    }
                    else if (tid == otherTenantId)
                    {
                        throw new Exception("Migration 0005 incorrectly granted permissions to tenant code 'other'.");
                    }
                    else
                    {
                        throw new Exception("Unknown tenant found in role_permissions.");
                    }
                }
                Assert.Equal(1, rows); // Only devTenantId should be in the result
            }
        }
    }

    [SkippableFact]
    public async Task CustomerApi_SchemaCleanedUpOnMigrationFailure()
    {
        var url = GetRequiredEnvVar("TEST_DATABASE_URL");
        await VerifyRoleAsync(url, expectElevated: false);
        
        string expectedSchemaName = null;
        try
        {
            await SetupIsolatedSchemaAsync(url, "CREATE TABLE this_is_invalid_sql ( id ); SYNTAX ERROR!!!");
            Assert.Fail("SetupIsolatedSchemaAsync should have failed.");
        }
        catch (PostgresException ex)
        {
            expectedSchemaName = ex.Data["SchemaName"] as string;
            Assert.NotNull(expectedSchemaName);
        }

        await using var conn = new NpgsqlConnection(url);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT schema_name FROM information_schema.schemata WHERE schema_name = @schema", conn);
        cmd.Parameters.AddWithValue("schema", expectedSchemaName);
        
        var existingSchema = await cmd.ExecuteScalarAsync();
        Assert.Null(existingSchema); // Schema should have been dropped
    }
}










