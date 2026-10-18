using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
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

            // Every test has its own pool (its own search path). Closing the idle connections keeps the whole run under
            // PostgreSQL's connection limit (100 by default, also in CI).
            await using var poolKey = new NpgsqlConnection(ConnectionString);
            NpgsqlConnection.ClearPool(poolKey);
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
        // Every test has its own pools; idle connections close after a few seconds, so the whole run stays under PostgreSQL's limit of 100 connections (also in CI).
        builder.ConnectionIdleLifetime = 3;
        builder.ConnectionPruningInterval = 1;
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
                var m16 = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "0016_shifts_tracking.sql"));
                var m17 = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "0017_offline_checkpoints.sql"));
                var m18 = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "0018_customer_approval.sql"));
                var m19 = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "0019_live_map.sql"));
                var m20 = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "0020_flexible_planning.sql"));
                var m21 = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "0021_places_customer_profile.sql"));
                var m22 = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "0022_region_rep_notifications.sql"));
                var m23 = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "0023_customer_priority.sql"));
                var m24 = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "0024_catalog_prices.sql"));
                var m25 = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "0025_city_prices_discounts.sql"));
                var m26 = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "0026_orders_warehouses.sql"));
                var m27 = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "0027_warehouse_work.sql"));
                var m28 = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "0028_delivery.sql"));
                var m29 = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "0029_payments.sql"));
                var m30 = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "0030_samples.sql"));
                var m31 = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "0031_visit_report_details.sql"));
                var m32 = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "0032_follow_ups.sql"));
                var m33 = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "0033_alerts.sql"));
                var m34 = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "0034_antifraud.sql"));
                var m35 = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "0035_route_history.sql"));
                var m36 = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "0036_reports.sql"));
                var m37 = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "0037_push.sql"));
                var m38 = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "0038_platform.sql"));
                var m39 = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "0039_saved_reports.sql"));
                var m40 = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "0040_custom_fields.sql"));
                var m41 = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "0041_antifraud_visits.sql"));
                var m42 = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "0042_channels.sql"));
                migrations = m1 + m2 + m3 + m4 + m5 + m6 + m7 + m8 + m9 + m10 + m11 + m12 + m13 + m14 + m15 + m16 + m17 + m18 + m19 + m20 + m21 + m22 + m23 + m24 + m25 + m26 + m27 + m28 + m29 + m30 + m31 + m32 + m33 + m34 + m35 + m36 + m37 + m38 + m39 + m40 + m41 + m42;
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
    public async Task MobileClient_RunsAWholeVisitAgainstTheRealApi()
    {
        // Plan step 1.12: the mobile library's requests and error mapping match the server contract end to end.
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

        Guid t1 = Guid.NewGuid(), rep = Guid.NewGuid(), device = Guid.NewGuid(), customer = Guid.NewGuid(), visit = Guid.NewGuid();
        await using (var conn = new NpgsqlConnection(schemaBlock.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(@"
                INSERT INTO tenants (id, name, code) VALUES (@t1, 'T1', 'T1');
                INSERT INTO users (id, tenant_id) VALUES (@r, @t1);
                INSERT INTO devices (id, tenant_id, user_id, device_label, platform, install_id, app_version, secret_hash, status)
                VALUES (@d, @t1, @r, 'Phone', 'android', 'install-1', '1.0', '\x00', 'approved');
                SELECT set_config('app.current_tenant', @t1::text, false);
                INSERT INTO customers (id, tenant_id, type, code, name, geom, geofence_radius_m, status)
                VALUES (@c, @t1, 'Pharmacy', 'C-M', 'Mobile Pharmacy', ST_SetSRID(ST_MakePoint(44.0092, 36.1911), 4326)::geography, 30, 'located');
                INSERT INTO visits (id, tenant_id, user_id, customer_id, status, schedule_date) VALUES (@v, @t1, @r, @c, 'Planned', '2026-10-05');", conn);
            foreach (var (name, value) in new[] { ("t1", t1), ("r", rep), ("d", device), ("c", customer), ("v", visit) })
                cmd.Parameters.AddWithValue(name, value);
            await cmd.ExecuteNonQueryAsync();
        }

        var token = GenerateToken(t1, rep, conf["Jwt:SigningKey"]!, conf["Jwt:Issuer"]!, conf["Jwt:Audience"]!, deviceId: device);
        var api = new LifeDrop.Mobile.Visits.VisitsApiClient(factory.CreateClient(), new StaticMobileSession(token));
        var inside = new LifeDrop.Mobile.Visits.DeviceFix(36.1912, 44.0093, 8, now.AddSeconds(-5), false);   // ~14 m
        var outside = new LifeDrop.Mobile.Visits.DeviceFix(36.1916, 44.0092, 8, now.AddSeconds(-5), false);  // ~55 m
        var location = new SwitchableLocation(outside);

        // The daily plan as the phone sees it
        var plan = await api.GetDailyPlanAsync(new DateOnly(2026, 10, 5));
        Assert.Equal(LifeDrop.Mobile.Visits.PlanLoadStatus.Success, plan.Status);
        var planned = Assert.Single(plan.Visits);
        Assert.Equal(("Mobile Pharmacy", "C-M", "Planned"), (planned.CustomerName, planned.CustomerCode, planned.Status));

        var lifecycle = new LifeDrop.Mobile.Visits.VisitLifecycle(api, planned, location, () => "replay-key");
        await lifecycle.RunNextStepAsync();   // en-route
        await lifecycle.RunNextStepAsync();   // arrive
        await lifecycle.RunNextStepAsync();   // check-in outside the radius
        Assert.Equal("Arrived", lifecycle.Visit.Status);
        Assert.StartsWith("You are not at the customer's location.", lifecycle.Message);
        location.Fix = inside;
        await lifecycle.RunNextStepAsync();   // check-in inside
        Assert.Equal("InVisit", lifecycle.Visit.Status);

        // A lost reply: the same key again is accepted as a replay, with no second record
        Assert.Equal(LifeDrop.Mobile.Visits.VisitActionResult.Success, await api.CheckInAsync(visit, inside, "replay-key"));
        Assert.Equal(LifeDrop.Mobile.Visits.VisitActionResult.InvalidState, await api.CheckInAsync(visit, inside, "other-key"));

        // An invalid report is refused by the server and check-out is not started
        Assert.Equal(LifeDrop.Mobile.Visits.VisitActionResult.InvalidData,
            await api.SaveReportAsync(visit, new LifeDrop.Mobile.Visits.VisitReportDraft("order_confirmed", "Ordered")));
        Assert.Equal(LifeDrop.Mobile.Visits.VisitActionResult.ReportRequired, await api.CheckOutAsync(visit, inside, "no-report"));

        // Report saved, check-out refused outside: partial success, then check-out alone succeeds inside
        location.Fix = outside;
        await lifecycle.SubmitReportAndCheckOutAsync("products_presented", "Presented the new range");
        Assert.True(lifecycle.ReportSaved);
        Assert.Equal("InVisit", lifecycle.Visit.Status);
        Assert.Equal((LifeDrop.Mobile.Visits.ReportLoadStatus.Found, new LifeDrop.Mobile.Visits.VisitReportDraft("products_presented", "Presented the new range")),
            await api.GetReportAsync(visit));
        location.Fix = inside;
        await lifecycle.RetryCheckOutAsync();
        Assert.Equal("Completed", lifecycle.Visit.Status);

        await using (var conn = new NpgsqlConnection(schemaBlock.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(@"
                SELECT set_config('app.current_tenant', @t1::text, false);
                SELECT (SELECT status FROM visits WHERE id = @v),
                       (SELECT string_agg(kind || ':' || coalesce(rejection_reason, 'accepted'), ',' ORDER BY received_at, kind COLLATE ""C"")
                        FROM visit_location_events WHERE visit_id = @v),
                       (SELECT string_agg(coalesce(from_status, '-') || '>' || to_status, ',' ORDER BY occurred_at) FROM visit_status_history WHERE visit_id = @v);", conn);
            cmd.Parameters.AddWithValue("v", visit);
            cmd.Parameters.AddWithValue("t1", t1);
            await using var reader = await cmd.ExecuteReaderAsync();
            await reader.NextResultAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal("Completed", reader.GetString(0));
            Assert.Equal("check_in:outside_geofence,check_in:accepted,check_out:outside_geofence,check_out:accepted", reader.GetString(1));
            Assert.Equal("->Planned,Planned>EnRoute,EnRoute>Arrived,Arrived>InVisit,InVisit>Completed", reader.GetString(2));
        }
    }

    private sealed class MobileMonotonic : LifeDrop.Mobile.Offline.IMonotonicClock
    {
        public long ElapsedMs { get; set; }
        public string? BootId { get; set; }
    }

    private sealed class StaticMobileSession(string token) : LifeDrop.Mobile.Auth.IAuthSession
    {
        public Task<LifeDrop.Mobile.Auth.Models.AuthResult<string>> GetAccessTokenAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(LifeDrop.Mobile.Auth.Models.AuthResult<string>.Ok(token));
    }

    private sealed class SwitchableLocation(LifeDrop.Mobile.Visits.DeviceFix fix) : LifeDrop.Mobile.Visits.ILocationSource
    {
        public LifeDrop.Mobile.Visits.DeviceFix Fix { get; set; } = fix;
        public Task<LifeDrop.Mobile.Visits.FixResult> GetFixAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(LifeDrop.Mobile.Visits.FixResult.Ok(Fix));
    }

    [SkippableFact]
    public async Task Shift_StartTrackEndWithDeviceDedupAndSummary()
    {
        // Plan step 1.13, decision D1: tracking only inside an explicit shift, from the bound device.
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

        Guid t1 = Guid.NewGuid(), t2 = Guid.NewGuid(), rep = Guid.NewGuid(), other = Guid.NewGuid(), foreign = Guid.NewGuid();
        Guid device = Guid.NewGuid(), device2 = Guid.NewGuid(), otherDevice = Guid.NewGuid(), foreignDevice = Guid.NewGuid();
        await using (var conn = new NpgsqlConnection(schemaBlock.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(@"
                INSERT INTO tenants (id, name, code) VALUES (@t1, 'T1', 'T1'), (@t2, 'T2', 'T2');
                INSERT INTO users (id, tenant_id) VALUES (@r, @t1), (@o, @t1), (@f, @t2);
                INSERT INTO devices (id, tenant_id, user_id, device_label, platform, install_id, app_version, secret_hash, status) VALUES
                    (@d, @t1, @r, 'Phone', 'android', 'install-1', '1.0', '\x00', 'approved'),
                    (@d2, @t1, @r, 'Phone 2', 'android', 'install-2', '1.0', '\x00', 'approved'),
                    (@od, @t1, @o, 'Other', 'android', 'install-3', '1.0', '\x00', 'approved'),
                    (@fd, @t2, @f, 'Foreign', 'android', 'install-4', '1.0', '\x00', 'approved');", conn);
            foreach (var (name, value) in new[] { ("t1", t1), ("t2", t2), ("r", rep), ("o", other), ("f", foreign), ("d", device), ("d2", device2), ("od", otherDevice), ("fd", foreignDevice) })
                cmd.Parameters.AddWithValue(name, value);
            await cmd.ExecuteNonQueryAsync();
        }

        void As(Guid tenant, Guid user, Guid? dev) => client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            GenerateToken(tenant, user, conf["Jwt:SigningKey"]!, conf["Jwt:Issuer"]!, conf["Jwt:Audience"]!, deviceId: dev));
        object Point(Guid id, DateTimeOffset at, double lat = 36.19, double lon = 44.01, double accuracy = 12) =>
            new { ClientPointId = id, Latitude = lat, Longitude = lon, AccuracyMeters = accuracy, CapturedAt = at, IsMocked = false };
        async Task<LifeDrop.Application.Tracking.UploadPointsResponse> Upload(Guid shift, params object[] points)
        {
            var response = await client.PostAsJsonAsync($"/shifts/{shift}/points", new { Points = points });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<LifeDrop.Application.Tracking.UploadPointsResponse>())!;
        }

        // A token without a bound device cannot start a shift
        As(t1, rep, null);
        await AssertProblemAsync(await client.PostAsync("/shifts", null), HttpStatusCode.Forbidden, "DEVICE_REQUIRED");

        // Start, and a repeated start returns the same open shift
        As(t1, rep, device);
        await AssertProblemAsync(await client.GetAsync("/shifts/current"), HttpStatusCode.NotFound, "NOT_FOUND");
        var started = await client.PostAsync("/shifts", null);
        Assert.Equal(HttpStatusCode.Created, started.StatusCode);
        var shift = (await started.Content.ReadFromJsonAsync<LifeDrop.Application.Tracking.ShiftDto>())!;
        Assert.Equal(device, shift.DeviceId);
        var again = await client.PostAsync("/shifts", null);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(shift.Id, (await again.Content.ReadFromJsonAsync<LifeDrop.Application.Tracking.ShiftDto>())!.Id);
        Assert.Equal(shift.Id, (await client.GetFromJsonAsync<LifeDrop.Application.Tracking.ShiftDto>("/shifts/current"))!.Id);

        // Upload: valid points stored once; malformed or outside the shift are counted as rejected
        Guid p1 = Guid.NewGuid(), p2 = Guid.NewGuid(), p3 = Guid.NewGuid();
        var t0 = shift.StartedAt;
        var first = await Upload(shift.Id,
            Point(p1, t0.AddSeconds(1)), Point(p2, t0.AddSeconds(2)), Point(p2, t0.AddSeconds(2)),  // duplicate in the batch
            Point(Guid.NewGuid(), t0.AddSeconds(-60)),                                              // before the shift
            Point(Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(5)),                            // in the future
            Point(Guid.NewGuid(), t0.AddSeconds(3), lat: 0, lon: 0),                               // 0,0
            Point(Guid.NewGuid(), t0.AddSeconds(3), accuracy: 0),                                  // no accuracy
            Point(Guid.Empty, t0.AddSeconds(3)));                                                  // no client id
        Assert.Equal(new LifeDrop.Application.Tracking.UploadPointsResponse(2, 1, 5), first);
        // A retried batch is not stored twice
        Assert.Equal(new LifeDrop.Application.Tracking.UploadPointsResponse(1, 2, 0), await Upload(shift.Id, Point(p1, t0.AddSeconds(1)), Point(p2, t0.AddSeconds(2)), Point(p3, t0.AddSeconds(4))));

        // Batch limits
        await AssertProblemAsync(await client.PostAsJsonAsync($"/shifts/{shift.Id}/points", new { Points = Array.Empty<object>() }), HttpStatusCode.BadRequest, "INVALID_DATA");
        await AssertProblemAsync(await client.PostAsJsonAsync($"/shifts/{shift.Id}/points",
            new { Points = Enumerable.Range(0, 201).Select(i => Point(Guid.NewGuid(), t0.AddSeconds(1))).ToArray() }), HttpStatusCode.BadRequest, "INVALID_DATA");

        // Another device of the same user, another user, another tenant
        As(t1, rep, device2);
        await AssertProblemAsync(await client.PostAsJsonAsync($"/shifts/{shift.Id}/points", new { Points = new[] { Point(Guid.NewGuid(), t0.AddSeconds(5)) } }),
            HttpStatusCode.Forbidden, "DEVICE_MISMATCH");
        As(t1, other, otherDevice);
        await AssertProblemAsync(await client.PostAsJsonAsync($"/shifts/{shift.Id}/points", new { Points = new[] { Point(Guid.NewGuid(), t0.AddSeconds(5)) } }),
            HttpStatusCode.NotFound, "NOT_FOUND");
        await AssertProblemAsync(await client.GetAsync($"/shifts/{shift.Id}/summary"), HttpStatusCode.NotFound, "NOT_FOUND");
        await AssertProblemAsync(await client.PostAsync($"/shifts/{shift.Id}/end", null), HttpStatusCode.NotFound, "NOT_FOUND");
        As(t2, foreign, foreignDevice);
        await AssertProblemAsync(await client.GetAsync($"/shifts/{shift.Id}/summary"), HttpStatusCode.NotFound, "NOT_FOUND");

        // Summary and end; ending twice succeeds; points after the end are rejected
        As(t1, rep, device);
        var summary = (await client.GetFromJsonAsync<LifeDrop.Application.Tracking.ShiftSummaryDto>($"/shifts/{shift.Id}/summary"))!;
        Assert.Equal((shift.Id, 3), (summary.ShiftId, summary.PointCount));
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync($"/shifts/{shift.Id}/end", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync($"/shifts/{shift.Id}/end", null)).StatusCode);
        await AssertProblemAsync(await client.GetAsync("/shifts/current"), HttpStatusCode.NotFound, "NOT_FOUND");
        Assert.Equal(new LifeDrop.Application.Tracking.UploadPointsResponse(0, 0, 1),
            await Upload(shift.Id, Point(Guid.NewGuid(), DateTimeOffset.UtcNow.AddSeconds(20))));
        var ended = (await client.GetFromJsonAsync<LifeDrop.Application.Tracking.ShiftSummaryDto>($"/shifts/{shift.Id}/summary"))!;
        Assert.NotNull(ended.EndedAt);

        // A new shift can start after the end
        var next = await client.PostAsync("/shifts", null);
        Assert.Equal(HttpStatusCode.Created, next.StatusCode);
        Assert.NotEqual(shift.Id, (await next.Content.ReadFromJsonAsync<LifeDrop.Application.Tracking.ShiftDto>())!.Id);

        // The mobile tracker continues that shift, uploads its points and ends it through the real API (step 1.13b)
        var tracker = new LifeDrop.Mobile.Tracking.ShiftTracker(new LifeDrop.Mobile.Tracking.ShiftApiClient(factory.CreateClient(),
            new StaticMobileSession(GenerateToken(t1, rep, conf["Jwt:SigningKey"]!, conf["Jwt:Issuer"]!, conf["Jwt:Audience"]!, deviceId: device))),
            TimeProvider.System);
        Assert.Equal(LifeDrop.Mobile.Tracking.ShiftCallStatus.Success, await tracker.ResumeAsync());
        var mobileShift = tracker.Shift!.Id;
        // Times come from the shift start the server gave (not this computer's clock): the database may run in Docker/WSL2 with a clock a few milliseconds apart,
        // and the server refuses points captured before the shift started.
        for (var i = 0; i < 3; i++) tracker.AddFix(36.19 + i * 0.0001, 44.01, 9, tracker.Shift!.StartedAt.AddSeconds(1 + i), false);
        Assert.Equal(LifeDrop.Mobile.Tracking.ShiftCallStatus.Success, await tracker.EndAsync());
        Assert.Equal((0, 3), (tracker.QueuedPoints, tracker.SentPoints));
        var mobileSummary = (await client.GetFromJsonAsync<LifeDrop.Application.Tracking.ShiftSummaryDto>($"/shifts/{mobileShift}/summary"))!;
        Assert.Equal(3, mobileSummary.PointCount);
        Assert.NotNull(mobileSummary.EndedAt);

        await using (var conn = new NpgsqlConnection(schemaBlock.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(@"
                SELECT string_agg(action || '=' || n, ',' ORDER BY action COLLATE ""C"")
                FROM (SELECT action, count(*) AS n FROM audit_logs WHERE tenant_id = @t1 AND action LIKE 'shift.%' GROUP BY action) a;", conn);
            cmd.Parameters.AddWithValue("t1", t1);
            Assert.Equal("shift.ended=2,shift.started=2", (string?)await cmd.ExecuteScalarAsync());

            // At most one open shift per user, enforced by the database
            await using var dup = new NpgsqlCommand(@"
                SELECT set_config('app.current_tenant', @t1::text, false);
                INSERT INTO shifts (id, tenant_id, user_id, device_id) VALUES (gen_random_uuid(), @t1, @o, @od);
                INSERT INTO shifts (id, tenant_id, user_id, device_id) VALUES (gen_random_uuid(), @t1, @o, @od);", conn);
            dup.Parameters.AddWithValue("t1", t1);
            dup.Parameters.AddWithValue("o", other);
            dup.Parameters.AddWithValue("od", otherDevice);
            Assert.Equal("23505", (await Assert.ThrowsAsync<PostgresException>(() => dup.ExecuteNonQueryAsync())).SqlState);
        }
    }

    [SkippableFact]
    public async Task OfflineCheckpoints_AutoAcceptReviewOrRejectAndManagerDecides()
    {
        // Plan step 1.14, decision D5: offline check-in/out up to 24 h late; unconfirmed ones go to a manager.
        var url = GetRequiredEnvVar("TEST_DATABASE_URL");
        await VerifyRoleAsync(url, expectElevated: false);

        await using var schemaBlock = await SetupIsolatedSchemaAsync(url);
        var now = new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
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

        Guid t1 = Guid.NewGuid(), manager = Guid.NewGuid(), supervisorB = Guid.NewGuid(), rep = Guid.NewGuid(), device = Guid.NewGuid(), customer = Guid.NewGuid();
        Guid vTracked = Guid.NewGuid(), vUntracked = Guid.NewGuid(), vOld = Guid.NewGuid(), vOutside = Guid.NewGuid(), vOnline = Guid.NewGuid(), shift = Guid.NewGuid();
        Guid vMobile = Guid.NewGuid();
        await using (var conn = new NpgsqlConnection(schemaBlock.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(@"
                INSERT INTO tenants (id, name, code) VALUES (@t1, 'T1', 'T1');
                SELECT seed_default_roles(@t1);
                INSERT INTO users (id, tenant_id) VALUES (@m, @t1), (@sb, @t1), (@r, @t1);
                INSERT INTO user_roles (tenant_id, user_id, role_id)
                SELECT @t1, @m, id FROM roles WHERE tenant_id = @t1 AND name = 'SalesManager'
                UNION ALL SELECT @t1, @sb, id FROM roles WHERE tenant_id = @t1 AND name = 'Supervisor'
                UNION ALL SELECT @t1, @r, id FROM roles WHERE tenant_id = @t1 AND name = 'SalesRep';
                INSERT INTO devices (id, tenant_id, user_id, device_label, platform, install_id, app_version, secret_hash, status)
                VALUES (@d, @t1, @r, 'Phone', 'android', 'install-1', '1.0', '\x00', 'approved');
                SELECT set_config('app.current_tenant', @t1::text, false);
                INSERT INTO regions (id, tenant_id, name) VALUES ('77777777-0000-0000-0000-0000000000e1', @t1, 'Erbil'), ('77777777-0000-0000-0000-0000000000b1', @t1, 'Baghdad');
                INSERT INTO teams (id, tenant_id, region_id, name) VALUES
                    ('77777777-0000-0000-0000-0000000000a1', @t1, '77777777-0000-0000-0000-0000000000e1', 'Team A'),
                    ('77777777-0000-0000-0000-0000000000a2', @t1, '77777777-0000-0000-0000-0000000000b1', 'Team B');
                INSERT INTO team_members (tenant_id, user_id, team_id) VALUES
                    (@t1, @r, '77777777-0000-0000-0000-0000000000a1'), (@t1, @sb, '77777777-0000-0000-0000-0000000000a2');
                INSERT INTO customers (id, tenant_id, type, code, name, geom, geofence_radius_m, status)
                VALUES (@c, @t1, 'Pharmacy', 'C-O', 'Offline', ST_SetSRID(ST_MakePoint(44.0092, 36.1911), 4326)::geography, 30, 'located');
                INSERT INTO visits (id, tenant_id, user_id, customer_id, status, schedule_date) VALUES
                    (@v1, @t1, @r, @c, 'Arrived', '2026-10-05'), (@v2, @t1, @r, @c, 'Arrived', '2026-10-05'), (@v3, @t1, @r, @c, 'Arrived', '2026-10-05'),
                    (@v4, @t1, @r, @c, 'Arrived', '2026-10-05'), (@v5, @t1, @r, @c, 'Arrived', '2026-10-05'), (@v6, @t1, @r, @c, 'Arrived', '2026-10-05');
                -- The shift's tracking path passed the customer about an hour ago
                INSERT INTO shifts (id, tenant_id, user_id, device_id, started_at) VALUES (@s, @t1, @r, @d, @start);
                INSERT INTO tracking_points (tenant_id, shift_id, user_id, device_id, client_point_id, geom, accuracy_m, captured_at)
                VALUES (@t1, @s, @r, @d, gen_random_uuid(), ST_SetSRID(ST_MakePoint(44.0093, 36.1912), 4326)::geography, 12, @tp);", conn);
            foreach (var (name, value) in new[] { ("t1", t1), ("m", manager), ("sb", supervisorB), ("r", rep), ("d", device), ("c", customer),
                ("v1", vTracked), ("v2", vUntracked), ("v3", vOld), ("v4", vOutside), ("v5", vOnline), ("v6", vMobile), ("s", shift) })
                cmd.Parameters.AddWithValue(name, value);
            cmd.Parameters.AddWithValue("start", now.AddHours(-3));
            cmd.Parameters.AddWithValue("tp", now.AddMinutes(-62));
            await cmd.ExecuteNonQueryAsync();
        }

        var repToken = GenerateToken(t1, rep, conf["Jwt:SigningKey"]!, conf["Jwt:Issuer"]!, conf["Jwt:Audience"]!, deviceId: device);
        async Task<HttpResponseMessage> CheckIn(Guid visit, DateTimeOffset capturedAt, bool offline, long? elapsedMs, double lat = 36.1912, double lon = 44.0093)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/visits/{visit}/start")
            {
                Content = JsonContent.Create(new { Latitude = lat, Longitude = lon, AccuracyMeters = 10, CapturedAt = capturedAt, IsMocked = false,
                    IsOffline = offline, ElapsedSinceCaptureMs = elapsedMs })
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", repToken);
            request.Headers.Add("Idempotency-Key", $"k-{visit}");
            return await client.SendAsync(request);
        }
        async Task<LifeDrop.Application.Visits.OfflineCheckpointResponse> Ok(HttpResponseMessage response)
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<LifeDrop.Application.Visits.OfflineCheckpointResponse>())!;
        }

        // Tracking confirms it and the phone's monotonic clock agrees: accepted, no review
        var tracked = await Ok(await CheckIn(vTracked, now.AddMinutes(-60), true, 60 * 60_000));
        Assert.Equal((false, 0), (tracked.ReviewRequired, tracked.ReviewReasons.Count));
        // No tracking nearby and no monotonic time: accepted, but a manager must review it
        var untracked = await Ok(await CheckIn(vUntracked, now.AddHours(-3), true, null));
        Assert.True(untracked.ReviewRequired);
        Assert.Equal(new[] { "clock_mismatch", "no_tracking_nearby" }, untracked.ReviewReasons);
        // Older than 24 hours, or outside the radius: refused
        await AssertProblemAsync(await CheckIn(vOld, now.AddHours(-25), true, 25 * 3600_000L), (HttpStatusCode)422, "OFFLINE_EXPIRED");
        await AssertProblemAsync(await CheckIn(vOutside, now.AddMinutes(-60), true, 60 * 60_000, lat: 36.1916, lon: 44.0092), (HttpStatusCode)422, "OUTSIDE_GEOFENCE");
        // Online attempts keep the 2-minute freshness rule
        await AssertProblemAsync(await CheckIn(vOnline, now.AddMinutes(-60), false, null), (HttpStatusCode)422, "GPS_STALE");
        // A replay of an accepted offline check-in is a plain success
        Assert.Equal(HttpStatusCode.NoContent, (await CheckIn(vTracked, now.AddMinutes(-60), true, 60 * 60_000)).StatusCode);

        // The phone's outbox sends a check-in saved an hour ago without internet (step 1.14b) and the server accepts it
        var monotonic = new MobileMonotonic { ElapsedMs = 10_000_000, BootId = "boot" };
        var outbox = new LifeDrop.Mobile.Offline.Outbox(new LifeDrop.Mobile.Visits.VisitsApiClient(factory.CreateClient(), new StaticMobileSession(repToken)),
            monotonic, TimeProvider.System);
        outbox.Enqueue(vMobile, new DateOnly(2026, 10, 5), LifeDrop.Mobile.Offline.OutboxKind.CheckIn, "mobile-key",
            new LifeDrop.Mobile.Visits.DeviceFix(36.1912, 44.0093, 10, now.AddMinutes(-60), false));
        monotonic.ElapsedMs += 60 * 60_000;
        Assert.True(await outbox.SyncAsync());
        var sent = Assert.Single(outbox.Items);
        Assert.Equal((LifeDrop.Mobile.Offline.SyncState.Accepted, false), (sent.State, sent.ReviewRequired));

        void As(Guid user) => client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            GenerateToken(t1, user, conf["Jwt:SigningKey"]!, conf["Jwt:Issuer"]!, conf["Jwt:Audience"]!));

        // The rep cannot review; a supervisor of another region sees nothing; the manager sees the pending one
        As(rep);
        await AssertProblemAsync(await client.GetAsync("/offline-reviews"), HttpStatusCode.Forbidden, "FORBIDDEN");
        As(supervisorB);
        Assert.Empty((await client.GetFromJsonAsync<List<LifeDrop.Application.Visits.OfflineReviewDto>>("/offline-reviews"))!);
        As(manager);
        var pending = Assert.Single((await client.GetFromJsonAsync<List<LifeDrop.Application.Visits.OfflineReviewDto>>("/offline-reviews"))!);
        Assert.Equal((vUntracked, rep, "check_in", "pending"), (pending.VisitId, pending.UserId, pending.Kind, pending.State));
        await AssertProblemAsync(await client.GetAsync("/offline-reviews?state=unknown"), HttpStatusCode.BadRequest, "INVALID_DATA");

        // Supervisor of another region cannot decide; rejecting needs a note; a decision is final
        As(supervisorB);
        await AssertProblemAsync(await client.PostAsJsonAsync($"/offline-reviews/{pending.EventId}", new { Decision = "approve" }), HttpStatusCode.NotFound, "NOT_FOUND");
        As(manager);
        await AssertProblemAsync(await client.PostAsJsonAsync($"/offline-reviews/{pending.EventId}", new { Decision = "reject" }), HttpStatusCode.BadRequest, "INVALID_DATA");
        await AssertProblemAsync(await client.PostAsJsonAsync($"/offline-reviews/{pending.EventId}", new { Decision = "maybe" }), HttpStatusCode.BadRequest, "INVALID_DATA");
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync($"/offline-reviews/{pending.EventId}", new { Decision = "approve", Note = "Confirmed by phone call" })).StatusCode);
        await AssertProblemAsync(await client.PostAsJsonAsync($"/offline-reviews/{pending.EventId}", new { Decision = "reject", Note = "Changed mind" }), HttpStatusCode.Conflict, "INVALID_STATE");
        Assert.Empty((await client.GetFromJsonAsync<List<LifeDrop.Application.Visits.OfflineReviewDto>>("/offline-reviews"))!);
        var approved = Assert.Single((await client.GetFromJsonAsync<List<LifeDrop.Application.Visits.OfflineReviewDto>>("/offline-reviews?state=approved"))!);
        Assert.Equal((manager, "Confirmed by phone call"), (approved.ReviewedBy, approved.Note));

        await using (var conn = new NpgsqlConnection(schemaBlock.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(@"
                SELECT set_config('app.current_tenant', @t1::text, false);
                SELECT (SELECT string_agg(status, ',' ORDER BY status COLLATE ""C"") FROM visits WHERE id IN (@v1, @v2, @v3, @v4, @v5)),
                       (SELECT string_agg(action || '=' || n, ',' ORDER BY action COLLATE ""C"")
                        FROM (SELECT action, count(*) AS n FROM audit_logs WHERE tenant_id = @t1 AND action LIKE 'visit.%' GROUP BY action) a);", conn);
            foreach (var (name, value) in new[] { ("t1", t1), ("v1", vTracked), ("v2", vUntracked), ("v3", vOld), ("v4", vOutside), ("v5", vOnline) })
                cmd.Parameters.AddWithValue(name, value);
            await using var reader = await cmd.ExecuteReaderAsync();
            await reader.NextResultAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal("Arrived,Arrived,Arrived,InVisit,InVisit", reader.GetString(0));
            Assert.Equal("visit.check_in_rejected=3,visit.checked_in=3,visit.offline_review_approved=1,visit.offline_review_required=1", reader.GetString(1));
        }
    }

    [SkippableFact]
    public async Task CustomerApproval_OutOfRegionCustomerWaitsForManagerOrRegionSupervisor()
    {
        // Plan step 1.15, decision D11.
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

        Guid t1 = Guid.NewGuid(), manager = Guid.NewGuid(), supA = Guid.NewGuid(), supB = Guid.NewGuid(), rep = Guid.NewGuid();
        Guid regionA = Guid.Parse("88888888-0000-0000-0000-0000000000e1"), regionB = Guid.Parse("88888888-0000-0000-0000-0000000000b1");
        await using (var conn = new NpgsqlConnection(schemaBlock.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(@"
                INSERT INTO tenants (id, name, code) VALUES (@t1, 'T1', 'T1');
                SELECT seed_default_roles(@t1);
                INSERT INTO users (id, tenant_id) VALUES (@m, @t1), (@sa, @t1), (@sb, @t1), (@r, @t1);
                INSERT INTO user_roles (tenant_id, user_id, role_id)
                SELECT @t1, @m, id FROM roles WHERE tenant_id = @t1 AND name = 'SalesManager'
                UNION ALL SELECT @t1, @sa, id FROM roles WHERE tenant_id = @t1 AND name = 'Supervisor'
                UNION ALL SELECT @t1, @sb, id FROM roles WHERE tenant_id = @t1 AND name = 'Supervisor'
                UNION ALL SELECT @t1, @r, id FROM roles WHERE tenant_id = @t1 AND name = 'SalesRep';
                SELECT set_config('app.current_tenant', @t1::text, false);
                INSERT INTO regions (id, tenant_id, name) VALUES (@ra, @t1, 'Erbil'), (@rb, @t1, 'Baghdad');
                INSERT INTO teams (id, tenant_id, region_id, name) VALUES
                    ('88888888-0000-0000-0000-0000000000a1', @t1, @ra, 'Team A'), ('88888888-0000-0000-0000-0000000000a2', @t1, @rb, 'Team B');
                INSERT INTO team_members (tenant_id, user_id, team_id) VALUES
                    (@t1, @sa, '88888888-0000-0000-0000-0000000000a1'), (@t1, @r, '88888888-0000-0000-0000-0000000000a1'),
                    (@t1, @sb, '88888888-0000-0000-0000-0000000000a2');", conn);
            foreach (var (name, value) in new[] { ("t1", t1), ("m", manager), ("sa", supA), ("sb", supB), ("r", rep), ("ra", regionA), ("rb", regionB) })
                cmd.Parameters.AddWithValue(name, value);
            await cmd.ExecuteNonQueryAsync();
        }

        void As(Guid user) => client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            GenerateToken(t1, user, conf["Jwt:SigningKey"]!, conf["Jwt:Issuer"]!, conf["Jwt:Audience"]!));
        async Task<Guid> Create(object body)
        {
            var response = await client.PostAsJsonAsync("/customers", body);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            return await response.Content.ReadFromJsonAsync<Guid>();
        }
        async Task<LifeDrop.Application.Customers.CustomerDto> Get(Guid id) =>
            (await client.GetFromJsonAsync<LifeDrop.Application.Customers.CustomerDto>($"/customers/{id}"))!;
        Task<HttpResponseMessage> Unplanned(Guid customer) => client.PostAsJsonAsync("/visits/unplanned", new { CustomerId = customer, Reason = "Passing by" });
        List<LifeDrop.Application.Customers.CustomerDto> Pending(HttpResponseMessage r) => r.Content.ReadFromJsonAsync<List<LifeDrop.Application.Customers.CustomerDto>>().Result!;

        // The rep registers one customer in their own region and one in another region
        As(rep);
        var own = await Create(new { Type = "Pharmacy", Code = "P-OWN", Name = "Own region" });
        var outside = await Create(new { Type = "Pharmacy", Code = "P-OUT", Name = "Other region", RegionId = regionB });
        await AssertProblemAsync(await client.PostAsJsonAsync("/customers", new { Type = "Pharmacy", Code = "P-X", Name = "X", RegionId = Guid.NewGuid() }),
            HttpStatusCode.NotFound, "REGION_NOT_FOUND");
        Assert.Equal((regionA, "approved"), ((await Get(own)).RegionId, (await Get(own)).ApprovalStatus));
        Assert.Equal((regionB, "pending"), ((await Get(outside)).RegionId, (await Get(outside)).ApprovalStatus));
        // No visit for a customer waiting for approval
        await AssertProblemAsync(await Unplanned(outside), HttpStatusCode.Conflict, "CUSTOMER_NOT_APPROVED");
        Assert.Equal(HttpStatusCode.Created, (await Unplanned(own)).StatusCode);
        // The rep cannot approve
        await AssertProblemAsync(await client.GetAsync("/customer-approvals"), HttpStatusCode.Forbidden, "FORBIDDEN");
        await AssertProblemAsync(await client.PostAsJsonAsync($"/customers/{outside}/approval", new { Decision = "approve" }), HttpStatusCode.Forbidden, "FORBIDDEN");

        // Only the supervisor of the target region (and the sales manager) see and decide it
        As(supA);
        Assert.Empty(Pending(await client.GetAsync("/customer-approvals")));
        await AssertProblemAsync(await client.PostAsJsonAsync($"/customers/{outside}/approval", new { Decision = "approve" }), HttpStatusCode.NotFound, "NOT_FOUND");
        As(manager);
        Assert.Equal(outside, Assert.Single(Pending(await client.GetAsync("/customer-approvals"))).Id);
        As(supB);
        Assert.Equal(outside, Assert.Single(Pending(await client.GetAsync("/customer-approvals"))).Id);
        await AssertProblemAsync(await client.PostAsJsonAsync($"/customers/{outside}/approval", new { Decision = "reject" }), HttpStatusCode.BadRequest, "INVALID_DATA");
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync($"/customers/{outside}/approval", new { Decision = "approve" })).StatusCode);
        await AssertProblemAsync(await client.PostAsJsonAsync($"/customers/{outside}/approval", new { Decision = "reject", Note = "Late" }), HttpStatusCode.Conflict, "INVALID_STATE");
        Assert.Empty(Pending(await client.GetAsync("/customer-approvals")));

        // Approved: still the rep's customer, and visits are allowed
        As(rep);
        Assert.Equal("approved", (await Get(outside)).ApprovalStatus);
        Assert.Equal(HttpStatusCode.Created, (await Unplanned(outside)).StatusCode);

        await using (var conn = new NpgsqlConnection(schemaBlock.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(@"
                SELECT string_agg(action || '=' || n, ',' ORDER BY action COLLATE ""C"")
                FROM (SELECT action, count(*) AS n FROM audit_logs WHERE tenant_id = @t1 AND action LIKE 'customer.%' GROUP BY action) a;", conn);
            cmd.Parameters.AddWithValue("t1", t1);
            Assert.Equal("customer.approved=1,customer.created=1,customer.created_pending=1", (string?)await cmd.ExecuteScalarAsync());
        }
    }

    [SkippableFact]
    public async Task Cors_AllowsOnlyConfiguredPanelOrigin()
    {
        // Plan step 1.16: the web panel calls the API from its own origin; other sites get no CORS grant.
        var url = GetRequiredEnvVar("TEST_DATABASE_URL");
        await using var schemaBlock = await SetupIsolatedSchemaAsync(url);
        await using var factory = new WebApplicationFactory<LifeDrop.API.Program>()
            .WithWebHostBuilder(webBuilder =>
            {
                webBuilder.UseSetting("ConnectionStrings:LifeDrop", schemaBlock.ConnectionString);
                webBuilder.UseSetting("Cors:AllowedOrigins:0", "https://panel.lifedrop.test");
                TestHostSettings.UseTestJwt(webBuilder);
            });
        var client = factory.CreateClient();

        async Task<HttpResponseMessage> Preflight(string origin)
        {
            using var request = new HttpRequestMessage(HttpMethod.Options, "/auth/login");
            request.Headers.Add("Origin", origin);
            request.Headers.Add("Access-Control-Request-Method", "POST");
            request.Headers.Add("Access-Control-Request-Headers", "content-type");
            return await client.SendAsync(request);
        }

        var allowed = await Preflight("https://panel.lifedrop.test");
        Assert.Equal("https://panel.lifedrop.test", allowed.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.False(allowed.Headers.Contains("Access-Control-Allow-Credentials"));
        Assert.False((await Preflight("https://evil.example")).Headers.Contains("Access-Control-Allow-Origin"));
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
            "SalesRep:region:true", "ScientificRep:own:true", "Supervisor:region:false", "WarehouseManager:own:false"
        }, await RowsAsync(rolesSql));
        Assert.Equal(new[] { "catalog.read", "customers.location.write", "customers.read", "customers.write", "followups.create", "orders.create", "orders.read", "payments.record", "samples.record", "visits.create_unplanned", "visits.fail", "visits.plan" }, await RowsAsync(
            "SELECT rp.permission_key FROM role_permissions rp JOIN roles r ON r.id = rp.role_id WHERE r.tenant_id = @t1 AND r.name = 'SalesRep' ORDER BY 1"));
        var grantsBefore = await RowsAsync("SELECT count(*)::text FROM role_permissions WHERE tenant_id = @t1");
        Assert.Equal(new[] { "99" }, grantsBefore);   // 41 + visits.plan for the two reps (1.23) + places.manage for SalesManager and Supervisor (1.23-b) + catalog.read/manage, 7 grants (2.1) + orders and warehouses, 11 grants (2.2) + warehouse.work (2.3) + delivery.work (2.4) + payments (2.5) + samples (2.6) + followups (2.8) + alerts (2.9) + reports (2.12) + forms.manage for CompanyManager and SalesManager (3.2)

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
    public async Task PanelDirectory_ListsUsersDevicesAndRolesWithinScope()
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
        var conf = factory.Services.GetRequiredService<IConfiguration>();
        var client = factory.CreateClient();

        Guid t1 = Guid.NewGuid(), t2 = Guid.NewGuid();
        Guid manager = Guid.NewGuid(), supervisor = Guid.NewGuid(), plainUser = Guid.NewGuid(), repA = Guid.NewGuid(), repB = Guid.NewGuid(), foreign = Guid.NewGuid();
        Guid erbil = Guid.NewGuid(), baghdad = Guid.NewGuid(), teamA = Guid.NewGuid(), teamB = Guid.NewGuid();
        Guid deviceA = Guid.NewGuid(), deviceB = Guid.NewGuid(), foreignDevice = Guid.NewGuid();
        var families = new Dictionary<Guid, Guid>
        {
            [manager] = Guid.NewGuid(), [supervisor] = Guid.NewGuid(), [plainUser] = Guid.NewGuid(), [repA] = Guid.NewGuid(), [repB] = Guid.NewGuid(), [foreign] = Guid.NewGuid()
        };

        await using (var conn = new NpgsqlConnection(schemaBlock.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(@"
                INSERT INTO tenants (id, name, code) VALUES (@t1, 'T1', 'T1'), (@t2, 'T2', 'T2');
                INSERT INTO users (id, tenant_id, email) VALUES (@m, @t1, 'manager@t1'), (@sv, @t1, 'supervisor@t1'), (@pu, @t1, 'plain@t1'),
                    (@ra, @t1, 'rep-a@t1'), (@rb, @t1, 'rep-b@t1'), (@fu, @t2, 'foreign@t2');
                INSERT INTO roles (id, tenant_id, name) VALUES ('44444444-0000-0000-0000-000000000001', @t1, 'Admin');
                INSERT INTO roles (id, tenant_id, name, data_scope) VALUES ('44444444-0000-0000-0000-000000000002', @t1, 'Supervisor', 'region'),
                    ('44444444-0000-0000-0000-000000000003', @t2, 'ForeignAdmin', 'tenant');
                INSERT INTO role_permissions (tenant_id, role_id, permission_key) VALUES
                    (@t1, '44444444-0000-0000-0000-000000000001', 'sessions.kill'), (@t1, '44444444-0000-0000-0000-000000000001', 'device.approve'),
                    (@t1, '44444444-0000-0000-0000-000000000001', 'user.manageroles'),
                    (@t1, '44444444-0000-0000-0000-000000000002', 'sessions.kill'), (@t1, '44444444-0000-0000-0000-000000000002', 'customers.read'),
                    (@t2, '44444444-0000-0000-0000-000000000003', 'device.approve');
                INSERT INTO user_roles (tenant_id, user_id, role_id) VALUES
                    (@t1, @m, '44444444-0000-0000-0000-000000000001'), (@t1, @sv, '44444444-0000-0000-0000-000000000002'),
                    (@t2, @fu, '44444444-0000-0000-0000-000000000003');
                INSERT INTO devices (id, tenant_id, user_id, device_label, platform, install_id, app_version, secret_hash, status) VALUES
                    (@da, @t1, @ra, 'Rep A phone', 'android', 'i-a', '1.0', '\x01', 'pending'),
                    (@db, @t1, @rb, 'Rep B phone', 'android', 'i-b', '1.0', '\x02', 'approved'),
                    (@fd, @t2, @fu, 'Foreign phone', 'android', 'i-f', '1.0', '\x03', 'pending');
                SELECT set_config('app.current_tenant', @t1::text, false);
                INSERT INTO regions (id, tenant_id, name) VALUES (@er, @t1, 'Erbil'), (@bg, @t1, 'Baghdad');
                INSERT INTO teams (id, tenant_id, region_id, name) VALUES (@ta, @t1, @er, 'Team A'), (@tb, @t1, @bg, 'Team B');
                INSERT INTO team_members (tenant_id, user_id, team_id) VALUES (@t1, @sv, @ta), (@t1, @ra, @ta), (@t1, @rb, @tb);", conn);
            foreach (var (name, value) in new[] { ("t1", t1), ("t2", t2), ("m", manager), ("sv", supervisor), ("pu", plainUser), ("ra", repA), ("rb", repB),
                ("fu", foreign), ("er", erbil), ("bg", baghdad), ("ta", teamA), ("tb", teamB), ("da", deviceA), ("db", deviceB), ("fd", foreignDevice) })
                cmd.Parameters.AddWithValue(name, value);
            await cmd.ExecuteNonQueryAsync();

            foreach (var (user, family) in families)
            {
                await using var token = new NpgsqlCommand(@"
                    INSERT INTO refresh_tokens (id, family_id, tenant_id, user_id, token_hash, expires_at)
                    VALUES (gen_random_uuid(), @f, @t, @u, @h, now() + interval '1 day');", conn);
                token.Parameters.AddWithValue("f", family);
                token.Parameters.AddWithValue("t", user == foreign ? t2 : t1);
                token.Parameters.AddWithValue("u", user);
                token.Parameters.AddWithValue("h", Guid.NewGuid().ToByteArray());
                await token.ExecuteNonQueryAsync();
            }
        }

        Task<HttpResponseMessage> GetAsync(Guid actor, string path)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer",
                GenerateToken(actor == foreign ? t2 : t1, actor, conf["Jwt:SigningKey"]!, conf["Jwt:Issuer"]!, conf["Jwt:Audience"]!, families[actor]));
            return client.SendAsync(request);
        }
        async Task<JsonElement> JsonAsync(Guid actor, string path)
        {
            var response = await GetAsync(actor, path);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
        }
        static string[] Strings(JsonElement array, string? property = null) =>
            array.EnumerateArray().Select(e => (property is null ? e : e.GetProperty(property)).GetString()!).ToArray();

        // /me: the panel's view of the caller's own permissions (buttons only; every action is checked again by its route)
        await AssertProblemAsync(await client.GetAsync("/me"), HttpStatusCode.Unauthorized, "UNAUTHENTICATED");
        var me = await JsonAsync(manager, "/me");
        Assert.Equal("manager@t1", me.GetProperty("email").GetString());
        Assert.Equal(new[] { "Admin" }, Strings(me.GetProperty("roles")));
        Assert.Equal(new[] { "device.approve", "sessions.kill", "user.manageroles" }, Strings(me.GetProperty("permissions")));
        Assert.Empty(Strings((await JsonAsync(plainUser, "/me")).GetProperty("permissions")));

        // /users: tenant scope sees every user of the tenant; region scope only users of its region's teams (and self)
        await AssertProblemAsync(await GetAsync(plainUser, "/users"), HttpStatusCode.Forbidden, "FORBIDDEN");
        var all = await JsonAsync(manager, "/users");
        Assert.Equal(new[] { "manager@t1", "plain@t1", "rep-a@t1", "rep-b@t1", "supervisor@t1" }, Strings(all, "email"));
        var repARow = all.EnumerateArray().Single(u => u.GetProperty("email").GetString() == "rep-a@t1");
        Assert.Equal("Team A", repARow.GetProperty("teamName").GetString());
        Assert.Equal(erbil, repARow.GetProperty("regionId").GetGuid());
        Assert.Equal(1, repARow.GetProperty("activeSessions").GetInt32());
        Assert.Equal(new[] { "rep-a@t1", "supervisor@t1" }, Strings(await JsonAsync(supervisor, "/users"), "email"));
        await AssertProblemAsync(await GetAsync(foreign, "/users"), HttpStatusCode.Forbidden, "FORBIDDEN");

        // /roles: only for role managers, own tenant only
        await AssertProblemAsync(await GetAsync(supervisor, "/roles"), HttpStatusCode.Forbidden, "FORBIDDEN");
        Assert.Equal(new[] { "Admin", "Supervisor" }, Strings(await JsonAsync(manager, "/roles"), "name"));

        // /devices: same permissions as approve/block, own tenant only, filter by status, never the secret
        await AssertProblemAsync(await GetAsync(supervisor, "/devices"), HttpStatusCode.Forbidden, "FORBIDDEN");
        await AssertProblemAsync(await GetAsync(manager, "/devices?status=lost"), HttpStatusCode.BadRequest, "INVALID_DATA");
        var devices = await JsonAsync(manager, "/devices");
        Assert.Equal(new[] { deviceA, deviceB }.OrderBy(d => d).ToArray(), devices.EnumerateArray().Select(d => d.GetProperty("id").GetGuid()).OrderBy(d => d).ToArray());
        Assert.DoesNotContain("secret", devices.GetRawText(), StringComparison.OrdinalIgnoreCase);
        var pending = await JsonAsync(manager, "/devices?status=pending");
        Assert.Equal(deviceA, pending.EnumerateArray().Single().GetProperty("id").GetGuid());
        Assert.Equal("rep-a@t1", pending.EnumerateArray().Single().GetProperty("userEmail").GetString());
        Assert.Equal(foreignDevice, (await JsonAsync(foreign, "/devices")).EnumerateArray().Single().GetProperty("id").GetGuid());
    }

    [SkippableFact]
    public async Task VisitPlan_ListsOneDayWithinVisitsWriteScope()
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
        Guid manager = Guid.NewGuid(), supervisor = Guid.NewGuid(), plainUser = Guid.NewGuid(), repA = Guid.NewGuid(), repB = Guid.NewGuid(), foreign = Guid.NewGuid();
        Guid erbil = Guid.NewGuid(), baghdad = Guid.NewGuid(), teamA = Guid.NewGuid(), teamB = Guid.NewGuid(), c1 = Guid.NewGuid(), cf = Guid.NewGuid();
        Guid visitA = Guid.NewGuid(), visitB = Guid.NewGuid(), visitANextDay = Guid.NewGuid(), visitForeign = Guid.NewGuid();

        await using (var conn = new NpgsqlConnection(schemaBlock.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(@"
                INSERT INTO tenants (id, name, code) VALUES (@t1, 'T1', 'T1'), (@t2, 'T2', 'T2');
                INSERT INTO users (id, tenant_id, email) VALUES (@m, @t1, 'manager@t1'), (@sv, @t1, 'supervisor@t1'), (@pu, @t1, 'plain@t1'),
                    (@ra, @t1, 'rep-a@t1'), (@rb, @t1, 'rep-b@t1'), (@fu, @t2, 'foreign@t2');
                INSERT INTO roles (id, tenant_id, name) VALUES ('44444444-0000-0000-0000-000000000001', @t1, 'Planner');
                INSERT INTO roles (id, tenant_id, name, data_scope) VALUES ('44444444-0000-0000-0000-000000000002', @t1, 'Supervisor', 'region'),
                    ('44444444-0000-0000-0000-000000000003', @t2, 'ForeignPlanner', 'tenant');
                INSERT INTO role_permissions (tenant_id, role_id, permission_key) VALUES
                    (@t1, '44444444-0000-0000-0000-000000000001', 'visits.write'), (@t1, '44444444-0000-0000-0000-000000000002', 'visits.write'),
                    (@t2, '44444444-0000-0000-0000-000000000003', 'visits.write');
                INSERT INTO user_roles (tenant_id, user_id, role_id) VALUES
                    (@t1, @m, '44444444-0000-0000-0000-000000000001'), (@t1, @sv, '44444444-0000-0000-0000-000000000002'),
                    (@t2, @fu, '44444444-0000-0000-0000-000000000003');
                SELECT set_config('app.current_tenant', @t1::text, false);
                INSERT INTO regions (id, tenant_id, name) VALUES (@er, @t1, 'Erbil'), (@bg, @t1, 'Baghdad');
                INSERT INTO teams (id, tenant_id, region_id, name) VALUES (@ta, @t1, @er, 'Team A'), (@tb, @t1, @bg, 'Team B');
                INSERT INTO team_members (tenant_id, user_id, team_id) VALUES (@t1, @sv, @ta), (@t1, @ra, @ta), (@t1, @rb, @tb);
                INSERT INTO customers (id, tenant_id, type, code, name) VALUES (@c1, @t1, 'Clinic', 'C001', 'Test Clinic');
                INSERT INTO visits (id, tenant_id, user_id, customer_id, status, schedule_date, planned_time, created_at) VALUES
                    (@va, @t1, @ra, @c1, 'Planned', '2026-10-05', '09:00', now()),
                    (@vb, @t1, @rb, @c1, 'Planned', '2026-10-05', '08:00', now()),
                    (@vn, @t1, @ra, @c1, 'Planned', '2026-10-06', NULL, now());
                SELECT set_config('app.current_tenant', @t2::text, false);
                INSERT INTO customers (id, tenant_id, type, code, name) VALUES (@cf, @t2, 'Clinic', 'F001', 'Foreign Clinic');
                INSERT INTO visits (id, tenant_id, user_id, customer_id, status, schedule_date, created_at) VALUES
                    (@vf, @t2, @fu, @cf, 'Planned', '2026-10-05', now());", conn);
            foreach (var (name, value) in new[] { ("t1", t1), ("t2", t2), ("m", manager), ("sv", supervisor), ("pu", plainUser), ("ra", repA), ("rb", repB),
                ("fu", foreign), ("er", erbil), ("bg", baghdad), ("ta", teamA), ("tb", teamB), ("c1", c1), ("cf", cf),
                ("va", visitA), ("vb", visitB), ("vn", visitANextDay), ("vf", visitForeign) })
                cmd.Parameters.AddWithValue(name, value);
            await cmd.ExecuteNonQueryAsync();
        }

        Task<HttpResponseMessage> GetAsync(Guid actor, string path)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer",
                GenerateToken(actor == foreign ? t2 : t1, actor, conf["Jwt:SigningKey"]!, conf["Jwt:Issuer"]!, conf["Jwt:Audience"]!));
            return client.SendAsync(request);
        }
        async Task<JsonElement> JsonAsync(Guid actor, string path)
        {
            var response = await GetAsync(actor, path);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
        }
        static Guid[] Ids(JsonElement array) => array.EnumerateArray().Select(e => e.GetProperty("id").GetGuid()).ToArray();

        // Same permission as create/reassign: without visits.write the board is refused
        await AssertProblemAsync(await client.GetAsync("/visit-plan?scheduleDate=2026-10-05"), HttpStatusCode.Unauthorized, "UNAUTHENTICATED");
        await AssertProblemAsync(await GetAsync(plainUser, "/visit-plan?scheduleDate=2026-10-05"), HttpStatusCode.Forbidden, "FORBIDDEN");

        // Tenant scope: every visit of the day in its tenant, ordered by planned time, with assignee and customer
        var day = await JsonAsync(manager, "/visit-plan?scheduleDate=2026-10-05");
        Assert.Equal(new[] { visitB, visitA }, Ids(day));
        Assert.Equal("rep-b@t1", day[0].GetProperty("assigneeEmail").GetString());
        Assert.Equal("C001", day[0].GetProperty("customerCode").GetString());
        Assert.Equal(new[] { visitA }, Ids(await JsonAsync(manager, $"/visit-plan?scheduleDate=2026-10-05&assignedUserId={repA}")));
        Assert.Equal(new[] { visitANextDay }, Ids(await JsonAsync(manager, "/visit-plan?scheduleDate=2026-10-06")));

        // Region scope: only assignees in the supervisor's region; the other tenant only sees its own visit
        Assert.Equal(new[] { visitA }, Ids(await JsonAsync(supervisor, "/visit-plan?scheduleDate=2026-10-05")));
        Assert.Empty(Ids(await JsonAsync(supervisor, $"/visit-plan?scheduleDate=2026-10-05&assignedUserId={repB}")));
        Assert.Equal(new[] { visitForeign }, Ids(await JsonAsync(foreign, "/visit-plan?scheduleDate=2026-10-05")));

        // /users lists the users a planner may assign visits to, in the same scope
        var users = (await JsonAsync(supervisor, "/users")).EnumerateArray().Select(u => u.GetProperty("email").GetString()).ToArray();
        Assert.Equal(new[] { "rep-a@t1", "supervisor@t1" }, users);
        await AssertProblemAsync(await GetAsync(plainUser, "/users"), HttpStatusCode.Forbidden, "FORBIDDEN");
    }

    [SkippableFact]
    public async Task LiveMap_ShowsPositionsOrStatusByPermissionWithinScope()
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
        Guid manager = Guid.NewGuid(), supervisor = Guid.NewGuid(), finance = Guid.NewGuid(), plainUser = Guid.NewGuid();
        Guid repA = Guid.NewGuid(), repB = Guid.NewGuid(), repC = Guid.NewGuid(), foreign = Guid.NewGuid();
        Guid erbil = Guid.NewGuid(), baghdad = Guid.NewGuid(), teamA = Guid.NewGuid(), teamB = Guid.NewGuid(), customer = Guid.NewGuid(), visit = Guid.NewGuid();
        Guid devA = Guid.NewGuid(), devB = Guid.NewGuid(), devC = Guid.NewGuid(), devF = Guid.NewGuid();
        Guid shiftA = Guid.NewGuid(), shiftB = Guid.NewGuid(), shiftC = Guid.NewGuid(), shiftF = Guid.NewGuid();

        await using (var conn = new NpgsqlConnection(schemaBlock.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(@"
                INSERT INTO tenants (id, name, code) VALUES (@t1, 'T1', 'T1'), (@t2, 'T2', 'T2');
                INSERT INTO users (id, tenant_id, email) VALUES (@m, @t1, 'manager@t1'), (@sv, @t1, 'supervisor@t1'), (@fi, @t1, 'finance@t1'),
                    (@pu, @t1, 'plain@t1'), (@ra, @t1, 'rep-a@t1'), (@rb, @t1, 'rep-b@t1'), (@rc, @t1, 'rep-c@t1'), (@fu, @t2, 'foreign@t2');
                INSERT INTO roles (id, tenant_id, name) VALUES ('44444444-0000-0000-0000-000000000001', @t1, 'Manager'),
                    ('44444444-0000-0000-0000-000000000003', @t1, 'Finance');
                INSERT INTO roles (id, tenant_id, name, data_scope) VALUES ('44444444-0000-0000-0000-000000000002', @t1, 'Supervisor', 'region'),
                    ('44444444-0000-0000-0000-000000000004', @t2, 'ForeignManager', 'tenant');
                INSERT INTO role_permissions (tenant_id, role_id, permission_key) VALUES
                    (@t1, '44444444-0000-0000-0000-000000000001', 'tracking.read'), (@t1, '44444444-0000-0000-0000-000000000002', 'tracking.read'),
                    (@t1, '44444444-0000-0000-0000-000000000003', 'tracking.summary'), (@t2, '44444444-0000-0000-0000-000000000004', 'tracking.read');
                INSERT INTO user_roles (tenant_id, user_id, role_id) VALUES
                    (@t1, @m, '44444444-0000-0000-0000-000000000001'), (@t1, @sv, '44444444-0000-0000-0000-000000000002'),
                    (@t1, @fi, '44444444-0000-0000-0000-000000000003'), (@t2, @fu, '44444444-0000-0000-0000-000000000004');
                INSERT INTO devices (id, tenant_id, user_id, device_label, platform, install_id, app_version, secret_hash, status) VALUES
                    (@da, @t1, @ra, 'A', 'android', 'i-a', '1.0', '\x01', 'approved'), (@db, @t1, @rb, 'B', 'android', 'i-b', '1.0', '\x02', 'approved'),
                    (@dc, @t1, @rc, 'C', 'android', 'i-c', '1.0', '\x03', 'approved'), (@df, @t2, @fu, 'F', 'android', 'i-f', '1.0', '\x04', 'approved');
                SELECT set_config('app.current_tenant', @t1::text, false);
                INSERT INTO regions (id, tenant_id, name) VALUES (@er, @t1, 'Erbil'), (@bg, @t1, 'Baghdad');
                INSERT INTO teams (id, tenant_id, region_id, name) VALUES (@ta, @t1, @er, 'Team A'), (@tb, @t1, @bg, 'Team B');
                INSERT INTO team_members (tenant_id, user_id, team_id) VALUES (@t1, @sv, @ta), (@t1, @ra, @ta), (@t1, @rb, @tb), (@t1, @rc, @ta);
                INSERT INTO shifts (id, tenant_id, user_id, device_id, started_at, ended_at) VALUES
                    (@sa, @t1, @ra, @da, now() - interval '2 hours', NULL), (@sb, @t1, @rb, @db, now() - interval '1 hour', NULL),
                    (@sc, @t1, @rc, @dc, now() - interval '3 hours', now() - interval '1 hour');
                INSERT INTO tracking_points (tenant_id, shift_id, user_id, device_id, client_point_id, geom, accuracy_m, captured_at) VALUES
                    (@t1, @sa, @ra, @da, gen_random_uuid(), ST_GeogFromText('SRID=4326;POINT(44.00 36.10)'), 30, now() - interval '30 minutes'),
                    (@t1, @sa, @ra, @da, gen_random_uuid(), ST_GeogFromText('SRID=4326;POINT(44.01 36.19)'), 12.5, now() - interval '1 minute'),
                    (@t1, @sb, @rb, @db, gen_random_uuid(), ST_GeogFromText('SRID=4326;POINT(44.36 33.31)'), 20, now() - interval '20 minutes'),
                    (@t1, @sc, @rc, @dc, gen_random_uuid(), ST_GeogFromText('SRID=4326;POINT(44.02 36.20)'), 10, now() - interval '61 minutes');
                INSERT INTO customers (id, tenant_id, type, code, name) VALUES (@cu, @t1, 'Pharmacy', 'P-1', 'Shifa');
                INSERT INTO visits (id, tenant_id, user_id, customer_id, status, schedule_date, created_at) VALUES (@vi, @t1, @ra, @cu, 'Arrived', CURRENT_DATE, now());
                SELECT set_config('app.current_actor', @ra::text, false);
                UPDATE visits SET status = 'InVisit' WHERE id = @vi;
                SELECT set_config('app.current_tenant', @t2::text, false);
                INSERT INTO shifts (id, tenant_id, user_id, device_id, started_at) VALUES (@sf, @t2, @fu, @df, now() - interval '1 hour');", conn);
            foreach (var (name, value) in new[] { ("t1", t1), ("t2", t2), ("m", manager), ("sv", supervisor), ("fi", finance), ("pu", plainUser), ("ra", repA),
                ("rb", repB), ("rc", repC), ("fu", foreign), ("er", erbil), ("bg", baghdad), ("ta", teamA), ("tb", teamB), ("cu", customer), ("vi", visit),
                ("da", devA), ("db", devB), ("dc", devC), ("df", devF), ("sa", shiftA), ("sb", shiftB), ("sc", shiftC), ("sf", shiftF) })
                cmd.Parameters.AddWithValue(name, value);
            await cmd.ExecuteNonQueryAsync();
        }

        Task<HttpResponseMessage> GetAsync(Guid actor)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/live-map");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer",
                GenerateToken(actor == foreign ? t2 : t1, actor, conf["Jwt:SigningKey"]!, conf["Jwt:Issuer"]!, conf["Jwt:Audience"]!));
            return client.SendAsync(request);
        }
        async Task<JsonElement[]> MapAsync(Guid actor)
        {
            var response = await GetAsync(actor);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone().EnumerateArray().ToArray();
        }
        static string?[] Emails(JsonElement[] rows) => rows.Select(r => r.GetProperty("email").GetString()).ToArray();

        await AssertProblemAsync(await client.GetAsync("/live-map"), HttpStatusCode.Unauthorized, "UNAUTHENTICATED");
        await AssertProblemAsync(await GetAsync(plainUser), HttpStatusCode.Forbidden, "FORBIDDEN");

        // tracking.read, tenant scope: open shifts only (rep C ended), last point, live only within 5 minutes
        var all = await MapAsync(manager);
        Assert.Equal(new[] { "rep-a@t1", "rep-b@t1" }, Emails(all));
        var a = all[0];
        Assert.True(a.GetProperty("precise").GetBoolean());
        Assert.True(a.GetProperty("isLive").GetBoolean());
        Assert.Equal(36.19, a.GetProperty("latitude").GetDouble(), 5);
        Assert.Equal(44.01, a.GetProperty("longitude").GetDouble(), 5);
        Assert.Equal(12.5, a.GetProperty("accuracyMeters").GetDouble(), 5);
        Assert.Equal("Shifa", a.GetProperty("lastCustomerName").GetString());
        Assert.Equal("Team A", a.GetProperty("teamName").GetString());
        Assert.False(all[1].GetProperty("isLive").GetBoolean()); // 20 minutes old: last known, not live
        Assert.Equal(33.31, all[1].GetProperty("latitude").GetDouble(), 5);

        // Region scope: only the supervisor's region
        Assert.Equal(new[] { "rep-a@t1" }, Emails(await MapAsync(supervisor)));

        // tracking.summary (decision D3): status and times, never coordinates
        var summary = await MapAsync(finance);
        Assert.Equal(new[] { "rep-a@t1", "rep-b@t1" }, Emails(summary));
        Assert.All(summary, r =>
        {
            Assert.False(r.GetProperty("precise").GetBoolean());
            Assert.Equal(JsonValueKind.Null, r.GetProperty("latitude").ValueKind);
            Assert.Equal(JsonValueKind.Null, r.GetProperty("longitude").ValueKind);
            Assert.Equal(JsonValueKind.Null, r.GetProperty("accuracyMeters").ValueKind);
        });
        Assert.True(summary[0].GetProperty("isLive").GetBoolean());
        Assert.Equal("Shifa", summary[0].GetProperty("lastCustomerName").GetString());

        // Other tenant only sees its own shift
        Assert.Equal(new[] { "foreign@t2" }, Emails(await MapAsync(foreign)));
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
            UPDATE users SET email = 'rep-a@t1' WHERE id = @ra;
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
        // Each row names its actor's email for the panel (step 1.20); null when the user has none
        var rows = (await (await client.GetAsync($"/audit-logs?actorId={repA}")).Content.ReadFromJsonAsync<List<LifeDrop.Application.Audit.AuditLogEntryDto>>())!;
        Assert.All(rows, r => Assert.Equal("rep-a@t1", r.ActorEmail));
        Assert.Null((await (await client.GetAsync($"/audit-logs?actorId={repB}")).Content.ReadFromJsonAsync<List<LifeDrop.Application.Audit.AuditLogEntryDto>>())!.First().ActorEmail);
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

    private async Task<(IsolatedSchema Schema, WebApplicationFactory<LifeDrop.API.Program> Factory)> StartFlexiblePlanHostAsync(string url)
    {
        var schemaBlock = await SetupIsolatedSchemaAsync(url);
        var factory = new WebApplicationFactory<LifeDrop.API.Program>()
            .WithWebHostBuilder(webBuilder =>
            {
                webBuilder.UseSetting("ConnectionStrings:LifeDrop", schemaBlock.ConnectionString);
                TestHostSettings.UseTestJwt(webBuilder);
                webBuilder.ConfigureTestServices(services =>
                {
                    var descriptor = System.Linq.Enumerable.SingleOrDefault(services, d => d.ServiceType == typeof(LifeDrop.Application.Auth.ISessionStore));
                    if (descriptor != null) services.Remove(descriptor);
                    services.AddScoped<LifeDrop.Application.Auth.ISessionStore, FakeSessionStore>();
                    // 06:00 UTC on 5 Oct = 09:00 on Monday 5 Oct 2026 in Baghdad.
                    services.AddSingleton<TimeProvider>(new FixedClock(new DateTimeOffset(2026, 10, 5, 6, 0, 0, TimeSpan.Zero)));
                });
            });
        return (schemaBlock, factory);
    }

    [SkippableFact]
    public async Task FlexiblePlan_OwnerAndManagersReorderAndPostponeWithinScopeAndAudit()
    {
        var url = GetRequiredEnvVar("TEST_DATABASE_URL");
        await VerifyRoleAsync(url, expectElevated: false);

        var (schemaBlock, factory) = await StartFlexiblePlanHostAsync(url);
        await using var _ = schemaBlock;
        await using var __ = factory;
        var conf = factory.Services.GetRequiredService<IConfiguration>();
        var client = factory.CreateClient();

        var t1 = Guid.NewGuid();
        Guid manager = Guid.NewGuid(), supervisor = Guid.NewGuid(), finance = Guid.NewGuid(), repA = Guid.NewGuid(), repB = Guid.NewGuid();
        Guid customer = Guid.NewGuid();
        Guid v1 = Guid.NewGuid(), v2 = Guid.NewGuid(), v3 = Guid.NewGuid(), v4 = Guid.NewGuid(), v5 = Guid.NewGuid(), vB = Guid.NewGuid();
        await using (var conn = new NpgsqlConnection(schemaBlock.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(@"
                INSERT INTO tenants (id, name, code) VALUES (@t1, 'T1', 'T1');
                SELECT seed_default_roles(@t1);
                INSERT INTO users (id, tenant_id) VALUES (@m, @t1), (@sv, @t1), (@fin, @t1), (@ra, @t1), (@rb, @t1);
                INSERT INTO user_roles (tenant_id, user_id, role_id)
                SELECT @t1, @m, id FROM roles WHERE tenant_id = @t1 AND name = 'SalesManager'
                UNION ALL SELECT @t1, @sv, id FROM roles WHERE tenant_id = @t1 AND name = 'Supervisor'
                UNION ALL SELECT @t1, @fin, id FROM roles WHERE tenant_id = @t1 AND name = 'FinanceManager'
                UNION ALL SELECT @t1, @ra, id FROM roles WHERE tenant_id = @t1 AND name = 'SalesRep'
                UNION ALL SELECT @t1, @rb, id FROM roles WHERE tenant_id = @t1 AND name = 'SalesRep';
                SELECT set_config('app.current_tenant', @t1::text, false);
                INSERT INTO regions (id, tenant_id, name) VALUES ('55555555-0000-0000-0000-0000000000e1', @t1, 'Erbil'), ('55555555-0000-0000-0000-0000000000b1', @t1, 'Baghdad');
                INSERT INTO teams (id, tenant_id, region_id, name) VALUES
                    ('55555555-0000-0000-0000-0000000000a1', @t1, '55555555-0000-0000-0000-0000000000e1', 'Team A'),
                    ('55555555-0000-0000-0000-0000000000a2', @t1, '55555555-0000-0000-0000-0000000000b1', 'Team B');
                INSERT INTO team_members (tenant_id, user_id, team_id) VALUES
                    (@t1, @sv, '55555555-0000-0000-0000-0000000000a1'), (@t1, @ra, '55555555-0000-0000-0000-0000000000a1'),
                    (@t1, @rb, '55555555-0000-0000-0000-0000000000a2');
                INSERT INTO customers (id, tenant_id, type, code, name) VALUES (@c, @t1, 'Pharmacy', 'C1', 'Customer');
                INSERT INTO visits (id, tenant_id, user_id, customer_id, status, schedule_date, created_at) VALUES
                    (@v1, @t1, @ra, @c, 'Planned', '2026-10-06', now() - interval '5 minutes'),
                    (@v2, @t1, @ra, @c, 'Planned', '2026-10-06', now() - interval '4 minutes'),
                    (@v3, @t1, @ra, @c, 'EnRoute', '2026-10-06', now() - interval '3 minutes'),
                    (@v4, @t1, @ra, @c, 'Completed', '2026-10-06', now() - interval '2 minutes'),
                    (@v5, @t1, @ra, @c, 'Planned', '2026-10-06', now() - interval '1 minutes'),
                    (@vb, @t1, @rb, @c, 'Planned', '2026-10-06', now());", conn);
            foreach (var (name, value) in new[] { ("t1", t1), ("m", manager), ("sv", supervisor), ("fin", finance), ("ra", repA), ("rb", repB), ("c", customer),
                ("v1", v1), ("v2", v2), ("v3", v3), ("v4", v4), ("v5", v5), ("vb", vB) })
                cmd.Parameters.AddWithValue(name, value);
            await cmd.ExecuteNonQueryAsync();
        }

        void As(Guid user) => client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            GenerateToken(t1, user, conf["Jwt:SigningKey"]!, conf["Jwt:Issuer"]!, conf["Jwt:Audience"]!));
        Task<HttpResponseMessage> Reorder(string day, IEnumerable<Guid> ids, Guid? assignee = null) =>
            client.PutAsJsonAsync("/visit-plan/order", new { ScheduleDate = day, VisitIds = ids, AssignedUserId = assignee });
        Task<HttpResponseMessage> Postpone(Guid visit, string day, string? reason = null) =>
            client.PostAsJsonAsync($"/visits/{visit}/postpone", new { ScheduleDate = day, Reason = reason });
        async Task<List<LifeDrop.Application.Visits.VisitDto>> OwnPlan(string day) =>
            (await (await client.GetAsync($"/visits?scheduleDate={day}")).Content.ReadFromJsonAsync<List<LifeDrop.Application.Visits.VisitDto>>())!;

        // Without any order: planned time, priority, then creation (all equal here: creation order), no positions.
        As(repA);
        var initial = await OwnPlan("2026-10-06");
        Assert.Equal(new[] { v1, v2, v3, v4, v5 }, initial.Select(v => v.Id).ToArray());
        Assert.All(initial, v => Assert.Null(v.SortOrder));

        // Finance has neither visits.plan nor visits.write
        As(finance);
        await AssertProblemAsync(await Reorder("2026-10-06", new[] { v1 }, repA), HttpStatusCode.Forbidden, "FORBIDDEN");
        await AssertProblemAsync(await Postpone(v1, "2026-10-07"), HttpStatusCode.Forbidden, "FORBIDDEN");

        // The owner reorders their own day (any status is allowed in the order)
        As(repA);
        Assert.Equal(HttpStatusCode.NoContent, (await Reorder("2026-10-06", new[] { v3, v1, v2, v4 })).StatusCode);
        var ordered = await OwnPlan("2026-10-06");
        Assert.Equal(new[] { v3, v1, v2, v4, v5 }, ordered.Select(v => v.Id).ToArray());   // v5 is not ordered yet: last
        Assert.Equal(new int?[] { 1, 2, 3, 4, null }, ordered.Select(v => v.SortOrder).ToArray());

        // Invalid or unsafe requests
        await AssertProblemAsync(await Reorder("2026-10-06", Array.Empty<Guid>()), HttpStatusCode.BadRequest, "INVALID_DATA");
        await AssertProblemAsync(await Reorder("2026-10-06", new[] { v1, v1 }), HttpStatusCode.BadRequest, "INVALID_DATA");
        await AssertProblemAsync(await Reorder("2026-10-06", new[] { v1, vB }), HttpStatusCode.Conflict, "CONCURRENCY_CONFLICT");   // vB is not repA's
        await AssertProblemAsync(await Reorder("2026-10-07", new[] { v1 }), HttpStatusCode.Conflict, "CONCURRENCY_CONFLICT");        // wrong day
        await AssertProblemAsync(await Reorder("2026-10-06", new[] { vB }, repB), HttpStatusCode.NotFound, "NOT_FOUND");            // another user's plan
        Assert.Equal(new[] { v3, v1, v2, v4, v5 }, (await OwnPlan("2026-10-06")).Select(v => v.Id).ToArray());                      // nothing changed

        // A supervisor reorders a rep of their region, not another region's rep
        As(supervisor);
        Assert.Equal(HttpStatusCode.NoContent, (await Reorder("2026-10-06", new[] { v5, v2, v1, v3, v4 }, repA)).StatusCode);
        await AssertProblemAsync(await Reorder("2026-10-06", new[] { vB }, repB), HttpStatusCode.NotFound, "NOT_FOUND");
        As(repA);
        Assert.Equal(new[] { v5, v2, v1, v3, v4 }, (await OwnPlan("2026-10-06")).Select(v => v.Id).ToArray());

        // Postpone: reason limits, past day, same day, finished visit
        await AssertProblemAsync(await Postpone(v1, "2026-10-04"), HttpStatusCode.BadRequest, "INVALID_DATA");              // before today (5 Oct)
        await AssertProblemAsync(await Postpone(v1, "2026-10-06"), HttpStatusCode.BadRequest, "INVALID_DATA");              // same day
        await AssertProblemAsync(await Postpone(v1, "2026-10-07", new string('x', 501)), HttpStatusCode.BadRequest, "INVALID_DATA");
        await AssertProblemAsync(await Postpone(v4, "2026-10-07"), HttpStatusCode.BadRequest, "INVALID_STATE");              // Completed
        await AssertProblemAsync(await Postpone(vB, "2026-10-07"), HttpStatusCode.NotFound, "NOT_FOUND");                    // not repA's visit
        await AssertProblemAsync(await Postpone(Guid.NewGuid(), "2026-10-07"), HttpStatusCode.NotFound, "NOT_FOUND");

        // An en-route visit can be postponed: back to Planned on the new day, at the end of that day, counted with its reason
        Assert.Equal(HttpStatusCode.NoContent, (await Postpone(v3, "2026-10-07", "  Customer not available  ")).StatusCode);
        Assert.DoesNotContain(v3, (await OwnPlan("2026-10-06")).Select(v => v.Id));
        var moved = Assert.Single(await OwnPlan("2026-10-07"));
        Assert.Equal((v3, "Planned", null, 1, "Customer not available", "2026-10-07"),
            (moved.Id, moved.Status, moved.SortOrder, moved.PostponeCount, moved.PostponeReason, moved.ScheduleDate.ToString("yyyy-MM-dd")));
        // ... and again, the count grows and the position stays cleared; postponing to today is allowed
        Assert.Equal(HttpStatusCode.NoContent, (await Postpone(v3, "2026-10-05")).StatusCode);
        var movedAgain = Assert.Single(await OwnPlan("2026-10-05"));
        Assert.Equal((2, (string?)null, (int?)null), (movedAgain.PostponeCount, movedAgain.PostponeReason, movedAgain.SortOrder));

        // The sales manager moves any visit (tenant scope); a supervisor only inside the region
        As(manager);
        Assert.Equal(HttpStatusCode.NoContent, (await Postpone(vB, "2026-10-08", "Holiday")).StatusCode);
        As(supervisor);
        await AssertProblemAsync(await Postpone(vB, "2026-10-09"), HttpStatusCode.NotFound, "NOT_FOUND");
        Assert.Equal(HttpStatusCode.NoContent, (await Postpone(v5, "2026-10-09")).StatusCode);

        // Status history: the en-route visit went back to Planned, recorded for the rep; audit has one row per success
        await using (var conn = new NpgsqlConnection(schemaBlock.ConnectionString))
        {
            await conn.OpenAsync();
            await using (var setTenant = new NpgsqlCommand("SELECT set_config('app.current_tenant', @t1::text, false)", conn))
            {
                setTenant.Parameters.AddWithValue("t1", t1);
                await setTenant.ExecuteNonQueryAsync();
            }
            await using var history = new NpgsqlCommand(@"
                SELECT from_status, to_status, actor_id FROM visit_status_history WHERE visit_id = @v ORDER BY occurred_at, id;", conn);
            history.Parameters.AddWithValue("v", v3);
            await using (var reader = await history.ExecuteReaderAsync())
            {
                var rows = new List<(string?, string, Guid?)>();
                while (await reader.ReadAsync()) rows.Add((reader.IsDBNull(0) ? null : reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetGuid(2)));
                Assert.Equal(new (string?, string, Guid?)[] { (null, "EnRoute", null), ("EnRoute", "Planned", repA) }, rows.ToArray());
            }

            await using var audit = new NpgsqlCommand(@"
                SELECT action, actor_id, entity_id FROM audit_logs WHERE tenant_id = @t1 AND action IN ('visit.reordered', 'visit.postponed') ORDER BY occurred_at, action;", conn);
            audit.Parameters.AddWithValue("t1", t1);
            await using var auditReader = await audit.ExecuteReaderAsync();
            var auditRows = new List<(string, Guid, string)>();
            while (await auditReader.ReadAsync()) auditRows.Add((auditReader.GetString(0), auditReader.GetGuid(1), auditReader.GetString(2)));
            Assert.Equal(new[]
            {
                ("visit.reordered", repA, repA.ToString()),
                ("visit.reordered", supervisor, repA.ToString()),
                ("visit.postponed", repA, v3.ToString()),
                ("visit.postponed", repA, v3.ToString()),
                ("visit.postponed", manager, vB.ToString()),
                ("visit.postponed", supervisor, v5.ToString())
            }, auditRows.ToArray());
        }
    }

    [SkippableFact]
    public async Task CustomerVisitWindows_AreEditedWithinScopeAndFlagVisitsOutsideThem()
    {
        var url = GetRequiredEnvVar("TEST_DATABASE_URL");
        await VerifyRoleAsync(url, expectElevated: false);

        var (schemaBlock, factory) = await StartFlexiblePlanHostAsync(url);
        await using var _ = schemaBlock;
        await using var __ = factory;
        var conf = factory.Services.GetRequiredService<IConfiguration>();
        var client = factory.CreateClient();

        var t1 = Guid.NewGuid();
        Guid manager = Guid.NewGuid(), finance = Guid.NewGuid(), repA = Guid.NewGuid(), repB = Guid.NewGuid();
        Guid cWindows = Guid.NewGuid(), cFree = Guid.NewGuid();
        Guid vTue = Guid.NewGuid(), vTueNoon = Guid.NewGuid(), vTueAfternoon = Guid.NewGuid(), vWed = Guid.NewGuid(), vFree = Guid.NewGuid();
        await using (var conn = new NpgsqlConnection(schemaBlock.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(@"
                INSERT INTO tenants (id, name, code) VALUES (@t1, 'T1', 'T1');
                SELECT seed_default_roles(@t1);
                INSERT INTO users (id, tenant_id) VALUES (@m, @t1), (@fin, @t1), (@ra, @t1), (@rb, @t1);
                INSERT INTO user_roles (tenant_id, user_id, role_id)
                SELECT @t1, @m, id FROM roles WHERE tenant_id = @t1 AND name = 'SalesManager'
                UNION ALL SELECT @t1, @fin, id FROM roles WHERE tenant_id = @t1 AND name = 'FinanceManager'
                UNION ALL SELECT @t1, @ra, id FROM roles WHERE tenant_id = @t1 AND name = 'SalesRep'
                UNION ALL SELECT @t1, @rb, id FROM roles WHERE tenant_id = @t1 AND name = 'SalesRep';
                SELECT set_config('app.current_tenant', @t1::text, false);
                INSERT INTO customers (id, tenant_id, type, code, name, owner_user_id) VALUES
                    (@cw, @t1, 'Pharmacy', 'W1', 'Has windows', @ra), (@cf, @t1, 'Pharmacy', 'F1', 'No windows', @ra);
                INSERT INTO visits (id, tenant_id, user_id, customer_id, status, schedule_date, planned_time) VALUES
                    (@vtue, @t1, @ra, @cw, 'Planned', '2026-10-06', NULL),
                    (@vnoon, @t1, @ra, @cw, 'Planned', '2026-10-06', '10:00'),
                    (@vaft, @t1, @ra, @cw, 'Planned', '2026-10-06', '14:00'),
                    (@vwed, @t1, @ra, @cw, 'Planned', '2026-10-07', NULL),
                    (@vfree, @t1, @ra, @cf, 'Planned', '2026-10-06', '03:00');", conn);
            foreach (var (name, value) in new[] { ("t1", t1), ("m", manager), ("fin", finance), ("ra", repA), ("rb", repB), ("cw", cWindows), ("cf", cFree),
                ("vtue", vTue), ("vnoon", vTueNoon), ("vaft", vTueAfternoon), ("vwed", vWed), ("vfree", vFree) })
                cmd.Parameters.AddWithValue(name, value);
            await cmd.ExecuteNonQueryAsync();
        }

        void As(Guid user) => client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            GenerateToken(t1, user, conf["Jwt:SigningKey"]!, conf["Jwt:Issuer"]!, conf["Jwt:Audience"]!));
        Task<HttpResponseMessage> Put(Guid customer, params object[] windows) =>
            client.PutAsJsonAsync($"/customers/{customer}/visit-windows", new { Windows = windows });
        object W(int weekday, string opens, string closes) => new { Weekday = weekday, OpensAt = opens, ClosesAt = closes };
        async Task<Dictionary<Guid, string>> StatusesAsync(string day) =>
            (await (await client.GetAsync($"/visits?scheduleDate={day}")).Content.ReadFromJsonAsync<List<LifeDrop.Application.Visits.VisitDto>>())!
                .ToDictionary(v => v.Id, v => v.WindowStatus);

        // No windows yet: nothing is flagged
        As(repA);
        Assert.All((await StatusesAsync("2026-10-06")).Values, status => Assert.Equal("none", status));

        // Roles: the owner (customers.write) edits; another rep's customer is not visible; finance may read but not edit
        As(repB);
        await AssertProblemAsync(await Put(cWindows, W(2, "09:00:00", "13:00:00")), HttpStatusCode.NotFound, "NOT_FOUND");
        await AssertProblemAsync(await client.GetAsync($"/customers/{cWindows}/visit-windows"), HttpStatusCode.NotFound, "NOT_FOUND");
        As(finance);
        await AssertProblemAsync(await Put(cWindows, W(2, "09:00:00", "13:00:00")), HttpStatusCode.Forbidden, "FORBIDDEN");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/customers/{cWindows}/visit-windows")).StatusCode);

        // Validation: order, overlap, weekday range, count
        As(repA);
        await AssertProblemAsync(await Put(cWindows, W(2, "13:00:00", "09:00:00")), HttpStatusCode.BadRequest, "INVALID_DATA");
        await AssertProblemAsync(await Put(cWindows, W(2, "09:00:00", "09:00:00")), HttpStatusCode.BadRequest, "INVALID_DATA");
        await AssertProblemAsync(await Put(cWindows, W(2, "09:00:00", "13:00:00"), W(2, "12:00:00", "15:00:00")), HttpStatusCode.BadRequest, "INVALID_DATA");
        await AssertProblemAsync(await Put(cWindows, W(7, "09:00:00", "13:00:00")), HttpStatusCode.BadRequest, "INVALID_DATA");
        await AssertProblemAsync(await Put(cWindows, W(-1, "09:00:00", "13:00:00")), HttpStatusCode.BadRequest, "INVALID_DATA");
        await AssertProblemAsync(await Put(cWindows, Enumerable.Range(0, 22).Select(i => W(i % 7, $"{i / 7 * 6:00}:00:00", $"{i / 7 * 6 + 5:00}:00:00")).ToArray()),
            HttpStatusCode.BadRequest, "INVALID_DATA");
        await AssertProblemAsync(await Put(Guid.NewGuid(), W(2, "09:00:00", "13:00:00")), HttpStatusCode.NotFound, "NOT_FOUND");

        // Tuesday 09:00-13:00 and 17:00-20:00 (given out of order); Sunday all morning
        Assert.Equal(HttpStatusCode.NoContent, (await Put(cWindows, W(2, "17:00:00", "20:00:00"), W(0, "08:00:00", "12:00:00"), W(2, "09:00:00", "13:00:00"))).StatusCode);
        var stored = (await (await client.GetAsync($"/customers/{cWindows}/visit-windows")).Content.ReadFromJsonAsync<List<VisitWindowDto>>())!;
        Assert.Equal(new[] { (0, "08:00:00", "12:00:00"), (2, "09:00:00", "13:00:00"), (2, "17:00:00", "20:00:00") },
            stored.Select(w => (w.Weekday, w.OpensAt.ToString("HH:mm:ss"), w.ClosesAt.ToString("HH:mm:ss"))).ToArray());

        // Visit day and planned time against the windows (6 Oct 2026 is a Tuesday, 7 Oct a Wednesday)
        var tuesday = await StatusesAsync("2026-10-06");
        Assert.Equal("within", tuesday[vTue]);          // no planned time: the day is accepted
        Assert.Equal("within", tuesday[vTueNoon]);      // 10:00 inside 09:00-13:00
        Assert.Equal("outside", tuesday[vTueAfternoon]); // 14:00 between the two windows
        Assert.Equal("none", tuesday[vFree]);           // that customer has no windows
        Assert.Equal("outside", (await StatusesAsync("2026-10-07"))[vWed]);

        // The planning board shows the same flag to a manager
        As(manager);
        var board = (await (await client.GetAsync("/visit-plan?scheduleDate=2026-10-06")).Content.ReadFromJsonAsync<List<LifeDrop.Application.Visits.VisitDto>>())!;
        Assert.Equal("outside", board.Single(v => v.Id == vTueAfternoon).WindowStatus);

        // Replacing with an empty list lifts every restriction; each change is audited
        As(repA);
        Assert.Equal(HttpStatusCode.NoContent, (await Put(cWindows)).StatusCode);
        Assert.Empty((await (await client.GetAsync($"/customers/{cWindows}/visit-windows")).Content.ReadFromJsonAsync<List<VisitWindowDto>>())!);
        Assert.Equal("none", (await StatusesAsync("2026-10-07"))[vWed]);

        await using (var conn = new NpgsqlConnection(schemaBlock.ConnectionString))
        {
            await conn.OpenAsync();
            await using var audit = new NpgsqlCommand(@"
                SELECT actor_id, entity_id FROM audit_logs WHERE tenant_id = @t1 AND action = 'customer.visit_windows_changed' ORDER BY occurred_at;", conn);
            audit.Parameters.AddWithValue("t1", t1);
            await using var reader = await audit.ExecuteReaderAsync();
            var rows = new List<(Guid, string)>();
            while (await reader.ReadAsync()) rows.Add((reader.GetGuid(0), reader.GetString(1)));
            Assert.Equal(new[] { (repA, cWindows.ToString()), (repA, cWindows.ToString()) }, rows.ToArray());
        }
    }

    // Calls of the places tests (the API allows 100 requests per minute from one address, so each test has its own host).
    private sealed class PlacesApi(HttpClient client, Func<Guid, Guid, string> token)
    {
        public void As(Guid tenant, Guid user) => client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token(tenant, user));
        public Task<HttpResponseMessage> Import(params object[] rows) => client.PostAsJsonAsync("/places/import", new { Rows = rows });
        public static object Row(string kind, string name, string? city = null, string? neighbourhood = null, string? street = null, string? ckb = null, string? ar = null, string? en = null, bool active = true) =>
            new { Kind = kind, Name = name, City = city, Neighbourhood = neighbourhood, Street = street, NameCkb = ckb, NameAr = ar, NameEn = en, Active = active };
        public async Task<List<LifeDrop.Application.Places.PlaceDto>> Places(string query) =>
            (await (await client.GetAsync("/places" + query)).Content.ReadFromJsonAsync<List<LifeDrop.Application.Places.PlaceDto>>())!;
        public async Task<Guid> Add(string kind, string name, Guid? parent)
        {
            var response = await Post(new { ParentId = parent, Kind = kind, Name = name });
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<CreatedResponseDto>())!.Id;
        }
        public Task<HttpResponseMessage> Post(object body) => client.PostAsJsonAsync("/places", body);
        public Task<HttpResponseMessage> Put(Guid id, object body) => client.PutAsJsonAsync($"/places/{id}", body);
        public Task<HttpResponseMessage> Merge(Guid id, Guid into) => client.PostAsJsonAsync($"/places/{id}/merge", new { IntoPlaceId = into });
        public async Task<LifeDrop.Application.Customers.CustomerDto> Customer(Guid id) =>
            (await client.GetFromJsonAsync<LifeDrop.Application.Customers.CustomerDto>($"/customers/{id}"))!;
        public Task<HttpResponseMessage> Profile(Guid customer, object body) => client.PutAsJsonAsync($"/customers/{customer}/profile", body);
    }

    [SkippableFact]
    public async Task Places_TreeImportAddAndEditByPermissionAndCustomerProfile()
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
        Guid manager = Guid.NewGuid(), finance = Guid.NewGuid(), rep = Guid.NewGuid(), repB = Guid.NewGuid(), manager2 = Guid.NewGuid();
        Guid c1 = Guid.NewGuid(), c2 = Guid.NewGuid();
        await using (var conn = new NpgsqlConnection(schemaBlock.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(@"
                INSERT INTO tenants (id, name, code) VALUES (@t1, 'T1', 'T1'), (@t2, 'T2', 'T2');
                SELECT seed_default_roles(@t1);
                SELECT seed_default_roles(@t2);
                INSERT INTO users (id, tenant_id) VALUES (@m, @t1), (@fin, @t1), (@r, @t1), (@rb, @t1), (@m2, @t2);
                INSERT INTO user_roles (tenant_id, user_id, role_id)
                SELECT @t1, @m, id FROM roles WHERE tenant_id = @t1 AND name = 'SalesManager'
                UNION ALL SELECT @t1, @fin, id FROM roles WHERE tenant_id = @t1 AND name = 'FinanceManager'
                UNION ALL SELECT @t1, @r, id FROM roles WHERE tenant_id = @t1 AND name = 'SalesRep'
                UNION ALL SELECT @t1, @rb, id FROM roles WHERE tenant_id = @t1 AND name = 'SalesRep'
                UNION ALL SELECT @t2, @m2, id FROM roles WHERE tenant_id = @t2 AND name = 'SalesManager';
                SELECT set_config('app.current_tenant', @t1::text, false);
                INSERT INTO customers (id, tenant_id, type, code, name, owner_user_id) VALUES
                    (@c1, @t1, 'Pharmacy', 'C1', 'First', @r), (@c2, @t1, 'Pharmacy', 'C2', 'Second', @r);", conn);
            foreach (var (name, value) in new[] { ("t1", t1), ("t2", t2), ("m", manager), ("fin", finance), ("r", rep), ("rb", repB), ("m2", manager2), ("c1", c1), ("c2", c2) })
                cmd.Parameters.AddWithValue(name, value);
            await cmd.ExecuteNonQueryAsync();
        }

        var api = new PlacesApi(client, (tenant, user) => GenerateToken(tenant, user, conf["Jwt:SigningKey"]!, conf["Jwt:Issuer"]!, conf["Jwt:Audience"]!));

        // Importing and exporting need places.manage; finance may only read; a field user cannot add a city
        api.As(t1, finance);
        await AssertProblemAsync(await api.Import(PlacesApi.Row("city", "Erbil")), HttpStatusCode.Forbidden, "FORBIDDEN");
        await AssertProblemAsync(await client.GetAsync("/places/export"), HttpStatusCode.Forbidden, "FORBIDDEN");
        await AssertProblemAsync(await api.Post(new { Kind = "city", Name = "Erbil" }), HttpStatusCode.Forbidden, "FORBIDDEN");
        Assert.Empty(await api.Places("?kind=city"));
        api.As(t1, rep);
        await AssertProblemAsync(await api.Post(new { Kind = "city", Name = "Erbil" }), HttpStatusCode.Forbidden, "FORBIDDEN");
        await AssertProblemAsync(await client.GetAsync("/places/export"), HttpStatusCode.Forbidden, "FORBIDDEN");

        // A whole file is rejected when one row is bad; nothing is stored then
        api.As(t1, manager);
        await AssertProblemAsync(await api.Import(), HttpStatusCode.BadRequest, "INVALID_DATA");
        await AssertProblemAsync(await api.Import(PlacesApi.Row("city", "Erbil"), PlacesApi.Row("road", "Bad kind", "Erbil")), HttpStatusCode.BadRequest, "INVALID_DATA");
        await AssertProblemAsync(await api.Import(PlacesApi.Row("neighbourhood", "No city")), HttpStatusCode.BadRequest, "INVALID_DATA");
        await AssertProblemAsync(await api.Import(PlacesApi.Row("street", "No neighbourhood", "Erbil")), HttpStatusCode.BadRequest, "INVALID_DATA");
        await AssertProblemAsync(await api.Import(PlacesApi.Row("alley", "No neighbourhood", "Erbil", street: "Main")), HttpStatusCode.BadRequest, "INVALID_DATA");
        await AssertProblemAsync(await api.Import(PlacesApi.Row("city", "Erbil", neighbourhood: "Ankawa")), HttpStatusCode.BadRequest, "INVALID_DATA");
        await AssertProblemAsync(await api.Import(PlacesApi.Row("city", new string('x', 161))), HttpStatusCode.BadRequest, "INVALID_DATA");
        await AssertProblemAsync(await api.Import(Enumerable.Range(0, 2001).Select(i => PlacesApi.Row("city", "C" + i)).ToArray()), HttpStatusCode.BadRequest, "INVALID_DATA");
        Assert.Empty(await api.Places("?kind=city"));

        // Import: missing places above a row are created, names are found case-insensitively, existing places are skipped
        // (never overwritten), an inactive row is kept but hidden
        var first = await api.Import(
            PlacesApi.Row("city", "Erbil", ckb: "هەولێر", ar: "أربيل", en: "Erbil"), PlacesApi.Row("city", "Duhok"),
            PlacesApi.Row("neighbourhood", "Ankawa", "Erbil"), PlacesApi.Row("neighbourhood", "Bakhtiari", "Erbil"), PlacesApi.Row("neighbourhood", " ankawa ", "ERBIL"),
            PlacesApi.Row("neighbourhood", "Old Quarter", "Duhok", active: false),
            PlacesApi.Row("street", "Main Street", "Erbil", "Ankawa"), PlacesApi.Row("street", "Gulan Street", "Erbil", "Shorsh"),
            PlacesApi.Row("alley", "Alley 1", "Erbil", "Ankawa", "Main Street"), PlacesApi.Row("alley", "Alley 2", "Erbil", "Ankawa"));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(new LifeDrop.Application.Places.ImportPlacesResult(10, 1), await first.Content.ReadFromJsonAsync<LifeDrop.Application.Places.ImportPlacesResult>());   // Shorsh was created by its street
        var again = await (await api.Import(PlacesApi.Row("city", "erbil"), PlacesApi.Row("street", "Main Street", "Erbil", "Ankawa"))).Content.ReadFromJsonAsync<LifeDrop.Application.Places.ImportPlacesResult>();
        Assert.Equal(new LifeDrop.Application.Places.ImportPlacesResult(0, 2), again);

        // Browsing the tree: roots with kind=city, then the children of a place; counts only active places
        api.As(t1, rep);
        var cities = await api.Places("?kind=city");
        Assert.Equal(new[] { ("Duhok", 0), ("Erbil", 3) }, cities.Select(c => (c.Name, c.ChildCount)).ToArray());
        var erbilCity = cities[1];
        Assert.Equal(("هەولێر", "أربيل", "Erbil", (Guid?)null), (erbilCity.NameCkb, erbilCity.NameAr, erbilCity.NameEn, erbilCity.ParentId));
        var neighbourhoods = await api.Places($"?parentId={erbilCity.Id}");
        Assert.Equal(new[] { ("Ankawa", 2), ("Bakhtiari", 0), ("Shorsh", 1) }, neighbourhoods.Select(n => (n.Name, n.ChildCount)).ToArray());
        Guid erbil = erbilCity.Id, ankawa = neighbourhoods[0].Id, bakhtiari = neighbourhoods[1].Id, shorsh = neighbourhoods[2].Id;
        var inAnkawa = await api.Places($"?parentId={ankawa}");
        Assert.Equal(new[] { ("Alley 2", "alley", 0), ("Main Street", "street", 1) }, inAnkawa.Select(p => (p.Name, p.Kind, p.ChildCount)).ToArray());
        var mainStreet = inAnkawa[1].Id;
        var alley1 = (await api.Places($"?parentId={mainStreet}")).Single();
        Assert.Equal("Alley 1", alley1.Name);
        var gulanStreet = (await api.Places($"?parentId={shorsh}")).Single().Id;
        Assert.Equal(new[] { "Gulan Street" }, (await api.Places("?q=GUL")).Select(p => p.Name).ToArray());
        Assert.Empty(await api.Places("?q=%25"));
        Assert.Equal(new[] { "Main Street", "Gulan Street" }.OrderBy(n => n, StringComparer.Ordinal), (await api.Places("?kind=street")).Select(p => p.Name));
        Assert.Empty(await api.Places($"?parentId={cities[0].Id}&includeInactive=true"));   // a field user does not get inactive places
        await AssertProblemAsync(await client.GetAsync("/places?kind=lane"), HttpStatusCode.BadRequest, "INVALID_DATA");
        await AssertProblemAsync(await client.GetAsync("/places?limit=0"), HttpStatusCode.BadRequest, "INVALID_DATA");
        api.As(t1, manager);
        var oldQuarter = (await api.Places($"?parentId={cities[0].Id}&includeInactive=true")).Single();
        Assert.Equal(("Old Quarter", "inactive"), (oldQuarter.Name, oldQuarter.Status));

        // Any employee adds a neighbourhood, street or alley; it is part of the reference at once, no approval
        api.As(t1, rep);
        var hospital = await api.Add("street", "Hospital Street", ankawa);
        var alley3 = await api.Add("alley", "Alley 3", hospital);
        var nishtiman = await api.Add("neighbourhood", "Nishtiman", erbil);
        Assert.All(await api.Places($"?q=nishtiman"), p => Assert.Equal("active", p.Status));
        await AssertProblemAsync(await api.Post(new { ParentId = ankawa, Kind = "street", Name = "  hospital STREET " }), HttpStatusCode.Conflict, "DUPLICATE_NAME");
        await AssertProblemAsync(await api.Post(new { ParentId = erbil, Kind = "street", Name = "Skips a level" }), HttpStatusCode.BadRequest, "INVALID_DATA");
        await AssertProblemAsync(await api.Post(new { ParentId = ankawa, Kind = "neighbourhood", Name = "Inside" }), HttpStatusCode.BadRequest, "INVALID_DATA");
        await AssertProblemAsync(await api.Post(new { ParentId = erbil, Kind = "alley", Name = "Alley under city" }), HttpStatusCode.BadRequest, "INVALID_DATA");
        api.As(t1, manager);
        await AssertProblemAsync(await api.Post(new { ParentId = erbil, Kind = "city", Name = "Parent for a city" }), HttpStatusCode.BadRequest, "INVALID_DATA");
        api.As(t1, rep);
        await AssertProblemAsync(await api.Post(new { Kind = "street", Name = "No parent" }), HttpStatusCode.BadRequest, "INVALID_DATA");
        await AssertProblemAsync(await api.Post(new { ParentId = Guid.NewGuid(), Kind = "street", Name = "Unknown parent" }), HttpStatusCode.NotFound, "NOT_FOUND");
        await AssertProblemAsync(await api.Post(new { ParentId = oldQuarter.Id, Kind = "street", Name = "Under inactive" }), HttpStatusCode.BadRequest, "INVALID_DATA");
        await AssertProblemAsync(await api.Post(new { ParentId = ankawa, Kind = "road", Name = "Bad kind" }), HttpStatusCode.BadRequest, "INVALID_DATA");
        await AssertProblemAsync(await api.Post(new { ParentId = ankawa, Kind = "street", Name = new string('x', 161) }), HttpStatusCode.BadRequest, "INVALID_DATA");
        api.As(t1, finance);
        await AssertProblemAsync(await api.Post(new { ParentId = ankawa, Kind = "street", Name = "Finance Street" }), HttpStatusCode.Forbidden, "FORBIDDEN");

        // Managers fix spelling without an approval step; a field user cannot
        api.As(t1, rep);
        await AssertProblemAsync(await api.Put(hospital, new { Name = "Mine", Status = "active" }), HttpStatusCode.Forbidden, "FORBIDDEN");
        await AssertProblemAsync(await api.Merge(hospital, mainStreet), HttpStatusCode.Forbidden, "FORBIDDEN");
        api.As(t1, manager);
        Assert.Equal(HttpStatusCode.NoContent, (await api.Put(hospital, new { Name = "Hospital St.", NameCkb = "شەقامی نەخۆشخانە", Status = "active" })).StatusCode);
        Assert.Equal(("Hospital St.", "شەقامی نەخۆشخانە"), (await api.Places($"?parentId={ankawa}&q=hospital")).Select(p => (p.Name, p.NameCkb!)).Single());
        await AssertProblemAsync(await api.Put(hospital, new { Name = "MAIN street", Status = "active" }), HttpStatusCode.Conflict, "DUPLICATE_NAME");
        await AssertProblemAsync(await api.Put(hospital, new { Name = "Hospital St.", Status = "suggested" }), HttpStatusCode.BadRequest, "INVALID_DATA");
        await AssertProblemAsync(await api.Put(Guid.NewGuid(), new { Name = "X", Status = "active" }), HttpStatusCode.NotFound, "NOT_FOUND");
        // Moving: a street to another neighbourhood (its alley goes along); not to a city, an inactive place or a name that exists there
        Assert.Equal(HttpStatusCode.NoContent, (await api.Put(hospital, new { Name = "Hospital St.", Status = "active", ParentId = bakhtiari })).StatusCode);
        Assert.Equal(hospital, (await api.Places($"?parentId={bakhtiari}")).Single().Id);
        Assert.Equal(alley3, (await api.Places($"?parentId={hospital}")).Single().Id);
        await AssertProblemAsync(await api.Put(hospital, new { Name = "Hospital St.", Status = "active", ParentId = erbil }), HttpStatusCode.BadRequest, "INVALID_DATA");
        await AssertProblemAsync(await api.Put(hospital, new { Name = "Hospital St.", Status = "active", ParentId = oldQuarter.Id }), HttpStatusCode.BadRequest, "INVALID_DATA");
        await AssertProblemAsync(await api.Put(hospital, new { Name = "Hospital St.", Status = "active", ParentId = Guid.NewGuid() }), HttpStatusCode.NotFound, "NOT_FOUND");
        Assert.Equal(HttpStatusCode.NoContent, (await api.Put(nishtiman, new { Name = "Nishtiman", Status = "inactive" })).StatusCode);
        api.As(t1, rep);
        Assert.DoesNotContain(nishtiman, (await api.Places($"?parentId={erbil}")).Select(p => p.Id));
        await AssertProblemAsync(await api.Post(new { ParentId = nishtiman, Kind = "street", Name = "Under deactivated" }), HttpStatusCode.BadRequest, "INVALID_DATA");

        // Customer profile: the owner sets phone, address and the most precise place; the path above it comes from the tree
        Assert.Equal(HttpStatusCode.NoContent, (await api.Profile(c1, new { Phone = " 0750 000 0000 ", Address = "Near the bridge", PlaceId = alley1.Id })).StatusCode);
        var stored = await api.Customer(c1);
        Assert.Equal(("0750 000 0000", "Near the bridge", alley1.Id), (stored.Phone, stored.Address, stored.PlaceId));
        Assert.Equal(new[] { ("city", "Erbil"), ("neighbourhood", "Ankawa"), ("street", "Main Street"), ("alley", "Alley 1") }, stored.PlacePath!.Select(p => (p.Kind, p.Name)).ToArray());
        Assert.Equal(stored.PlacePath, (await client.GetFromJsonAsync<List<LifeDrop.Application.Customers.CustomerDto>>("/customers?limit=10"))!.Single(c => c.Id == c1).PlacePath);
        Assert.Equal(HttpStatusCode.NoContent, (await api.Profile(c1, new { PlaceId = erbil })).StatusCode);   // a city alone is allowed
        Assert.Equal(new[] { "Erbil" }, (await api.Customer(c1)).PlacePath!.Select(p => p.Name).ToArray());
        await AssertProblemAsync(await api.Profile(c1, new { PlaceId = Guid.NewGuid() }), HttpStatusCode.BadRequest, "INVALID_DATA");
        await AssertProblemAsync(await api.Profile(c1, new { PlaceId = oldQuarter.Id }), HttpStatusCode.BadRequest, "INVALID_DATA");   // inactive
        await AssertProblemAsync(await api.Profile(c1, new { PlaceId = nishtiman }), HttpStatusCode.BadRequest, "INVALID_DATA");
        await AssertProblemAsync(await api.Profile(c1, new { Phone = new string('1', 41) }), HttpStatusCode.BadRequest, "INVALID_DATA");
        await AssertProblemAsync(await api.Profile(c1, new { Address = new string('a', 301) }), HttpStatusCode.BadRequest, "INVALID_DATA");
        await AssertProblemAsync(await api.Profile(Guid.NewGuid(), new { Phone = "1" }), HttpStatusCode.NotFound, "NOT_FOUND");
        Assert.Equal(erbil, (await api.Customer(c1)).PlaceId);   // refused requests changed nothing
        api.As(t1, repB);   // another rep's customer is out of scope
        await AssertProblemAsync(await api.Profile(c1, new { Phone = "1" }), HttpStatusCode.NotFound, "NOT_FOUND");
        api.As(t1, finance);
        await AssertProblemAsync(await api.Profile(c1, new { Phone = "1" }), HttpStatusCode.Forbidden, "FORBIDDEN");

        await using (var conn = new NpgsqlConnection(schemaBlock.ConnectionString))
        {
            await conn.OpenAsync();
            await using var audit = new NpgsqlCommand(@"
                SELECT action || ':' || count(*) FROM audit_logs
                WHERE tenant_id = @t1 AND (action LIKE 'place%' OR action = 'customer.profile_changed')
                GROUP BY action ORDER BY action COLLATE ""C"";", conn);
            audit.Parameters.AddWithValue("t1", t1);
            await using var reader = await audit.ExecuteReaderAsync();
            var auditRows = new List<string>();
            while (await reader.ReadAsync()) auditRows.Add(reader.GetString(0));
            Assert.Equal(new[] { "customer.profile_changed:2", "place.created:3", "place.updated:3", "places.imported:2" }, auditRows.ToArray());
        }
    }

    [SkippableFact]
    public async Task Places_MergeExportAndImportAgainWithinTenantAndTreeRulesInTheDatabase()
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
        Guid manager = Guid.NewGuid(), finance = Guid.NewGuid(), rep = Guid.NewGuid(), repB = Guid.NewGuid(), manager2 = Guid.NewGuid();
        Guid c1 = Guid.NewGuid(), c2 = Guid.NewGuid();
        await using (var conn = new NpgsqlConnection(schemaBlock.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(@"
                INSERT INTO tenants (id, name, code) VALUES (@t1, 'T1', 'T1'), (@t2, 'T2', 'T2');
                SELECT seed_default_roles(@t1);
                SELECT seed_default_roles(@t2);
                INSERT INTO users (id, tenant_id) VALUES (@m, @t1), (@fin, @t1), (@r, @t1), (@rb, @t1), (@m2, @t2);
                INSERT INTO user_roles (tenant_id, user_id, role_id)
                SELECT @t1, @m, id FROM roles WHERE tenant_id = @t1 AND name = 'SalesManager'
                UNION ALL SELECT @t1, @fin, id FROM roles WHERE tenant_id = @t1 AND name = 'FinanceManager'
                UNION ALL SELECT @t1, @r, id FROM roles WHERE tenant_id = @t1 AND name = 'SalesRep'
                UNION ALL SELECT @t1, @rb, id FROM roles WHERE tenant_id = @t1 AND name = 'SalesRep'
                UNION ALL SELECT @t2, @m2, id FROM roles WHERE tenant_id = @t2 AND name = 'SalesManager';
                SELECT set_config('app.current_tenant', @t1::text, false);
                INSERT INTO customers (id, tenant_id, type, code, name, owner_user_id) VALUES
                    (@c1, @t1, 'Pharmacy', 'C1', 'First', @r), (@c2, @t1, 'Pharmacy', 'C2', 'Second', @r);", conn);
            foreach (var (name, value) in new[] { ("t1", t1), ("t2", t2), ("m", manager), ("fin", finance), ("r", rep), ("rb", repB), ("m2", manager2), ("c1", c1), ("c2", c2) })
                cmd.Parameters.AddWithValue(name, value);
            await cmd.ExecuteNonQueryAsync();
        }

        var api = new PlacesApi(client, (tenant, user) => GenerateToken(tenant, user, conf["Jwt:SigningKey"]!, conf["Jwt:Issuer"]!, conf["Jwt:Audience"]!));

        // A small tree to work on
        api.As(t1, manager);
        Assert.Equal(HttpStatusCode.OK, (await api.Import(
            PlacesApi.Row("city", "Erbil", ckb: "هەولێر", ar: "أربيل", en: "Erbil"), PlacesApi.Row("city", "Duhok"),
            PlacesApi.Row("neighbourhood", "Old Quarter", "Duhok", active: false),
            PlacesApi.Row("street", "Main Street", "Erbil", "Ankawa"), PlacesApi.Row("street", "Gulan Street", "Erbil", "Shorsh"),
            PlacesApi.Row("alley", "Alley 1", "Erbil", "Ankawa", "Main Street"))).StatusCode);
        var erbil = (await api.Places("?kind=city")).Single(c => c.Name == "Erbil").Id;
        var neighbourhoods = await api.Places($"?parentId={erbil}");
        Guid ankawa = neighbourhoods.Single(n => n.Name == "Ankawa").Id, shorsh = neighbourhoods.Single(n => n.Name == "Shorsh").Id;
        var mainStreet = (await api.Places($"?parentId={ankawa}")).Single().Id;
        var alley1 = (await api.Places($"?parentId={mainStreet}")).Single();
        var gulanStreet = (await api.Places($"?parentId={shorsh}")).Single().Id;
        var oldQuarter = (await api.Places("?kind=neighbourhood&includeInactive=true&q=old")).Single();

        // Merge: customers and the places below move to the kept place; a name clash below it stops the merge
        api.As(t1, rep);
        var gulanDuplicate = await api.Add("street", "Gulan St", shorsh);
        var alleyX = await api.Add("alley", "Alley X", gulanDuplicate);
        Assert.Equal(HttpStatusCode.NoContent, (await api.Profile(c2, new { PlaceId = gulanDuplicate })).StatusCode);
        var gulanRoad = await api.Add("street", "Gulan Rd", shorsh);
        await api.Add("alley", "Alley X", gulanRoad);
        api.As(t1, manager);
        await AssertProblemAsync(await api.Merge(gulanDuplicate, gulanDuplicate), HttpStatusCode.BadRequest, "INVALID_DATA");
        await AssertProblemAsync(await api.Merge(gulanDuplicate, shorsh), HttpStatusCode.BadRequest, "INVALID_DATA");      // another kind
        await AssertProblemAsync(await api.Merge(gulanDuplicate, Guid.NewGuid()), HttpStatusCode.NotFound, "NOT_FOUND");
        await AssertProblemAsync(await api.Merge(gulanDuplicate, oldQuarter.Id), HttpStatusCode.Conflict, "CONCURRENCY_CONFLICT");   // an inactive target is refused first
        Assert.Equal(HttpStatusCode.NoContent, (await api.Merge(gulanDuplicate, gulanStreet)).StatusCode);
        api.As(t1, rep);
        Assert.Equal(gulanStreet, (await api.Customer(c2)).PlaceId);
        Assert.Equal(new[] { "Alley X" }, (await api.Places($"?parentId={gulanStreet}")).Select(p => p.Name).ToArray());
        Assert.Equal(gulanStreet, (await api.Places($"?q=alley x&kind=alley")).Select(p => p.ParentId).Single(p => p == gulanStreet));
        Assert.DoesNotContain(gulanDuplicate, (await api.Places($"?parentId={shorsh}")).Select(p => p.Id));
        await AssertProblemAsync(await api.Profile(c2, new { PlaceId = gulanDuplicate }), HttpStatusCode.BadRequest, "INVALID_DATA");   // merged away
        api.As(t1, manager);
        await AssertProblemAsync(await api.Merge(gulanDuplicate, gulanStreet), HttpStatusCode.Conflict, "CONCURRENCY_CONFLICT");        // already merged
        await AssertProblemAsync(await api.Put(gulanDuplicate, new { Name = "Again", Status = "active" }), HttpStatusCode.NotFound, "NOT_FOUND");
        await AssertProblemAsync(await api.Merge(gulanRoad, gulanStreet), HttpStatusCode.Conflict, "CONCURRENCY_CONFLICT");            // both have an "Alley X"
        Assert.Equal(2, (await api.Places($"?parentId={shorsh}")).Count);                                                                // nothing moved
        api.As(t1, rep);
        await api.Add("street", "Gulan St", shorsh);                                                                                    // the merged name is free again
        Assert.Equal(alleyX, (await api.Places("?q=alley x&kind=alley")).Select(p => p.Id).Intersect(new[] { alleyX }).Single());

        // The tree rules also hold in the database itself
        await using (var conn = new NpgsqlConnection(schemaBlock.ConnectionString))
        {
            await conn.OpenAsync();
            await using (var tenant = new NpgsqlCommand("SELECT set_config('app.current_tenant', @t1::text, false)", conn))
            {
                tenant.Parameters.AddWithValue("t1", t1);
                await tenant.ExecuteNonQueryAsync();
            }
            async Task<string?> SqlState(string sql, params (string, object)[] parameters)
            {
                await using var command = new NpgsqlCommand(sql, conn);
                command.Parameters.AddWithValue("t1", t1);
                foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
                try { await command.ExecuteNonQueryAsync(); return null; }
                catch (PostgresException e) { return e.SqlState; }
            }
            Assert.Equal("23514", await SqlState("INSERT INTO places (tenant_id, parent_id, kind, name) VALUES (@t1, @p, 'alley', 'x')", ("p", erbil)));
            Assert.Equal("23514", await SqlState("INSERT INTO places (tenant_id, parent_id, kind, name) VALUES (@t1, NULL, 'street', 'x')"));
            Assert.Equal("23514", await SqlState("UPDATE places SET kind = 'street' WHERE id = @p", ("p", ankawa)));
            Assert.Equal("23505", await SqlState("INSERT INTO places (tenant_id, parent_id, kind, name) VALUES (@t1, @p, 'neighbourhood', ' ANKAWA ')", ("p", erbil)));
        }

        // Export for other projects: the whole tree with the names of each path, JSON or CSV; inactive places only when asked
        api.As(t1, manager);
        var exported = (await client.GetFromJsonAsync<List<LifeDrop.Application.Places.PlaceExportRow>>("/places/export"))!;
        Assert.DoesNotContain(exported, r => r.Status != "active");
        var exportedAlley = exported.Single(r => r.Id == alley1.Id);
        Assert.Equal(("alley", "Erbil", "Ankawa", "Main Street", "Alley 1", mainStreet), (exportedAlley.Kind, exportedAlley.City, exportedAlley.Neighbourhood, exportedAlley.Street, exportedAlley.Name, exportedAlley.ParentId!.Value));
        var exportedCity = exported.Single(r => r.Id == erbil);
        Assert.Equal((null, "Erbil", null, null, "هەولێر", "أربيل"), ((Guid?)exportedCity.ParentId, exportedCity.City, exportedCity.Neighbourhood, exportedCity.Street, exportedCity.NameCkb, exportedCity.NameAr));
        Assert.Equal(("Gulan Street", "Shorsh", "Erbil"), (exported.Single(r => r.Id == gulanStreet).Name, exported.Single(r => r.Id == gulanStreet).Neighbourhood, exported.Single(r => r.Id == gulanStreet).City));
        var everything = (await client.GetFromJsonAsync<List<LifeDrop.Application.Places.PlaceExportRow>>("/places/export?includeInactive=true"))!;
        Assert.True(everything.Count > exported.Count);
        Assert.Contains(everything, r => r.Id == oldQuarter.Id && r.Status == "inactive");
        await AssertProblemAsync(await client.GetAsync("/places/export?format=xml"), HttpStatusCode.BadRequest, "INVALID_DATA");

        await api.Add("street", "Street, with \"quotes\"", ankawa);
        await api.Add("street", "=SUM(1+1)", ankawa);
        var csvResponse = await client.GetAsync("/places/export?format=csv");
        Assert.True(csvResponse.IsSuccessStatusCode, await csvResponse.Content.ReadAsStringAsync());
        Assert.Equal("text/csv", csvResponse.Content.Headers.ContentType!.MediaType);
        var csv = await csvResponse.Content.ReadAsStringAsync();
        Assert.StartsWith(LifeDrop.Application.Places.PlaceExport.CsvHeader, csv.TrimStart('﻿'));
        Assert.Contains("\"Street, with \"\"quotes\"\"\"", csv);
        Assert.Contains(",'=SUM(1+1),", csv);   // a spreadsheet would run a leading "=": the text is kept as text
        Assert.Contains("هەولێر", csv);

        // The export is also an import file: another company imports it and gets the same tree
        var rows = exported.Select(r => PlacesApi.Row(r.Kind, r.Name, r.City, r.Kind is "city" or "neighbourhood" ? null : r.Neighbourhood,
            r.Kind == "alley" ? r.Street : null, r.NameCkb, r.NameAr, r.NameEn)).ToArray();
        api.As(t2, manager2);
        Assert.Empty(await api.Places("?kind=city"));   // nothing of the first company is visible
        var copied = await (await api.Import(rows)).Content.ReadFromJsonAsync<LifeDrop.Application.Places.ImportPlacesResult>();
        Assert.Equal(new LifeDrop.Application.Places.ImportPlacesResult(exported.Count, 0), copied);
        var copy = (await client.GetFromJsonAsync<List<LifeDrop.Application.Places.PlaceExportRow>>("/places/export"))!;
        Assert.Equal(exported.Select(r => (r.Kind, r.City, r.Neighbourhood, r.Street, r.Name, r.NameCkb)).OrderBy(r => r.ToString(), StringComparer.Ordinal),
            copy.Select(r => (r.Kind, r.City, r.Neighbourhood, r.Street, r.Name, r.NameCkb)).OrderBy(r => r.ToString(), StringComparer.Ordinal));
        Assert.DoesNotContain(copy, r => exported.Any(o => o.Id == r.Id));   // new ids in the other company
        // ... and cannot touch the first company's places
        await AssertProblemAsync(await api.Merge(mainStreet, gulanStreet), HttpStatusCode.NotFound, "NOT_FOUND");
        await AssertProblemAsync(await api.Put(mainStreet, new { Name = "Hijacked", Status = "active" }), HttpStatusCode.NotFound, "NOT_FOUND");
        await AssertProblemAsync(await api.Post(new { ParentId = erbil, Kind = "neighbourhood", Name = "Foreign" }), HttpStatusCode.NotFound, "NOT_FOUND");

        // Audit: one row per successful change, refused ones leave none
        await using (var conn = new NpgsqlConnection(schemaBlock.ConnectionString))
        {
            await conn.OpenAsync();
            await using var audit = new NpgsqlCommand(@"
                SELECT action || ':' || count(*) FROM audit_logs
                WHERE tenant_id = @t1 AND (action LIKE 'place%' OR action = 'customer.profile_changed')
                GROUP BY action ORDER BY action COLLATE ""C"";", conn);
            audit.Parameters.AddWithValue("t1", t1);
            await using var reader = await audit.ExecuteReaderAsync();
            var auditRows = new List<string>();
            while (await reader.ReadAsync()) auditRows.Add(reader.GetString(0));
            Assert.Equal(new[] { "customer.profile_changed:1", "place.created:7", "place.merged:1", "places.imported:1" }, auditRows.ToArray());
        }
    }

    private sealed record CreatedResponseDto(Guid Id);

    [SkippableFact]
    public async Task MobileClient_PicksAddsPlacesAndRegistersCustomersAgainstTheRealApi()
    {
        // Plan step 1.23-b: the phone's customer and place calls match the server contract end to end.
        var url = GetRequiredEnvVar("TEST_DATABASE_URL");
        await VerifyRoleAsync(url, expectElevated: false);

        await using var schemaBlock = await SetupIsolatedSchemaAsync(url);
        var now = new DateTimeOffset(2026, 10, 5, 6, 0, 0, TimeSpan.Zero);
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

        Guid t1 = Guid.NewGuid(), rep = Guid.NewGuid(), manager = Guid.NewGuid(), device = Guid.NewGuid(), mine = Guid.NewGuid(), notMine = Guid.NewGuid();
        await using (var conn = new NpgsqlConnection(schemaBlock.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(@"
                INSERT INTO tenants (id, name, code) VALUES (@t1, 'T1', 'T1');
                SELECT seed_default_roles(@t1);
                INSERT INTO users (id, tenant_id) VALUES (@r, @t1), (@m, @t1);
                INSERT INTO user_roles (tenant_id, user_id, role_id)
                SELECT @t1, @r, id FROM roles WHERE tenant_id = @t1 AND name = 'SalesRep'
                UNION ALL SELECT @t1, @m, id FROM roles WHERE tenant_id = @t1 AND name = 'SalesManager';
                INSERT INTO devices (id, tenant_id, user_id, device_label, platform, install_id, app_version, secret_hash, status)
                VALUES (@d, @t1, @r, 'Phone', 'android', 'install-1', '1.0', '\x00', 'approved');
                SELECT set_config('app.current_tenant', @t1::text, false);
                INSERT INTO customers (id, tenant_id, type, code, name, owner_user_id) VALUES
                    (@mine, @t1, 'Pharmacy', 'C-MINE', 'My Pharmacy', @r), (@other, @t1, 'Pharmacy', 'C-OTHER', 'Not mine', NULL);", conn);
            foreach (var (name, value) in new[] { ("t1", t1), ("r", rep), ("m", manager), ("d", device), ("mine", mine), ("other", notMine) })
                cmd.Parameters.AddWithValue(name, value);
            await cmd.ExecuteNonQueryAsync();
        }

        // A manager prepares the cities (the app never adds cities)
        var managerClient = factory.CreateClient();
        managerClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", GenerateToken(t1, manager, conf["Jwt:SigningKey"]!, conf["Jwt:Issuer"]!, conf["Jwt:Audience"]!));
        Assert.Equal(HttpStatusCode.OK, (await managerClient.PostAsJsonAsync("/places/import", new
        {
            Rows = new object[]
            {
                new { Kind = "city", Name = "Erbil", NameCkb = "هەولێر", NameEn = "Erbil" },
                new { Kind = "city", Name = "Duhok" },
                new { Kind = "neighbourhood", Name = "Ankawa", City = "Erbil", NameCkb = "عەنکاوە" }
            }
        })).StatusCode);

        var token = GenerateToken(t1, rep, conf["Jwt:SigningKey"]!, conf["Jwt:Issuer"]!, conf["Jwt:Audience"]!, deviceId: device);
        var api = new LifeDrop.Mobile.Visits.CustomersApiClient(factory.CreateClient(), new StaticMobileSession(token));

        // Pick: the cities, then the neighbourhoods below one; suggestions follow the typed text
        var picker = new LifeDrop.Mobile.Visits.PlacePicker(api);
        await picker.LoadAsync(null);
        Assert.Equal(new[] { "Duhok", "Erbil" }, picker.Levels[0].Items.Select(p => p.Name).ToArray());
        await picker.SelectAsync(0, picker.Levels[0].Items.Single(p => p.Name == "Erbil").Id);
        Assert.Equal(new[] { "Ankawa" }, picker.Suggest(1, "عەنکا", "ckb").Select(p => p.Name).ToArray());

        // Add: a neighbourhood, a street under it and an alley under the street are part of the reference at once
        Assert.Equal(LifeDrop.Mobile.Visits.PlaceAddResult.Added, await picker.AddAsync(1, "neighbourhood", "Nishtiman"));
        Assert.Equal(LifeDrop.Mobile.Visits.PlaceAddResult.Added, await picker.AddAsync(2, "street", "Hospital Street"));
        Assert.Equal(LifeDrop.Mobile.Visits.PlaceAddResult.Added, await picker.AddAsync(3, "alley", "Alley 1"));
        var alley = picker.PlaceId;
        Assert.Equal(LifeDrop.Mobile.Visits.PlaceAddResult.UsedExisting, await picker.AddAsync(1, "neighbourhood", " nishtiman "));   // the same name is chosen, not added twice
        Assert.Equal(picker.Levels[1].SelectedId, picker.PlaceId);   // choosing the neighbourhood again closes the lists below it
        await picker.SelectAsync(1, picker.Levels[1].Items.Single(p => p.Name == "Nishtiman").Id);
        Assert.Equal(new[] { "Hospital Street" }, picker.Levels[2].Items.Select(p => p.Name).ToArray());

        // Save the profile of a customer of mine and read it back with the path above the alley
        Assert.Equal(LifeDrop.Mobile.Visits.VisitActionResult.Success, await api.SetProfileAsync(mine, "0750 000 0000", "By the bridge", alley));
        var (found, profile) = await api.GetCustomerAsync(mine);
        Assert.Equal(LifeDrop.Mobile.Visits.VisitActionResult.Success, found);
        Assert.Equal(("0750 000 0000", "By the bridge", alley), (profile!.Phone, profile.Address, profile.PlaceId));
        Assert.Equal(new[] { ("city", "Erbil"), ("neighbourhood", "Nishtiman"), ("street", "Hospital Street"), ("alley", "Alley 1") }, profile.PlacePath!.Select(p => (p.Kind, p.Name)).ToArray());

        // A second picker opens on the saved path
        var reopened = new LifeDrop.Mobile.Visits.PlacePicker(api);
        await reopened.LoadAsync(profile.PlacePath);
        Assert.Equal(alley, reopened.PlaceId);
        Assert.Equal(4, reopened.Levels.Count);

        // A customer I did not add cannot be changed or read by me
        Assert.Equal(LifeDrop.Mobile.Visits.VisitActionResult.NotFound, await api.SetProfileAsync(notMine, "1", null, null));
        Assert.Equal(LifeDrop.Mobile.Visits.VisitActionResult.NotFound, (await api.GetCustomerAsync(notMine)).Result);
        Assert.Equal(LifeDrop.Mobile.Visits.VisitActionResult.InvalidData, await api.SetProfileAsync(mine, "1", null, Guid.NewGuid()));
        Assert.Equal(LifeDrop.Mobile.Visits.VisitActionResult.NotFound, (await api.CreatePlaceAsync(Guid.NewGuid(), "neighbourhood", "x")).Result);   // an unknown parent

        // Register a new customer: code made by the app, profile and GPS position saved
        var fix = new LifeDrop.Mobile.Visits.DeviceFix(36.1912, 44.0093, 8, now.AddSeconds(-5), false);
        var flow = new LifeDrop.Mobile.Visits.NewCustomerFlow(api, new FixedClock(now), _ => 3);
        var outcome = await flow.CreateAsync(new LifeDrop.Mobile.Visits.NewCustomerRequest("  Shifa Pharmacy ", "Pharmacy", "0751", "Near the clinic", alley, fix));
        Assert.True(outcome.Complete, outcome.ToString());
        Assert.Equal("N261005-DDDD", outcome.Code);
        var (_, created) = await api.GetCustomerAsync(outcome.CustomerId!.Value);
        Assert.Equal(("N261005-DDDD", "Shifa Pharmacy", "Pharmacy", "0751", alley), (created!.Code, created.Name, created.Type, created.Phone, created.PlaceId));

        // The same code again is taken: the app tries other codes, and gives up after five
        var taken = await new LifeDrop.Mobile.Visits.NewCustomerFlow(api, new FixedClock(now), _ => 3).CreateAsync(new LifeDrop.Mobile.Visits.NewCustomerRequest("Second", "Doctor", null, null, null, null));
        Assert.Equal(LifeDrop.Mobile.Visits.VisitActionResult.Conflict, taken.Create);
        var counter = 0;
        var retried = await new LifeDrop.Mobile.Visits.NewCustomerFlow(api, new FixedClock(now), n => counter++ < 4 ? 3 : 5 % n).CreateAsync(new LifeDrop.Mobile.Visits.NewCustomerRequest("Third", "Doctor", null, null, null, null));
        Assert.True(retried.Complete);
        Assert.Equal("N261005-FFFF", retried.Code);   // the first attempt reused the taken code, the next one was free

        await using (var conn = new NpgsqlConnection(schemaBlock.ConnectionString))
        {
            await conn.OpenAsync();
            await using (var tenant = new NpgsqlCommand("SELECT set_config('app.current_tenant', @t1::text, false)", conn))
            {
                tenant.Parameters.AddWithValue("t1", t1);
                await tenant.ExecuteNonQueryAsync();
            }
            await using var state = new NpgsqlCommand("SELECT status, location_source, owner_user_id FROM customers WHERE code = 'N261005-DDDD'", conn);
            await using var reader = await state.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(("located", "gps", rep), (reader.GetString(0), reader.GetString(1), reader.GetGuid(2)));   // placed by GPS and owned by the user who added it
        }
    }

    private sealed class FakePushSender : LifeDrop.Application.Push.IPushSender
    {
        public List<(string Token, Guid Id)> Sent { get; } = new();
        public bool IsConfigured { get; set; } = true;
        public Task<LifeDrop.Application.Push.PushOutcome> SendAsync(string token, LifeDrop.Application.Push.PushMessage message, CancellationToken cancellationToken)
        {
            if (token.StartsWith("dead-", StringComparison.Ordinal)) return Task.FromResult(LifeDrop.Application.Push.PushOutcome.InvalidToken);
            if (token.StartsWith("flaky-", StringComparison.Ordinal)) return Task.FromResult(LifeDrop.Application.Push.PushOutcome.Failed);
            Sent.Add((token, message.NotificationId));
            return Task.FromResult(LifeDrop.Application.Push.PushOutcome.Sent);
        }
    }

    [SkippableFact]
    public async Task Push_TokensAreOwnedByOneUser_AndWaitingMessagesAreSentOnce()
    {
        var url = GetRequiredEnvVar("TEST_DATABASE_URL");
        await VerifyRoleAsync(url, expectElevated: false);

        await using var schemaBlock = await SetupIsolatedSchemaAsync(url);
        var sender = new FakePushSender();
        await using var factory = new WebApplicationFactory<LifeDrop.API.Program>()
            .WithWebHostBuilder(webBuilder =>
            {
                webBuilder.UseSetting("ConnectionStrings:LifeDrop", schemaBlock.ConnectionString);
                TestHostSettings.UseTestJwt(webBuilder);
                webBuilder.UseSetting("Push:DispatchIntervalSeconds", "0");
                webBuilder.ConfigureTestServices(services =>
                {
                    var descriptor = System.Linq.Enumerable.SingleOrDefault(services, d => d.ServiceType == typeof(LifeDrop.Application.Auth.ISessionStore));
                    if (descriptor != null) services.Remove(descriptor);
                    services.AddScoped<LifeDrop.Application.Auth.ISessionStore, FakeSessionStore>();
                    services.AddSingleton<LifeDrop.Application.Push.IPushSender>(sender);
                });
            });
        var conf = factory.Services.GetRequiredService<IConfiguration>();
        var client = factory.CreateClient();

        Guid t1 = Guid.NewGuid(), t2 = Guid.NewGuid(), alice = Guid.NewGuid(), bob = Guid.NewGuid(), carol = Guid.NewGuid(), other = Guid.NewGuid();
        Guid n1 = Guid.NewGuid(), n2 = Guid.NewGuid(), n3 = Guid.NewGuid(), n4 = Guid.NewGuid(), nOther = Guid.NewGuid();
        await using (var conn = new NpgsqlConnection(schemaBlock.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(@"
                INSERT INTO tenants (id, name, code) VALUES (@t1, 'T1', 'T1'), (@t2, 'T2', 'T2');
                INSERT INTO users (id, tenant_id, email) VALUES (@a, @t1, 'alice@t1'), (@b, @t1, 'bob@t1'), (@c, @t1, 'carol@t1'), (@o, @t2, 'other@t2');", conn);
            cmd.Parameters.AddWithValue("t1", t1); cmd.Parameters.AddWithValue("t2", t2);
            cmd.Parameters.AddWithValue("a", alice); cmd.Parameters.AddWithValue("b", bob); cmd.Parameters.AddWithValue("c", carol); cmd.Parameters.AddWithValue("o", other);
            await cmd.ExecuteNonQueryAsync();
        }
        void As(Guid tenant, Guid user) => client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            GenerateToken(tenant, user, conf["Jwt:SigningKey"]!, conf["Jwt:Issuer"]!, conf["Jwt:Audience"]!));
        Task<HttpResponseMessage> Register(string token, string platform = "android") => client.PutAsJsonAsync("/push-token", new { Token = token, Platform = platform });
        Task<HttpResponseMessage> Remove(string token) => client.PostAsJsonAsync("/push-token/remove", new { Token = token });
        async Task Notify(Guid tenant, Guid id, Guid user, Guid actor, string? createdAgo = null)
        {
            await using var conn = new NpgsqlConnection(schemaBlock.ConnectionString);
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand($@"
                SELECT set_config('app.current_tenant', @t::text, false);
                INSERT INTO notifications (id, tenant_id, user_id, kind, entity_id, actor_id, created_at)
                VALUES (@id, @t, @u, 'customer.profile_changed', 'c-1', @actor, now() - interval '{createdAgo ?? "0 seconds"}')", conn);
            cmd.Parameters.AddWithValue("t", tenant); cmd.Parameters.AddWithValue("id", id); cmd.Parameters.AddWithValue("u", user); cmd.Parameters.AddWithValue("actor", actor);
            await cmd.ExecuteNonQueryAsync();
        }
        async Task<int> Dispatch(Guid tenant)
        {
            using var scope = factory.Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<LifeDrop.Application.Push.PushDispatcher>().DispatchTenantAsync(new LifeDrop.Domain.Common.TenantId(tenant), CancellationToken.None);
        }
        async Task<(DateTimeOffset? Pushed, int Attempts)> State(Guid tenant, Guid id)
        {
            await using var conn = new NpgsqlConnection(schemaBlock.ConnectionString);
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand("SELECT set_config('app.current_tenant', @t::text, false); SELECT pushed_at, push_attempts FROM notifications WHERE id = @id", conn);
            cmd.Parameters.AddWithValue("t", tenant); cmd.Parameters.AddWithValue("id", id);
            await using var reader = await cmd.ExecuteReaderAsync();
            await reader.NextResultAsync();
            Assert.True(await reader.ReadAsync());
            return (reader.IsDBNull(0) ? null : reader.GetFieldValue<DateTimeOffset>(0), reader.GetInt32(1));
        }

        var phoneA = "token-alice-phone-0000000001";
        var phoneB = "token-bob-phone-000000000002";

        // Validation: short token, unknown platform, no sign-in.
        client.DefaultRequestHeaders.Authorization = null;
        Assert.Equal(HttpStatusCode.Unauthorized, (await Register(phoneA)).StatusCode);
        As(t1, alice);
        Assert.Equal(HttpStatusCode.BadRequest, (await Register("short")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Register(phoneA, "windows")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Register(phoneA)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Register(phoneA)).StatusCode);   // repeating is harmless

        // The same phone is used by Bob later: the token moves, Alice no longer gets pushes on it.
        As(t1, bob);
        Assert.Equal(HttpStatusCode.NoContent, (await Register(phoneA)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Register(phoneB)).StatusCode);
        As(t1, alice);
        Assert.Equal(HttpStatusCode.NotFound, (await Remove(phoneA)).StatusCode);   // not Alice's any more
        As(t2, other);
        Assert.Equal(HttpStatusCode.NotFound, (await Remove(phoneB)).StatusCode);   // another company never sees it

        await Notify(t1, n1, alice, carol);   // Alice has no token now: marked, nothing sent
        await Notify(t1, n2, bob, carol);     // Bob has two phones: both get it
        Assert.Equal(1, await Dispatch(t1));
        Assert.Equal(new[] { (phoneA, n2), (phoneB, n2) }, sender.Sent.OrderBy(s => s.Token, StringComparer.Ordinal).ToArray());
        Assert.NotNull((await State(t1, n1)).Pushed);
        Assert.NotNull((await State(t1, n2)).Pushed);

        // Sent once only.
        Assert.Equal(0, await Dispatch(t1));
        Assert.Equal(2, sender.Sent.Count);

        // A dead token is dropped; a temporary failure is retried and finally given up after MaxAttempts.
        As(t1, carol);
        Assert.Equal(HttpStatusCode.NoContent, (await Register("dead-token-carol-0000000001")).StatusCode);
        await Notify(t1, n3, carol, alice);
        Assert.Equal(0, await Dispatch(t1));
        Assert.NotNull((await State(t1, n3)).Pushed);
        As(t1, carol);
        Assert.Equal(HttpStatusCode.NotFound, (await Remove("dead-token-carol-0000000001")).StatusCode);   // dropped by the dispatcher
        Assert.Equal(HttpStatusCode.NoContent, (await Register("flaky-token-carol-000000001")).StatusCode);
        await Notify(t1, n4, carol, alice);
        for (var i = 1; i < LifeDrop.Application.Push.PushDispatcher.MaxAttempts; i++)
        {
            Assert.Equal(0, await Dispatch(t1));
            Assert.Equal((null, i), await State(t1, n4));   // waiting, attempts counted
        }
        Assert.Equal(0, await Dispatch(t1));
        Assert.NotNull((await State(t1, n4)).Pushed);   // given up

        // Old messages are not pushed; another company's messages are untouched by this company's run; no key = nothing consumed.
        var nOld = Guid.NewGuid();
        await Notify(t1, nOld, bob, carol, "2 days");
        await Notify(t2, nOther, other, other);
        sender.Sent.Clear();
        Assert.Equal(0, await Dispatch(t1));
        Assert.Empty(sender.Sent);
        Assert.NotNull((await State(t1, nOld)).Pushed);
        Assert.Null((await State(t2, nOther)).Pushed);
        sender.IsConfigured = false;
        Assert.Equal(0, await Dispatch(t2));
        Assert.Null((await State(t2, nOther)).Pushed);
    }

    [SkippableFact]
    public async Task RegionReps_EditRegionCustomersAndTheResponsibleAreNotified()
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
        Guid manager = Guid.NewGuid(), manager2 = Guid.NewGuid(), sv = Guid.NewGuid(), sv2 = Guid.NewGuid();
        Guid repA = Guid.NewGuid(), repA2 = Guid.NewGuid(), repB = Guid.NewGuid(), repB2 = Guid.NewGuid(), repX = Guid.NewGuid(), tenant2Manager = Guid.NewGuid();
        Guid cA = Guid.NewGuid(), cA2 = Guid.NewGuid(), cB = Guid.NewGuid();
        await using (var conn = new NpgsqlConnection(schemaBlock.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(@"
                INSERT INTO tenants (id, name, code) VALUES (@t1, 'T1', 'T1'), (@t2, 'T2', 'T2');
                SELECT seed_default_roles(@t1);
                SELECT seed_default_roles(@t2);
                INSERT INTO users (id, tenant_id, email) VALUES
                    (@m, @t1, 'manager@t1'), (@m2, @t1, 'manager2@t1'), (@sv, @t1, 'sv@t1'), (@sv2, @t1, 'sv2@t1'),
                    (@ra, @t1, 'repa@t1'), (@ra2, @t1, 'repa2@t1'), (@rb, @t1, 'repb@t1'), (@rb2, @t1, 'repb2@t1'), (@rx, @t1, 'repx@t1'), (@tm, @t2, 'manager@t2');
                INSERT INTO user_roles (tenant_id, user_id, role_id)
                SELECT @t1, u.id, r.id FROM (VALUES (@m, 'SalesManager'), (@m2, 'SalesManager'), (@sv, 'Supervisor'), (@sv2, 'Supervisor'),
                    (@ra, 'SalesRep'), (@ra2, 'SalesRep'), (@rb, 'SalesRep'), (@rb2, 'SalesRep'), (@rx, 'SalesRep')) AS u(id, role)
                JOIN roles r ON r.tenant_id = @t1 AND r.name = u.role
                UNION ALL SELECT @t2, @tm, id FROM roles WHERE tenant_id = @t2 AND name = 'SalesManager';
                SELECT set_config('app.current_tenant', @t1::text, false);
                INSERT INTO regions (id, tenant_id, name) VALUES ('55555555-0000-0000-0000-0000000000e1', @t1, 'Erbil'), ('55555555-0000-0000-0000-0000000000b1', @t1, 'Sulaymaniyah');
                INSERT INTO teams (id, tenant_id, region_id, name) VALUES
                    ('55555555-0000-0000-0000-0000000000a1', @t1, '55555555-0000-0000-0000-0000000000e1', 'Team Erbil'),
                    ('55555555-0000-0000-0000-0000000000a2', @t1, '55555555-0000-0000-0000-0000000000b1', 'Team Sulaymaniyah');
                INSERT INTO team_members (tenant_id, user_id, team_id) VALUES
                    (@t1, @sv, '55555555-0000-0000-0000-0000000000a1'), (@t1, @sv2, '55555555-0000-0000-0000-0000000000a1'),
                    (@t1, @ra, '55555555-0000-0000-0000-0000000000a1'), (@t1, @ra2, '55555555-0000-0000-0000-0000000000a1'),
                    (@t1, @rb, '55555555-0000-0000-0000-0000000000a2'), (@t1, @rb2, '55555555-0000-0000-0000-0000000000a2');
                INSERT INTO customers (id, tenant_id, type, code, name, region_id, owner_user_id) VALUES
                    (@ca, @t1, 'Pharmacy', 'CA', 'Erbil One', '55555555-0000-0000-0000-0000000000e1', @ra),
                    (@ca2, @t1, 'Pharmacy', 'CA2', 'Erbil Two', '55555555-0000-0000-0000-0000000000e1', @ra2),
                    (@cb, @t1, 'Pharmacy', 'CB', 'Sulaymaniyah One', '55555555-0000-0000-0000-0000000000b1', @rb);", conn);
            foreach (var (name, value) in new[] { ("t1", t1), ("t2", t2), ("m", manager), ("m2", manager2), ("sv", sv), ("sv2", sv2), ("ra", repA), ("ra2", repA2), ("rb", repB), ("rb2", repB2), ("rx", repX), ("tm", tenant2Manager),
                ("ca", cA), ("ca2", cA2), ("cb", cB) })
                cmd.Parameters.AddWithValue(name, value);
            await cmd.ExecuteNonQueryAsync();
        }

        void As(Guid tenant, Guid user) => client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            GenerateToken(tenant, user, conf["Jwt:SigningKey"]!, conf["Jwt:Issuer"]!, conf["Jwt:Audience"]!));
        Task<HttpResponseMessage> Profile(Guid customer, string phone) => client.PutAsJsonAsync($"/customers/{customer}/profile", new { Phone = phone });
        Task<HttpResponseMessage> Location(Guid customer) => client.PutAsJsonAsync($"/customers/{customer}/location", new { Latitude = 36.19, Longitude = 44.0, Source = "manual" });
        async Task<List<LifeDrop.Application.Notifications.NotificationDto>> Inbox(string query = "") =>
            (await client.GetFromJsonAsync<List<LifeDrop.Application.Notifications.NotificationDto>>("/notifications" + query))!;
        async Task<int> Unread() => (await client.GetFromJsonAsync<UnreadCountDto>("/notifications/unread-count"))!.Count;

        // A rep sees and changes the customers of the region of their team, not those of another region
        As(t1, repA);
        Assert.Equal(new[] { "CA", "CA2" }, (await client.GetFromJsonAsync<List<LifeDrop.Application.Customers.CustomerDto>>("/customers?limit=10"))!.Select(c => c.Code).OrderBy(c => c, StringComparer.Ordinal).ToArray());
        await AssertProblemAsync(await Profile(cB, "1"), HttpStatusCode.NotFound, "NOT_FOUND");
        // A rep without a team keeps to the customers they added
        As(t1, repX);
        Assert.Empty(await client.GetFromJsonAsync<List<LifeDrop.Application.Customers.CustomerDto>>("/customers?limit=10") ?? new());
        await AssertProblemAsync(await Profile(cA2, "1"), HttpStatusCode.NotFound, "NOT_FOUND");

        // Region with supervisors: a rep's change of a colleague's customer is told to both supervisors, nobody else
        As(t1, repA);
        Assert.Equal(HttpStatusCode.NoContent, (await Profile(cA2, "0750 111")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Profile(cA, "0750 222")).StatusCode);   // the rep's own customer: nobody is told
        Assert.Equal(0, await Unread());
        foreach (var supervisor in new[] { sv, sv2 })
        {
            As(t1, supervisor);
            var message = Assert.Single(await Inbox());
            Assert.Equal(("customer.profile_changed", cA2.ToString(), repA, "repa@t1", "CA2", "Erbil Two"), (message.Kind, message.EntityId, message.ActorId, message.ActorEmail, message.CustomerCode, message.CustomerName));
            Assert.Null(message.ReadAt);
            Assert.Equal(1, await Unread());
        }
        As(t1, manager);
        Assert.Empty(await Inbox());   // the region has supervisors, so the sales manager is not told

        // A supervisor's own change tells the other supervisor, not the manager and not themselves
        As(t1, sv);
        Assert.Equal(HttpStatusCode.NoContent, (await Profile(cA2, "0750 333")).StatusCode);
        As(t1, sv2);
        Assert.Equal(2, (await Inbox()).Count);
        As(t1, sv);
        Assert.Equal(1, (await Inbox()).Count);

        // Region without a supervisor: the sales managers are told (profile and location changes alike)
        As(t1, repB2);
        Assert.Equal(HttpStatusCode.NoContent, (await Profile(cB, "0750 444")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Location(cB)).StatusCode);
        As(t1, repB);
        Assert.Equal(HttpStatusCode.NoContent, (await Profile(cB, "0750 555")).StatusCode);   // the owner: nobody is told
        foreach (var salesManager in new[] { manager, manager2 })
        {
            As(t1, salesManager);
            var inbox = await Inbox();
            Assert.Equal(new[] { "customer.location_changed", "customer.profile_changed" }, inbox.Select(n => n.Kind).OrderBy(k => k, StringComparer.Ordinal).ToArray());
            Assert.All(inbox, n => Assert.Equal(("repb2@t1", "CB"), (n.ActorEmail, n.CustomerCode)));
            Assert.True(inbox[0].CreatedAt >= inbox[1].CreatedAt);   // newest first
        }
        // The sales manager's own change tells the other manager only if the region has none... it has none, so the other manager
        As(t1, manager);
        Assert.Equal(HttpStatusCode.NoContent, (await Profile(cB, "0750 666")).StatusCode);
        As(t1, manager2);
        Assert.Equal(3, (await Inbox()).Count);
        As(t1, manager);
        Assert.Equal(2, (await Inbox()).Count);

        // Everybody reads and marks only their own messages
        As(t1, manager2);
        var mine = await Inbox("?unread=true&limit=2");
        Assert.Equal(2, mine.Count);
        Assert.Equal(3, await Unread());
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync($"/notifications/{mine[0].Id}/read", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync($"/notifications/{mine[0].Id}/read", null)).StatusCode);   // again: harmless
        Assert.Equal(2, await Unread());
        Assert.Equal(2, (await Inbox("?unread=true")).Count);
        Assert.Equal(3, (await Inbox()).Count);
        As(t1, manager);
        await AssertProblemAsync(await client.PostAsync($"/notifications/{mine[0].Id}/read", null), HttpStatusCode.NotFound, "NOT_FOUND");   // not the manager's message
        Assert.Equal(2, await Unread());
        As(t1, manager2);
        await AssertProblemAsync(await client.GetAsync("/notifications?limit=0"), HttpStatusCode.BadRequest, "INVALID_DATA");
        await AssertProblemAsync(await client.GetAsync("/notifications?limit=201"), HttpStatusCode.BadRequest, "INVALID_DATA");
        await AssertProblemAsync(await client.PostAsync($"/notifications/{Guid.NewGuid()}/read", null), HttpStatusCode.NotFound, "NOT_FOUND");
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/notifications/read-all", null)).StatusCode);
        Assert.Equal(0, await Unread());
        Assert.Empty(await Inbox("?unread=true"));
        Assert.Equal(3, (await Inbox()).Count);
        As(t1, sv);
        Assert.Equal(1, await Unread());   // marking one's own messages did not touch the others'

        // Another company sees none of it
        As(t2, tenant2Manager);
        Assert.Empty(await Inbox());
        Assert.Equal(0, await Unread());
        await AssertProblemAsync(await client.PostAsync($"/notifications/{mine[1].Id}/read", null), HttpStatusCode.NotFound, "NOT_FOUND");
    }

    private sealed record UnreadCountDto(int Count);

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
            new { Outcome = "order_confirmed", Note = "No such outcome" },
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
            "GET /openapi/{documentName}.json", // Development only
            // Monitors (plan step 3.7): no business data; /metrics answers 404 without Metrics:Token and 401 without the token.
            "GET /health/live", "GET /health/ready", "GET /metrics",
            // Company directory for the app (plan step 4.2): public facts only (name, API address, support phone).
            "GET /company-config/{code}"
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
            "The API contract changed. If intended, regenerate docs/api/openapi-v1.json with UPDATE_OPENAPI_SNAPSHOT=1 and review the diff. " + FirstDifference(expected, actual));
    }

    // The first line where the committed contract and the generated one differ, so a failed run shows what changed without downloading anything.
    private static string FirstDifference(string expected, string actual)
    {
        var a = expected.Split('\n');
        var b = actual.Split('\n');
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
            if (a[i] != b[i])
                return $"First difference at line {i + 1}: committed '{a[i].Trim()}' but generated '{b[i].Trim()}'. Generated context: " +
                       string.Join(" | ", b.Skip(Math.Max(0, i - 3)).Take(8).Select(l => l.Trim()));
        return $"The files differ in length only (committed {a.Length} lines, generated {b.Length}).";
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

    [SkippableFact]
    public async Task PlanFromPlaces_PreviewThenCreateByPlaceRegionCountOrderAndHours()
    {
        var url = GetRequiredEnvVar("TEST_DATABASE_URL");
        await VerifyRoleAsync(url, expectElevated: false);

        var (schemaBlock, factory) = await StartFlexiblePlanHostAsync(url);
        await using var _ = schemaBlock;
        await using var __ = factory;
        var conf = factory.Services.GetRequiredService<IConfiguration>();
        var client = factory.CreateClient();

        var t1 = Guid.NewGuid();
        Guid manager = Guid.NewGuid(), supervisor = Guid.NewGuid(), finance = Guid.NewGuid(), repA = Guid.NewGuid(), repB = Guid.NewGuid();
        Guid erbil = Guid.Parse("55555555-0000-0000-0000-0000000000e1"), baghdad = Guid.Parse("55555555-0000-0000-0000-0000000000b1");
        Guid city = Guid.NewGuid(), n1 = Guid.NewGuid(), n2 = Guid.NewGuid(), street = Guid.NewGuid();
        var c = Enumerable.Range(0, 10).Select(_ => Guid.NewGuid()).ToArray();   // c[1]..c[9] = C1..C9
        await using (var conn = new NpgsqlConnection(schemaBlock.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(@"
                INSERT INTO tenants (id, name, code) VALUES (@t1, 'T1', 'T1');
                SELECT seed_default_roles(@t1);
                INSERT INTO users (id, tenant_id) VALUES (@m, @t1), (@sv, @t1), (@fin, @t1), (@ra, @t1), (@rb, @t1);
                INSERT INTO user_roles (tenant_id, user_id, role_id)
                SELECT @t1, @m, id FROM roles WHERE tenant_id = @t1 AND name = 'SalesManager'
                UNION ALL SELECT @t1, @sv, id FROM roles WHERE tenant_id = @t1 AND name = 'Supervisor'
                UNION ALL SELECT @t1, @fin, id FROM roles WHERE tenant_id = @t1 AND name = 'FinanceManager'
                UNION ALL SELECT @t1, @ra, id FROM roles WHERE tenant_id = @t1 AND name = 'SalesRep'
                UNION ALL SELECT @t1, @rb, id FROM roles WHERE tenant_id = @t1 AND name = 'SalesRep';
                SELECT set_config('app.current_tenant', @t1::text, false);
                INSERT INTO regions (id, tenant_id, name) VALUES (@erbil, @t1, 'Erbil'), (@baghdad, @t1, 'Baghdad');
                INSERT INTO teams (id, tenant_id, region_id, name) VALUES
                    ('55555555-0000-0000-0000-0000000000a1', @t1, @erbil, 'Team A'), ('55555555-0000-0000-0000-0000000000a2', @t1, @baghdad, 'Team B');
                INSERT INTO team_members (tenant_id, user_id, team_id) VALUES
                    (@t1, @sv, '55555555-0000-0000-0000-0000000000a1'), (@t1, @ra, '55555555-0000-0000-0000-0000000000a1'),
                    (@t1, @rb, '55555555-0000-0000-0000-0000000000a2');
                INSERT INTO places (id, tenant_id, parent_id, kind, name) VALUES
                    (@city, @t1, NULL, 'city', 'Erbil'), (@n1, @t1, @city, 'neighbourhood', 'N1'), (@n2, @t1, @city, 'neighbourhood', 'N2'),
                    (@street, @t1, @n1, 'street', 'S1');
                -- C1 accepts visits only on Wednesday; C2, C3 are in the street; C4, C5 in the neighbourhood; C6 in N2;
                -- C7 is pending; C8 already has a visit that day; C9 belongs to the other region.
                INSERT INTO customers (id, tenant_id, type, code, name, place_id, region_id, approval_status) VALUES
                    (@c1, @t1, 'Pharmacy', 'C1', 'Name 1', @n1, @erbil, 'approved'),
                    (@c2, @t1, 'Pharmacy', 'C2', 'Name 2', @street, @erbil, 'approved'),
                    (@c3, @t1, 'Pharmacy', 'C3', 'Name 3', @street, @erbil, 'approved'),
                    (@c4, @t1, 'Pharmacy', 'C4', 'Name 4', @n1, @erbil, 'approved'),
                    (@c5, @t1, 'Doctor', 'C5', 'Name 5', @n1, @erbil, 'approved'),
                    (@c6, @t1, 'Pharmacy', 'C6', 'Name 6', @n2, @erbil, 'approved'),
                    (@c7, @t1, 'Pharmacy', 'C7', 'Name 7', @n1, @erbil, 'pending'),
                    (@c8, @t1, 'Pharmacy', 'C8', 'Name 8', @n1, @erbil, 'approved'),
                    (@c9, @t1, 'Pharmacy', 'C9', 'Name 9', @n1, @baghdad, 'approved');
                INSERT INTO customer_visit_windows (tenant_id, customer_id, weekday, opens_at, closes_at) VALUES (@t1, @c1, 3, '09:00', '17:00');
                INSERT INTO visits (id, tenant_id, user_id, customer_id, status, schedule_date, created_at) VALUES
                    (gen_random_uuid(), @t1, @ra, @c2, 'Completed', '2026-09-01', now()),
                    (gen_random_uuid(), @t1, @ra, @c3, 'Completed', '2026-09-20', now()),
                    (gen_random_uuid(), @t1, @ra, @c8, 'Planned', '2026-10-06', now()),
                    (gen_random_uuid(), @t1, @ra, @c6, 'Cancelled', '2026-10-06', now());", conn);
            foreach (var (name, value) in new[] { ("t1", t1), ("m", manager), ("sv", supervisor), ("fin", finance), ("ra", repA), ("rb", repB),
                ("erbil", erbil), ("baghdad", baghdad), ("city", city), ("n1", n1), ("n2", n2), ("street", street) })
                cmd.Parameters.AddWithValue(name, value);
            for (var i = 1; i <= 9; i++) cmd.Parameters.AddWithValue("c" + i, c[i]);
            await cmd.ExecuteNonQueryAsync();
        }

        void As(Guid user) => client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            GenerateToken(t1, user, conf["Jwt:SigningKey"]!, conf["Jwt:Issuer"]!, conf["Jwt:Audience"]!));
        Task<HttpResponseMessage> Preview(object body) => client.PostAsJsonAsync("/visit-plan/preview", body);
        Task<HttpResponseMessage> Generate(object body) => client.PostAsJsonAsync("/visit-plan/generate", body);
        async Task<LifeDrop.Application.Visits.PlanPreviewDto> PreviewOk(object body)
        {
            var response = await Preview(body);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<LifeDrop.Application.Visits.PlanPreviewDto>())!;
        }
        static string[] Codes(IEnumerable<LifeDrop.Application.Visits.PlanCandidateDto> list) => list.Select(x => x.Code).ToArray();

        // Who may plan: finance has no visits.write; a rep (visits.plan only) only for themselves (below).
        As(finance);
        await AssertProblemAsync(await Preview(new { AssignedUserId = repA, ScheduleDate = "2026-10-06", PlaceId = n1 }), HttpStatusCode.Forbidden, "FORBIDDEN");
        As(repA);
        await AssertProblemAsync(await Generate(new { AssignedUserId = repB, ScheduleDate = "2026-10-06", PlaceId = n1 }), HttpStatusCode.Forbidden, "FORBIDDEN");

        As(supervisor);
        // Bad requests
        await AssertProblemAsync(await Preview(new { AssignedUserId = repA, ScheduleDate = "2026-10-06" }), HttpStatusCode.BadRequest, "INVALID_DATA");
        await AssertProblemAsync(await Preview(new { AssignedUserId = repA, ScheduleDate = "2026-10-04", PlaceId = n1 }), HttpStatusCode.BadRequest, "INVALID_DATA");
        await AssertProblemAsync(await Preview(new { AssignedUserId = repA, ScheduleDate = "2026-10-06", PlaceId = n1, Count = 0 }), HttpStatusCode.BadRequest, "INVALID_DATA");
        await AssertProblemAsync(await Preview(new { AssignedUserId = repA, ScheduleDate = "2026-10-06", PlaceId = n1, Order = "random" }), HttpStatusCode.BadRequest, "INVALID_DATA");
        await AssertProblemAsync(await Preview(new { AssignedUserId = Guid.NewGuid(), ScheduleDate = "2026-10-06", PlaceId = n1 }), HttpStatusCode.NotFound, "USER_NOT_FOUND");
        // The supervisor of Erbil cannot plan for a rep of Baghdad
        await AssertProblemAsync(await Preview(new { AssignedUserId = repB, ScheduleDate = "2026-10-06", PlaceId = n1 }), HttpStatusCode.Forbidden, "FORBIDDEN");

        // A neighbourhood includes its streets. Order: never visited first (by code), then the oldest last visit. C1 accepts only
        // Wednesdays (this day is Tuesday); C7 pending, C8 planned already, C6 elsewhere, C9 in another region: all left out.
        var all = await PreviewOk(new { AssignedUserId = repA, ScheduleDate = "2026-10-06", PlaceId = n1 });
        Assert.Equal(new[] { "C1", "C4", "C5", "C2", "C3" }, Codes(all.Candidates));
        Assert.Equal(5, all.Matching);
        Assert.Equal(4, all.Included);
        Assert.Equal("outside_hours", all.Candidates[0].SkipReason);
        Assert.Equal("Erbil › N1", all.Candidates[1].Place);
        Assert.Equal("Erbil › N1 › S1", all.Candidates[3].Place);
        Assert.Equal(new DateOnly(2026, 9, 1), all.Candidates[3].LastVisitDate);

        // Only the street
        Assert.Equal(new[] { "C2", "C3" }, Codes((await PreviewOk(new { AssignedUserId = repA, ScheduleDate = "2026-10-06", PlaceId = street })).Candidates));
        // A cancelled visit on that day does not block a customer (C6)
        Assert.Equal(new[] { "C6" }, Codes((await PreviewOk(new { AssignedUserId = repA, ScheduleDate = "2026-10-06", PlaceId = n2 })).Candidates));
        // Limit: the first two that fit; the rest are marked over the limit
        var two = await PreviewOk(new { AssignedUserId = repA, ScheduleDate = "2026-10-06", PlaceId = n1, Count = 2 });
        Assert.Equal(2, two.Included);
        Assert.Equal(new[] { "C4", "C5" }, Codes(two.Candidates.Where(x => x.Included)));
        Assert.Equal("over_limit", two.Candidates.Single(x => x.Code == "C2").SkipReason);
        // Order by name (descending names do not exist: ascending), hours ignored on request
        var byCode = await PreviewOk(new { AssignedUserId = repA, ScheduleDate = "2026-10-06", PlaceId = n1, Order = "code", RespectWindows = false });
        Assert.Equal(new[] { "C1", "C2", "C3", "C4", "C5" }, Codes(byCode.Candidates));
        Assert.Equal(5, byCode.Included);
        // On Wednesday C1 is within its hours
        Assert.Equal("within", (await PreviewOk(new { AssignedUserId = repA, ScheduleDate = "2026-10-07", PlaceId = n1 })).Candidates.Single(x => x.Code == "C1").WindowStatus);
        // The supervisor's scope: Erbil customers only, so a region filter for Baghdad finds nothing; the manager sees C9
        Assert.Empty((await PreviewOk(new { AssignedUserId = repA, ScheduleDate = "2026-10-06", RegionId = baghdad })).Candidates);
        As(manager);
        Assert.Equal(new[] { "C9" }, Codes((await PreviewOk(new { AssignedUserId = repB, ScheduleDate = "2026-10-06", RegionId = baghdad })).Candidates));

        // Create the first two for rep A: after the visit already in the plan (C8), type by customer type, one audit row per visit
        As(supervisor);
        var created = await Generate(new { AssignedUserId = repA, ScheduleDate = "2026-10-06", PlaceId = n1, Count = 2, Priority = "high" });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var result = (await created.Content.ReadFromJsonAsync<LifeDrop.Application.Visits.PlanGeneratedDto>())!;
        Assert.Equal(2, result.Created);
        As(repA);
        var plan = (await (await client.GetAsync("/visits?scheduleDate=2026-10-06")).Content.ReadFromJsonAsync<List<LifeDrop.Application.Visits.VisitDto>>())!;
        Assert.Equal(new[] { c[8], c[4], c[5], c[6] }.Take(3).ToArray(), plan.Where(v => v.Status == "Planned").Select(v => v.CustomerId).ToArray());
        Assert.Equal(new[] { "normal", "high", "high" }, plan.Where(v => v.Status == "Planned").Select(v => v.Priority).ToArray());
        Assert.Equal(new[] { "pharmacy", "doctor" }, plan.Where(v => v.CustomerId == c[4] || v.CustomerId == c[5]).Select(v => v.VisitType).ToArray());
        await using (var conn = new NpgsqlConnection(schemaBlock.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(@"
                SELECT set_config('app.current_tenant', @t1::text, false);
                SELECT (SELECT count(*) FROM audit_logs WHERE action = 'visit.created' AND actor_id = @sv),
                       (SELECT count(*) FROM audit_logs WHERE action = 'visit.plan_generated' AND entity_id = @ra::text AND actor_id = @sv);", conn);
            cmd.Parameters.AddWithValue("t1", t1);
            cmd.Parameters.AddWithValue("sv", supervisor);
            cmd.Parameters.AddWithValue("ra", repA);
            await using var reader = await cmd.ExecuteReaderAsync();
            await reader.NextResultAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(2L, reader.GetInt64(0));
            Assert.Equal(1L, reader.GetInt64(1));
        }

        // Again: the two planned ones are no longer candidates. Chosen customers only (C6 is outside the place and is skipped)
        As(supervisor);
        var rest = await PreviewOk(new { AssignedUserId = repA, ScheduleDate = "2026-10-06", PlaceId = n1 });
        Assert.Equal(new[] { "C1", "C2", "C3" }, Codes(rest.Candidates));
        var chosen = await Generate(new { AssignedUserId = repA, ScheduleDate = "2026-10-06", PlaceId = n1, CustomerIds = new[] { c[2], c[6] } });
        var chosenResult = (await chosen.Content.ReadFromJsonAsync<LifeDrop.Application.Visits.PlanGeneratedDto>())!;
        Assert.Equal(1, chosenResult.Created);
        Assert.Equal(1, chosenResult.Skipped);

        // A rep plans for themselves (plan step 1.25): a search text is enough, only customers of their region, never for somebody else
        As(repA);
        Assert.Equal(new[] { "C6" }, Codes((await PreviewOk(new { AssignedUserId = repA, ScheduleDate = "2026-10-06", Query = "name 6" })).Candidates));
        Assert.Empty((await PreviewOk(new { AssignedUserId = repA, ScheduleDate = "2026-10-06", Query = "Name 9" })).Candidates);   // other region
        Assert.Empty((await PreviewOk(new { AssignedUserId = repA, ScheduleDate = "2026-10-06", Query = "%" })).Candidates);       // no wildcard
        await AssertProblemAsync(await Preview(new { AssignedUserId = repB, ScheduleDate = "2026-10-06", Query = "Name" }), HttpStatusCode.Forbidden, "FORBIDDEN");
        await AssertProblemAsync(await Preview(new { AssignedUserId = repA, ScheduleDate = "2026-10-06" }), HttpStatusCode.BadRequest, "INVALID_DATA");
        var own = await Generate(new { AssignedUserId = repA, ScheduleDate = "2026-10-06", Query = "name 6", CustomerIds = new[] { c[6] } });
        Assert.Equal(1, (await own.Content.ReadFromJsonAsync<LifeDrop.Application.Visits.PlanGeneratedDto>())!.Created);
    }

    [SkippableFact]
    public async Task CustomerPriority_ManagerSetsByIdsOrPlaceWithinScopeAndPlansShowAndUseIt()
    {
        var url = GetRequiredEnvVar("TEST_DATABASE_URL");
        await VerifyRoleAsync(url, expectElevated: false);

        var (schemaBlock, factory) = await StartFlexiblePlanHostAsync(url);
        await using var _ = schemaBlock;
        await using var __ = factory;
        var conf = factory.Services.GetRequiredService<IConfiguration>();
        var client = factory.CreateClient();

        var t1 = Guid.NewGuid();
        Guid manager = Guid.NewGuid(), supervisor = Guid.NewGuid(), finance = Guid.NewGuid(), repA = Guid.NewGuid();
        Guid erbil = Guid.NewGuid(), baghdad = Guid.NewGuid(), city = Guid.NewGuid(), n1 = Guid.NewGuid(), street = Guid.NewGuid();
        var c = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToArray();   // C1..C4
        await using (var conn = new NpgsqlConnection(schemaBlock.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(@"
                INSERT INTO tenants (id, name, code) VALUES (@t1, 'T1', 'T1');
                SELECT seed_default_roles(@t1);
                INSERT INTO users (id, tenant_id) VALUES (@m, @t1), (@sv, @t1), (@fin, @t1), (@ra, @t1);
                INSERT INTO user_roles (tenant_id, user_id, role_id)
                SELECT @t1, @m, id FROM roles WHERE tenant_id = @t1 AND name = 'SalesManager'
                UNION ALL SELECT @t1, @sv, id FROM roles WHERE tenant_id = @t1 AND name = 'Supervisor'
                UNION ALL SELECT @t1, @fin, id FROM roles WHERE tenant_id = @t1 AND name = 'FinanceManager'
                UNION ALL SELECT @t1, @ra, id FROM roles WHERE tenant_id = @t1 AND name = 'SalesRep';
                SELECT set_config('app.current_tenant', @t1::text, false);
                INSERT INTO regions (id, tenant_id, name) VALUES (@erbil, @t1, 'Erbil'), (@baghdad, @t1, 'Baghdad');
                INSERT INTO teams (id, tenant_id, region_id, name) VALUES ('55555555-0000-0000-0000-0000000000a1', @t1, @erbil, 'Team A');
                INSERT INTO team_members (tenant_id, user_id, team_id) VALUES
                    (@t1, @sv, '55555555-0000-0000-0000-0000000000a1'), (@t1, @ra, '55555555-0000-0000-0000-0000000000a1');
                INSERT INTO places (id, tenant_id, parent_id, kind, name) VALUES
                    (@city, @t1, NULL, 'city', 'Erbil'), (@n1, @t1, @city, 'neighbourhood', 'N1'), (@street, @t1, @n1, 'street', 'S1');
                -- C1, C2 in the street; C3 in the neighbourhood; C4 in the same neighbourhood but in another region
                INSERT INTO customers (id, tenant_id, type, code, name, place_id, region_id) VALUES
                    (@c1, @t1, 'Pharmacy', 'C1', 'Name 1', @street, @erbil), (@c2, @t1, 'Pharmacy', 'C2', 'Name 2', @street, @erbil),
                    (@c3, @t1, 'Pharmacy', 'C3', 'Name 3', @n1, @erbil), (@c4, @t1, 'Pharmacy', 'C4', 'Name 4', @n1, @baghdad);", conn);
            foreach (var (name, value) in new[] { ("t1", t1), ("m", manager), ("sv", supervisor), ("fin", finance), ("ra", repA), ("erbil", erbil), ("baghdad", baghdad),
                ("city", city), ("n1", n1), ("street", street) })
                cmd.Parameters.AddWithValue(name, value);
            for (var i = 1; i <= 4; i++) cmd.Parameters.AddWithValue("c" + i, c[i]);
            await cmd.ExecuteNonQueryAsync();
        }

        void As(Guid user) => client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            GenerateToken(t1, user, conf["Jwt:SigningKey"]!, conf["Jwt:Issuer"]!, conf["Jwt:Audience"]!));
        Task<HttpResponseMessage> SetPriority(object body) => client.PutAsJsonAsync("/customers/priority", body);
        async Task<int> Updated(object body)
        {
            var response = await SetPriority(body);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<LifeDrop.Application.Customers.CustomerPriorityDto>())!.Updated;
        }
        async Task<string> PriorityOf(Guid customer) =>
            (await client.GetFromJsonAsync<LifeDrop.Application.Customers.CustomerDto>($"/customers/{customer}"))!.Priority;

        // Who may: reps and finance have no visits.write
        As(repA);
        await AssertProblemAsync(await SetPriority(new { Priority = "high", CustomerIds = new[] { c[1] } }), HttpStatusCode.Forbidden, "FORBIDDEN");
        As(finance);
        await AssertProblemAsync(await SetPriority(new { Priority = "high", CustomerIds = new[] { c[1] } }), HttpStatusCode.Forbidden, "FORBIDDEN");

        As(supervisor);
        // Bad requests: both or none of ids and place, unknown or missing priority, too many ids
        await AssertProblemAsync(await SetPriority(new { Priority = "high", CustomerIds = new[] { c[1] }, PlaceId = n1 }), HttpStatusCode.BadRequest, "INVALID_DATA");
        await AssertProblemAsync(await SetPriority(new { Priority = "high" }), HttpStatusCode.BadRequest, "INVALID_DATA");
        await AssertProblemAsync(await SetPriority(new { Priority = "urgent", PlaceId = n1 }), HttpStatusCode.BadRequest, "INVALID_DATA");
        await AssertProblemAsync(await SetPriority(new { PlaceId = n1 }), HttpStatusCode.BadRequest, "INVALID_DATA");
        await AssertProblemAsync(await SetPriority(new { Priority = "high", CustomerIds = Enumerable.Range(0, 201).Select(_ => Guid.NewGuid()).ToArray() }), HttpStatusCode.BadRequest, "INVALID_DATA");

        // By ids; the same again changes nothing; the customer shows it
        Assert.Equal(1, await Updated(new { Priority = "high", CustomerIds = new[] { c[1] } }));
        Assert.Equal(0, await Updated(new { Priority = "high", CustomerIds = new[] { c[1] } }));
        Assert.Equal("high", await PriorityOf(c[1]));
        Assert.Equal("normal", await PriorityOf(c[2]));

        // By place: everything below it, but only inside the supervisor's region (C4 is in another region)
        Assert.Equal(3, await Updated(new { Priority = "low", PlaceId = n1 }));
        Assert.All(new[] { c[1], c[2], c[3] }, id => Assert.Equal("low", PriorityOf(id).Result));
        // A customer outside the scope is not changed even when named
        Assert.Equal(0, await Updated(new { Priority = "high", CustomerIds = new[] { c[4] } }));
        As(manager);
        Assert.Equal("normal", await PriorityOf(c[4]));
        Assert.Equal(4, await Updated(new { Priority = "high", PlaceId = n1 }));
        Assert.Equal(4, await Updated(new { Priority = "normal", PlaceId = n1 }));
        Assert.Equal(1, await Updated(new { Priority = "high", CustomerIds = new[] { c[3] } }));
        As(supervisor);
        Assert.Equal(1, await Updated(new { Priority = "low", CustomerIds = new[] { c[1] } }));

        await using (var conn = new NpgsqlConnection(schemaBlock.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand("SELECT set_config('app.current_tenant', @t::text, false); SELECT count(*) FROM audit_logs WHERE action = 'customer.priority_set'", conn);
            cmd.Parameters.AddWithValue("t", t1);
            await using var reader = await cmd.ExecuteReaderAsync();
            await reader.NextResultAsync();
            await reader.ReadAsync();
            Assert.Equal(1L + 3 + 4 + 4 + 1 + 1, reader.GetInt64(0));   // one row per changed customer
        }

        // Suggestions: high first, then normal, then low, whatever the order inside a level (by name: C1, C2, C3)
        var preview = await client.PostAsJsonAsync("/visit-plan/preview", new { AssignedUserId = repA, ScheduleDate = "2026-10-06", PlaceId = n1, Order = "name" });
        var found = (await preview.Content.ReadFromJsonAsync<LifeDrop.Application.Visits.PlanPreviewDto>())!;
        Assert.Equal(new[] { "C3", "C2", "C1" }, found.Candidates.Select(x => x.Code).ToArray());
        Assert.Equal(new[] { "high", "normal", "low" }, found.Candidates.Select(x => x.CustomerPriority).ToArray());
        // With a limit the high one is taken first
        var one = await client.PostAsJsonAsync("/visit-plan/generate", new { AssignedUserId = repA, ScheduleDate = "2026-10-06", PlaceId = n1, Order = "name", Count = 1 });
        Assert.Equal(1, (await one.Content.ReadFromJsonAsync<LifeDrop.Application.Visits.PlanGeneratedDto>())!.Created);

        // The rep sees the manager's priority in the plan (not the priority of the visit, which stays normal)
        As(repA);
        var plan = (await (await client.GetAsync("/visits?scheduleDate=2026-10-06")).Content.ReadFromJsonAsync<List<LifeDrop.Application.Visits.VisitDto>>())!;
        var visit = Assert.Single(plan);
        Assert.Equal((c[3], "high", "normal"), (visit.CustomerId, visit.CustomerPriority, visit.Priority));
    }

    [SkippableFact]
    public async Task PlanFromArea_DrawnAreaFindsLocatedApprovedCustomersInScopeAndRefusesBadShapes()
    {
        var url = GetRequiredEnvVar("TEST_DATABASE_URL");
        await VerifyRoleAsync(url, expectElevated: false);

        var (schemaBlock, factory) = await StartFlexiblePlanHostAsync(url);
        await using var _ = schemaBlock;
        await using var __ = factory;
        var conf = factory.Services.GetRequiredService<IConfiguration>();
        var client = factory.CreateClient();

        var t1 = Guid.NewGuid();
        Guid supervisor = Guid.NewGuid(), repA = Guid.NewGuid(), erbil = Guid.NewGuid(), baghdad = Guid.NewGuid();
        var c = Enumerable.Range(0, 8).Select(_ => Guid.NewGuid()).ToArray();   // C1..C7
        await using (var conn = new NpgsqlConnection(schemaBlock.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(@"
                INSERT INTO tenants (id, name, code) VALUES (@t1, 'T1', 'T1');
                SELECT seed_default_roles(@t1);
                INSERT INTO users (id, tenant_id) VALUES (@sv, @t1), (@ra, @t1);
                INSERT INTO user_roles (tenant_id, user_id, role_id)
                SELECT @t1, @sv, id FROM roles WHERE tenant_id = @t1 AND name = 'Supervisor'
                UNION ALL SELECT @t1, @ra, id FROM roles WHERE tenant_id = @t1 AND name = 'SalesRep';
                SELECT set_config('app.current_tenant', @t1::text, false);
                INSERT INTO regions (id, tenant_id, name) VALUES (@erbil, @t1, 'Erbil'), (@baghdad, @t1, 'Baghdad');
                INSERT INTO teams (id, tenant_id, region_id, name) VALUES ('55555555-0000-0000-0000-0000000000a1', @t1, @erbil, 'Team A');
                INSERT INTO team_members (tenant_id, user_id, team_id) VALUES
                    (@t1, @sv, '55555555-0000-0000-0000-0000000000a1'), (@t1, @ra, '55555555-0000-0000-0000-0000000000a1');
                -- The area is the square lon 44.00-44.02, lat 36.18-36.20.
                -- C1, C2 inside; C3 inside but pending; C4 outside; C5 without a position; C6 inside, other region; C7 on the border
                INSERT INTO customers (id, tenant_id, type, code, name, region_id, approval_status, geom, status) VALUES
                    (@c1, @t1, 'Pharmacy', 'C1', 'Name 1', @erbil, 'approved', ST_SetSRID(ST_MakePoint(44.010, 36.190), 4326)::geography, 'located'),
                    (@c2, @t1, 'Pharmacy', 'C2', 'Name 2', @erbil, 'approved', ST_SetSRID(ST_MakePoint(44.005, 36.185), 4326)::geography, 'located'),
                    (@c3, @t1, 'Pharmacy', 'C3', 'Name 3', @erbil, 'pending', ST_SetSRID(ST_MakePoint(44.015, 36.195), 4326)::geography, 'located'),
                    (@c4, @t1, 'Pharmacy', 'C4', 'Name 4', @erbil, 'approved', ST_SetSRID(ST_MakePoint(44.050, 36.190), 4326)::geography, 'located'),
                    (@c5, @t1, 'Pharmacy', 'C5', 'Name 5', @erbil, 'approved', NULL, 'no_location'),
                    (@c6, @t1, 'Pharmacy', 'C6', 'Name 6', @baghdad, 'approved', ST_SetSRID(ST_MakePoint(44.012, 36.192), 4326)::geography, 'located'),
                    (@c7, @t1, 'Pharmacy', 'C7', 'Name 7', @erbil, 'approved', ST_SetSRID(ST_MakePoint(44.020, 36.190), 4326)::geography, 'located');", conn);
            foreach (var (name, value) in new[] { ("t1", t1), ("sv", supervisor), ("ra", repA), ("erbil", erbil), ("baghdad", baghdad) })
                cmd.Parameters.AddWithValue(name, value);
            for (var i = 1; i <= 7; i++) cmd.Parameters.AddWithValue("c" + i, c[i]);
            await cmd.ExecuteNonQueryAsync();
        }

        void As(Guid user) => client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            GenerateToken(t1, user, conf["Jwt:SigningKey"]!, conf["Jwt:Issuer"]!, conf["Jwt:Audience"]!));
        static object[] Square(double west = 44.00, double east = 44.02, double south = 36.18, double north = 36.20) => new object[]
        {
            new { Latitude = south, Longitude = west }, new { Latitude = south, Longitude = east },
            new { Latitude = north, Longitude = east }, new { Latitude = north, Longitude = west }
        };
        Task<HttpResponseMessage> Preview(object area, object? assignee = null) => client.PostAsJsonAsync("/visit-plan/preview",
            new { AssignedUserId = assignee, ScheduleDate = "2026-10-06", Area = area });
        async Task<string[]> Codes(HttpResponseMessage response)
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<LifeDrop.Application.Visits.PlanPreviewDto>())!.Candidates.Select(x => x.Code).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        }

        // The supervisor of Erbil: located and approved customers inside the area and in their region (the border counts as inside)
        As(supervisor);
        Assert.Equal(new[] { "C1", "C2", "C7" }, await Codes(await Preview(Square(), repA)));
        // A smaller area
        Assert.Equal(new[] { "C2" }, await Codes(await Preview(Square(east: 44.008), repA)));
        // The drawing may start at any corner and go either way round
        Assert.Equal(new[] { "C1", "C2", "C7" }, await Codes(await Preview(Square().Reverse().ToArray(), repA)));

        // A rep plans for themselves from the area (no assignee) and the customers are added
        As(repA);
        Assert.Equal(new[] { "C1", "C2", "C7" }, await Codes(await Preview(Square())));
        var created = await client.PostAsJsonAsync("/visit-plan/generate", new { ScheduleDate = "2026-10-06", Area = Square(), CustomerIds = new[] { c[1], c[2] } });
        Assert.Equal(2, (await created.Content.ReadFromJsonAsync<LifeDrop.Application.Visits.PlanGeneratedDto>())!.Created);
        Assert.Equal(new[] { "C7" }, await Codes(await Preview(Square())));   // the planned ones are not offered again

        // Bad shapes: two points, a line, a spot, a coordinate that does not exist, over 100 points, a box wider than 0.5 degrees
        await AssertProblemAsync(await Preview(Square().Take(2).ToArray()), HttpStatusCode.BadRequest, "INVALID_DATA");
        await AssertProblemAsync(await Preview(new object[] { new { Latitude = 36.19, Longitude = 44.00 }, new { Latitude = 36.19, Longitude = 44.01 }, new { Latitude = 36.19, Longitude = 44.02 } }), HttpStatusCode.BadRequest, "INVALID_DATA");
        await AssertProblemAsync(await Preview(new object[] { new { Latitude = 36.19, Longitude = 44.0 }, new { Latitude = 36.19, Longitude = 44.0 }, new { Latitude = 36.19, Longitude = 44.0 } }), HttpStatusCode.BadRequest, "INVALID_DATA");
        await AssertProblemAsync(await Preview(Square(south: 99, north: 100)), HttpStatusCode.BadRequest, "INVALID_DATA");
        await AssertProblemAsync(await Preview(Enumerable.Range(0, 101).Select(i => (object)new { Latitude = 36.18 + (i % 2) * 0.01, Longitude = 44.0 + i * 0.0001 }).ToArray()), HttpStatusCode.BadRequest, "INVALID_DATA");
        await AssertProblemAsync(await Preview(Square(west: 43.0, east: 44.02)), HttpStatusCode.BadRequest, "INVALID_DATA");
    }

    [SkippableFact]
    public async Task Catalog_ManagerMaintainsProductsCityPricesAndDiscountsAndEveryoneGetsTheServerPrice()
    {
        var url = GetRequiredEnvVar("TEST_DATABASE_URL");
        await VerifyRoleAsync(url, expectElevated: false);

        var (schemaBlock, factory) = await StartFlexiblePlanHostAsync(url);
        await using var _ = schemaBlock;
        await using var __ = factory;
        var conf = factory.Services.GetRequiredService<IConfiguration>();
        var client = factory.CreateClient();

        var t1 = Guid.NewGuid();
        Guid manager = Guid.NewGuid(), supervisor = Guid.NewGuid(), finance = Guid.NewGuid(), repA = Guid.NewGuid(), driver = Guid.NewGuid();
        Guid erbilRegion = Guid.NewGuid(), baghdadRegion = Guid.NewGuid(), erbil = Guid.NewGuid(), n1 = Guid.NewGuid(), suli = Guid.NewGuid();
        Guid c1 = Guid.NewGuid(), c2 = Guid.NewGuid(), c3 = Guid.NewGuid();
        await using (var conn = new NpgsqlConnection(schemaBlock.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(@"
                INSERT INTO tenants (id, name, code) VALUES (@t1, 'T1', 'T1');
                SELECT seed_default_roles(@t1);
                INSERT INTO users (id, tenant_id) VALUES (@m, @t1), (@sv, @t1), (@fin, @t1), (@ra, @t1), (@dr, @t1);
                INSERT INTO user_roles (tenant_id, user_id, role_id)
                SELECT @t1, @m, id FROM roles WHERE tenant_id = @t1 AND name = 'SalesManager'
                UNION ALL SELECT @t1, @sv, id FROM roles WHERE tenant_id = @t1 AND name = 'Supervisor'
                UNION ALL SELECT @t1, @fin, id FROM roles WHERE tenant_id = @t1 AND name = 'FinanceManager'
                UNION ALL SELECT @t1, @ra, id FROM roles WHERE tenant_id = @t1 AND name = 'SalesRep'
                UNION ALL SELECT @t1, @dr, id FROM roles WHERE tenant_id = @t1 AND name = 'Driver';
                SELECT set_config('app.current_tenant', @t1::text, false);
                INSERT INTO regions (id, tenant_id, name) VALUES (@er, @t1, 'Erbil team region'), (@br, @t1, 'Baghdad team region');
                INSERT INTO teams (id, tenant_id, region_id, name) VALUES ('55555555-0000-0000-0000-0000000000a1', @t1, @er, 'Team A');
                INSERT INTO team_members (tenant_id, user_id, team_id) VALUES
                    (@t1, @sv, '55555555-0000-0000-0000-0000000000a1'), (@t1, @ra, '55555555-0000-0000-0000-0000000000a1');
                INSERT INTO places (id, tenant_id, parent_id, kind, name) VALUES
                    (@erbil, @t1, NULL, 'city', 'Erbil'), (@n1, @t1, @erbil, 'neighbourhood', 'N1'), (@suli, @t1, NULL, 'city', 'Sulaymaniyah');
                -- C1 lives in a neighbourhood of Erbil, C2 in Sulaymaniyah, C3 has no place
                INSERT INTO customers (id, tenant_id, type, code, name, region_id, place_id) VALUES
                    (@c1, @t1, 'Pharmacy', 'C1', 'Customer 1', @er, @n1), (@c2, @t1, 'Pharmacy', 'C2', 'Customer 2', @br, @suli),
                    (@c3, @t1, 'Pharmacy', 'C3', 'Customer 3', @br, NULL);", conn);
            foreach (var (name, value) in new[] { ("t1", t1), ("m", manager), ("sv", supervisor), ("fin", finance), ("ra", repA), ("dr", driver), ("er", erbilRegion), ("br", baghdadRegion),
                ("erbil", erbil), ("n1", n1), ("suli", suli), ("c1", c1), ("c2", c2), ("c3", c3) })
                cmd.Parameters.AddWithValue(name, value);
            await cmd.ExecuteNonQueryAsync();
        }

        void As(Guid user) => client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            GenerateToken(t1, user, conf["Jwt:SigningKey"]!, conf["Jwt:Issuer"]!, conf["Jwt:Audience"]!));
        object Product(string code = "PAR-500", string name = "Paracetamol 500", string currency = "IQD", decimal price = 1500m, string? status = null, string unit = "box") =>
            new { Code = code, Name = name, NameCkb = "پاراسیتامۆل", Unit = unit, Currency = currency, BasePrice = price, Status = status };
        async Task<Guid> CreateProduct(object body)
        {
            var response = await client.PostAsJsonAsync("/products", body);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<CreatedResponseDto>())!.Id;
        }
        async Task<List<LifeDrop.Application.Catalog.ProductDto>> Products(string query = "") =>
            (await client.GetFromJsonAsync<List<LifeDrop.Application.Catalog.ProductDto>>("/products" + query))!;
        Task<HttpResponseMessage> AddCityPrice(Guid product, object body) => client.PostAsJsonAsync($"/products/{product}/city-prices", body);
        Task<HttpResponseMessage> AddDiscount(Guid customer, object body) => client.PostAsJsonAsync($"/customers/{customer}/discounts", body);
        async Task<List<LifeDrop.Application.Catalog.EffectivePriceDto>> Prices(Guid customer, string date) =>
            (await client.GetFromJsonAsync<List<LifeDrop.Application.Catalog.EffectivePriceDto>>($"/customers/{customer}/prices?date={date}"))!;
        async Task<(decimal List, string Source, decimal Price)> PriceOf(Guid customer, string date, string code = "PAR-500")
        {
            var row = (await Prices(customer, date)).Single(p => p.Code == code);
            return (row.ListPrice, row.ListSource, row.Price);
        }

        // Who may: nobody without catalog.read; reading roles cannot change anything
        As(driver);
        await AssertProblemAsync(await client.GetAsync("/products"), HttpStatusCode.Forbidden, "FORBIDDEN");
        foreach (var reader in new[] { supervisor, finance, repA })
        {
            As(reader);
            Assert.Empty(await Products());
            await AssertProblemAsync(await client.PostAsJsonAsync("/products", Product()), HttpStatusCode.Forbidden, "FORBIDDEN");
        }

        As(manager);
        // Bad data: missing name, bad currency, negative price, more than three decimals, bad code, no unit
        foreach (var bad in new object[]
        {
            new { Code = "A1", Name = "", Unit = "box", Currency = "IQD", BasePrice = 1m },
            Product(currency: "EUR"), Product(price: -1m), Product(price: 1.2345m), Product(code: "has space"), Product(code: "  "), Product(unit: " ")
        })
            await AssertProblemAsync(await client.PostAsJsonAsync("/products", bad), HttpStatusCode.BadRequest, "INVALID_DATA");

        var p1 = await CreateProduct(Product());
        var p2 = await CreateProduct(Product(code: "AMX-250", name: "Amoxicillin 250", currency: "USD", price: 3.5m));
        var p3 = await CreateProduct(Product(code: "ZZZ-10", name: "Zinc 10", currency: "USD", price: 10m));
        await AssertProblemAsync(await client.PostAsJsonAsync("/products", Product(name: "Same code again")), HttpStatusCode.Conflict, "CONFLICT");
        Assert.Equal(new[] { "AMX-250", "PAR-500", "ZZZ-10" }, (await Products()).Select(p => p.Code).ToArray());
        Assert.Equal("پاراسیتامۆل", (await Products("?q=par")).Single().NameCkb);
        Assert.Empty(await Products("?q=%25"));   // a % is a plain character

        // Edit: the code never changes; an inactive product leaves the lists and the prices (managers can still list it)
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync($"/products/{p2}", Product(code: "IGNORED", name: "Amoxicillin 250 mg", currency: "USD", price: 4m, status: "inactive"))).StatusCode);
        await AssertProblemAsync(await client.PutAsJsonAsync($"/products/{Guid.NewGuid()}", Product()), HttpStatusCode.NotFound, "NOT_FOUND");
        await AssertProblemAsync(await client.PutAsJsonAsync($"/products/{p1}", Product(price: -5m)), HttpStatusCode.BadRequest, "INVALID_DATA");
        Assert.Equal(new[] { "PAR-500", "ZZZ-10" }, (await Products()).Select(p => p.Code).ToArray());
        var all = await Products("?includeInactive=true");
        Assert.Equal(("AMX-250", "Amoxicillin 250 mg", 4m, "inactive"), (all[0].Code, all[0].Name, all[0].BasePrice, all[0].Status));
        As(repA);
        Assert.Equal(2, (await Products("?includeInactive=true")).Count);   // only managers see inactive ones

        // Without city prices everybody pays the general price
        As(manager);
        Assert.Equal((1500m, "base", 1500m), await PriceOf(c1, "2026-10-06"));

        // City prices: Erbil from 1 Oct (open), Sulaymaniyah only until the end of the year
        Assert.Equal(HttpStatusCode.Created, (await AddCityPrice(p1, new { CityId = erbil, Price = 1800m, ValidFrom = "2026-10-01" })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await AddCityPrice(p1, new { CityId = suli, Price = 1600m, ValidFrom = "2026-10-01", ValidTo = "2026-12-31" })).StatusCode);
        Assert.Equal((1800m, "city", 1800m), await PriceOf(c1, "2026-10-06"));      // the city is found from the neighbourhood
        Assert.Equal((1600m, "city", 1600m), await PriceOf(c2, "2026-10-06"));
        Assert.Equal((1500m, "base", 1500m), await PriceOf(c3, "2026-10-06"));      // no place: the general price
        Assert.Equal((1600m, "city", 1600m), await PriceOf(c2, "2026-12-31"));      // last day counts
        Assert.Equal((1500m, "base", 1500m), await PriceOf(c2, "2027-01-01"));      // the city price has ended
        Assert.Equal((1500m, "base", 1500m), await PriceOf(c1, "2026-09-30"));      // before it starts
        Assert.Equal((10m, "base", 10m), await PriceOf(c1, "2026-10-06", "ZZZ-10")); // other products are not touched

        // Overlap, bad prices, and what is not a city
        await AssertProblemAsync(await AddCityPrice(p1, new { CityId = suli, Price = 1000m, ValidFrom = "2026-12-31", ValidTo = "2027-02-01" }), HttpStatusCode.Conflict, "CONFLICT");
        await AssertProblemAsync(await AddCityPrice(p1, new { CityId = erbil, Price = 1000m, ValidFrom = "2027-03-01" }), HttpStatusCode.Conflict, "CONFLICT");   // the open price covers it
        Assert.Equal(HttpStatusCode.Created, (await AddCityPrice(p1, new { CityId = suli, Price = 1650m, ValidFrom = "2027-01-01", ValidTo = "2027-03-31" })).StatusCode);
        Assert.Equal((1650m, "city", 1650m), await PriceOf(c2, "2027-01-15"));
        await AssertProblemAsync(await AddCityPrice(p1, new { CityId = n1, Price = 1m, ValidFrom = "2026-12-01" }), HttpStatusCode.BadRequest, "INVALID_DATA");   // a neighbourhood is not a city
        await AssertProblemAsync(await AddCityPrice(p1, new { Price = 1m, ValidFrom = "2026-12-01" }), HttpStatusCode.BadRequest, "INVALID_DATA");
        await AssertProblemAsync(await AddCityPrice(p1, new { CityId = suli, Price = -1m, ValidFrom = "2027-06-01" }), HttpStatusCode.BadRequest, "INVALID_DATA");
        await AssertProblemAsync(await AddCityPrice(p1, new { CityId = suli, Price = 1m, ValidFrom = "2027-06-05", ValidTo = "2027-06-01" }), HttpStatusCode.BadRequest, "INVALID_DATA");
        await AssertProblemAsync(await AddCityPrice(p1, new { CityId = Guid.NewGuid(), Price = 1m, ValidFrom = "2027-06-01" }), HttpStatusCode.NotFound, "NOT_FOUND");
        await AssertProblemAsync(await AddCityPrice(Guid.NewGuid(), new { CityId = suli, Price = 1m, ValidFrom = "2027-06-01" }), HttpStatusCode.NotFound, "NOT_FOUND");

        // Ending a city price (never deleting): not before its start, not later than the end it has
        var prices = (await client.GetFromJsonAsync<List<LifeDrop.Application.Catalog.CityPriceDto>>($"/products/{p1}/city-prices"))!;
        Assert.Equal(3, prices.Count);
        var open = prices.Single(r => r.CityName == "Erbil");
        await AssertProblemAsync(await client.PostAsJsonAsync($"/city-prices/{open.Id}/end", new { ValidTo = "2026-09-30" }), HttpStatusCode.BadRequest, "INVALID_DATA");
        await AssertProblemAsync(await client.PostAsJsonAsync($"/city-prices/{open.Id}/end", new { }), HttpStatusCode.BadRequest, "INVALID_DATA");
        await AssertProblemAsync(await client.PostAsJsonAsync($"/city-prices/{Guid.NewGuid()}/end", new { ValidTo = "2026-12-31" }), HttpStatusCode.NotFound, "NOT_FOUND");
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync($"/city-prices/{open.Id}/end", new { ValidTo = "2026-12-31" })).StatusCode);
        await AssertProblemAsync(await client.PostAsJsonAsync($"/city-prices/{open.Id}/end", new { ValidTo = "2027-01-31" }), HttpStatusCode.BadRequest, "INVALID_DATA");
        Assert.Equal((1500m, "base", 1500m), await PriceOf(c1, "2027-01-15"));
        Assert.Equal((1800m, "city", 1800m), await PriceOf(c1, "2026-12-31"));
        // The city can get a new price after the old one ended (the manager keeps it or changes it, month by month)
        Assert.Equal(HttpStatusCode.Created, (await AddCityPrice(p1, new { CityId = erbil, Price = 1750m, ValidFrom = "2027-01-01" })).StatusCode);
        Assert.Equal((1750m, "city", 1750m), await PriceOf(c1, "2027-01-15"));

        // Discounts of C1, oldest first on the price left: 10 % on everything, then 100 off PAR-500, then 5 % on PAR-500
        Assert.Equal(HttpStatusCode.Created, (await AddDiscount(c1, new { Kind = "percent", Value = 10m, ValidFrom = "2026-10-01" })).StatusCode);
        Assert.Equal((1800m, "city", 1620m), await PriceOf(c1, "2026-10-06"));
        Assert.Equal((10m, "base", 9m), await PriceOf(c1, "2026-10-06", "ZZZ-10"));
        Assert.Equal(HttpStatusCode.Created, (await AddDiscount(c1, new { ProductId = p1, Kind = "amount", Value = 100m, ValidFrom = "2026-10-01" })).StatusCode);
        var third = await AddDiscount(c1, new { ProductId = p1, Kind = "percent", Value = 5m, ValidFrom = "2026-10-01", ValidTo = "2026-10-20" });
        Assert.Equal(HttpStatusCode.Created, third.StatusCode);
        var row = (await Prices(c1, "2026-10-06")).Single(p => p.Code == "PAR-500");
        Assert.Equal(1444m, row.Price);   // 1800 -10 % = 1620, -100 = 1520, -5 % = 1444
        Assert.Equal(new[] { "percent", "amount", "percent" }, row.Discounts.Select(d => d.Kind).ToArray());
        Assert.Equal(9m, (await Prices(c1, "2026-10-06")).Single(p => p.Code == "ZZZ-10").Price);   // the amount belongs to PAR-500 only
        Assert.Equal(1520m, (await PriceOf(c1, "2026-10-21")).Price);                                // the third discount has ended by then
        Assert.Equal(1500m, (await PriceOf(c1, "2026-09-30")).Price);                                // before any discount

        // Ending a discount; discounts of another customer are separate; a price never goes below 0
        var discounts = (await client.GetFromJsonAsync<List<LifeDrop.Application.Catalog.CustomerDiscountDto>>($"/customers/{c1}/discounts"))!;
        Assert.Equal(3, discounts.Count);
        Assert.Equal("PAR-500", discounts.First(d => d.Kind == "amount").ProductCode);
        var tenPercent = discounts.Single(d => d.ProductId is null);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync($"/discounts/{tenPercent.Id}/end", new { ValidTo = "2026-10-31" })).StatusCode);
        await AssertProblemAsync(await client.PostAsJsonAsync($"/discounts/{tenPercent.Id}/end", new { ValidTo = "2026-11-30" }), HttpStatusCode.BadRequest, "INVALID_DATA");
        await AssertProblemAsync(await client.PostAsJsonAsync($"/discounts/{Guid.NewGuid()}/end", new { ValidTo = "2026-11-30" }), HttpStatusCode.NotFound, "NOT_FOUND");
        Assert.Equal(1700m, (await PriceOf(c1, "2026-11-05")).Price);                                // only the 100 off is left
        Assert.Equal((1500m, "base", 1500m), await PriceOf(c3, "2026-10-06"));
        Assert.Equal(HttpStatusCode.Created, (await AddDiscount(c3, new { ProductId = p1, Kind = "amount", Value = 5000m, ValidFrom = "2026-10-01" })).StatusCode);
        Assert.Equal((1500m, "base", 0m), await PriceOf(c3, "2026-10-06"));

        // Bad discounts: an amount without a product, over 100 %, zero, unknown kind, more than three decimals, unknown customer or product
        foreach (var bad in new object[]
        {
            new { Kind = "amount", Value = 5m }, new { Kind = "percent", Value = 100.5m }, new { Kind = "percent", Value = 0m }, new { Kind = "gift", Value = 5m },
            new { Kind = "percent", Value = 1.2345m }, new { ProductId = p1, Kind = "percent", Value = 5m, ValidFrom = "2026-11-05", ValidTo = "2026-11-01" }, new { Value = 5m }
        })
            await AssertProblemAsync(await AddDiscount(c1, bad), HttpStatusCode.BadRequest, "INVALID_DATA");
        await AssertProblemAsync(await AddDiscount(Guid.NewGuid(), new { Kind = "percent", Value = 5m }), HttpStatusCode.NotFound, "NOT_FOUND");
        await AssertProblemAsync(await AddDiscount(c1, new { ProductId = Guid.NewGuid(), Kind = "percent", Value = 5m }), HttpStatusCode.NotFound, "NOT_FOUND");

        // A rep sees prices and discounts of customers in their own region only, and changes nothing
        As(repA);
        Assert.Equal((1800m, "city", 1520m), await PriceOf(c1, "2026-10-21"));
        Assert.Equal(3, (await client.GetFromJsonAsync<List<LifeDrop.Application.Catalog.CustomerDiscountDto>>($"/customers/{c1}/discounts"))!.Count);
        await AssertProblemAsync(await client.GetAsync($"/customers/{c2}/prices"), HttpStatusCode.NotFound, "NOT_FOUND");
        await AssertProblemAsync(await client.GetAsync($"/customers/{c2}/discounts"), HttpStatusCode.NotFound, "NOT_FOUND");
        await AssertProblemAsync(await AddDiscount(c1, new { Kind = "percent", Value = 50m }), HttpStatusCode.Forbidden, "FORBIDDEN");
        await AssertProblemAsync(await AddCityPrice(p1, new { CityId = erbil, Price = 1m, ValidFrom = "2028-06-01" }), HttpStatusCode.Forbidden, "FORBIDDEN");
        await AssertProblemAsync(await client.PostAsJsonAsync($"/discounts/{tenPercent.Id}/end", new { ValidTo = "2026-10-15" }), HttpStatusCode.Forbidden, "FORBIDDEN");
        await AssertProblemAsync(await client.PostAsJsonAsync($"/city-prices/{open.Id}/end", new { ValidTo = "2026-11-01" }), HttpStatusCode.Forbidden, "FORBIDDEN");
        As(driver);
        await AssertProblemAsync(await client.GetAsync($"/customers/{c1}/prices"), HttpStatusCode.Forbidden, "FORBIDDEN");

        await using (var conn = new NpgsqlConnection(schemaBlock.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(@"SELECT set_config('app.current_tenant', @t::text, false);
                SELECT (SELECT string_agg(action || '=' || n, ',' ORDER BY action COLLATE ""C"")
                        FROM (SELECT action, count(*) n FROM audit_logs WHERE action LIKE 'product.%' OR action LIKE 'city_price.%' OR action LIKE 'discount.%' GROUP BY action) a)", conn);
            cmd.Parameters.AddWithValue("t", t1);
            await using var reader = await cmd.ExecuteReaderAsync();
            await reader.NextResultAsync();
            await reader.ReadAsync();
            Assert.Equal("city_price.created=4,city_price.ended=1,discount.created=4,discount.ended=1,product.created=3,product.updated=1", reader.GetString(0));
        }
    }

    [SkippableFact]
    public async Task Orders_ServerPricesWarehouseChosenByVisitorScopeCancelAndRetry()
    {
        var url = GetRequiredEnvVar("TEST_DATABASE_URL");
        await VerifyRoleAsync(url, expectElevated: false);

        var (schemaBlock, factory) = await StartFlexiblePlanHostAsync(url);
        await using var _ = schemaBlock;
        await using var __ = factory;
        var conf = factory.Services.GetRequiredService<IConfiguration>();
        var client = factory.CreateClient();

        var t1 = Guid.NewGuid();
        Guid manager = Guid.NewGuid(), supervisor = Guid.NewGuid(), finance = Guid.NewGuid(), repA = Guid.NewGuid(), repB = Guid.NewGuid(), driver = Guid.NewGuid();
        Guid erbilRegion = Guid.NewGuid(), baghdadRegion = Guid.NewGuid(), erbil = Guid.NewGuid(), n1 = Guid.NewGuid();
        Guid c1 = Guid.NewGuid(), c2 = Guid.NewGuid(), c3 = Guid.NewGuid();
        Guid p1 = Guid.NewGuid(), p2 = Guid.NewGuid(), p3 = Guid.NewGuid(), p4 = Guid.NewGuid();
        Guid visitA = Guid.NewGuid(), visitOther = Guid.NewGuid(), visitB = Guid.NewGuid();
        await using (var conn = new NpgsqlConnection(schemaBlock.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(@"
                INSERT INTO tenants (id, name, code) VALUES (@t1, 'T1', 'T1');
                SELECT seed_default_roles(@t1);
                INSERT INTO users (id, tenant_id) VALUES (@m, @t1), (@sv, @t1), (@fin, @t1), (@ra, @t1), (@rb, @t1), (@dr, @t1);
                INSERT INTO user_roles (tenant_id, user_id, role_id)
                SELECT @t1, @m, id FROM roles WHERE tenant_id = @t1 AND name = 'SalesManager'
                UNION ALL SELECT @t1, @sv, id FROM roles WHERE tenant_id = @t1 AND name = 'Supervisor'
                UNION ALL SELECT @t1, @fin, id FROM roles WHERE tenant_id = @t1 AND name = 'FinanceManager'
                UNION ALL SELECT @t1, @ra, id FROM roles WHERE tenant_id = @t1 AND name = 'SalesRep'
                UNION ALL SELECT @t1, @rb, id FROM roles WHERE tenant_id = @t1 AND name = 'SalesRep'
                UNION ALL SELECT @t1, @dr, id FROM roles WHERE tenant_id = @t1 AND name = 'Driver';
                SELECT set_config('app.current_tenant', @t1::text, false);
                INSERT INTO regions (id, tenant_id, name) VALUES (@er, @t1, 'Erbil team region'), (@br, @t1, 'Baghdad team region');
                INSERT INTO teams (id, tenant_id, region_id, name) VALUES
                    ('55555555-0000-0000-0000-0000000000a1', @t1, @er, 'Team A'), ('55555555-0000-0000-0000-0000000000a2', @t1, @br, 'Team B');
                INSERT INTO team_members (tenant_id, user_id, team_id) VALUES
                    (@t1, @sv, '55555555-0000-0000-0000-0000000000a1'), (@t1, @ra, '55555555-0000-0000-0000-0000000000a1'),
                    (@t1, @rb, '55555555-0000-0000-0000-0000000000a2');
                INSERT INTO places (id, tenant_id, parent_id, kind, name) VALUES (@erbil, @t1, NULL, 'city', 'Erbil'), (@n1, @t1, @erbil, 'neighbourhood', 'N1');
                INSERT INTO customers (id, tenant_id, type, code, name, region_id, place_id, approval_status) VALUES
                    (@c1, @t1, 'Pharmacy', 'C1', 'Customer 1', @er, @n1, 'approved'),
                    (@c2, @t1, 'Pharmacy', 'C2', 'Customer 2', @br, NULL, 'approved'),
                    (@c3, @t1, 'Pharmacy', 'C3', 'Customer 3', @er, @n1, 'pending');
                INSERT INTO products (id, tenant_id, code, name, unit, currency, base_price, status) VALUES
                    (@p1, @t1, 'P1', 'Product 1', 'box', 'IQD', 1500, 'active'), (@p2, @t1, 'P2', 'Product 2', 'box', 'IQD', 2000, 'active'),
                    (@p3, @t1, 'P3', 'Product 3', 'box', 'USD', 10, 'active'), (@p4, @t1, 'P4', 'Product 4', 'box', 'IQD', 500, 'inactive');
                INSERT INTO city_prices (tenant_id, product_id, city_id, price, valid_from) VALUES (@t1, @p1, @erbil, 1800, '2026-10-01');
                INSERT INTO customer_discounts (tenant_id, customer_id, kind, value, valid_from) VALUES (@t1, @c1, 'percent', 10, '2026-10-01');
                INSERT INTO visits (id, tenant_id, user_id, customer_id, status, schedule_date, created_at) VALUES
                    (@va, @t1, @ra, @c1, 'InVisit', '2026-10-05', now()), (@vo, @t1, @ra, @c3, 'InVisit', '2026-10-05', now()),
                    (@vb, @t1, @rb, @c2, 'InVisit', '2026-10-05', now());", conn);
            foreach (var (name, value) in new[] { ("t1", t1), ("m", manager), ("sv", supervisor), ("fin", finance), ("ra", repA), ("rb", repB), ("dr", driver), ("er", erbilRegion), ("br", baghdadRegion),
                ("erbil", erbil), ("n1", n1), ("c1", c1), ("c2", c2), ("c3", c3), ("p1", p1), ("p2", p2), ("p3", p3), ("p4", p4), ("va", visitA), ("vo", visitOther), ("vb", visitB) })
                cmd.Parameters.AddWithValue(name, value);
            await cmd.ExecuteNonQueryAsync();
        }

        void As(Guid user) => client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            GenerateToken(t1, user, conf["Jwt:SigningKey"]!, conf["Jwt:Issuer"]!, conf["Jwt:Audience"]!));
        Task<HttpResponseMessage> Place(object body) => client.PostAsJsonAsync("/orders", body);
        object Body(Guid customer, Guid warehouse, Guid? clientRef = null, Guid? visit = null, params (Guid Product, decimal Quantity)[] lines) => new
        {
            CustomerId = customer, WarehouseId = warehouse, VisitId = visit, ClientRef = clientRef ?? Guid.NewGuid(), Note = " leave at the door ",
            Items = lines.Select(l => new { ProductId = l.Product, l.Quantity }).ToArray()
        };
        async Task<LifeDrop.Application.Orders.CreatedOrderDto> PlaceOk(object body, HttpStatusCode status = HttpStatusCode.Created)
        {
            var response = await Place(body);
            Assert.Equal(status, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<LifeDrop.Application.Orders.CreatedOrderDto>())!;
        }
        async Task<List<LifeDrop.Application.Orders.OrderDto>> Orders(string query = "") =>
            (await client.GetFromJsonAsync<List<LifeDrop.Application.Orders.OrderDto>>("/orders" + query))!;

        // Warehouses: only the sales manager maintains them; everybody who orders can list the active ones
        As(driver);
        await AssertProblemAsync(await client.GetAsync("/warehouses"), HttpStatusCode.Forbidden, "FORBIDDEN");
        As(repA);
        await AssertProblemAsync(await client.PostAsJsonAsync("/warehouses", new { Code = "W1", Name = "Main" }), HttpStatusCode.Forbidden, "FORBIDDEN");
        As(manager);
        async Task<Guid> AddWarehouse(string code, string name)
        {
            var response = await client.PostAsJsonAsync("/warehouses", new { Code = code, Name = name });
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<CreatedResponseDto>())!.Id;
        }
        var w1 = await AddWarehouse("W1", "Erbil warehouse");
        var w2 = await AddWarehouse("W2", "Old warehouse");
        await AssertProblemAsync(await client.PostAsJsonAsync("/warehouses", new { Code = "W1", Name = "Again" }), HttpStatusCode.Conflict, "CONFLICT");
        await AssertProblemAsync(await client.PostAsJsonAsync("/warehouses", new { Code = "W3", Name = " " }), HttpStatusCode.BadRequest, "INVALID_DATA");
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync($"/warehouses/{w2}", new { Code = "ignored", Name = "Old warehouse", Status = "inactive" })).StatusCode);
        await AssertProblemAsync(await client.PutAsJsonAsync($"/warehouses/{Guid.NewGuid()}", new { Name = "x" }), HttpStatusCode.NotFound, "NOT_FOUND");
        Assert.Equal(2, (await client.GetFromJsonAsync<List<LifeDrop.Application.Orders.WarehouseDto>>("/warehouses?includeInactive=true"))!.Count);
        As(repA);
        Assert.Equal(new[] { "W1" }, (await client.GetFromJsonAsync<List<LifeDrop.Application.Orders.WarehouseDto>>("/warehouses?includeInactive=true"))!.Select(w => w.Code).ToArray());

        // "Order placed" is a result of the report only when the visit has a real order
        Task<HttpResponseMessage> Report(Guid visit) => client.PutAsJsonAsync($"/visits/{visit}/report", new { Outcome = "order_placed", Note = "Ordered three boxes" });
        await AssertProblemAsync(await Report(visitA), HttpStatusCode.Conflict, "ORDER_REQUIRED");

        // A rep places an order for a visit: the server prices every line (city price 1800 for P1 then 10 % off; P2 general 2000 then 10 % off)
        var clientRef = Guid.NewGuid();
        var created = await PlaceOk(Body(c1, w1, clientRef, visitA, (p1, 3m), (p2, 2m)));
        Assert.Equal(("O-000001", "IQD", 8460m, false), (created.Number, created.Currency, created.Total, created.Replayed));
        Assert.Equal(HttpStatusCode.NoContent, (await Report(visitA)).StatusCode);
        var order = (await client.GetFromJsonAsync<LifeDrop.Application.Orders.OrderDto>($"/orders/{created.Id}"))!;
        Assert.Equal(("placed", "Erbil warehouse", visitA, "leave at the door"), (order.Status, order.WarehouseName, order.VisitId, order.Note));
        Assert.Equal(new[] { (1, "P1", 3m, 1800m, 1620m, 4860m), (2, "P2", 2m, 2000m, 1800m, 3600m) },
            order.Items.Select(i => (i.LineNo, i.ProductCode, i.Quantity, i.ListPrice, i.UnitPrice, i.LineTotal)).ToArray());
        Assert.Equal("percent", order.Items[0].Discounts.Single().Kind);

        // A retry of the same send gives the same order (200); the same reference from another person is refused
        var again = await PlaceOk(Body(c1, w1, clientRef, visitA, (p1, 3m), (p2, 2m)), HttpStatusCode.OK);
        Assert.Equal((created.Id, true), (again.Id, again.Replayed));
        As(supervisor);
        await AssertProblemAsync(await Place(Body(c1, w1, clientRef, null, (p1, 1m))), HttpStatusCode.Conflict, "CONFLICT");   // another person with the same reference
        As(repA);
        Assert.Single(await Orders());

        // The order keeps the price it was made with when the catalogue changes later
        As(manager);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync($"/products/{p2}", new { Name = "Product 2", Unit = "box", Currency = "IQD", BasePrice = 9999m })).StatusCode);
        As(repA);
        Assert.Equal(1800m, (await client.GetFromJsonAsync<LifeDrop.Application.Orders.OrderDto>($"/orders/{created.Id}"))!.Items[1].UnitPrice);

        // The phone's own client (what the app really sends and reads) against this API: prices, warehouses, an order, and a retry of it
        var phone = new LifeDrop.Mobile.Visits.OrdersApiClient(factory.CreateClient(), new StaticMobileSession(
            GenerateToken(t1, repA, conf["Jwt:SigningKey"]!, conf["Jwt:Issuer"]!, conf["Jwt:Audience"]!)));
        var (priceResult, priced) = await phone.GetPricesAsync(c1, new DateOnly(2026, 10, 6));
        Assert.Equal(LifeDrop.Mobile.Visits.VisitActionResult.Success, priceResult);
        Assert.Equal((1800m, 1620m), (priced.Single(p => p.Code == "P1").ListPrice, priced.Single(p => p.Code == "P1").Price));
        var (warehouseResult, phoneWarehouses) = await phone.GetWarehousesAsync();
        Assert.Equal(("W1", LifeDrop.Mobile.Visits.VisitActionResult.Success), (phoneWarehouses.Single().Code, warehouseResult));
        var phoneRef = Guid.NewGuid();
        var lines = new[] { new LifeDrop.Mobile.Visits.OrderLineInput(priced.Single(p => p.Code == "P1").ProductId, 2m) };
        var (placeResult, phoneOrder) = await phone.PlaceAsync(c1, phoneWarehouses[0].Id, null, phoneRef, null, lines);
        Assert.Equal((LifeDrop.Mobile.Visits.VisitActionResult.Success, "O-000002", 3240m, false), (placeResult, phoneOrder!.Number, phoneOrder.Total, phoneOrder.Replayed));
        var (_, replay) = await phone.PlaceAsync(c1, phoneWarehouses[0].Id, null, phoneRef, null, lines);
        Assert.Equal((phoneOrder.Id, true), (replay!.Id, replay.Replayed));
        Assert.Equal(LifeDrop.Mobile.Visits.VisitActionResult.CurrencyMismatch, (await phone.PlaceAsync(c1, phoneWarehouses[0].Id, null, Guid.NewGuid(), null,
            new[] { lines[0], new LifeDrop.Mobile.Visits.OrderLineInput(p3, 1m) })).Result);

        // Bad orders: no lines, the same product twice, bad quantities, two currencies, inactive or unknown product, bad warehouse, bad visit
        var bad = new[]
        {
            Body(c1, w1), Body(c1, w1, null, null, (p1, 1m), (p1, 2m)), Body(c1, w1, null, null, (p1, 0m)), Body(c1, w1, null, null, (p1, -1m)),
            Body(c1, w1, null, null, (p1, 1.2345m)), Body(c1, w1, null, null, (p1, 100001m)), Body(c1, w1, null, null, (p4, 1m)), Body(c1, w1, null, null, (Guid.NewGuid(), 1m)),
            Body(c1, w2, null, null, (p1, 1m)),   // an inactive warehouse
            (object)new { CustomerId = c1, WarehouseId = w1, ClientRef = Guid.Empty, Items = new[] { new { ProductId = p1, Quantity = 1m } } },
            (object)new { CustomerId = c1, WarehouseId = w1, ClientRef = Guid.NewGuid(), Note = new string('x', 501), Items = new[] { new { ProductId = p1, Quantity = 1m } } }
        };
        foreach (var body in bad) await AssertProblemAsync(await Place(body), HttpStatusCode.BadRequest, "INVALID_DATA");
        await AssertProblemAsync(await Place(Body(c1, w1, null, null, (p1, 1m), (p3, 1m))), HttpStatusCode.BadRequest, "CURRENCY_MISMATCH");
        await AssertProblemAsync(await Place(Body(c1, Guid.NewGuid(), null, null, (p1, 1m))), HttpStatusCode.NotFound, "NOT_FOUND");
        await AssertProblemAsync(await Place(Body(Guid.NewGuid(), w1, null, null, (p1, 1m))), HttpStatusCode.NotFound, "NOT_FOUND");
        await AssertProblemAsync(await Place(Body(c2, w1, null, null, (p1, 1m))), HttpStatusCode.NotFound, "NOT_FOUND");                 // another region
        await AssertProblemAsync(await Place(Body(c3, w1, null, null, (p1, 1m))), HttpStatusCode.Conflict, "CUSTOMER_NOT_APPROVED");
        await AssertProblemAsync(await Place(Body(c1, w1, null, visitOther, (p1, 1m))), HttpStatusCode.NotFound, "NOT_FOUND");           // a visit of another customer
        await AssertProblemAsync(await Place(Body(c1, w1, null, Guid.NewGuid(), (p1, 1m))), HttpStatusCode.NotFound, "NOT_FOUND");
        As(finance);
        await AssertProblemAsync(await Place(Body(c1, w1, null, null, (p1, 1m))), HttpStatusCode.Forbidden, "FORBIDDEN");
        As(driver);
        await AssertProblemAsync(await Place(Body(c1, w1, null, null, (p1, 1m))), HttpStatusCode.Forbidden, "FORBIDDEN");
        await AssertProblemAsync(await client.GetAsync("/orders"), HttpStatusCode.Forbidden, "FORBIDDEN");
        // The visit of rep B is not usable by rep A
        As(repB);
        await AssertProblemAsync(await Place(Body(c2, w1, null, visitA, (p1, 1m))), HttpStatusCode.NotFound, "NOT_FOUND");
        Assert.Equal("O-000003", (await PlaceOk(Body(c2, w1, null, visitB, (p3, 5m)))).Number);   // a Baghdad order in USD: general price, no discount

        // Reading: a rep sees the orders of their own region and their own; the other region does not; finance and managers read
        Assert.Empty((await Orders()).Where(o => o.CustomerId == c1));
        await AssertProblemAsync(await client.GetAsync($"/orders/{created.Id}"), HttpStatusCode.NotFound, "NOT_FOUND");
        As(supervisor);
        Assert.Equal(new[] { phoneOrder.Id, created.Id }, (await Orders()).Select(o => o.Id).ToArray());
        As(finance);
        Assert.Equal(3, (await Orders()).Count);
        Assert.Equal(new[] { "O-000003", "O-000002", "O-000001" }, (await Orders()).Select(o => o.Number).ToArray());   // newest first
        Assert.Equal(new[] { "O-000002", "O-000001" }, (await Orders($"?customerId={c1}")).Select(o => o.Number).ToArray());
        var firstPage = await Orders("?limit=2");
        var next = await Orders($"?limit=2&before={Uri.EscapeDataString(firstPage[1].CreatedAt.ToString("O"))}&beforeId={firstPage[1].Id}");
        Assert.Equal(new[] { "O-000003", "O-000002", "O-000001" }, firstPage.Concat(next).Select(o => o.Number).ToArray());
        await AssertProblemAsync(await client.GetAsync("/orders?limit=0"), HttpStatusCode.BadRequest, "INVALID_DATA");
        await AssertProblemAsync(await client.GetAsync("/orders?status=sent"), HttpStatusCode.BadRequest, "INVALID_DATA");

        // Cancelling: the creator while it is placed, managers in their area; others cannot
        As(supervisor);
        var supervisorsOrder = await PlaceOk(Body(c1, w1, null, null, (p2, 1m)));
        As(repA);
        await AssertProblemAsync(await client.PostAsJsonAsync($"/orders/{supervisorsOrder.Id}/cancel", new { Reason = "no" }), HttpStatusCode.Forbidden, "FORBIDDEN");   // sees it, did not place it
        await AssertProblemAsync(await client.PostAsJsonAsync($"/orders/{created.Id}/cancel", new { Reason = " " }), HttpStatusCode.BadRequest, "INVALID_DATA");
        As(repB);
        await AssertProblemAsync(await client.PostAsJsonAsync($"/orders/{created.Id}/cancel", new { Reason = "no" }), HttpStatusCode.NotFound, "NOT_FOUND");
        As(repA);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync($"/orders/{created.Id}/cancel", new { Reason = "Customer changed his mind" })).StatusCode);
        await AssertProblemAsync(await client.PostAsJsonAsync($"/orders/{created.Id}/cancel", new { Reason = "again" }), HttpStatusCode.Conflict, "CONFLICT");
        var cancelled = (await client.GetFromJsonAsync<LifeDrop.Application.Orders.OrderDto>($"/orders/{created.Id}"))!;
        Assert.Equal(("cancelled", "Customer changed his mind"), (cancelled.Status, cancelled.CancelReason));
        As(supervisor);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync($"/orders/{supervisorsOrder.Id}/cancel", new { Reason = "Wrong customer" })).StatusCode);
        As(finance);
        await AssertProblemAsync(await client.PostAsJsonAsync($"/orders/{created.Id}/cancel", new { Reason = "x" }), HttpStatusCode.Forbidden, "FORBIDDEN" /* finance reads but has no orders.cancel and did not place it */);

        await using (var conn = new NpgsqlConnection(schemaBlock.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(@"SELECT set_config('app.current_tenant', @t::text, false);
                SELECT (SELECT string_agg(action || '=' || n, ',' ORDER BY action COLLATE ""C"")
                        FROM (SELECT action, count(*) n FROM audit_logs WHERE action LIKE 'order.%' OR action LIKE 'warehouse.%' GROUP BY action) a),
                       (SELECT last_number FROM order_counters WHERE tenant_id = @t)", conn);
            cmd.Parameters.AddWithValue("t", t1);
            await using var reader = await cmd.ExecuteReaderAsync();
            await reader.NextResultAsync();
            await reader.ReadAsync();
            Assert.Equal("order.cancelled=2,order.created=4,warehouse.created=2,warehouse.updated=1", reader.GetString(0));
            Assert.Equal(4L, reader.GetInt64(1));   // retries and refused orders did not use a number
        }
    }

    [SkippableFact]
    public async Task WarehouseWork_StaffSeeOnlyTheirWarehouseOrdersAndMoveThemThroughPreparationToTheDriver()
    {
        var url = GetRequiredEnvVar("TEST_DATABASE_URL");
        await VerifyRoleAsync(url, expectElevated: false);

        var (schemaBlock, factory) = await StartFlexiblePlanHostAsync(url);
        await using var _ = schemaBlock;
        await using var __ = factory;
        var conf = factory.Services.GetRequiredService<IConfiguration>();
        var client = factory.CreateClient();

        var t1 = Guid.NewGuid();
        Guid manager = Guid.NewGuid(), worker1 = Guid.NewGuid(), worker2 = Guid.NewGuid(), driver = Guid.NewGuid(), rep = Guid.NewGuid(), stranger = Guid.NewGuid();
        Guid w1 = Guid.NewGuid(), w2 = Guid.NewGuid(), c1 = Guid.NewGuid(), p1 = Guid.NewGuid(), p2 = Guid.NewGuid();
        await using (var conn = new NpgsqlConnection(schemaBlock.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(@"
                INSERT INTO tenants (id, name, code) VALUES (@t1, 'T1', 'T1');
                SELECT seed_default_roles(@t1);
                INSERT INTO users (id, tenant_id) VALUES (@m, @t1), (@w1u, @t1), (@w2u, @t1), (@dr, @t1), (@rep, @t1), (@st, @t1);
                INSERT INTO user_roles (tenant_id, user_id, role_id)
                SELECT @t1, @m, id FROM roles WHERE tenant_id = @t1 AND name = 'SalesManager'
                UNION ALL SELECT @t1, @w1u, id FROM roles WHERE tenant_id = @t1 AND name = 'WarehouseManager'
                UNION ALL SELECT @t1, @w2u, id FROM roles WHERE tenant_id = @t1 AND name = 'WarehouseManager'
                UNION ALL SELECT @t1, @dr, id FROM roles WHERE tenant_id = @t1 AND name = 'Driver'
                UNION ALL SELECT @t1, @rep, id FROM roles WHERE tenant_id = @t1 AND name = 'SalesRep'
                UNION ALL SELECT @t1, @st, id FROM roles WHERE tenant_id = @t1 AND name = 'WarehouseManager';
                SELECT set_config('app.current_tenant', @t1::text, false);
                INSERT INTO warehouses (id, tenant_id, code, name) VALUES (@w1, @t1, 'W1', 'Erbil warehouse'), (@w2, @t1, 'W2', 'Basra warehouse');
                INSERT INTO customers (id, tenant_id, type, code, name) VALUES (@c1, @t1, 'Pharmacy', 'C1', 'Customer 1');
                INSERT INTO products (id, tenant_id, code, name, unit, currency, base_price) VALUES
                    (@p1, @t1, 'P1', 'Product 1', 'box', 'IQD', 1000), (@p2, @t1, 'P2', 'Product 2', 'box', 'IQD', 2000);", conn);
            foreach (var (name, value) in new[] { ("t1", t1), ("m", manager), ("w1u", worker1), ("w2u", worker2), ("dr", driver), ("rep", rep), ("st", stranger), ("w1", w1), ("w2", w2), ("c1", c1), ("p1", p1), ("p2", p2) })
                cmd.Parameters.AddWithValue(name, value);
            await cmd.ExecuteNonQueryAsync();
        }

        void As(Guid user) => client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            GenerateToken(t1, user, conf["Jwt:SigningKey"]!, conf["Jwt:Issuer"]!, conf["Jwt:Audience"]!));
        async Task<LifeDrop.Application.Orders.CreatedOrderDto> PlaceOrder(Guid warehouse)
        {
            var response = await client.PostAsJsonAsync("/orders", new { CustomerId = c1, WarehouseId = warehouse, ClientRef = Guid.NewGuid(), Items = new[] { new { ProductId = p1, Quantity = 10m }, new { ProductId = p2, Quantity = 4m } } });
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<LifeDrop.Application.Orders.CreatedOrderDto>())!;
        }
        Task<HttpResponseMessage> Post(string path, object? body = null) => client.PostAsJsonAsync(path, body ?? new { });
        async Task<List<LifeDrop.Application.Orders.OrderDto>> Queue(string query = "") =>
            (await client.GetFromJsonAsync<List<LifeDrop.Application.Orders.OrderDto>>("/warehouse/orders" + query))!;
        async Task<LifeDrop.Application.Orders.OrderDto> Detail(Guid id) =>
            (await client.GetFromJsonAsync<LifeDrop.Application.Orders.OrderDto>($"/warehouse/orders/{id}"))!;

        As(manager);
        var order1 = await PlaceOrder(w1);   // for the Erbil warehouse
        var order2 = await PlaceOrder(w2);   // for the Basra warehouse

        // Nobody works anywhere yet; only the sales manager decides who works where, and only a warehouse worker can be put to work
        As(worker1);
        Assert.Empty(await Queue());
        await AssertProblemAsync(await client.GetAsync($"/warehouse/orders/{order1.Id}"), HttpStatusCode.NotFound, "NOT_FOUND");
        await AssertProblemAsync(await Post($"/warehouse/orders/{order1.Id}/start"), HttpStatusCode.NotFound, "NOT_FOUND");
        await AssertProblemAsync(await Post($"/warehouses/{w1}/staff", new { UserId = worker1 }), HttpStatusCode.Forbidden, "FORBIDDEN");
        As(rep);
        await AssertProblemAsync(await client.GetAsync("/warehouse/orders"), HttpStatusCode.Forbidden, "FORBIDDEN");
        await AssertProblemAsync(await client.GetAsync("/warehouse/drivers"), HttpStatusCode.Forbidden, "FORBIDDEN");
        await AssertProblemAsync(await Post($"/warehouse/orders/{order1.Id}/start"), HttpStatusCode.Forbidden, "FORBIDDEN");
        As(manager);
        Assert.Equal(HttpStatusCode.NoContent, (await Post($"/warehouses/{w1}/staff", new { UserId = worker1 })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Post($"/warehouses/{w1}/staff", new { UserId = worker1 })).StatusCode);   // again: nothing changes
        Assert.Equal(HttpStatusCode.NoContent, (await Post($"/warehouses/{w2}/staff", new { UserId = worker2 })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Post($"/warehouses/{w2}/staff", new { UserId = stranger })).StatusCode);
        await AssertProblemAsync(await Post($"/warehouses/{w1}/staff", new { UserId = rep }), HttpStatusCode.BadRequest, "INVALID_DATA");   // a rep is no warehouse worker
        await AssertProblemAsync(await Post($"/warehouses/{w1}/staff", new { UserId = Guid.NewGuid() }), HttpStatusCode.NotFound, "NOT_FOUND");
        await AssertProblemAsync(await Post($"/warehouses/{Guid.NewGuid()}/staff", new { UserId = worker1 }), HttpStatusCode.NotFound, "NOT_FOUND");
        await AssertProblemAsync(await Post($"/warehouses/{w1}/staff", new { }), HttpStatusCode.BadRequest, "INVALID_DATA");
        Assert.Equal(2, (await client.GetFromJsonAsync<List<LifeDrop.Application.Orders.StaffMemberDto>>($"/warehouses/{w2}/staff"))!.Count);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/warehouses/{w2}/staff/{stranger}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/warehouses/{w2}/staff/{stranger}")).StatusCode);

        // A worker sees only the orders of their own warehouse
        As(worker1);
        Assert.Equal(new[] { order1.Id }, (await Queue()).Select(o => o.Id).ToArray());
        await AssertProblemAsync(await client.GetAsync($"/warehouse/orders/{order2.Id}"), HttpStatusCode.NotFound, "NOT_FOUND");
        await AssertProblemAsync(await Post($"/warehouse/orders/{order2.Id}/start"), HttpStatusCode.NotFound, "NOT_FOUND");
        As(worker2);
        Assert.Equal(new[] { order2.Id }, (await Queue()).Select(o => o.Id).ToArray());
        As(stranger);
        Assert.Empty(await Queue());   // removed from the warehouse
        As(worker1);

        // The steps come in order
        await AssertProblemAsync(await Post($"/warehouse/orders/{order1.Id}/ready", new { Lines = new[] { new { LineNo = 1, Quantity = 10m }, new { LineNo = 2, Quantity = 4m } } }), HttpStatusCode.Conflict, "CONFLICT");
        await AssertProblemAsync(await Post($"/warehouse/orders/{order1.Id}/handover", new { DriverId = driver }), HttpStatusCode.Conflict, "CONFLICT");
        Assert.Equal(HttpStatusCode.NoContent, (await Post($"/warehouse/orders/{order1.Id}/start")).StatusCode);
        await AssertProblemAsync(await Post($"/warehouse/orders/{order1.Id}/start"), HttpStatusCode.Conflict, "CONFLICT");
        Assert.Equal("preparing", (await Detail(order1.Id)).Status);

        // Reporting what was prepared: every line once, never more than ordered, less needs a reason
        foreach (var bad in new object[]
        {
            new { Lines = new[] { new { LineNo = 1, Quantity = 10m } } },                                                       // line 2 missing
            new { Lines = new[] { new { LineNo = 1, Quantity = 11m }, new { LineNo = 2, Quantity = 4m } } },                    // more than ordered
            new { Lines = new[] { new { LineNo = 1, Quantity = 10m }, new { LineNo = 2, Quantity = 3m } } },                    // less, no reason
            new { Lines = new[] { new { LineNo = 1, Quantity = 10m }, new { LineNo = 3, Quantity = 4m } } },                    // no such line
            new { Lines = new[] { new { LineNo = 1, Quantity = 10m }, new { LineNo = 1, Quantity = 10m } } },                   // twice
            new { Lines = new[] { new { LineNo = 1, Quantity = -1m }, new { LineNo = 2, Quantity = 4m } } },
            new { Lines = Array.Empty<object>() }
        })
            await AssertProblemAsync(await Post($"/warehouse/orders/{order1.Id}/ready", bad), HttpStatusCode.BadRequest, "INVALID_DATA");
        Assert.Equal("preparing", (await Detail(order1.Id)).Status);
        Assert.Equal(HttpStatusCode.NoContent, (await Post($"/warehouse/orders/{order1.Id}/ready", new { Lines = new object[] { new { LineNo = 1, Quantity = 10m }, new { LineNo = 2, Quantity = 3m, Reason = " only 3 in stock " } } })).StatusCode);

        var ready = await Detail(order1.Id);
        Assert.Equal("ready", ready.Status);
        Assert.Equal(new decimal?[] { 10m, 3m }, ready.Items.Select(i => i.PreparedQuantity).ToArray());
        Assert.Equal(new string?[] { null, "only 3 in stock" }, ready.Items.Select(i => i.ShortageReason).ToArray());
        Assert.Equal(18000m, ready.Total);   // the total of the order is not changed by a shortage
        Assert.Equal(new[] { "placed", "preparing", "ready" }, ready.History!.Select(h => h.Status).ToArray());

        // Hand over: only to a driver, only when ready
        await AssertProblemAsync(await Post($"/warehouse/orders/{order1.Id}/handover", new { DriverId = rep }), HttpStatusCode.BadRequest, "INVALID_DATA");
        await AssertProblemAsync(await Post($"/warehouse/orders/{order1.Id}/handover", new { DriverId = Guid.NewGuid() }), HttpStatusCode.BadRequest, "INVALID_DATA");
        await AssertProblemAsync(await Post($"/warehouse/orders/{order1.Id}/handover", new { }), HttpStatusCode.BadRequest, "INVALID_DATA");
        Assert.Equal(new[] { driver }, (await client.GetFromJsonAsync<List<LifeDrop.Application.Orders.DriverDto>>("/warehouse/drivers"))!.Select(d => d.Id).ToArray());
        Assert.Equal(HttpStatusCode.NoContent, (await Post($"/warehouse/orders/{order1.Id}/handover", new { DriverId = driver })).StatusCode);
        var handed = await Detail(order1.Id);
        Assert.Equal(("handed_over", driver), (handed.Status, handed.DriverId));
        Assert.Equal(new[] { "placed", "preparing", "ready", "handed_over" }, handed.History!.Select(h => h.Status).ToArray());
        Assert.Empty(await Queue());                                                                        // no longer open work
        Assert.Single(await Queue("?status=handed_over"));
        await AssertProblemAsync(await client.GetAsync("/warehouse/orders?status=sent"), HttpStatusCode.BadRequest, "INVALID_DATA");

        // The people who sell see the new steps; the creator can no longer cancel; a manager cannot cancel after the hand-over
        As(manager);
        var seen = (await client.GetFromJsonAsync<LifeDrop.Application.Orders.OrderDto>($"/orders/{order1.Id}"))!;
        Assert.Equal(("handed_over", 3m), (seen.Status, seen.Items[1].PreparedQuantity!.Value));
        await AssertProblemAsync(await Post($"/orders/{order1.Id}/cancel", new { Reason = "too late" }), HttpStatusCode.Conflict, "CONFLICT");
        // A third order: the manager can cancel it while it is being prepared, and the warehouse can do nothing with it afterwards
        var order3 = await PlaceOrder(w1);
        As(worker1);
        Assert.Equal(HttpStatusCode.NoContent, (await Post($"/warehouse/orders/{order3.Id}/start")).StatusCode);
        As(manager);
        Assert.Equal(HttpStatusCode.NoContent, (await Post($"/orders/{order3.Id}/cancel", new { Reason = "Customer left" })).StatusCode);
        As(worker1);
        await AssertProblemAsync(await Post($"/warehouse/orders/{order3.Id}/ready", new { Lines = new[] { new { LineNo = 1, Quantity = 10m }, new { LineNo = 2, Quantity = 4m } } }), HttpStatusCode.Conflict, "CONFLICT");
        Assert.Equal("cancelled", (await Detail(order3.Id)).Status);

        await using (var conn = new NpgsqlConnection(schemaBlock.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(@"SELECT set_config('app.current_tenant', @t::text, false);
                SELECT (SELECT string_agg(action || '=' || n, ',' ORDER BY action COLLATE ""C"")
                        FROM (SELECT action, count(*) n FROM audit_logs WHERE action LIKE 'order.%' OR action LIKE 'warehouse.staff%' GROUP BY action) a)", conn);
            cmd.Parameters.AddWithValue("t", t1);
            await using var reader = await cmd.ExecuteReaderAsync();
            await reader.NextResultAsync();
            await reader.ReadAsync();
            Assert.Equal("order.cancelled=1,order.created=3,order.handed_over=1,order.preparing=2,order.ready=1,order.shortage=1,warehouse.staff_added=3,warehouse.staff_removed=1", reader.GetString(0));
        }
    }

    [SkippableFact]
    public async Task OfflineRules_SavedWorkIsKeptWithoutItsCancelledOrderOrVisitAndStepsSentAgainAreNotConflicts()
    {
        var url = GetRequiredEnvVar("TEST_DATABASE_URL");
        await VerifyRoleAsync(url, expectElevated: false);

        var (schemaBlock, factory) = await StartFlexiblePlanHostAsync(url);
        await using var _ = schemaBlock;
        await using var __ = factory;
        var conf = factory.Services.GetRequiredService<IConfiguration>();
        var client = factory.CreateClient();

        var t1 = Guid.NewGuid();
        Guid rep = Guid.NewGuid(), rep2 = Guid.NewGuid(), manager = Guid.NewGuid(), w1 = Guid.NewGuid(), c1 = Guid.NewGuid(), c2 = Guid.NewGuid(), p1 = Guid.NewGuid(), vOpen = Guid.NewGuid(), vCancelled = Guid.NewGuid(), oCancelled = Guid.NewGuid();
        await using (var conn = new NpgsqlConnection(schemaBlock.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(@"
                INSERT INTO tenants (id, name, code) VALUES (@t1, 'T1', 'T1');
                SELECT seed_default_roles(@t1);
                INSERT INTO users (id, tenant_id) VALUES (@rep, @t1), (@rep2, @t1), (@m, @t1);
                INSERT INTO user_roles (tenant_id, user_id, role_id)
                SELECT @t1, @rep, id FROM roles WHERE tenant_id = @t1 AND name = 'SalesRep'
                UNION ALL SELECT @t1, @rep2, id FROM roles WHERE tenant_id = @t1 AND name = 'SalesRep'
                UNION ALL SELECT @t1, @m, id FROM roles WHERE tenant_id = @t1 AND name = 'SalesManager';
                SELECT set_config('app.current_tenant', @t1::text, false);
                INSERT INTO warehouses (id, tenant_id, code, name) VALUES (@w1, @t1, 'W1', 'Warehouse');
                INSERT INTO products (id, tenant_id, code, name, unit, currency, base_price, status) VALUES (@p1, @t1, 'P1', 'Product 1', 'box', 'IQD', 1000, 'active');
                INSERT INTO customers (id, tenant_id, type, code, name, status, owner_user_id) VALUES
                    (@c1, @t1, 'Pharmacy', 'C1', 'Customer 1', 'no_location', @rep), (@c2, @t1, 'Pharmacy', 'C2', 'Customer 2', 'no_location', @rep2);
                INSERT INTO visits (id, tenant_id, user_id, customer_id, status, schedule_date, created_at) VALUES
                    (@vo, @t1, @rep, @c1, 'Planned', '2026-10-05', now()), (@vc, @t1, @rep, @c1, 'Cancelled', '2026-10-05', now());
                INSERT INTO orders (id, tenant_id, number, customer_id, warehouse_id, created_by, status, currency, total, client_ref, cancelled_at, cancel_reason)
                VALUES (@oc, @t1, 900, @c1, @w1, @rep, 'cancelled', 'IQD', 1000, gen_random_uuid(), now(), 'mistake');", conn);
            foreach (var (name, value) in new[] { ("t1", t1), ("rep", rep), ("rep2", rep2), ("m", manager), ("w1", w1), ("c1", c1), ("c2", c2), ("p1", p1), ("vo", vOpen), ("vc", vCancelled), ("oc", oCancelled) })
                cmd.Parameters.AddWithValue(name, value);
            await cmd.ExecuteNonQueryAsync();
        }

        void As(Guid user) => client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            GenerateToken(t1, user, conf["Jwt:SigningKey"]!, conf["Jwt:Issuer"]!, conf["Jwt:Audience"]!));
        Task<HttpResponseMessage> Post(string path, object? body = null) => client.PostAsJsonAsync(path, body ?? new { });

        // Payment: with the order cancelled, a normal request is refused; the one the phone kept offline is recorded without the order and says so
        As(rep);
        object Pay(Guid cr, decimal amount = 5000m, Guid? customer = null, bool? detach = true) =>
            new { CustomerId = customer ?? c1, OrderId = oCancelled, Method = "cash", Currency = "IQD", Amount = amount, ClientRef = cr, DetachOrderIfInvalid = detach };
        await AssertProblemAsync(await Post("/payments", Pay(Guid.NewGuid(), detach: null)), HttpStatusCode.BadRequest, "INVALID_DATA");
        var ref1 = Guid.NewGuid();
        var paid = await Post("/payments", Pay(ref1));
        Assert.Equal(HttpStatusCode.Created, paid.StatusCode);
        var paymentDto = (await paid.Content.ReadFromJsonAsync<LifeDrop.Application.Payments.CreatedPaymentDto>())!;
        Assert.Equal(("collected", true), (paymentDto.Status, paymentDto.Detached));
        var mine = (await client.GetFromJsonAsync<List<LifeDrop.Application.Payments.PaymentDto>>("/payments/mine"))!;
        Assert.Null(Assert.Single(mine).OrderId);                                       // recorded on the customer, not on the cancelled order
        var again = await Post("/payments", Pay(ref1));                                 // sent again after a lost answer: the same payment
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(paymentDto.Id, (await again.Content.ReadFromJsonAsync<LifeDrop.Application.Payments.CreatedPaymentDto>())!.Id);
        await AssertProblemAsync(await Post("/payments", Pay(Guid.NewGuid(), amount: 0m)), HttpStatusCode.BadRequest, "INVALID_DATA");   // a wrong amount is still refused
        await AssertProblemAsync(await Post("/payments", Pay(Guid.NewGuid(), customer: c2)), HttpStatusCode.NotFound, "NOT_FOUND");      // and a customer outside the area

        // Sample: the visit was cancelled meanwhile
        object Sample(Guid cr, bool? detach) => new { CustomerId = c1, VisitId = vCancelled, ProductId = p1, Quantity = 2m, ClientRef = cr, DetachVisitIfInvalid = detach };
        await AssertProblemAsync(await Post("/samples", Sample(Guid.NewGuid(), null)), HttpStatusCode.BadRequest, "INVALID_DATA");
        var sample = await Post("/samples", Sample(Guid.NewGuid(), true));
        Assert.Equal(HttpStatusCode.Created, sample.StatusCode);
        var sampleDto = (await sample.Content.ReadFromJsonAsync<LifeDrop.Application.Samples.CreatedSampleDto>())!;
        Assert.True(sampleDto.Detached);
        Assert.Null((await client.GetFromJsonAsync<LifeDrop.Application.Samples.SampleDto>($"/samples/{sampleDto.Id}"))!.VisitId);
        var linked = await Post("/samples", new { CustomerId = c1, VisitId = vOpen, ProductId = p1, Quantity = 1m, ClientRef = Guid.NewGuid(), DetachVisitIfInvalid = true });   // a good visit stays linked
        Assert.False((await linked.Content.ReadFromJsonAsync<LifeDrop.Application.Samples.CreatedSampleDto>())!.Detached);

        // Order: the same for its visit; a customer that is not approved or an unknown product is still refused
        object Order(Guid cr, bool? detach) => new { CustomerId = c1, WarehouseId = w1, VisitId = vCancelled, ClientRef = cr, Items = new[] { new { ProductId = p1, Quantity = 3m } }, DetachVisitIfInvalid = detach };
        await AssertProblemAsync(await Post("/orders", Order(Guid.NewGuid(), null)), HttpStatusCode.BadRequest, "INVALID_DATA");
        var order = await Post("/orders", Order(Guid.NewGuid(), true));
        Assert.True(order.StatusCode == HttpStatusCode.Created, await order.Content.ReadAsStringAsync());
        var orderDto = (await order.Content.ReadFromJsonAsync<LifeDrop.Application.Orders.CreatedOrderDto>())!;
        Assert.Equal((3000m, true), (orderDto.Total, orderDto.VisitDetached));         // the server priced it when it arrived
        Assert.Null((await client.GetFromJsonAsync<LifeDrop.Application.Orders.OrderDto>($"/orders/{orderDto.Id}"))!.VisitId);
        await AssertProblemAsync(await Post("/orders", new { CustomerId = c1, WarehouseId = w1, ClientRef = Guid.NewGuid(), Items = new[] { new { ProductId = Guid.NewGuid(), Quantity = 3m } }, DetachVisitIfInvalid = true }), HttpStatusCode.BadRequest, "INVALID_DATA");

        // Follow-up steps sent again are not conflicts, other texts or other statuses are
        async Task<Guid> Followup(string subject) => (await (await Post("/followups", new { CustomerId = c1, Subject = subject, DueDate = "2026-10-12", ClientRef = Guid.NewGuid() })).Content.ReadFromJsonAsync<LifeDrop.Application.FollowUps.CreatedFollowUpDto>())!.Id;
        var f1 = await Followup("one");
        Assert.Equal(HttpStatusCode.NoContent, (await Post($"/followups/{f1}/start")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Post($"/followups/{f1}/start")).StatusCode);                         // sent again: still fine
        Assert.Equal(HttpStatusCode.NoContent, (await Post($"/followups/{f1}/complete", new { ResultNote = "got it" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Post($"/followups/{f1}/complete", new { ResultNote = "got it" })).StatusCode);   // the same note: the same step
        await AssertProblemAsync(await Post($"/followups/{f1}/complete", new { ResultNote = "something else" }), HttpStatusCode.Conflict, "CONFLICT");
        await AssertProblemAsync(await Post($"/followups/{f1}/start"), HttpStatusCode.Conflict, "CONFLICT");                   // completed: a start is a real conflict
        var f2 = await Followup("two");
        Assert.Equal(HttpStatusCode.NoContent, (await Post($"/followups/{f2}/cancel", new { Reason = "closed" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Post($"/followups/{f2}/cancel", new { Reason = "closed" })).StatusCode);
        await AssertProblemAsync(await Post($"/followups/{f2}/cancel", new { Reason = "other reason" }), HttpStatusCode.Conflict, "CONFLICT");
        await AssertProblemAsync(await Post($"/followups/{Guid.NewGuid()}/start"), HttpStatusCode.NotFound, "NOT_FOUND");

        await using var verify = new NpgsqlConnection(schemaBlock.ConnectionString);
        await verify.OpenAsync();
        await using var audit = new NpgsqlCommand("SELECT action, count(*) FROM audit_logs WHERE action LIKE 'followup.%' OR action = 'payment.recorded_detached' GROUP BY action ORDER BY action", verify);
        var actions = new List<string>();
        await using (var reader = await audit.ExecuteReaderAsync())
            while (await reader.ReadAsync()) actions.Add($"{reader.GetString(0)}={reader.GetInt64(1)}");
        Assert.Equal(new[] { "followup.cancelled=1", "followup.completed=1", "followup.created=2", "followup.started=1", "payment.recorded_detached=1" }, actions);   // a step sent again writes nothing twice
    }

    [SkippableFact]
    public async Task Reports_KpisAreCountedFromRealDataWithDocumentedNumeratorsAndDenominatorsInsideTheReadersArea()
    {
        var url = GetRequiredEnvVar("TEST_DATABASE_URL");
        await VerifyRoleAsync(url, expectElevated: false);

        var (schemaBlock, factory) = await StartFlexiblePlanHostAsync(url);
        await using var _ = schemaBlock;
        await using var __ = factory;
        var conf = factory.Services.GetRequiredService<IConfiguration>();
        var client = factory.CreateClient();

        var t1 = Guid.NewGuid();
        Guid rep = Guid.NewGuid(), rep2 = Guid.NewGuid(), sup = Guid.NewGuid(), manager = Guid.NewGuid(), finance = Guid.NewGuid();
        Guid r1 = Guid.NewGuid(), r2 = Guid.NewGuid(), teamA = Guid.NewGuid(), teamB = Guid.NewGuid(), w1 = Guid.NewGuid();
        Guid c1 = Guid.NewGuid(), c2 = Guid.NewGuid(), c3 = Guid.NewGuid();
        var v = Enumerable.Range(0, 9).Select(_ => Guid.NewGuid()).ToArray();
        await using (var conn = new NpgsqlConnection(schemaBlock.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(@"
                INSERT INTO tenants (id, name, code) VALUES (@t1, 'T1', 'T1');
                SELECT seed_default_roles(@t1);
                INSERT INTO users (id, tenant_id) VALUES (@rep, @t1), (@rep2, @t1), (@sup, @t1), (@m, @t1), (@f, @t1);
                INSERT INTO user_roles (tenant_id, user_id, role_id)
                SELECT @t1, @rep, id FROM roles WHERE tenant_id = @t1 AND name = 'SalesRep'
                UNION ALL SELECT @t1, @rep2, id FROM roles WHERE tenant_id = @t1 AND name = 'SalesRep'
                UNION ALL SELECT @t1, @sup, id FROM roles WHERE tenant_id = @t1 AND name = 'Supervisor'
                UNION ALL SELECT @t1, @m, id FROM roles WHERE tenant_id = @t1 AND name = 'SalesManager'
                UNION ALL SELECT @t1, @f, id FROM roles WHERE tenant_id = @t1 AND name = 'FinanceManager';
                SELECT set_config('app.current_tenant', @t1::text, false);
                INSERT INTO regions (id, tenant_id, name) VALUES (@r1, @t1, 'Erbil'), (@r2, @t1, 'Baghdad');
                INSERT INTO teams (id, tenant_id, region_id, name) VALUES (@ta, @t1, @r1, 'Team A'), (@tb, @t1, @r2, 'Team B');
                INSERT INTO team_members (tenant_id, user_id, team_id) VALUES (@t1, @rep, @ta), (@t1, @sup, @ta), (@t1, @rep2, @tb);
                INSERT INTO warehouses (id, tenant_id, code, name) VALUES (@w1, @t1, 'W1', 'Warehouse');
                INSERT INTO customers (id, tenant_id, type, code, name, status, owner_user_id, region_id, approval_status, created_at) VALUES
                    (@c1, @t1, 'Pharmacy', 'C1', 'Customer 1', 'no_location', @rep, @r1, 'approved', '2026-10-03 08:00:00+00'),
                    (@c2, @t1, 'Doctor', 'C2', 'Customer 2', 'no_location', @rep, @r1, 'pending', '2026-10-04 08:00:00+00'),
                    (@c3, @t1, 'Pharmacy', 'C3', 'Customer 3', 'no_location', @rep2, @r2, 'approved', '2026-09-01 08:00:00+00');
                -- visits of rep: on time, late, failed (no time), never started and overdue, cancelled, never started but not yet due, a doctor visit
                INSERT INTO visits (id, tenant_id, user_id, customer_id, status, schedule_date, planned_time, created_at) VALUES
                    (@v0, @t1, @rep, @c1, 'Completed', '2026-10-01', '09:00', now()), (@v1, @t1, @rep, @c1, 'Completed', '2026-10-02', '09:00', now()),
                    (@v2, @t1, @rep, @c1, 'Failed', '2026-10-03', NULL, now()), (@v3, @t1, @rep, @c1, 'Planned', '2026-10-04', '10:00', now()),
                    (@v4, @t1, @rep, @c1, 'Cancelled', '2026-10-04', NULL, now()), (@v5, @t1, @rep, @c1, 'Planned', '2026-10-05', '15:00', now()),
                    (@v6, @t1, @rep, @c2, 'Completed', '2026-10-05', NULL, now()), (@v7, @t1, @rep2, @c3, 'Completed', '2026-10-02', NULL, now()),
                    (@v8, @t1, @rep, @c1, 'Completed', '2026-09-20', NULL, now());
                INSERT INTO visit_status_history (tenant_id, visit_id, to_status, occurred_at) VALUES
                    (@t1, @v0, 'InVisit', '2026-10-01 06:10:00+00'), (@t1, @v1, 'InVisit', '2026-10-02 06:50:00+00');
                INSERT INTO orders (tenant_id, number, customer_id, warehouse_id, created_by, status, currency, total, client_ref, created_at, driver_id, receiver_name, failure_reason, cancelled_at, cancel_reason) VALUES
                    (@t1, 1, @c1, @w1, @rep, 'delivered', 'IQD', 1000, gen_random_uuid(), '2026-10-02 08:00:00+00', @rep, 'Ali', NULL, NULL, NULL),
                    (@t1, 2, @c1, @w1, @rep, 'delivery_failed', 'IQD', 2000, gen_random_uuid(), '2026-10-03 08:00:00+00', @rep, NULL, 'closed', NULL, NULL),
                    (@t1, 3, @c1, @w1, @rep, 'placed', 'USD', 5, gen_random_uuid(), '2026-10-04 08:00:00+00', NULL, NULL, NULL, NULL, NULL),
                    (@t1, 4, @c1, @w1, @rep, 'cancelled', 'IQD', 500, gen_random_uuid(), '2026-10-04 09:00:00+00', NULL, NULL, NULL, '2026-10-04 10:00:00+00', 'mistake');
                INSERT INTO follow_ups (tenant_id, customer_id, subject, due_date, assignee_id, created_by, status, result_note, completed_at, cancel_reason, cancelled_at, client_ref) VALUES
                    (@t1, @c1, 'open and overdue', '2026-10-02', @rep, @rep, 'pending', NULL, NULL, NULL, NULL, gen_random_uuid()),
                    (@t1, @c1, 'completed', '2026-10-03', @rep, @rep, 'completed', 'done', '2026-10-03 08:00:00+00', NULL, NULL, gen_random_uuid()),
                    (@t1, @c1, 'open, due today', '2026-10-05', @rep, @rep, 'pending', NULL, NULL, NULL, NULL, gen_random_uuid()),
                    (@t1, @c1, 'cancelled', '2026-10-04', @rep, @rep, 'cancelled', NULL, NULL, 'no', '2026-10-04 08:00:00+00', gen_random_uuid()),
                    (@t1, @c1, 'after the range', '2026-10-06', @rep, @rep, 'pending', NULL, NULL, NULL, NULL, gen_random_uuid());
                INSERT INTO alerts (tenant_id, kind, category, severity, subject_user_id, entity_id, dedupe_key, occurred_at, status, handled_at, handled_by, handled_note, handled_outcome) VALUES
                    (@t1, 'gps_mocked', 'security', 'warning', @rep, 'x', 'k1', '2026-10-03 08:00:00+00', 'handled', now(), @m, 'checked', 'fraud'),
                    (@t1, 'followup_overdue', 'operational', 'warning', @rep, 'y', 'k2', '2026-10-04 08:00:00+00', 'new', NULL, NULL, NULL, NULL),
                    (@t1, 'followup_overdue', 'operational', 'warning', @rep2, 'z', 'k3', '2026-10-04 08:00:00+00', 'new', NULL, NULL, NULL, NULL);", conn);
            foreach (var (name, value) in new[] { ("t1", t1), ("rep", rep), ("rep2", rep2), ("sup", sup), ("m", manager), ("f", finance), ("r1", r1), ("r2", r2), ("ta", teamA), ("tb", teamB), ("w1", w1), ("c1", c1), ("c2", c2), ("c3", c3) })
                cmd.Parameters.AddWithValue(name, value);
            for (var i = 0; i < v.Length; i++) cmd.Parameters.AddWithValue($"v{i}", v[i]);
            await cmd.ExecuteNonQueryAsync();
        }

        void As(Guid user) => client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            GenerateToken(t1, user, conf["Jwt:SigningKey"]!, conf["Jwt:Issuer"]!, conf["Jwt:Audience"]!));
        async Task<LifeDrop.Application.Reports.KpiReportDto> Kpis(string query = "") =>
            (await client.GetFromJsonAsync<LifeDrop.Application.Reports.KpiReportDto>("/reports/kpis?from=2026-10-01&to=2026-10-05" + query))!;

        As(finance);
        await AssertProblemAsync(await client.GetAsync("/reports/kpis?from=2026-10-01&to=2026-10-05"), HttpStatusCode.Forbidden, "FORBIDDEN");   // no reports.read

        // The sales manager sees everything: counts with the numerators and denominators of docs/KPI.md
        As(manager);
        var report = await Kpis();
        var row = report.Users.Single(u => u.UserId == rep);
        var c = row.Counts;
        Assert.Equal((6, 3, 1), (c.VisitsPlanned, c.VisitsCompleted, c.VisitsFailed));   // the cancelled visit and the one of 20 September are not counted
        Assert.Equal((3, 2), (c.DelayBase, c.DelayedVisits));                           // on time, late, never started and overdue; the visit of 15:00 is not due yet
        Assert.Equal((2, 1), (c.NewCustomers, c.ApprovedNewCustomers));
        Assert.Equal((3, 1, 1), (c.Orders, c.OrdersDelivered, c.OrdersDeliveryFailed));   // the cancelled order is not counted
        Assert.Equal(new[] { ("IQD", 3000m), ("USD", 5m) }, c.OrderTotals.Select(t => (t.Currency, t.Total)).ToArray());
        Assert.Equal((3, 1), (c.FollowUpBase, c.FollowUpsOverdue));                     // cancelled and later-than-range follow-ups are not counted
        Assert.Equal((50.0, 16.7, 66.7, 50.0, 33.3), (row.Rates.VisitCompletion, row.Rates.VisitFailure, row.Rates.Delay, row.Rates.OrderDelivery, row.Rates.FollowUpOverdue));
        Assert.Equal(new[] { rep, rep2 }.OrderBy(i => i).ToArray(), report.Users.Select(u => u.UserId).OrderBy(i => i).ToArray());   // users without any data are not listed
        Assert.Equal(("Team A", "Team B"), (row.TeamName, report.Users.Single(u => u.UserId == rep2).TeamName));

        // The total adds numerators and denominators of the users (6 + 1 planned, 3 + 1 completed)
        Assert.Equal((7, 4), (report.Total.Counts.VisitsPlanned, report.Total.Counts.VisitsCompleted));
        Assert.Equal(57.1, report.Total.Rates.VisitCompletion);
        Assert.Null(report.Users.Single(u => u.UserId == rep2).Rates.OrderDelivery);   // nothing to measure is no value, never 0 %

        // Alerts by kind with the result of the review; the sales manager may read both categories
        Assert.Equal(new[] { "followup_overdue=2/0", "gps_mocked=1/1" }, report.Alerts.OrderBy(a => a.Kind, StringComparer.Ordinal).Select(a => $"{a.Kind}={a.Total}/{a.Fraud + a.Valid + a.Suspicious}").ToArray());
        Assert.Equal(1, report.Alerts.Single(a => a.Kind == "gps_mocked").Fraud);

        // Filters: customer type, team and region
        Assert.Equal((1, 1), ((await Kpis("&customerType=Doctor")).Total.Counts.VisitsPlanned, (await Kpis("&customerType=Doctor")).Total.Counts.VisitsCompleted));
        Assert.Equal(new[] { rep2 }, (await Kpis($"&teamId={teamB}")).Users.Select(u => u.UserId).ToArray());
        Assert.Equal(new[] { rep }, (await Kpis($"&regionId={r1}")).Users.Select(u => u.UserId).ToArray());
        Assert.Empty((await Kpis($"&teamId={teamA}&regionId={r2}")).Users);   // a team that is not in that region

        // A supervisor sees the users of the region only, and only the alert categories they may read (operational)
        As(sup);
        var region = await Kpis();
        Assert.Equal(new[] { rep }, region.Users.Select(u => u.UserId).ToArray());
        Assert.Equal(new[] { "followup_overdue=1" }, region.Alerts.Select(a => $"{a.Kind}={a.Total}").ToArray());

        // What is refused
        As(manager);
        await AssertProblemAsync(await client.GetAsync("/reports/kpis?from=2026-10-05&to=2026-10-01"), HttpStatusCode.BadRequest, "INVALID_DATA");
        await AssertProblemAsync(await client.GetAsync("/reports/kpis?from=2025-01-01&to=2026-10-05"), HttpStatusCode.BadRequest, "INVALID_DATA");   // more than a year
        await AssertProblemAsync(await client.GetAsync("/reports/kpis?from=2026-10-01"), HttpStatusCode.BadRequest, "BAD_REQUEST");   // the end of the range is missing
    }

    [SkippableFact]
    public async Task RouteHistory_ReadWithinTheAreaAuditedWithStopsEventsCustomerMapAndNinetyDayRetention()
    {
        var url = GetRequiredEnvVar("TEST_DATABASE_URL");
        await VerifyRoleAsync(url, expectElevated: false);

        var (schemaBlock, factory) = await StartFlexiblePlanHostAsync(url);
        await using var _ = schemaBlock;
        await using var __ = factory;
        var conf = factory.Services.GetRequiredService<IConfiguration>();
        var client = factory.CreateClient();

        var t1 = Guid.NewGuid();
        Guid rep = Guid.NewGuid(), rep2 = Guid.NewGuid(), sup = Guid.NewGuid(), manager = Guid.NewGuid(), finance = Guid.NewGuid();
        Guid r1 = Guid.NewGuid(), r2 = Guid.NewGuid(), teamA = Guid.NewGuid(), teamB = Guid.NewGuid();
        Guid c1 = Guid.NewGuid(), c2 = Guid.NewGuid(), v1 = Guid.NewGuid(), d1 = Guid.NewGuid(), d2 = Guid.NewGuid(), s1 = Guid.NewGuid(), s2 = Guid.NewGuid(), sOld = Guid.NewGuid();
        await using (var conn = new NpgsqlConnection(schemaBlock.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(@"
                INSERT INTO tenants (id, name, code) VALUES (@t1, 'T1', 'T1');
                SELECT seed_default_roles(@t1);
                INSERT INTO users (id, tenant_id) VALUES (@rep, @t1), (@rep2, @t1), (@sup, @t1), (@m, @t1), (@f, @t1);
                INSERT INTO user_roles (tenant_id, user_id, role_id)
                SELECT @t1, @rep, id FROM roles WHERE tenant_id = @t1 AND name = 'SalesRep'
                UNION ALL SELECT @t1, @rep2, id FROM roles WHERE tenant_id = @t1 AND name = 'SalesRep'
                UNION ALL SELECT @t1, @sup, id FROM roles WHERE tenant_id = @t1 AND name = 'Supervisor'
                UNION ALL SELECT @t1, @m, id FROM roles WHERE tenant_id = @t1 AND name = 'SalesManager'
                UNION ALL SELECT @t1, @f, id FROM roles WHERE tenant_id = @t1 AND name = 'FinanceManager';
                SELECT set_config('app.current_tenant', @t1::text, false);
                INSERT INTO regions (id, tenant_id, name) VALUES (@r1, @t1, 'Erbil'), (@r2, @t1, 'Baghdad');
                INSERT INTO teams (id, tenant_id, region_id, name) VALUES (@ta, @t1, @r1, 'Team A'), (@tb, @t1, @r2, 'Team B');
                INSERT INTO team_members (tenant_id, user_id, team_id) VALUES (@t1, @rep, @ta), (@t1, @sup, @ta), (@t1, @rep2, @tb);
                INSERT INTO customers (id, tenant_id, type, code, name, geom, status, owner_user_id, region_id, geofence_radius_m) VALUES
                    (@c1, @t1, 'Pharmacy', 'C1', 'Customer 1', ST_SetSRID(ST_MakePoint(44.0092, 36.1911), 4326)::geography, 'located', @rep, @r1, 50),
                    (@c2, @t1, 'Pharmacy', 'C2', 'Customer 2', ST_SetSRID(ST_MakePoint(44.4000, 33.3000), 4326)::geography, 'located', @rep2, @r2, NULL),
                    (gen_random_uuid(), @t1, 'Pharmacy', 'C3', 'No location', NULL, 'no_location', @rep, @r1, NULL);
                INSERT INTO devices (id, tenant_id, user_id, device_label, platform, install_id, app_version, secret_hash, status) VALUES
                    (@d1, @t1, @rep, 'Rep phone', 'android', 'i1', '1.0', '\x00', 'approved'), (@d2, @t1, @rep2, 'Rep2 phone', 'android', 'i2', '1.0', '\x00', 'approved');
                INSERT INTO visits (id, tenant_id, user_id, customer_id, status, schedule_date, created_at) VALUES (@v1, @t1, @rep, @c1, 'Completed', '2026-10-05', now());
                INSERT INTO visit_location_events (id, tenant_id, visit_id, user_id, device_id, kind, accepted, geom, accuracy_m, radius_m, captured_at)
                VALUES (gen_random_uuid(), @t1, @v1, @rep, @d1, 'check_in', true, ST_SetSRID(ST_MakePoint(44.0092, 36.1911), 4326)::geography, 5, 50, '2026-10-05 04:10:00+00');
                INSERT INTO shifts (id, tenant_id, user_id, device_id, started_at, ended_at) VALUES
                    (@s1, @t1, @rep, @d1, '2026-10-05 04:00:00+00', '2026-10-05 05:30:00+00'),
                    (@s2, @t1, @rep2, @d2, '2026-10-05 04:00:00+00', '2026-10-05 05:30:00+00'),
                    (@so, @t1, @rep, @d1, '2026-06-01 04:00:00+00', '2026-06-01 06:00:00+00');
                -- the day of the route: a stop of 5.5 minutes at the customer, then three points while driving, one point the phone says is fake
                INSERT INTO tracking_points (tenant_id, shift_id, user_id, device_id, client_point_id, geom, accuracy_m, captured_at)
                SELECT @t1, @s1, @rep, @d1, gen_random_uuid(), ST_SetSRID(ST_MakePoint(44.0092, 36.1911), 4326)::geography, 8, '2026-10-05 04:10:00+00'::timestamptz + n * interval '30 seconds'
                FROM generate_series(0, 11) n;
                INSERT INTO tracking_points (tenant_id, shift_id, user_id, device_id, client_point_id, geom, accuracy_m, captured_at, is_mocked)
                SELECT @t1, @s1, @rep, @d1, gen_random_uuid(), ST_SetSRID(ST_MakePoint(44.0092 + n * 0.01, 36.1911), 4326)::geography, 8, '2026-10-05 04:17:00+00'::timestamptz + n * interval '30 seconds', n = 3
                FROM generate_series(1, 3) n;
                INSERT INTO tracking_points (tenant_id, shift_id, user_id, device_id, client_point_id, geom, accuracy_m, captured_at) VALUES
                    (@t1, @s2, @rep2, @d2, gen_random_uuid(), ST_SetSRID(ST_MakePoint(44.4, 33.3), 4326)::geography, 8, '2026-10-05 04:10:00+00'),
                    (@t1, @so, @rep, @d1, gen_random_uuid(), ST_SetSRID(ST_MakePoint(44.0, 36.0), 4326)::geography, 8, '2026-06-01 04:10:00+00'),
                    (@t1, @so, @rep, @d1, gen_random_uuid(), ST_SetSRID(ST_MakePoint(44.0, 36.001), 4326)::geography, 8, '2026-06-01 04:20:00+00'),
                    (@t1, @so, @rep, @d1, gen_random_uuid(), ST_SetSRID(ST_MakePoint(44.0, 36.002), 4326)::geography, 8, '2026-06-01 05:20:00+00');", conn);
            foreach (var (name, value) in new[] { ("t1", t1), ("rep", rep), ("rep2", rep2), ("sup", sup), ("m", manager), ("f", finance), ("r1", r1), ("r2", r2), ("ta", teamA), ("tb", teamB), ("c1", c1), ("c2", c2),
                ("v1", v1), ("d1", d1), ("d2", d2), ("s1", s1), ("s2", s2), ("so", sOld) })
                cmd.Parameters.AddWithValue(name, value);
            await cmd.ExecuteNonQueryAsync();
        }

        void As(Guid user) => client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            GenerateToken(t1, user, conf["Jwt:SigningKey"]!, conf["Jwt:Issuer"]!, conf["Jwt:Audience"]!));
        async Task<LifeDrop.Application.Tracking.RouteHistoryDto> Route(Guid user, string date)
        {
            var response = await client.GetAsync($"/tracking/history?userId={user}&date={date}");
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
            return (await response.Content.ReadFromJsonAsync<LifeDrop.Application.Tracking.RouteHistoryDto>())!;
        }

        // Without tracking.read nothing is shown (the finance manager has only tracking.summary)
        As(finance);
        await AssertProblemAsync(await client.GetAsync($"/tracking/history?userId={rep}&date=2026-10-05"), HttpStatusCode.Forbidden, "FORBIDDEN");
        await AssertProblemAsync(await client.GetAsync("/tracking/users"), HttpStatusCode.Forbidden, "FORBIDDEN");

        // The manager reads the whole route of one day: points in order, the stop with its customer, the check-in with the customer's radius
        As(manager);
        Assert.Equal(new[] { rep, rep2 }.OrderBy(i => i).ToArray(), (await client.GetFromJsonAsync<List<LifeDrop.Application.Tracking.TrackableUserDto>>("/tracking/users"))!.Select(u => u.Id).OrderBy(i => i).ToArray());
        var route = await Route(rep, "2026-10-05");
        Assert.Equal(15, route.Points.Count);
        Assert.Equal(route.Points.OrderBy(p => p.CapturedAt).Select(p => p.CapturedAt), route.Points.Select(p => p.CapturedAt));
        Assert.Single(route.Points, p => p.IsMocked);
        var stop = Assert.Single(route.Stops);
        Assert.Equal(("Customer 1", 330), (stop.CustomerName, stop.Seconds));
        Assert.InRange(stop.CustomerDistanceMeters!.Value, 0, 5);
        var checkIn = Assert.Single(route.Events);
        Assert.Equal(("check_in", true, "Customer 1", 50), (checkIn.Kind, checkIn.Accepted, checkIn.CustomerName, checkIn.RadiusMeters));
        var shift = Assert.Single(route.Shifts);
        Assert.Equal((s1, 15, false), (shift.ShiftId, shift.PointCount, shift.PointsPurged));
        Assert.False(route.Truncated);
        Assert.Empty((await Route(rep, "2026-10-04")).Points);   // another day: nothing

        // A supervisor reads only their region; limits of the date and the user
        As(sup);
        Assert.Equal(new[] { rep }, (await client.GetFromJsonAsync<List<LifeDrop.Application.Tracking.TrackableUserDto>>("/tracking/users"))!.Select(u => u.Id).ToArray());
        Assert.Equal(15, (await Route(rep, "2026-10-05")).Points.Count);
        await AssertProblemAsync(await client.GetAsync($"/tracking/history?userId={rep2}&date=2026-10-05"), HttpStatusCode.NotFound, "NOT_FOUND");
        As(manager);
        await AssertProblemAsync(await client.GetAsync($"/tracking/history?userId={rep}&date=2026-10-06"), HttpStatusCode.BadRequest, "INVALID_DATA");   // a day that has not come
        await AssertProblemAsync(await client.GetAsync($"/tracking/history?userId={rep}&date=2026-06-01"), HttpStatusCode.BadRequest, "INVALID_DATA");   // beyond 90 days
        await AssertProblemAsync(await client.GetAsync($"/tracking/history?userId={Guid.NewGuid()}&date=2026-10-05"), HttpStatusCode.NotFound, "NOT_FOUND");

        // The customer map: located customers of the area only, with the check-in radius (the default when the customer has none)
        var map = (await client.GetFromJsonAsync<List<LifeDrop.Application.Tracking.MapCustomerDto>>("/customers/map"))!;
        Assert.Equal(new[] { ("C1", 50), ("C2", 20) }, map.OrderBy(c => c.Code, StringComparer.Ordinal).Select(c => (c.Code, c.GeofenceRadiusMeters)).ToArray());
        As(sup);
        Assert.Equal(new[] { "C1" }, (await client.GetFromJsonAsync<List<LifeDrop.Application.Tracking.MapCustomerDto>>("/customers/map"))!.Select(c => c.Code).ToArray());
        As(rep);
        await AssertProblemAsync(await client.GetAsync($"/tracking/history?userId={rep}&date=2026-10-05"), HttpStatusCode.Forbidden, "FORBIDDEN");   // a visitor cannot read even their own route

        // Every successful read is in the audit log
        await using var verify = new NpgsqlConnection(schemaBlock.ConnectionString);
        await verify.OpenAsync();
        await using (var audit = new NpgsqlCommand("SELECT actor_id, entity_id FROM audit_logs WHERE action = 'tracking.history_viewed' ORDER BY entity_id, actor_id", verify))
        {
            var reads = new List<string>();
            await using var reader = await audit.ExecuteReaderAsync();
            while (await reader.ReadAsync()) reads.Add($"{reader.GetGuid(0) == manager}:{reader.GetString(1)}");
            Assert.Equal(new[] { true, true, false }.Length, reads.Count);   // manager twice (two days), supervisor once
            Assert.Equal(2, reads.Count(r => r.StartsWith("True:")));
        }

        // Retention: the points of a shift that ended more than 90 days ago go, a summary stays; recent points are untouched
        using (var scope = factory.Services.CreateScope())
        {
            var retention = scope.ServiceProvider.GetRequiredService<LifeDrop.Application.Tracking.TrackingRetention>();
            Assert.Equal(3, await retention.PurgeAsync(new LifeDrop.Domain.Common.TenantId(t1), CancellationToken.None));
            Assert.Equal(0, await retention.PurgeAsync(new LifeDrop.Domain.Common.TenantId(t1), CancellationToken.None));
        }
        await using (var check = new NpgsqlCommand("SELECT set_config('app.current_tenant', @t::text, false); SELECT point_count, max_gap_seconds, distance_m, mocked_points FROM shift_summaries WHERE shift_id = @s", verify))
        {
            check.Parameters.AddWithValue("t", t1);
            check.Parameters.AddWithValue("s", sOld);
            await using var reader = await check.ExecuteReaderAsync();
            await reader.NextResultAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal((3, 3600), (reader.GetInt32(0), reader.GetInt32(1)));
            Assert.InRange(reader.GetDecimal(2), 200m, 300m);   // 0.002 degrees of latitude is about 222 m
            Assert.Equal(0, reader.GetInt32(3));
        }
        await using (var left = new NpgsqlCommand("SELECT set_config('app.current_tenant', @t::text, false); SELECT count(*) FROM tracking_points WHERE shift_id = @s", verify))
        {
            left.Parameters.AddWithValue("t", t1);
            left.Parameters.AddWithValue("s", s1);
            await using var reader = await left.ExecuteReaderAsync();
            await reader.NextResultAsync();
            await reader.ReadAsync();
            Assert.Equal(15L, reader.GetInt64(0));
        }
    }

    [SkippableFact]
    public async Task AntiFraud_TrackingSignalsMakeSecurityAlertsWithEvidenceAndNeedAReviewResult()
    {
        var url = GetRequiredEnvVar("TEST_DATABASE_URL");
        await VerifyRoleAsync(url, expectElevated: false);

        var (schemaBlock, factory) = await StartFlexiblePlanHostAsync(url);
        await using var _ = schemaBlock;
        await using var __ = factory;
        var conf = factory.Services.GetRequiredService<IConfiguration>();
        var client = factory.CreateClient();

        var t1 = Guid.NewGuid();
        Guid rep = Guid.NewGuid(), rep2 = Guid.NewGuid(), sup = Guid.NewGuid(), manager = Guid.NewGuid(), company = Guid.NewGuid();
        Guid r1 = Guid.NewGuid(), r2 = Guid.NewGuid(), teamA = Guid.NewGuid(), teamB = Guid.NewGuid();
        Guid c1 = Guid.NewGuid(), vEvent = Guid.NewGuid(), d1 = Guid.NewGuid(), d1b = Guid.NewGuid(), d2 = Guid.NewGuid(), s1 = Guid.NewGuid(), s2 = Guid.NewGuid();
        await using (var conn = new NpgsqlConnection(schemaBlock.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(@"
                INSERT INTO tenants (id, name, code) VALUES (@t1, 'T1', 'T1');
                SELECT seed_default_roles(@t1);
                INSERT INTO users (id, tenant_id) VALUES (@rep, @t1), (@rep2, @t1), (@sup, @t1), (@m, @t1), (@co, @t1);
                INSERT INTO user_roles (tenant_id, user_id, role_id)
                SELECT @t1, @rep, id FROM roles WHERE tenant_id = @t1 AND name = 'SalesRep'
                UNION ALL SELECT @t1, @rep2, id FROM roles WHERE tenant_id = @t1 AND name = 'SalesRep'
                UNION ALL SELECT @t1, @sup, id FROM roles WHERE tenant_id = @t1 AND name = 'Supervisor'
                UNION ALL SELECT @t1, @m, id FROM roles WHERE tenant_id = @t1 AND name = 'SalesManager'
                UNION ALL SELECT @t1, @co, id FROM roles WHERE tenant_id = @t1 AND name = 'CompanyManager';
                SELECT set_config('app.current_tenant', @t1::text, false);
                INSERT INTO regions (id, tenant_id, name) VALUES (@r1, @t1, 'Erbil'), (@r2, @t1, 'Baghdad');
                INSERT INTO teams (id, tenant_id, region_id, name) VALUES (@ta, @t1, @r1, 'Team A'), (@tb, @t1, @r2, 'Team B');
                INSERT INTO team_members (tenant_id, user_id, team_id) VALUES (@t1, @rep, @ta), (@t1, @sup, @ta), (@t1, @rep2, @tb);
                INSERT INTO customers (id, tenant_id, type, code, name, geom, status, owner_user_id, region_id) VALUES
                    (@c1, @t1, 'Pharmacy', 'C1', 'Customer 1', ST_SetSRID(ST_MakePoint(44.0092, 36.1911), 4326)::geography, 'located', @rep, @r1);
                INSERT INTO devices (id, tenant_id, user_id, device_label, platform, install_id, app_version, secret_hash, status) VALUES
                    (@d1, @t1, @rep, 'Rep phone', 'android', 'i1', '1.0', '\x00', 'approved'), (@d1b, @t1, @rep, 'Rep old phone', 'android', 'i1b', '1.0', '\x00', 'approved'),
                    (@d2, @t1, @rep2, 'Rep2 phone', 'android', 'i2', '1.0', '\x00', 'approved');
                INSERT INTO visits (id, tenant_id, user_id, customer_id, status, schedule_date, created_at) VALUES (@ve, @t1, @rep, @c1, 'Planned', '2026-10-05', now());
                INSERT INTO visit_location_events (id, tenant_id, visit_id, user_id, device_id, kind, accepted, rejection_reason, geom, accuracy_m, captured_at, is_mocked)
                VALUES (gen_random_uuid(), @t1, @ve, @rep, @d1, 'check_in', false, 'clock_skew', ST_SetSRID(ST_MakePoint(44.0092, 36.1911), 4326)::geography, 5, '2026-10-05 05:00:00+00', false);
                INSERT INTO shifts (id, tenant_id, user_id, device_id, started_at, ended_at) VALUES
                    (@s1, @t1, @rep, @d1, '2026-10-05 03:00:00+00', '2026-10-05 05:30:00+00'), (@s2, @t1, @rep2, @d2, '2026-10-05 03:00:00+00', '2026-10-05 05:30:00+00');
                -- Shift 1: a normal point, a jump of about 100 km in 10 minutes (and far from every customer), five points with a bad accuracy, a point from another phone, a clock 40 minutes ahead
                INSERT INTO tracking_points (tenant_id, shift_id, user_id, device_id, client_point_id, geom, accuracy_m, captured_at, received_at) VALUES
                    (@t1, @s1, @rep, @d1, gen_random_uuid(), ST_SetSRID(ST_MakePoint(44.0092, 36.1911), 4326)::geography, 10, '2026-10-05 03:10:00+00', '2026-10-05 03:10:05+00'),
                    (@t1, @s1, @rep, @d1, gen_random_uuid(), ST_SetSRID(ST_MakePoint(44.0092, 37.1000), 4326)::geography, 10, '2026-10-05 03:20:00+00', '2026-10-05 03:20:05+00'),
                    (@t1, @s1, @rep, @d1b, gen_random_uuid(), ST_SetSRID(ST_MakePoint(44.0092, 36.1911), 4326)::geography, 10, '2026-10-05 03:40:00+00', '2026-10-05 03:40:05+00'),
                    (@t1, @s1, @rep, @d1, gen_random_uuid(), ST_SetSRID(ST_MakePoint(44.0100, 36.1911), 4326)::geography, 10, '2026-10-05 05:20:00+00', '2026-10-05 04:40:00+00');
                INSERT INTO tracking_points (tenant_id, shift_id, user_id, device_id, client_point_id, geom, accuracy_m, captured_at, received_at)
                SELECT @t1, @s1, @rep, @d1, gen_random_uuid(), ST_SetSRID(ST_MakePoint(44.0092, 36.1911), 4326)::geography, 300, '2026-10-05 04:00:00+00'::timestamptz + n * interval '1 minute', '2026-10-05 04:10:00+00'
                FROM generate_series(1, 5) n;
                -- Shift 2: a clean shift of a user whose region has no located customer (nothing to compare with)
                INSERT INTO tracking_points (tenant_id, shift_id, user_id, device_id, client_point_id, geom, accuracy_m, captured_at, received_at) VALUES
                    (@t1, @s2, @rep2, @d2, gen_random_uuid(), ST_SetSRID(ST_MakePoint(44.4000, 33.3000), 4326)::geography, 10, '2026-10-05 03:10:00+00', '2026-10-05 03:10:05+00'),
                    (@t1, @s2, @rep2, @d2, gen_random_uuid(), ST_SetSRID(ST_MakePoint(44.4010, 33.3000), 4326)::geography, 10, '2026-10-05 03:20:00+00', '2026-10-05 03:20:05+00');", conn);
            foreach (var (name, value) in new[] { ("t1", t1), ("rep", rep), ("rep2", rep2), ("sup", sup), ("m", manager), ("co", company), ("r1", r1), ("r2", r2), ("ta", teamA), ("tb", teamB), ("c1", c1),
                ("ve", vEvent), ("d1", d1), ("d1b", d1b), ("d2", d2), ("s1", s1), ("s2", s2) })
                cmd.Parameters.AddWithValue(name, value);
            await cmd.ExecuteNonQueryAsync();
        }

        void As(Guid user) => client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            GenerateToken(t1, user, conf["Jwt:SigningKey"]!, conf["Jwt:Issuer"]!, conf["Jwt:Audience"]!));
        Task<HttpResponseMessage> Post(string path, object? body = null) => client.PostAsJsonAsync(path, body ?? new { });
        async Task<List<LifeDrop.Application.Alerts.AlertDto>> List(string query = "") => (await client.GetFromJsonAsync<List<LifeDrop.Application.Alerts.AlertDto>>("/alerts" + query))!;

        As(manager);
        Assert.Equal(8, (await (await Post("/alerts/scan")).Content.ReadFromJsonAsync<LifeDrop.Application.Alerts.ScanResultDto>())!.Created);
        Assert.Equal(0, (await (await Post("/alerts/scan")).Content.ReadFromJsonAsync<LifeDrop.Application.Alerts.ScanResultDto>())!.Created);   // one alert per event
        var all = await List("?category=security");   // the two finished sparse shifts also make low_coverage (operational, step 4.8), checked in its own test
        Assert.All(all, a => Assert.Equal(("security", "new"), (a.Category, a.Status)));
        Assert.Equal(new[] { "clock_tamper=2", "device_mismatch=1", "impossible_speed=1", "outside_area=1", "poor_accuracy=1" },
            all.GroupBy(a => a.Kind).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{g.Key}={g.Count()}").ToArray());
        Assert.All(all, a => Assert.NotNull(a.Evidence));
        Assert.All(all.Where(a => a.Kind != "clock_tamper" || a.EntityId == s1.ToString()), a => Assert.Equal(rep, a.SubjectUserId));   // nothing about the user whose shift was clean

        // The evidence is kept: where, when and the computed numbers
        var speed = all.Single(a => a.Kind == "impossible_speed");
        using (var evidence = System.Text.Json.JsonDocument.Parse(speed.Evidence!))
        {
            Assert.True(evidence.RootElement.GetProperty("distanceM").GetDouble() > 90_000);
            Assert.True(evidence.RootElement.GetProperty("speedKmh").GetDouble() > 180);
            Assert.Equal(600, evidence.RootElement.GetProperty("seconds").GetInt32());
        }
        Assert.Equal(s1.ToString(), speed.EntityId);
        using (var evidence = System.Text.Json.JsonDocument.Parse(all.Single(a => a.Kind == "poor_accuracy").Evidence!))
            Assert.Equal(5, evidence.RootElement.GetProperty("points").GetInt32());
        using (var evidence = System.Text.Json.JsonDocument.Parse(all.Single(a => a.Kind == "clock_tamper" && a.EntityId == s1.ToString()).Evidence!))
            Assert.Equal(2400, evidence.RootElement.GetProperty("aheadSeconds").GetInt32());   // captured 05:20, received 04:40
        Assert.Equal(vEvent.ToString(), all.Single(a => a.Kind == "clock_tamper" && a.EntityId != s1.ToString()).EntityId);   // the refused check-in

        // Only security readers see them; a supervisor only reads operational alerts
        As(company);
        Assert.Equal(6, (await List("?category=security")).Count);
        As(sup);
        Assert.Empty(await List("?category=security"));   // alerts.read only: no security audience

        // Human review: the result is needed for these alerts
        As(manager);
        Assert.Equal(HttpStatusCode.NoContent, (await Post($"/alerts/{speed.Id}/seen")).StatusCode);
        await AssertProblemAsync(await Post($"/alerts/{speed.Id}/handle", new { Note = "checked" }), HttpStatusCode.BadRequest, "INVALID_DATA");
        await AssertProblemAsync(await Post($"/alerts/{speed.Id}/handle", new { Note = "checked", Outcome = "guilty" }), HttpStatusCode.BadRequest, "INVALID_DATA");
        Assert.Equal(HttpStatusCode.NoContent, (await Post($"/alerts/{speed.Id}/handle", new { Note = "phone was in a car on the highway", Outcome = "valid" })).StatusCode);
        var reviewed = (await client.GetFromJsonAsync<LifeDrop.Application.Alerts.AlertDto>($"/alerts/{speed.Id}"))!;
        Assert.Equal(("handled", "valid"), (reviewed.Status, reviewed.HandledOutcome));
        Assert.NotNull(reviewed.Evidence);   // the evidence stays after the review
        var poor = all.Single(a => a.Kind == "poor_accuracy");
        Assert.Equal(HttpStatusCode.NoContent, (await Post($"/alerts/{poor.Id}/handle", new { Note = "bad phone", Outcome = "suspicious" })).StatusCode);
        Assert.Equal("suspicious", (await client.GetFromJsonAsync<LifeDrop.Application.Alerts.AlertDto>($"/alerts/{poor.Id}"))!.HandledOutcome);
        Assert.Equal(0, (await (await Post("/alerts/scan")).Content.ReadFromJsonAsync<LifeDrop.Application.Alerts.ScanResultDto>())!.Created);   // reviewed ones are not made again
    }

    [SkippableFact]
    public async Task AntiFraud_VisitSignalsTravelTimeShortStayAndLowCoverage()
    {
        var url = GetRequiredEnvVar("TEST_DATABASE_URL");
        await VerifyRoleAsync(url, expectElevated: false);

        var (schemaBlock, factory) = await StartFlexiblePlanHostAsync(url);
        await using var _ = schemaBlock;
        await using var __ = factory;
        var conf = factory.Services.GetRequiredService<IConfiguration>();
        var client = factory.CreateClient();

        var t1 = Guid.NewGuid();
        Guid rep = Guid.NewGuid(), manager = Guid.NewGuid(), r1 = Guid.NewGuid(), teamA = Guid.NewGuid();
        Guid c1 = Guid.NewGuid(), c2 = Guid.NewGuid(), c3 = Guid.NewGuid(), v1 = Guid.NewGuid(), v2 = Guid.NewGuid(), v3 = Guid.NewGuid(), d1 = Guid.NewGuid(), s1 = Guid.NewGuid(), s2 = Guid.NewGuid();
        await using (var conn = new NpgsqlConnection(schemaBlock.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(@"
                INSERT INTO tenants (id, name, code) VALUES (@t1, 'T1', 'T1');
                SELECT seed_default_roles(@t1);
                INSERT INTO users (id, tenant_id) VALUES (@rep, @t1), (@m, @t1);
                INSERT INTO user_roles (tenant_id, user_id, role_id)
                SELECT @t1, @rep, id FROM roles WHERE tenant_id = @t1 AND name = 'SalesRep'
                UNION ALL SELECT @t1, @m, id FROM roles WHERE tenant_id = @t1 AND name = 'SalesManager';
                SELECT set_config('app.current_tenant', @t1::text, false);
                INSERT INTO regions (id, tenant_id, name) VALUES (@r1, @t1, 'Erbil');
                INSERT INTO teams (id, tenant_id, region_id, name) VALUES (@ta, @t1, @r1, 'Team A');
                INSERT INTO team_members (tenant_id, user_id, team_id) VALUES (@t1, @rep, @ta);
                INSERT INTO customers (id, tenant_id, type, code, name, geom, status, owner_user_id, region_id) VALUES
                    (@c1, @t1, 'Pharmacy', 'C1', 'Customer 1', ST_SetSRID(ST_MakePoint(44.0092, 36.1911), 4326)::geography, 'located', @rep, @r1),
                    (@c2, @t1, 'Pharmacy', 'C2', 'Customer 2', ST_SetSRID(ST_MakePoint(44.0092, 36.6000), 4326)::geography, 'located', @rep, @r1),
                    (@c3, @t1, 'Pharmacy', 'C3', 'Customer 3', ST_SetSRID(ST_MakePoint(44.0100, 36.6000), 4326)::geography, 'located', @rep, @r1);
                INSERT INTO devices (id, tenant_id, user_id, device_label, platform, install_id, app_version, secret_hash, status) VALUES
                    (@d1, @t1, @rep, 'Rep phone', 'android', 'i1', '1.0', '\x00', 'approved');
                INSERT INTO visits (id, tenant_id, user_id, customer_id, status, schedule_date, created_at) VALUES
                    (@v1, @t1, @rep, @c1, 'Planned', '2026-10-05', now()), (@v2, @t1, @rep, @c2, 'Planned', '2026-10-05', now()), (@v3, @t1, @rep, @c3, 'Planned', '2026-10-05', now());
                -- Visit 1: a normal stay (30 minutes). Visit 2: arrives 10 minutes later at a customer about 45 km away, leaves after 1 minute. Visit 3: a normal stay nearby.
                INSERT INTO visit_location_events (id, tenant_id, visit_id, user_id, device_id, kind, accepted, geom, accuracy_m, captured_at, is_mocked) VALUES
                    (gen_random_uuid(), @t1, @v1, @rep, @d1, 'check_in', true, ST_SetSRID(ST_MakePoint(44.0092, 36.1911), 4326)::geography, 5, '2026-10-05 05:00:00+00', false),
                    (gen_random_uuid(), @t1, @v1, @rep, @d1, 'check_out', true, ST_SetSRID(ST_MakePoint(44.0092, 36.1911), 4326)::geography, 5, '2026-10-05 05:30:00+00', false),
                    (gen_random_uuid(), @t1, @v2, @rep, @d1, 'check_in', true, ST_SetSRID(ST_MakePoint(44.0092, 36.6000), 4326)::geography, 5, '2026-10-05 05:40:00+00', false),
                    (gen_random_uuid(), @t1, @v2, @rep, @d1, 'check_out', true, ST_SetSRID(ST_MakePoint(44.0092, 36.6000), 4326)::geography, 5, '2026-10-05 05:41:00+00', false),
                    (gen_random_uuid(), @t1, @v3, @rep, @d1, 'check_in', true, ST_SetSRID(ST_MakePoint(44.0100, 36.6000), 4326)::geography, 5, '2026-10-05 06:30:00+00', false),
                    (gen_random_uuid(), @t1, @v3, @rep, @d1, 'check_out', true, ST_SetSRID(ST_MakePoint(44.0100, 36.6000), 4326)::geography, 5, '2026-10-05 07:00:00+00', false);
                -- Shift 1: 4 hours with only 10 points (expected 80). Shift 2: 4 hours with 80 points. Both finished.
                INSERT INTO shifts (id, tenant_id, user_id, device_id, started_at, ended_at) VALUES
                    (@s1, @t1, @rep, @d1, '2026-10-05 04:00:00+00', '2026-10-05 08:00:00+00');
                INSERT INTO tracking_points (tenant_id, shift_id, user_id, device_id, client_point_id, geom, accuracy_m, captured_at, received_at)
                SELECT @t1, @s1, @rep, @d1, gen_random_uuid(), ST_SetSRID(ST_MakePoint(44.0092, 36.1911), 4326)::geography, 10, '2026-10-05 04:00:00+00'::timestamptz + n * interval '20 minutes', '2026-10-05 04:00:00+00'::timestamptz + n * interval '20 minutes'
                FROM generate_series(1, 10) n;", conn);
            foreach (var (name, value) in new[] { ("t1", t1), ("rep", rep), ("m", manager), ("r1", r1), ("ta", teamA), ("c1", c1), ("c2", c2), ("c3", c3), ("v1", v1), ("v2", v2), ("v3", v3), ("d1", d1), ("s1", s1) })
                cmd.Parameters.AddWithValue(name, value);
            await cmd.ExecuteNonQueryAsync();
        }

        void As(Guid user) => client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            GenerateToken(t1, user, conf["Jwt:SigningKey"]!, conf["Jwt:Issuer"]!, conf["Jwt:Audience"]!));
        Task<HttpResponseMessage> Post(string path, object? body = null) => client.PostAsJsonAsync(path, body ?? new { });

        As(manager);
        Assert.Equal(3, (await (await Post("/alerts/scan")).Content.ReadFromJsonAsync<LifeDrop.Application.Alerts.ScanResultDto>())!.Created);
        Assert.Equal(0, (await (await Post("/alerts/scan")).Content.ReadFromJsonAsync<LifeDrop.Application.Alerts.ScanResultDto>())!.Created);   // one alert per event
        var all = (await client.GetFromJsonAsync<List<LifeDrop.Application.Alerts.AlertDto>>("/alerts"))!;
        Assert.Equal(new[] { "impossible_travel=1", "low_coverage=1", "short_stay=1" },
            all.GroupBy(a => a.Kind).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{g.Key}={g.Count()}").ToArray());
        Assert.All(all, a => Assert.Equal(rep, a.SubjectUserId));

        var travel = all.Single(a => a.Kind == "impossible_travel");
        Assert.Equal(("security", v2.ToString()), (travel.Category, travel.EntityId));   // the visit that was reached too fast (visit 3 is a normal stay, 59 minutes after leaving visit 2)
        using (var evidence = System.Text.Json.JsonDocument.Parse(travel.Evidence!))
        {
            Assert.True(evidence.RootElement.GetProperty("distanceM").GetDouble() > 40_000);
            Assert.True(evidence.RootElement.GetProperty("speedKmh").GetDouble() > 180);
            Assert.Equal(600, evidence.RootElement.GetProperty("seconds").GetInt32());   // from the check-out at 05:30 to the check-in at 05:40
        }
        var shortStay = all.Single(a => a.Kind == "short_stay");
        Assert.Equal(("security", v2.ToString()), (shortStay.Category, shortStay.EntityId));
        using (var evidence = System.Text.Json.JsonDocument.Parse(shortStay.Evidence!))
            Assert.Equal(60, evidence.RootElement.GetProperty("seconds").GetInt32());
        var coverage = all.Single(a => a.Kind == "low_coverage");
        Assert.Equal(("operational", s1.ToString()), (coverage.Category, coverage.EntityId));
        using (var evidence = System.Text.Json.JsonDocument.Parse(coverage.Evidence!))
        {
            Assert.Equal(10, evidence.RootElement.GetProperty("points").GetInt32());
            Assert.Equal(80, evidence.RootElement.GetProperty("expectedPoints").GetInt32());
        }

        // The review result is needed for the behaviour signals, not for the coverage alert
        await AssertProblemAsync(await Post($"/alerts/{travel.Id}/handle", new { Note = "checked" }), HttpStatusCode.BadRequest, "INVALID_DATA");
        Assert.Equal(HttpStatusCode.NoContent, (await Post($"/alerts/{travel.Id}/handle", new { Note = "customer address was wrong", Outcome = "valid" })).StatusCode);
        await AssertProblemAsync(await Post($"/alerts/{coverage.Id}/handle", new { Note = "phone battery", Outcome = "valid" }), HttpStatusCode.BadRequest, "INVALID_DATA");
        Assert.Equal(HttpStatusCode.NoContent, (await Post($"/alerts/{coverage.Id}/handle", new { Note = "phone battery" })).StatusCode);
        Assert.Equal(0, (await (await Post("/alerts/scan")).Content.ReadFromJsonAsync<LifeDrop.Application.Alerts.ScanResultDto>())!.Created);
    }

    private sealed class ChannelBotFake : LifeDrop.Application.Channels.ITelegramBot
    {
        public bool IsConfigured => true;
        public string? BotUsername => "lifedrop_bot";
        public LifeDrop.Application.Channels.ChannelOutcome Next { get; set; } = LifeDrop.Application.Channels.ChannelOutcome.Sent;
        public List<(string Chat, string Text)> Sent { get; } = new();
        public Task<LifeDrop.Application.Channels.ChannelOutcome> SendAsync(string chatId, string text, CancellationToken cancellationToken)
        {
            Sent.Add((chatId, text));
            return Task.FromResult(Next);
        }
        public Task<IReadOnlyList<LifeDrop.Application.Channels.TelegramUpdate>?> GetUpdatesAsync(long offset, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<LifeDrop.Application.Channels.TelegramUpdate>?>(null);
    }

    private sealed class ChannelTextsFake : LifeDrop.Application.Channels.IChannelTexts
    {
        public string Get(string key, params (string Name, object Value)[] vars) => vars.Length == 0 ? key : key + ":" + string.Join(",", vars.Select(v => v.Name + "=" + v.Value));
    }

    [SkippableFact]
    public async Task Channels_TelegramLinkNeedsTheOneTimeCodeFromAPrivateChatThenMessagesGoAndTheLinkCanBeDropped()
    {
        // Plan step 3.4, D17: optional channels. The fake bot stands for Telegram; the test drives the background job itself (Channels:IntervalSeconds = 0).
        var url = GetRequiredEnvVar("TEST_DATABASE_URL");
        await VerifyRoleAsync(url, expectElevated: false);

        var (schemaBlock, baseFactory) = await StartFlexiblePlanHostAsync(url);
        await using var _ = schemaBlock;
        await using var __ = baseFactory;
        var bot = new ChannelBotFake();
        await using var factory = baseFactory.WithWebHostBuilder(webBuilder =>
        {
            webBuilder.UseSetting("Channels:IntervalSeconds", "0");
            webBuilder.ConfigureTestServices(services =>
            {
                services.AddSingleton<LifeDrop.Application.Channels.ITelegramBot>(bot);
                services.AddSingleton<LifeDrop.Application.Channels.IChannelTexts>(new ChannelTextsFake());
            });
        });
        var conf = factory.Services.GetRequiredService<IConfiguration>();
        var client = factory.CreateClient();

        var t1 = Guid.NewGuid();
        Guid staff = Guid.NewGuid(), other = Guid.NewGuid(), actor = Guid.NewGuid(), region = Guid.NewGuid(), c1 = Guid.NewGuid();
        await using (var conn = new NpgsqlConnection(schemaBlock.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(@"
                INSERT INTO tenants (id, name, code) VALUES (@t1, 'T1', 'T1');
                INSERT INTO users (id, tenant_id) VALUES (@staff, @t1), (@other, @t1), (@actor, @t1);
                SELECT set_config('app.current_tenant', @t1::text, false);
                INSERT INTO regions (id, tenant_id, name) VALUES (@r1, @t1, 'Erbil');
                INSERT INTO customers (id, tenant_id, type, code, name, geom, status, owner_user_id, region_id) VALUES
                    (@c1, @t1, 'Pharmacy', 'P-1', 'Shifa', ST_SetSRID(ST_MakePoint(44.0092, 36.1911), 4326)::geography, 'located', @staff, @r1);", conn);
            foreach (var (name, value) in new[] { ("t1", t1), ("staff", staff), ("other", other), ("actor", actor), ("r1", region), ("c1", c1) })
                cmd.Parameters.AddWithValue(name, value);
            await cmd.ExecuteNonQueryAsync();
        }

        async Task<T> Sql<T>(string sql, params (string Name, object Value)[] args)
        {
            await using var conn = new NpgsqlConnection(schemaBlock.ConnectionString);
            await conn.OpenAsync();
            await using (var set = new NpgsqlCommand("SELECT set_config('app.current_tenant', @t1::text, false)", conn))
            {
                set.Parameters.AddWithValue("t1", t1);
                await set.ExecuteNonQueryAsync();
            }
            await using var query = new NpgsqlCommand(sql, conn);
            query.Parameters.AddWithValue("t1", t1);
            foreach (var (name, value) in args) query.Parameters.AddWithValue(name, value);
            var result = await query.ExecuteScalarAsync();
            return result is null or DBNull ? default! : (T)result;
        }
        Task<long> AddNotification(Guid user) => Sql<long>(@"
            WITH x AS (INSERT INTO notifications (tenant_id, user_id, kind, entity_id, actor_id) VALUES (@t1, @user, 'customer.profile_changed', @c1::text, @actor) RETURNING 1)
            SELECT count(*) FROM x", ("user", user), ("c1", c1), ("actor", actor));
        Task<long> UnsentCount() => Sql<long>("SELECT count(*) FROM notifications WHERE channel_sent_at IS NULL");

        void As(Guid user) => client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            GenerateToken(t1, user, conf["Jwt:SigningKey"]!, conf["Jwt:Issuer"]!, conf["Jwt:Audience"]!));
        async Task<LifeDrop.Application.Channels.ChannelStatus> Status() => (await client.GetFromJsonAsync<LifeDrop.Application.Channels.ChannelStatus>("/channels"))!;
        async Task<LifeDrop.Application.Channels.TelegramLink> StartLink()
        {
            var response = await client.PostAsync("/channels/telegram/link", null);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<LifeDrop.Application.Channels.TelegramLink>())!;
        }
        long updateId = 0;
        async Task Update(string chat, bool isPrivate, string text)
        {
            using var scope = factory.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<LifeDrop.Application.Channels.ChannelLinkService>()
                .HandleUpdateAsync(new LifeDrop.Application.Channels.TelegramUpdate(++updateId, chat, isPrivate, text), CancellationToken.None);
        }
        async Task<int> Dispatch()
        {
            using var scope = factory.Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<LifeDrop.Application.Channels.ChannelDispatcher>()
                .DispatchTenantAsync(new LifeDrop.Domain.Common.TenantId(t1), CancellationToken.None);
        }

        // Everybody sees only their own status; the bot is on
        As(staff);
        var status = await Status();
        Assert.Equal((true, "lifedrop_bot", false), (status.Enabled, status.BotUsername, status.TelegramLinked));

        // The code is the company code plus a random part; only its hash is stored
        var link = await StartLink();
        Assert.StartsWith("t1_", link.Code);
        Assert.Equal("t1_".Length + LifeDrop.Application.Channels.ChannelLinkService.RandomLength, link.Code.Length);
        Assert.Equal(32, await Sql<int>("SELECT octet_length(code_hash) FROM channel_link_codes WHERE user_id = @u", ("u", staff)));
        Assert.Equal(0L, await Sql<long>("SELECT count(*) FROM channel_link_codes WHERE code_hash = convert_to(@code, 'UTF8')", ("code", link.Code)));

        // A group chat and other talk do nothing; a wrong code is answered as invalid; the right code from a private chat links
        await Update("-100", false, "/start " + link.Code);
        await Update("777", true, "hello");
        Assert.Empty(bot.Sent);
        Assert.False((await Status()).TelegramLinked);
        await Update("777", true, "/start t1_zzzzzzzzzzzz");
        Assert.Equal(("777", "msg.channel.linkInvalid"), bot.Sent.Last());
        Assert.False((await Status()).TelegramLinked);
        await Update("777", true, "/start " + link.Code);
        Assert.Equal(("777", "msg.channel.linked"), bot.Sent.Last());
        Assert.True((await Status()).TelegramLinked);
        await Update("888", true, "/start " + link.Code);   // the code works once
        Assert.Equal(("888", "msg.channel.linkInvalid"), bot.Sent.Last());
        Assert.Equal("777", await Sql<string>("SELECT address FROM user_channels WHERE user_id = @u", ("u", staff)));
        As(other);
        Assert.False((await Status()).TelegramLinked);   // nobody else is linked by it

        // Messages: the linked user gets one with the customer; the user without a link gets nothing and is not left waiting
        await AddNotification(staff);
        await AddNotification(other);
        var sentBefore = bot.Sent.Count;
        Assert.Equal(1, await Dispatch());
        Assert.Equal(("777", "msg.channel.customerProfileChanged:name=Shifa,code=P-1"), bot.Sent[sentBefore]);
        Assert.Equal(sentBefore + 1, bot.Sent.Count);
        Assert.Equal(0L, await UnsentCount());
        Assert.Equal(0, await Dispatch());   // each message once

        // Unlinking: only the own link; the second time there is nothing to remove
        As(staff);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/channels/telegram/unlink", null)).StatusCode);
        await AssertProblemAsync(await client.PostAsync("/channels/telegram/unlink", null), HttpStatusCode.NotFound, "NOT_FOUND");
        Assert.False((await Status()).TelegramLinked);

        // A chat Telegram says is gone loses its link, and its message is done
        var again = await StartLink();
        await Update("999", true, "/start " + again.Code);
        Assert.True((await Status()).TelegramLinked);
        bot.Next = LifeDrop.Application.Channels.ChannelOutcome.InvalidAddress;
        await AddNotification(staff);
        Assert.Equal(0, await Dispatch());
        Assert.False((await Status()).TelegramLinked);
        Assert.Equal(0L, await UnsentCount());

        Assert.Equal("channel.telegram_linked=2,channel.telegram_unlinked=1", await Sql<string>(@"
            SELECT string_agg(action || '=' || n, ',' ORDER BY action COLLATE ""C"")
            FROM (SELECT action, count(*) AS n FROM audit_logs WHERE tenant_id = @t1 AND action LIKE 'channel.%' GROUP BY action) a"));
    }

    [SkippableFact]
    public async Task Alerts_ScanMakesOneAlertPerEventSeparatesSecurityAndRespectsTheArea()
    {
        var url = GetRequiredEnvVar("TEST_DATABASE_URL");
        await VerifyRoleAsync(url, expectElevated: false);

        var (schemaBlock, factory) = await StartFlexiblePlanHostAsync(url);
        await using var _ = schemaBlock;
        await using var __ = factory;
        var conf = factory.Services.GetRequiredService<IConfiguration>();
        var client = factory.CreateClient();

        var t1 = Guid.NewGuid();
        Guid rep = Guid.NewGuid(), rep2 = Guid.NewGuid(), sup = Guid.NewGuid(), manager = Guid.NewGuid(), company = Guid.NewGuid();
        Guid r1 = Guid.NewGuid(), r2 = Guid.NewGuid(), teamA = Guid.NewGuid(), teamB = Guid.NewGuid();
        Guid c1 = Guid.NewGuid(), c2 = Guid.NewGuid(), vLate = Guid.NewGuid(), vOnTime = Guid.NewGuid(), vIn = Guid.NewGuid(), d1 = Guid.NewGuid(), d2 = Guid.NewGuid();
        await using (var conn = new NpgsqlConnection(schemaBlock.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(@"
                INSERT INTO tenants (id, name, code) VALUES (@t1, 'T1', 'T1');
                SELECT seed_default_roles(@t1);
                INSERT INTO users (id, tenant_id) VALUES (@rep, @t1), (@rep2, @t1), (@sup, @t1), (@m, @t1), (@co, @t1);
                INSERT INTO user_roles (tenant_id, user_id, role_id)
                SELECT @t1, @rep, id FROM roles WHERE tenant_id = @t1 AND name = 'SalesRep'
                UNION ALL SELECT @t1, @rep2, id FROM roles WHERE tenant_id = @t1 AND name = 'SalesRep'
                UNION ALL SELECT @t1, @sup, id FROM roles WHERE tenant_id = @t1 AND name = 'Supervisor'
                UNION ALL SELECT @t1, @m, id FROM roles WHERE tenant_id = @t1 AND name = 'SalesManager'
                UNION ALL SELECT @t1, @co, id FROM roles WHERE tenant_id = @t1 AND name = 'CompanyManager';
                SELECT set_config('app.current_tenant', @t1::text, false);
                INSERT INTO regions (id, tenant_id, name) VALUES (@r1, @t1, 'Erbil'), (@r2, @t1, 'Baghdad');
                INSERT INTO teams (id, tenant_id, region_id, name) VALUES (@ta, @t1, @r1, 'Team A'), (@tb, @t1, @r2, 'Team B');
                INSERT INTO team_members (tenant_id, user_id, team_id) VALUES (@t1, @rep, @ta), (@t1, @sup, @ta), (@t1, @rep2, @tb);
                INSERT INTO customers (id, tenant_id, type, code, name, geom, status, owner_user_id) VALUES
                    (@c1, @t1, 'Pharmacy', 'C1', 'Customer 1', ST_SetSRID(ST_MakePoint(44.0092, 36.1911), 4326)::geography, 'located', @rep),
                    (@c2, @t1, 'Pharmacy', 'C2', 'Customer 2', ST_SetSRID(ST_MakePoint(44.0192, 36.1911), 4326)::geography, 'located', @rep2);
                INSERT INTO devices (id, tenant_id, user_id, device_label, platform, install_id, app_version, secret_hash, status) VALUES
                    (@d1, @t1, @rep, 'Rep phone', 'android', 'i1', '1.0', '\x00', 'approved'), (@d2, @t1, @rep2, 'New phone of rep2', 'android', 'i2', '1.0', '\x00', 'pending');
                INSERT INTO visits (id, tenant_id, user_id, customer_id, status, schedule_date, planned_time, created_at) VALUES
                    (@vl, @t1, @rep, @c1, 'Planned', '2026-10-05', '08:00', now()),
                    (@vo, @t1, @rep, @c1, 'Planned', '2026-10-05', '08:45', now()),
                    (@vi, @t1, @rep, @c1, 'InVisit', '2026-10-05', NULL, now());
                INSERT INTO visit_status_history (tenant_id, visit_id, to_status, occurred_at) VALUES (@t1, @vi, 'InVisit', '2026-10-05 04:00:00+00');
                INSERT INTO visit_location_events (id, tenant_id, visit_id, user_id, device_id, kind, accepted, rejection_reason, geom, accuracy_m, captured_at, is_mocked)
                VALUES (gen_random_uuid(), @t1, @vi, @rep, @d1, 'check_in', false, 'mock_location', ST_SetSRID(ST_MakePoint(44.0092, 36.1911), 4326)::geography, 5, '2026-10-05 05:00:00+00', true);
                INSERT INTO shifts (id, tenant_id, user_id, device_id, started_at) VALUES (gen_random_uuid(), @t1, @rep2, @d2, '2026-10-05 04:00:00+00');
                INSERT INTO follow_ups (tenant_id, customer_id, subject, due_date, assignee_id, created_by, client_ref) VALUES
                    (@t1, @c1, 'Old promise', '2026-10-01', @rep, @rep, gen_random_uuid()),
                    (@t1, @c2, 'Old promise 2', '2026-10-02', @rep2, @rep2, gen_random_uuid()),
                    (@t1, @c1, 'Due today', '2026-10-05', @rep, @rep, gen_random_uuid());", conn);
            foreach (var (name, value) in new[] { ("t1", t1), ("rep", rep), ("rep2", rep2), ("sup", sup), ("m", manager), ("co", company), ("r1", r1), ("r2", r2), ("ta", teamA), ("tb", teamB), ("c1", c1), ("c2", c2),
                ("vl", vLate), ("vo", vOnTime), ("vi", vIn), ("d1", d1), ("d2", d2) })
                cmd.Parameters.AddWithValue(name, value);
            await cmd.ExecuteNonQueryAsync();
        }

        void As(Guid user) => client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            GenerateToken(t1, user, conf["Jwt:SigningKey"]!, conf["Jwt:Issuer"]!, conf["Jwt:Audience"]!));
        Task<HttpResponseMessage> Post(string path, object? body = null) => client.PostAsJsonAsync(path, body ?? new { });
        async Task<List<LifeDrop.Application.Alerts.AlertDto>> List(string query = "") => (await client.GetFromJsonAsync<List<LifeDrop.Application.Alerts.AlertDto>>("/alerts" + query))!;
        async Task<int> NewCount() => (await client.GetFromJsonAsync<LifeDrop.Application.Alerts.AlertCountDto>("/alerts/count"))!.New;

        // Visitors have no alerts; the scan itself needs alerts.handle
        As(rep);
        await AssertProblemAsync(await client.GetAsync("/alerts"), HttpStatusCode.Forbidden, "FORBIDDEN");
        await AssertProblemAsync(await Post("/alerts/scan"), HttpStatusCode.Forbidden, "FORBIDDEN");
        As(company);
        await AssertProblemAsync(await Post("/alerts/scan"), HttpStatusCode.Forbidden, "FORBIDDEN");   // reads, does not handle

        // One scan makes one alert per event; running it again makes none
        As(manager);
        var scan = (await (await Post("/alerts/scan")).Content.ReadFromJsonAsync<LifeDrop.Application.Alerts.ScanResultDto>())!;
        Assert.Equal(7, scan.Created);   // 2 follow-ups, 1 late visit, 1 long stay, 1 tracking gap, 1 new device, 1 fake position
        Assert.Equal(0, (await (await Post("/alerts/scan")).Content.ReadFromJsonAsync<LifeDrop.Application.Alerts.ScanResultDto>())!.Created);
        var all = await List();
        Assert.Equal(new[] { "followup_overdue=2", "gps_mocked=1", "new_device=1", "tracking_gap=1", "visit_delayed=1", "visit_long_stay=1" },
            all.GroupBy(a => a.Kind).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{g.Key}={g.Count()}").ToArray());
        Assert.All(all, a => Assert.Equal("new", a.Status));
        Assert.Equal(("operational", 5), ("operational", all.Count(a => a.Category == "operational")));
        Assert.Equal(new[] { "gps_mocked", "new_device" }, (await List("?category=security")).Select(a => a.Kind).OrderBy(k => k, StringComparer.Ordinal).ToArray());
        var longStay = all.Single(a => a.Kind == "visit_long_stay");
        Assert.Equal(("report_missing", vIn.ToString(), c1), (longStay.Detail, longStay.EntityId, longStay.CustomerId));
        Assert.Equal(vLate.ToString(), all.Single(a => a.Kind == "visit_delayed").EntityId);   // the 08:45 visit is not late yet
        Assert.Equal(7, await NewCount());
        await AssertProblemAsync(await client.GetAsync("/alerts?category=weird"), HttpStatusCode.BadRequest, "INVALID_DATA");

        // The company manager sees everything; the supervisor only the operational alerts of the region
        As(company);
        Assert.Equal(7, (await List()).Count);
        As(sup);
        var mine = await List();
        Assert.Equal(new[] { "followup_overdue", "visit_delayed", "visit_long_stay" }, mine.Select(a => a.Kind).OrderBy(k => k, StringComparer.Ordinal).ToArray());
        Assert.Equal(3, await NewCount());
        Assert.Empty(await List("?category=security"));
        var otherRegion = all.Single(a => a.Kind == "tracking_gap");
        var security = all.First(a => a.Category == "security");
        await AssertProblemAsync(await client.GetAsync($"/alerts/{otherRegion.Id}"), HttpStatusCode.NotFound, "NOT_FOUND");
        await AssertProblemAsync(await client.GetAsync($"/alerts/{security.Id}"), HttpStatusCode.NotFound, "NOT_FOUND");
        await AssertProblemAsync(await Post($"/alerts/{security.Id}/handle", new { Note = "x" }), HttpStatusCode.NotFound, "NOT_FOUND");

        // Status: new -> seen -> handled with a note; handled alerts are not made again
        var first = mine.First(a => a.Kind == "followup_overdue");
        Assert.Equal(HttpStatusCode.NoContent, (await Post($"/alerts/{first.Id}/seen")).StatusCode);
        await AssertProblemAsync(await Post($"/alerts/{first.Id}/seen"), HttpStatusCode.Conflict, "CONFLICT");
        Assert.Equal(2, await NewCount());
        await AssertProblemAsync(await Post($"/alerts/{first.Id}/handle", new { Note = " " }), HttpStatusCode.BadRequest, "INVALID_DATA");
        Assert.Equal(HttpStatusCode.NoContent, (await Post($"/alerts/{first.Id}/handle", new { Note = " called the pharmacy " })).StatusCode);
        await AssertProblemAsync(await Post($"/alerts/{first.Id}/handle", new { Note = "again" }), HttpStatusCode.Conflict, "CONFLICT");
        var handled = (await client.GetFromJsonAsync<LifeDrop.Application.Alerts.AlertDto>($"/alerts/{first.Id}"))!;
        Assert.Equal(("handled", "called the pharmacy"), (handled.Status, handled.HandledNote));
        Assert.NotNull(handled.SeenAt);
        Assert.Equal("handled", (await List()).Last().Status);   // handled ones come last
        Assert.Equal(0, (await (await Post("/alerts/scan")).Content.ReadFromJsonAsync<LifeDrop.Application.Alerts.ScanResultDto>())!.Created);

        // The manager handles a security alert; a new event makes a new alert
        As(manager);
        Assert.Equal(HttpStatusCode.NoContent, (await Post($"/alerts/{security.Id}/handle", new { Note = "phone approved" })).StatusCode);
        await using (var conn = new NpgsqlConnection(schemaBlock.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand("SELECT set_config('app.current_tenant', @t::text, false); INSERT INTO follow_ups (tenant_id, customer_id, subject, due_date, assignee_id, created_by, client_ref) VALUES (@t, @c, 'Another old one', '2026-10-03', @r, @r, gen_random_uuid())", conn);
            cmd.Parameters.AddWithValue("t", t1);
            cmd.Parameters.AddWithValue("c", c1);
            cmd.Parameters.AddWithValue("r", rep);
            await cmd.ExecuteNonQueryAsync();
        }
        Assert.Equal(1, (await (await Post("/alerts/scan")).Content.ReadFromJsonAsync<LifeDrop.Application.Alerts.ScanResultDto>())!.Created);

        await using var verify = new NpgsqlConnection(schemaBlock.ConnectionString);
        await verify.OpenAsync();
        await using var audit = new NpgsqlCommand("SELECT action, count(*) FROM audit_logs WHERE action LIKE 'alert.%' GROUP BY action ORDER BY action", verify);
        var actions = new List<string>();
        await using (var reader = await audit.ExecuteReaderAsync())
            while (await reader.ReadAsync()) actions.Add($"{reader.GetString(0)}={reader.GetInt64(1)}");
        Assert.Equal(new[] { "alert.handled=2", "alert.seen=1" }, actions);
    }

    [SkippableFact]
    public async Task FollowUps_VisitorCreatesForSelfManagerAssignsAndStatusIsChangedByTheResponsibleUser()
    {
        var url = GetRequiredEnvVar("TEST_DATABASE_URL");
        await VerifyRoleAsync(url, expectElevated: false);

        var (schemaBlock, factory) = await StartFlexiblePlanHostAsync(url);
        await using var _ = schemaBlock;
        await using var __ = factory;
        var conf = factory.Services.GetRequiredService<IConfiguration>();
        var client = factory.CreateClient();

        var t1 = Guid.NewGuid();
        Guid rep = Guid.NewGuid(), rep2 = Guid.NewGuid(), manager = Guid.NewGuid(), company = Guid.NewGuid(), driver = Guid.NewGuid();
        Guid c1 = Guid.NewGuid(), c2 = Guid.NewGuid(), oldOne = Guid.NewGuid();
        await using (var conn = new NpgsqlConnection(schemaBlock.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(@"
                INSERT INTO tenants (id, name, code) VALUES (@t1, 'T1', 'T1');
                SELECT seed_default_roles(@t1);
                INSERT INTO users (id, tenant_id) VALUES (@rep, @t1), (@rep2, @t1), (@m, @t1), (@co, @t1), (@d, @t1);
                INSERT INTO user_roles (tenant_id, user_id, role_id)
                SELECT @t1, @rep, id FROM roles WHERE tenant_id = @t1 AND name = 'SalesRep'
                UNION ALL SELECT @t1, @rep2, id FROM roles WHERE tenant_id = @t1 AND name = 'SalesRep'
                UNION ALL SELECT @t1, @m, id FROM roles WHERE tenant_id = @t1 AND name = 'SalesManager'
                UNION ALL SELECT @t1, @co, id FROM roles WHERE tenant_id = @t1 AND name = 'CompanyManager'
                UNION ALL SELECT @t1, @d, id FROM roles WHERE tenant_id = @t1 AND name = 'Driver';
                SELECT set_config('app.current_tenant', @t1::text, false);
                INSERT INTO customers (id, tenant_id, type, code, name, geom, status, owner_user_id) VALUES
                    (@c1, @t1, 'Pharmacy', 'C1', 'Customer 1', ST_SetSRID(ST_MakePoint(44.0092, 36.1911), 4326)::geography, 'located', @rep),
                    (@c2, @t1, 'Pharmacy', 'C2', 'Customer 2', ST_SetSRID(ST_MakePoint(44.0192, 36.1911), 4326)::geography, 'located', @rep2);
                INSERT INTO follow_ups (id, tenant_id, customer_id, subject, due_date, assignee_id, created_by, client_ref)
                VALUES (@old, @t1, @c1, 'Old promise', '2026-10-01', @rep, @rep, gen_random_uuid());", conn);
            foreach (var (name, value) in new[] { ("t1", t1), ("rep", rep), ("rep2", rep2), ("m", manager), ("co", company), ("d", driver), ("c1", c1), ("c2", c2), ("old", oldOne) })
                cmd.Parameters.AddWithValue(name, value);
            await cmd.ExecuteNonQueryAsync();
        }

        void As(Guid user) => client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            GenerateToken(t1, user, conf["Jwt:SigningKey"]!, conf["Jwt:Issuer"]!, conf["Jwt:Audience"]!));
        Task<HttpResponseMessage> Post(string path, object? body = null) => client.PostAsJsonAsync(path, body ?? new { });
        async Task<List<LifeDrop.Application.FollowUps.FollowUpDto>> List(string query = "") => (await client.GetFromJsonAsync<List<LifeDrop.Application.FollowUps.FollowUpDto>>("/followups" + query))!;
        async Task<LifeDrop.Application.FollowUps.FollowUpDto> Get(Guid id) => (await client.GetFromJsonAsync<LifeDrop.Application.FollowUps.FollowUpDto>($"/followups/{id}"))!;
        async Task<Guid> Create(object body) => (await (await Post("/followups", body)).Content.ReadFromJsonAsync<LifeDrop.Application.FollowUps.CreatedFollowUpDto>())!.Id;

        As(driver);
        await AssertProblemAsync(await Post("/followups", new { CustomerId = c1, Subject = "x", DueDate = "2026-10-10", ClientRef = Guid.NewGuid() }), HttpStatusCode.Forbidden, "FORBIDDEN");
        await AssertProblemAsync(await client.GetAsync("/followups"), HttpStatusCode.Forbidden, "FORBIDDEN");

        // A visitor creates a follow-up for themselves; a retry gives the same one
        As(rep);
        var ref1 = Guid.NewGuid();
        var first = await Post("/followups", new { CustomerId = c1, Subject = " Call about the order ", DueDate = "2026-10-12", ClientRef = ref1 });
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var f1 = (await first.Content.ReadFromJsonAsync<LifeDrop.Application.FollowUps.CreatedFollowUpDto>())!.Id;
        var again = await Post("/followups", new { CustomerId = c1, Subject = " Call about the order ", DueDate = "2026-10-12", ClientRef = ref1 });
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(f1, (await again.Content.ReadFromJsonAsync<LifeDrop.Application.FollowUps.CreatedFollowUpDto>())!.Id);
        var detail = await Get(f1);
        Assert.Equal(("pending", "Call about the order", "C1", rep, rep, false), (detail.Status, detail.Subject, detail.CustomerCode, detail.AssigneeId, detail.CreatedBy, detail.IsOverdue));

        // What is refused
        foreach (var bad in new object[]
        {
            new { CustomerId = c1, Subject = " ", DueDate = "2026-10-12", ClientRef = Guid.NewGuid() },
            new { CustomerId = c1, Subject = new string('x', 201), DueDate = "2026-10-12", ClientRef = Guid.NewGuid() },
            new { CustomerId = c1, Subject = "x", DueDate = "2026-10-04", ClientRef = Guid.NewGuid() },       // before today (2026-10-05)
            new { CustomerId = c1, Subject = "x", ClientRef = Guid.NewGuid() },
            new { Subject = "x", DueDate = "2026-10-12", ClientRef = Guid.NewGuid() },
            new { CustomerId = c1, Subject = "x", DueDate = "2026-10-12" }
        })
            await AssertProblemAsync(await Post("/followups", bad), HttpStatusCode.BadRequest, "INVALID_DATA");
        await AssertProblemAsync(await Post("/followups", new { CustomerId = c2, Subject = "x", DueDate = "2026-10-12", ClientRef = Guid.NewGuid() }), HttpStatusCode.NotFound, "NOT_FOUND");   // not the rep's customer
        await AssertProblemAsync(await Post("/followups", new { CustomerId = c1, Subject = "x", DueDate = "2026-10-12", AssigneeId = rep2, ClientRef = Guid.NewGuid() }), HttpStatusCode.Forbidden, "FORBIDDEN");   // only a manager gives it to others

        // A manager gives one to a visitor; a user who does not exist is not found
        As(manager);
        await AssertProblemAsync(await Post("/followups", new { CustomerId = c2, Subject = "x", DueDate = "2026-10-12", AssigneeId = Guid.NewGuid(), ClientRef = Guid.NewGuid() }), HttpStatusCode.NotFound, "NOT_FOUND");
        var f2 = await Create(new { CustomerId = c2, Subject = "Collect the signed form", DueDate = "2026-10-05", AssigneeId = rep2, ClientRef = Guid.NewGuid() });
        var f3 = await Create(new { CustomerId = c1, Subject = "Visit again", DueDate = "2026-10-20", AssigneeId = rep, ClientRef = Guid.NewGuid() });

        // Visibility: own and created-by-me; the company manager and the sales manager see all; overdue is derived from the due day
        As(rep);
        Assert.Equal(new[] { "Old promise", "Call about the order", "Visit again" }, (await List()).Select(f => f.Subject).ToArray());
        Assert.Equal(new[] { "Old promise" }, (await List("?overdue=true")).Select(f => f.Subject).ToArray());
        Assert.True((await Get(oldOne)).IsOverdue);
        await AssertProblemAsync(await client.GetAsync($"/followups/{f2}"), HttpStatusCode.NotFound, "NOT_FOUND");
        As(rep2);
        Assert.Equal(new[] { f2 }, (await List()).Select(f => f.Id).ToArray());
        Assert.False((await Get(f2)).IsOverdue);   // due today is not overdue
        As(company);
        Assert.Equal(4, (await List()).Count);
        Assert.Equal(new[] { f2 }, (await List($"?customerId={c2}&status=pending")).Select(f => f.Id).ToArray());
        await AssertProblemAsync(await client.GetAsync("/followups?status=weird"), HttpStatusCode.BadRequest, "INVALID_DATA");
        await AssertProblemAsync(await Post("/followups", new { CustomerId = c1, Subject = "x", DueDate = "2026-10-12", ClientRef = Guid.NewGuid() }), HttpStatusCode.Forbidden, "FORBIDDEN");

        // Status: only the responsible user; in order; completing needs a result note
        As(rep);
        await AssertProblemAsync(await Post($"/followups/{f2}/start"), HttpStatusCode.NotFound, "NOT_FOUND");   // cannot even see it
        As(manager);
        await AssertProblemAsync(await Post($"/followups/{f2}/start"), HttpStatusCode.Forbidden, "FORBIDDEN");   // sees it, but it is not theirs
        As(rep2);
        Assert.Equal(HttpStatusCode.NoContent, (await Post($"/followups/{f2}/start")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Post($"/followups/{f2}/start")).StatusCode);   // the same step sent again is not a conflict (2.13); nothing is written twice
        Assert.Equal("in_progress", (await Get(f2)).Status);
        await AssertProblemAsync(await Post($"/followups/{f2}/complete", new { ResultNote = " " }), HttpStatusCode.BadRequest, "INVALID_DATA");
        Assert.Equal(HttpStatusCode.NoContent, (await Post($"/followups/{f2}/complete", new { ResultNote = " got the form " })).StatusCode);
        var done = await Get(f2);
        Assert.Equal(("completed", "got the form"), (done.Status, done.ResultNote));
        await AssertProblemAsync(await Post($"/followups/{f2}/complete", new { ResultNote = "again" }), HttpStatusCode.Conflict, "CONFLICT");
        await AssertProblemAsync(await Post($"/followups/{f2}/cancel", new { Reason = "late" }), HttpStatusCode.Forbidden, "FORBIDDEN");   // not the creator, not a manager

        // A completed follow-up is never overdue; cancel: the creator or a manager, with a reason
        As(rep);
        await AssertProblemAsync(await Post($"/followups/{f3}/cancel", new { Reason = "x" }), HttpStatusCode.Forbidden, "FORBIDDEN");   // made by the manager for the visitor
        Assert.Equal(HttpStatusCode.NoContent, (await Post($"/followups/{f1}/cancel", new { Reason = " not needed " })).StatusCode);
        await AssertProblemAsync(await Post($"/followups/{f1}/cancel", new { Reason = "again" }), HttpStatusCode.Conflict, "CONFLICT");
        await AssertProblemAsync(await Post($"/followups/{f3}/reassign", new { AssigneeId = rep2 }), HttpStatusCode.Forbidden, "FORBIDDEN");
        As(manager);
        await AssertProblemAsync(await Post($"/followups/{f3}/cancel", new { Reason = " " }), HttpStatusCode.BadRequest, "INVALID_DATA");
        Assert.Equal(HttpStatusCode.NoContent, (await Post($"/followups/{f3}/cancel", new { Reason = "customer closed" })).StatusCode);
        Assert.Equal(("cancelled", "customer closed"), ((await Get(f3)).Status, (await Get(f3)).CancelReason));

        // Reassign: only a manager, to somebody in the area, only while open
        Assert.Equal(HttpStatusCode.NoContent, (await Post($"/followups/{oldOne}/reassign", new { AssigneeId = rep2 })).StatusCode);
        await AssertProblemAsync(await Post($"/followups/{oldOne}/reassign", new { AssigneeId = Guid.NewGuid() }), HttpStatusCode.NotFound, "NOT_FOUND");
        await AssertProblemAsync(await Post($"/followups/{oldOne}/reassign", new { }), HttpStatusCode.BadRequest, "INVALID_DATA");
        await AssertProblemAsync(await Post($"/followups/{f1}/reassign", new { AssigneeId = rep2 }), HttpStatusCode.Conflict, "CONFLICT");
        As(rep2);
        Assert.Equal(new[] { "Old promise" }, (await List("?overdue=true")).Select(f => f.Subject).ToArray());

        await using var verify = new NpgsqlConnection(schemaBlock.ConnectionString);
        await verify.OpenAsync();
        await using var audit = new NpgsqlCommand("SELECT action, count(*) FROM audit_logs WHERE action LIKE 'followup.%' GROUP BY action ORDER BY action", verify);
        var actions = new List<string>();
        await using (var reader = await audit.ExecuteReaderAsync())
            while (await reader.ReadAsync()) actions.Add($"{reader.GetString(0)}={reader.GetInt64(1)}");
        Assert.Equal(new[] { "followup.cancelled=2", "followup.completed=1", "followup.created=3", "followup.reassigned=1", "followup.started=1" }, actions);
    }

    [SkippableFact]
    public async Task VisitDetails_SpecialisedFormByVisitTypeOnlyForTheAssigneeWhileInVisit()
    {
        var url = GetRequiredEnvVar("TEST_DATABASE_URL");
        await VerifyRoleAsync(url, expectElevated: false);

        var (schemaBlock, factory) = await StartFlexiblePlanHostAsync(url);
        await using var _ = schemaBlock;
        await using var __ = factory;
        var conf = factory.Services.GetRequiredService<IConfiguration>();
        var client = factory.CreateClient();

        var t1 = Guid.NewGuid();
        Guid rep = Guid.NewGuid(), rep2 = Guid.NewGuid(), manager = Guid.NewGuid(), driver = Guid.NewGuid();
        Guid c1 = Guid.NewGuid(), p1 = Guid.NewGuid(), p2 = Guid.NewGuid(), p3 = Guid.NewGuid();
        Guid vDoctor = Guid.NewGuid(), vPharmacy = Guid.NewGuid(), vWholesaler = Guid.NewGuid(), vOther = Guid.NewGuid(), vPlanned = Guid.NewGuid();
        await using (var conn = new NpgsqlConnection(schemaBlock.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(@"
                INSERT INTO tenants (id, name, code) VALUES (@t1, 'T1', 'T1');
                SELECT seed_default_roles(@t1);
                INSERT INTO users (id, tenant_id) VALUES (@rep, @t1), (@rep2, @t1), (@m, @t1), (@d, @t1);
                INSERT INTO user_roles (tenant_id, user_id, role_id)
                SELECT @t1, @rep, id FROM roles WHERE tenant_id = @t1 AND name = 'SalesRep'
                UNION ALL SELECT @t1, @rep2, id FROM roles WHERE tenant_id = @t1 AND name = 'SalesRep'
                UNION ALL SELECT @t1, @m, id FROM roles WHERE tenant_id = @t1 AND name = 'SalesManager'
                UNION ALL SELECT @t1, @d, id FROM roles WHERE tenant_id = @t1 AND name = 'Driver';
                SELECT set_config('app.current_tenant', @t1::text, false);
                INSERT INTO customers (id, tenant_id, type, code, name, geom, status, owner_user_id) VALUES
                    (@c1, @t1, 'Pharmacy', 'C1', 'Customer 1', ST_SetSRID(ST_MakePoint(44.0092, 36.1911), 4326)::geography, 'located', @rep);
                INSERT INTO products (id, tenant_id, code, name, unit, currency, base_price, status) VALUES
                    (@p1, @t1, 'P1', 'Product 1', 'box', 'IQD', 1000, 'active'), (@p2, @t1, 'P2', 'Product 2', 'box', 'IQD', 2000, 'active'), (@p3, @t1, 'P3', 'Product 3', 'box', 'IQD', 3000, 'inactive');
                INSERT INTO visits (id, tenant_id, user_id, customer_id, status, schedule_date, visit_type, created_at) VALUES
                    (@vd, @t1, @rep, @c1, 'InVisit', '2026-10-05', 'doctor', now()), (@vp, @t1, @rep, @c1, 'InVisit', '2026-10-05', 'pharmacy', now()),
                    (@vw, @t1, @rep, @c1, 'InVisit', '2026-10-05', 'wholesaler', now()), (@vo, @t1, @rep, @c1, 'InVisit', '2026-10-05', 'other', now()),
                    (@vpl, @t1, @rep, @c1, 'Planned', '2026-10-05', 'doctor', now());", conn);
            foreach (var (name, value) in new[] { ("t1", t1), ("rep", rep), ("rep2", rep2), ("m", manager), ("d", driver), ("c1", c1), ("p1", p1), ("p2", p2), ("p3", p3), ("vd", vDoctor), ("vp", vPharmacy), ("vw", vWholesaler), ("vo", vOther), ("vpl", vPlanned) })
                cmd.Parameters.AddWithValue(name, value);
            await cmd.ExecuteNonQueryAsync();
        }

        void As(Guid user) => client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            GenerateToken(t1, user, conf["Jwt:SigningKey"]!, conf["Jwt:Issuer"]!, conf["Jwt:Audience"]!));
        Task<HttpResponseMessage> Put(Guid visit, object body) => client.PutAsJsonAsync($"/visits/{visit}/report/details", body);
        Task<LifeDrop.Application.Visits.VisitDetailsDto?> Get(Guid visit) => client.GetFromJsonAsync<LifeDrop.Application.Visits.VisitDetailsDto>($"/visits/{visit}/report/details");

        // A doctor visit: common fields and the doctor's own
        As(rep);
        await AssertProblemAsync(await client.GetAsync($"/visits/{vDoctor}/report/details"), HttpStatusCode.NotFound, "NOT_FOUND");   // nothing saved yet
        Assert.Equal(HttpStatusCode.NoContent, (await Put(vDoctor, new
        {
            Purpose = "product_intro", PersonMet = " Dr. Karwan ", PersonRole = "Cardiologist", Needs = "  samples of P1 ", ProductIds = new[] { p2, p1 }, Specialty = "Cardiology", DailyPatients = 40
        })).StatusCode);
        var saved = (await Get(vDoctor))!;
        Assert.Equal(("doctor", "product_intro", "Dr. Karwan", "samples of P1", "Cardiology", 40), (saved.VisitType, saved.Purpose, saved.PersonMet, saved.Needs, saved.Specialty, saved.DailyPatients));
        Assert.Equal(new[] { "P1", "P2" }, saved.Products.Select(p => p.Code).ToArray());
        Assert.Null(saved.MonthlySales);

        // A second save replaces everything
        Assert.Equal(HttpStatusCode.NoContent, (await Put(vDoctor, new { Purpose = "complaint" })).StatusCode);
        var replaced = (await Get(vDoctor))!;
        Assert.Equal(("complaint", null, null, 0), (replaced.Purpose, replaced.PersonMet, replaced.Specialty, replaced.Products.Count));

        // Pharmacy and wholesaler forms, with the currency that goes with the amount
        Assert.Equal(HttpStatusCode.NoContent, (await Put(vPharmacy, new { Purpose = "order_follow_up", MonthlySales = 1500000m, Currency = "IQD", Competitors = "Pharma X, Pharma Y" })).StatusCode);
        var pharmacy = (await Get(vPharmacy))!;
        Assert.Equal(("pharmacy", 1500000m, "IQD", "Pharma X, Pharma Y"), (pharmacy.VisitType, pharmacy.MonthlySales, pharmacy.Currency, pharmacy.Competitors));
        Assert.Equal(HttpStatusCode.NoContent, (await Put(vWholesaler, new { Purpose = "collection", MonthlyVolume = 20000m, Currency = "USD", PaymentTerms = "30 days" })).StatusCode);
        Assert.Equal(("USD", "30 days"), ((await Get(vWholesaler))!.Currency, (await Get(vWholesaler))!.PaymentTerms));
        Assert.Equal(HttpStatusCode.NoContent, (await Put(vOther, new { Purpose = "other", PersonMet = "Someone" })).StatusCode);

        // What is refused: another type's fields, a bad purpose, an amount without its currency, products that are inactive, repeated or unknown, too long texts
        foreach (var (visit, bad) in new (Guid, object)[]
        {
            (vDoctor, new { Purpose = "product_intro", MonthlySales = 10m, Currency = "IQD" }),
            (vDoctor, new { Purpose = "product_intro", Competitors = "X" }),
            (vPharmacy, new { Purpose = "product_intro", Specialty = "Cardiology" }),
            (vPharmacy, new { Purpose = "product_intro", MonthlyVolume = 10m, Currency = "IQD" }),
            (vWholesaler, new { Purpose = "product_intro", DailyPatients = 3 }),
            (vOther, new { Purpose = "product_intro", Specialty = "Cardiology" }),
            (vDoctor, new { Purpose = "lunch" }),
            (vDoctor, new { PersonMet = "No purpose" }),
            (vPharmacy, new { Purpose = "collection", MonthlySales = 10m }),
            (vPharmacy, new { Purpose = "collection", Currency = "IQD" }),
            (vPharmacy, new { Purpose = "collection", MonthlySales = -1m, Currency = "IQD" }),
            (vPharmacy, new { Purpose = "collection", MonthlySales = 10m, Currency = "EUR" }),
            (vDoctor, new { Purpose = "product_intro", DailyPatients = -1 }),
            (vDoctor, new { Purpose = "product_intro", ProductIds = new[] { p3 } }),
            (vDoctor, new { Purpose = "product_intro", ProductIds = new[] { p1, p1 } }),
            (vDoctor, new { Purpose = "product_intro", ProductIds = new[] { Guid.NewGuid() } }),
            (vDoctor, new { Purpose = "product_intro", Needs = new string('x', 1001) }),
            (vDoctor, new { Purpose = "product_intro", PersonMet = new string('x', 101) })
        })
            await AssertProblemAsync(await Put(visit, bad), HttpStatusCode.BadRequest, "INVALID_DATA");
        Assert.Equal("complaint", (await Get(vDoctor))!.Purpose);   // nothing refused changed it

        // Only while InVisit
        await AssertProblemAsync(await Put(vPlanned, new { Purpose = "other" }), HttpStatusCode.BadRequest, "INVALID_STATE");

        // Only the assignee writes; readers: the assignee and a manager whose scope covers them
        As(rep2);
        await AssertProblemAsync(await Put(vDoctor, new { Purpose = "other" }), HttpStatusCode.Forbidden, "FORBIDDEN");
        await AssertProblemAsync(await client.GetAsync($"/visits/{vDoctor}/report/details"), HttpStatusCode.NotFound, "NOT_FOUND");
        As(driver);
        await AssertProblemAsync(await client.GetAsync($"/visits/{vDoctor}/report/details"), HttpStatusCode.NotFound, "NOT_FOUND");
        As(manager);
        Assert.Equal("complaint", (await Get(vDoctor))!.Purpose);
        await AssertProblemAsync(await Put(vDoctor, new { Purpose = "other" }), HttpStatusCode.Forbidden, "FORBIDDEN");

        await using var verify = new NpgsqlConnection(schemaBlock.ConnectionString);
        await verify.OpenAsync();
        await using var audit = new NpgsqlCommand("SELECT count(*) FROM audit_logs WHERE action = 'visit.report_details_saved'", verify);
        Assert.Equal(5L, await audit.ExecuteScalarAsync());
    }

    [SkippableFact]
    public async Task Samples_RepRecordsForOwnCustomerWithChecksAndManagerVoidsWithReason()
    {
        var url = GetRequiredEnvVar("TEST_DATABASE_URL");
        await VerifyRoleAsync(url, expectElevated: false);

        var (schemaBlock, factory) = await StartFlexiblePlanHostAsync(url);
        await using var _ = schemaBlock;
        await using var __ = factory;
        var conf = factory.Services.GetRequiredService<IConfiguration>();
        var client = factory.CreateClient();

        var t1 = Guid.NewGuid();
        Guid sci = Guid.NewGuid(), rep2 = Guid.NewGuid(), manager = Guid.NewGuid(), company = Guid.NewGuid(), driver = Guid.NewGuid();
        Guid c1 = Guid.NewGuid(), c2 = Guid.NewGuid(), p1 = Guid.NewGuid(), p2 = Guid.NewGuid(), v1 = Guid.NewGuid(), v2 = Guid.NewGuid();
        await using (var conn = new NpgsqlConnection(schemaBlock.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(@"
                INSERT INTO tenants (id, name, code) VALUES (@t1, 'T1', 'T1');
                SELECT seed_default_roles(@t1);
                INSERT INTO users (id, tenant_id) VALUES (@s, @t1), (@rep2, @t1), (@m, @t1), (@co, @t1), (@d, @t1);
                INSERT INTO user_roles (tenant_id, user_id, role_id)
                SELECT @t1, @s, id FROM roles WHERE tenant_id = @t1 AND name = 'ScientificRep'
                UNION ALL SELECT @t1, @rep2, id FROM roles WHERE tenant_id = @t1 AND name = 'SalesRep'
                UNION ALL SELECT @t1, @m, id FROM roles WHERE tenant_id = @t1 AND name = 'SalesManager'
                UNION ALL SELECT @t1, @co, id FROM roles WHERE tenant_id = @t1 AND name = 'CompanyManager'
                UNION ALL SELECT @t1, @d, id FROM roles WHERE tenant_id = @t1 AND name = 'Driver';
                SELECT set_config('app.current_tenant', @t1::text, false);
                INSERT INTO customers (id, tenant_id, type, code, name, geom, status, owner_user_id) VALUES
                    (@c1, @t1, 'Pharmacy', 'C1', 'Customer 1', ST_SetSRID(ST_MakePoint(44.0092, 36.1911), 4326)::geography, 'located', @s),
                    (@c2, @t1, 'Pharmacy', 'C2', 'Customer 2', ST_SetSRID(ST_MakePoint(44.0192, 36.1911), 4326)::geography, 'located', @rep2);
                INSERT INTO products (id, tenant_id, code, name, unit, currency, base_price, status) VALUES
                    (@p1, @t1, 'P1', 'Product 1', 'box', 'IQD', 1000, 'active'), (@p2, @t1, 'P2', 'Product 2', 'box', 'IQD', 2000, 'inactive');
                INSERT INTO visits (id, tenant_id, user_id, customer_id, status, schedule_date, created_at) VALUES
                    (@v1, @t1, @s, @c1, 'Planned', '2026-10-05', now()), (@v2, @t1, @rep2, @c2, 'Planned', '2026-10-05', now());", conn);
            foreach (var (name, value) in new[] { ("t1", t1), ("s", sci), ("rep2", rep2), ("m", manager), ("co", company), ("d", driver), ("c1", c1), ("c2", c2), ("p1", p1), ("p2", p2), ("v1", v1), ("v2", v2) })
                cmd.Parameters.AddWithValue(name, value);
            await cmd.ExecuteNonQueryAsync();
        }

        void As(Guid user) => client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            GenerateToken(t1, user, conf["Jwt:SigningKey"]!, conf["Jwt:Issuer"]!, conf["Jwt:Audience"]!));
        Task<HttpResponseMessage> Post(string path, object? body = null) => client.PostAsJsonAsync(path, body ?? new { });

        // The driver cannot record or read; the company manager reads but cannot record
        As(driver);
        await AssertProblemAsync(await Post("/samples", new { CustomerId = c1, ProductId = p1, Quantity = 1m, ClientRef = Guid.NewGuid() }), HttpStatusCode.Forbidden, "FORBIDDEN");
        await AssertProblemAsync(await client.GetAsync("/samples"), HttpStatusCode.Forbidden, "FORBIDDEN");
        As(company);
        await AssertProblemAsync(await Post("/samples", new { CustomerId = c1, ProductId = p1, Quantity = 1m, ClientRef = Guid.NewGuid() }), HttpStatusCode.Forbidden, "FORBIDDEN");
        Assert.Empty((await client.GetFromJsonAsync<List<LifeDrop.Application.Samples.SampleDto>>("/samples"))!);

        // The scientific rep records for their own customer, in a visit, with batch, expiry and position
        As(sci);
        var ref1 = Guid.NewGuid();
        object Sample(Guid cr, decimal qty = 3m, object? extra = null) => new
        {
            CustomerId = c1, VisitId = v1, ProductId = p1, Quantity = qty, RecipientName = " Dr. Karwan ", BatchNumber = " B-2026-17 ", ExpiryDate = "2027-03-31", Note = "  ", ClientRef = cr,
            Location = new { Latitude = 36.1912, Longitude = 44.0093, AccuracyMeters = 8.0, IsMocked = false }
        };
        var first = await Post("/samples", Sample(ref1));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var created = (await first.Content.ReadFromJsonAsync<LifeDrop.Application.Samples.CreatedSampleDto>())!;
        Assert.Equal(("S-000001", false), (created.Number, created.Replayed));
        var again = await Post("/samples", Sample(ref1));
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal((created.Id, true), ((await again.Content.ReadFromJsonAsync<LifeDrop.Application.Samples.CreatedSampleDto>())!.Id, true));
        var detail = (await client.GetFromJsonAsync<LifeDrop.Application.Samples.SampleDto>($"/samples/{created.Id}"))!;
        Assert.Equal(("C1", "P1", 3m, "Dr. Karwan", "B-2026-17", v1, "given"), (detail.CustomerCode, detail.ProductCode, detail.Quantity, detail.RecipientName, detail.BatchNumber, detail.VisitId, detail.Status));
        Assert.Equal(new DateOnly(2027, 3, 31), detail.ExpiryDate);
        Assert.Null(detail.Note);
        As(rep2);
        await AssertProblemAsync(await Post("/samples", Sample(ref1)), HttpStatusCode.NotFound, "NOT_FOUND");   // customer outside the area (checked before the retry lookup)

        // What is refused
        As(sci);
        foreach (var bad in new object[]
        {
            new { CustomerId = c1, ProductId = p1, Quantity = 0m, ClientRef = Guid.NewGuid() },
            new { CustomerId = c1, ProductId = p1, Quantity = 1.0001m, ClientRef = Guid.NewGuid() },
            new { CustomerId = c1, ProductId = p1, Quantity = 1m },
            new { ProductId = p1, Quantity = 1m, ClientRef = Guid.NewGuid() },
            new { CustomerId = c1, Quantity = 1m, ClientRef = Guid.NewGuid() },
            new { CustomerId = c1, ProductId = p1, Quantity = 1m, ExpiryDate = "2026-10-04", ClientRef = Guid.NewGuid() },       // expired (today is 2026-10-05)
            new { CustomerId = c1, ProductId = p1, Quantity = 1m, BatchNumber = new string('x', 51), ClientRef = Guid.NewGuid() },
            new { CustomerId = c1, ProductId = p1, Quantity = 1m, Location = new { Latitude = 100.0, Longitude = 44.0 }, ClientRef = Guid.NewGuid() },
            new { CustomerId = c1, ProductId = p2, Quantity = 1m, ClientRef = Guid.NewGuid() }                                   // inactive product
        })
            await AssertProblemAsync(await Post("/samples", bad), HttpStatusCode.BadRequest, "INVALID_DATA");
        await AssertProblemAsync(await Post("/samples", new { CustomerId = c1, ProductId = p1, Quantity = 1m, Location = new { Latitude = 36.1, Longitude = 44.0, IsMocked = true }, ClientRef = Guid.NewGuid() }), HttpStatusCode.UnprocessableEntity, "GPS_MOCKED");
        await AssertProblemAsync(await Post("/samples", new { CustomerId = c2, ProductId = p1, Quantity = 1m, ClientRef = Guid.NewGuid() }), HttpStatusCode.NotFound, "NOT_FOUND");
        await AssertProblemAsync(await Post("/samples", new { CustomerId = c1, ProductId = Guid.NewGuid(), Quantity = 1m, ClientRef = Guid.NewGuid() }), HttpStatusCode.NotFound, "NOT_FOUND");
        await AssertProblemAsync(await Post("/samples", new { CustomerId = c1, VisitId = v2, ProductId = p1, Quantity = 1m, ClientRef = Guid.NewGuid() }), HttpStatusCode.NotFound, "NOT_FOUND");   // someone else's visit
        // Without a visit, batch, expiry or position is fine
        var plain = await Post("/samples", new { CustomerId = c1, ProductId = p1, Quantity = 0.5m, ClientRef = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.Created, plain.StatusCode);

        // Visibility: a recorder sees their own; another rep sees nothing; the managers see all
        Assert.Equal(2, (await client.GetFromJsonAsync<List<LifeDrop.Application.Samples.SampleDto>>("/samples"))!.Count);
        As(rep2);
        Assert.Empty((await client.GetFromJsonAsync<List<LifeDrop.Application.Samples.SampleDto>>("/samples"))!);
        await AssertProblemAsync(await client.GetAsync($"/samples/{created.Id}"), HttpStatusCode.NotFound, "NOT_FOUND");
        As(company);
        Assert.Equal(2, (await client.GetFromJsonAsync<List<LifeDrop.Application.Samples.SampleDto>>($"/samples?customerId={c1}&productId={p1}"))!.Count);
        Assert.Empty((await client.GetFromJsonAsync<List<LifeDrop.Application.Samples.SampleDto>>($"/samples?customerId={c2}"))!);

        // Voiding: only the sales manager, with a reason, once
        await AssertProblemAsync(await Post($"/samples/{created.Id}/void", new { Reason = "x" }), HttpStatusCode.Forbidden, "FORBIDDEN");
        As(sci);
        await AssertProblemAsync(await Post($"/samples/{created.Id}/void", new { Reason = "x" }), HttpStatusCode.Forbidden, "FORBIDDEN");
        As(manager);
        await AssertProblemAsync(await Post($"/samples/{created.Id}/void", new { Reason = " " }), HttpStatusCode.BadRequest, "INVALID_DATA");
        await AssertProblemAsync(await Post($"/samples/{Guid.NewGuid()}/void", new { Reason = "x" }), HttpStatusCode.NotFound, "NOT_FOUND");
        Assert.Equal(HttpStatusCode.NoContent, (await Post($"/samples/{created.Id}/void", new { Reason = "typed twice" })).StatusCode);
        await AssertProblemAsync(await Post($"/samples/{created.Id}/void", new { Reason = "again" }), HttpStatusCode.Conflict, "CONFLICT");
        var voided = (await client.GetFromJsonAsync<LifeDrop.Application.Samples.SampleDto>($"/samples/{created.Id}"))!;
        Assert.Equal(("voided", "typed twice"), (voided.Status, voided.VoidReason));

        await using var verify = new NpgsqlConnection(schemaBlock.ConnectionString);
        await verify.OpenAsync();
        await using var audit = new NpgsqlCommand("SELECT action, count(*) FROM audit_logs WHERE action LIKE 'sample.%' GROUP BY action ORDER BY action", verify);
        var actions = new List<string>();
        await using (var reader = await audit.ExecuteReaderAsync())
            while (await reader.ReadAsync()) actions.Add($"{reader.GetString(0)}={reader.GetInt64(1)}");
        Assert.Equal(new[] { "sample.recorded=2", "sample.voided=1" }, actions);
    }

    [SkippableFact]
    public async Task Payments_VisitorCollectsFinanceConfirmsVoidsAndCorrectsWithHistoryAndBalance()
    {
        var url = GetRequiredEnvVar("TEST_DATABASE_URL");
        await VerifyRoleAsync(url, expectElevated: false);

        var (schemaBlock, factory) = await StartFlexiblePlanHostAsync(url);
        await using var _ = schemaBlock;
        await using var __ = factory;
        var conf = factory.Services.GetRequiredService<IConfiguration>();
        var client = factory.CreateClient();

        var t1 = Guid.NewGuid();
        Guid finance = Guid.NewGuid(), rep = Guid.NewGuid(), rep2 = Guid.NewGuid(), manager = Guid.NewGuid(), worker = Guid.NewGuid(), driver = Guid.NewGuid();
        Guid w1 = Guid.NewGuid(), c1 = Guid.NewGuid(), c2 = Guid.NewGuid(), p1 = Guid.NewGuid(), p2 = Guid.NewGuid();
        await using (var conn = new NpgsqlConnection(schemaBlock.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(@"
                INSERT INTO tenants (id, name, code) VALUES (@t1, 'T1', 'T1');
                SELECT seed_default_roles(@t1);
                INSERT INTO users (id, tenant_id) VALUES (@f, @t1), (@rep, @t1), (@rep2, @t1), (@m, @t1), (@w, @t1), (@d, @t1);
                INSERT INTO user_roles (tenant_id, user_id, role_id)
                SELECT @t1, @f, id FROM roles WHERE tenant_id = @t1 AND name = 'FinanceManager'
                UNION ALL SELECT @t1, @rep, id FROM roles WHERE tenant_id = @t1 AND name = 'SalesRep'
                UNION ALL SELECT @t1, @rep2, id FROM roles WHERE tenant_id = @t1 AND name = 'SalesRep'
                UNION ALL SELECT @t1, @m, id FROM roles WHERE tenant_id = @t1 AND name = 'SalesManager'
                UNION ALL SELECT @t1, @w, id FROM roles WHERE tenant_id = @t1 AND name = 'WarehouseManager'
                UNION ALL SELECT @t1, @d, id FROM roles WHERE tenant_id = @t1 AND name = 'Driver';
                SELECT set_config('app.current_tenant', @t1::text, false);
                INSERT INTO warehouses (id, tenant_id, code, name) VALUES (@w1, @t1, 'W1', 'Erbil warehouse');
                INSERT INTO warehouse_staff (tenant_id, warehouse_id, user_id) VALUES (@t1, @w1, @w);
                INSERT INTO customers (id, tenant_id, type, code, name, geom, status, owner_user_id) VALUES
                    (@c1, @t1, 'Pharmacy', 'C1', 'Customer 1', ST_SetSRID(ST_MakePoint(44.0092, 36.1911), 4326)::geography, 'located', @rep),
                    (@c2, @t1, 'Pharmacy', 'C2', 'Customer 2', ST_SetSRID(ST_MakePoint(44.0192, 36.1911), 4326)::geography, 'located', @rep2);
                INSERT INTO products (id, tenant_id, code, name, unit, currency, base_price) VALUES
                    (@p1, @t1, 'P1', 'Product 1', 'box', 'IQD', 1000), (@p2, @t1, 'P2', 'Product 2', 'box', 'IQD', 2000);", conn);
            foreach (var (name, value) in new[] { ("t1", t1), ("f", finance), ("rep", rep), ("rep2", rep2), ("m", manager), ("w", worker), ("d", driver), ("w1", w1), ("c1", c1), ("c2", c2), ("p1", p1), ("p2", p2) })
                cmd.Parameters.AddWithValue(name, value);
            await cmd.ExecuteNonQueryAsync();
        }

        void As(Guid user) => client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            GenerateToken(t1, user, conf["Jwt:SigningKey"]!, conf["Jwt:Issuer"]!, conf["Jwt:Audience"]!));
        Task<HttpResponseMessage> Post(string path, object? body = null) => client.PostAsJsonAsync(path, body ?? new { });
        async Task<LifeDrop.Application.Payments.PaymentDto> Get(Guid id) => (await client.GetFromJsonAsync<LifeDrop.Application.Payments.PaymentDto>($"/payments/{id}"))!;

        // The order that is delivered: 10 x 1000 + 2 x 2000 = 14000 IQD
        As(manager);
        var created = await client.PostAsJsonAsync("/orders", new { CustomerId = c1, WarehouseId = w1, ClientRef = Guid.NewGuid(), Items = new[] { new { ProductId = p1, Quantity = 10m }, new { ProductId = p2, Quantity = 4m } } });
        var orderId = (await created.Content.ReadFromJsonAsync<LifeDrop.Application.Orders.CreatedOrderDto>())!.Id;
        As(worker);
        Assert.Equal(HttpStatusCode.NoContent, (await Post($"/warehouse/orders/{orderId}/start")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Post($"/warehouse/orders/{orderId}/ready", new { Lines = new object[] { new { LineNo = 1, Quantity = 10m }, new { LineNo = 2, Quantity = 3m, Reason = "stock" } } })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Post($"/warehouse/orders/{orderId}/handover", new { DriverId = driver })).StatusCode);
        As(driver);
        Assert.Equal(HttpStatusCode.NoContent, (await Post($"/deliveries/{orderId}/pickup")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Post($"/deliveries/{orderId}/depart")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Post($"/deliveries/{orderId}/deliver", new
        {
            ReceiverName = "Ali", Lines = new object[] { new { LineNo = 1, Quantity = 10m }, new { LineNo = 2, Quantity = 2m, Reason = "refused one" } }
        })).StatusCode);

        // The driver, the warehouse and the sales manager cannot record or read payments
        foreach (var other in new[] { driver, worker })
        {
            As(other);
            await AssertProblemAsync(await Post("/payments", new { CustomerId = c1, Method = "cash", Currency = "IQD", Amount = 1m, ClientRef = Guid.NewGuid() }), HttpStatusCode.Forbidden, "FORBIDDEN");
            await AssertProblemAsync(await client.GetAsync("/payments"), HttpStatusCode.Forbidden, "FORBIDDEN");
            await AssertProblemAsync(await client.GetAsync($"/customers/{c1}/balance"), HttpStatusCode.Forbidden, "FORBIDDEN");
        }

        // A visitor takes cash: "collected"; a retry gives the same payment; another user cannot reuse the reference
        As(rep);
        var ref1 = Guid.NewGuid();
        object Cash(decimal amount, Guid cr, Guid? order = null) => new { CustomerId = c1, OrderId = order, Method = "cash", Currency = "IQD", Amount = amount, Note = " first part ", ClientRef = cr };
        var first = await Post("/payments", Cash(5000m, ref1, orderId));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var firstPayment = (await first.Content.ReadFromJsonAsync<LifeDrop.Application.Payments.CreatedPaymentDto>())!;
        Assert.Equal(("P-000001", "collected", false), (firstPayment.Number, firstPayment.Status, firstPayment.Replayed));
        var again = await Post("/payments", Cash(5000m, ref1, orderId));
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        var replay = (await again.Content.ReadFromJsonAsync<LifeDrop.Application.Payments.CreatedPaymentDto>())!;
        Assert.Equal((firstPayment.Id, true), (replay.Id, replay.Replayed));
        As(finance);
        await AssertProblemAsync(await Post("/payments", Cash(5000m, ref1, orderId)), HttpStatusCode.Conflict, "CONFLICT");

        // What is refused: a system without its reference, no amount, too many decimals, another currency than the order, a customer outside the area, a missing order
        As(rep);
        foreach (var bad in new object[]
        {
            new { CustomerId = c1, Method = "fastpay", Currency = "IQD", Amount = 1000m, ClientRef = Guid.NewGuid() },
            new { CustomerId = c1, Method = "fib", Currency = "IQD", Amount = 1000m, Reference = " ", ClientRef = Guid.NewGuid() },
            new { CustomerId = c1, Method = "cash", Currency = "IQD", Amount = 0m, ClientRef = Guid.NewGuid() },
            new { CustomerId = c1, Method = "cash", Currency = "IQD", Amount = -5m, ClientRef = Guid.NewGuid() },
            new { CustomerId = c1, Method = "cash", Currency = "IQD", Amount = 1.0001m, ClientRef = Guid.NewGuid() },
            new { CustomerId = c1, Method = "cash", Currency = "EUR", Amount = 10m, ClientRef = Guid.NewGuid() },
            new { CustomerId = c1, Method = "barter", Currency = "IQD", Amount = 10m, ClientRef = Guid.NewGuid() },
            new { CustomerId = c1, Method = "cash", Currency = "IQD", Amount = 10m },
            new { Method = "cash", Currency = "IQD", Amount = 10m, ClientRef = Guid.NewGuid() },
            new { CustomerId = c1, Method = "cash", Currency = "IQD", Amount = 10m, Note = new string('x', 501), ClientRef = Guid.NewGuid() },
            new { CustomerId = c1, OrderId = orderId, Method = "cash", Currency = "USD", Amount = 10m, ClientRef = Guid.NewGuid() }   // the order is in IQD
        })
            await AssertProblemAsync(await Post("/payments", bad), HttpStatusCode.BadRequest, "INVALID_DATA");
        await AssertProblemAsync(await Post("/payments", new { CustomerId = c2, Method = "cash", Currency = "IQD", Amount = 10m, ClientRef = Guid.NewGuid() }), HttpStatusCode.NotFound, "NOT_FOUND");
        await AssertProblemAsync(await Post("/payments", new { CustomerId = c1, OrderId = Guid.NewGuid(), Method = "cash", Currency = "IQD", Amount = 10m, ClientRef = Guid.NewGuid() }), HttpStatusCode.NotFound, "NOT_FOUND");
        As(finance);
        var otherOrder = await Post("/payments", new { CustomerId = c2, OrderId = orderId, Method = "cash", Currency = "IQD", Amount = 10m, ClientRef = Guid.NewGuid() });   // the order is c1's
        await AssertProblemAsync(otherOrder, HttpStatusCode.BadRequest, "INVALID_DATA");

        // Visibility: a visitor sees what they recorded; the finance manager sees all
        As(rep);
        var mine = (await client.GetFromJsonAsync<List<LifeDrop.Application.Payments.PaymentDto>>("/payments/mine"))!;
        Assert.Equal(new[] { firstPayment.Id }, mine.Select(p => p.Id).ToArray());
        Assert.Equal(("C1", orderId, "O-000001", "first part"), (mine[0].CustomerCode, mine[0].OrderId, mine[0].OrderNumber, mine[0].Note));
        As(rep2);
        Assert.Empty((await client.GetFromJsonAsync<List<LifeDrop.Application.Payments.PaymentDto>>("/payments/mine"))!);
        Assert.Empty((await client.GetFromJsonAsync<List<LifeDrop.Application.Payments.PaymentDto>>("/payments"))!);
        await AssertProblemAsync(await client.GetAsync($"/payments/{firstPayment.Id}"), HttpStatusCode.NotFound, "NOT_FOUND");
        await AssertProblemAsync(await client.GetAsync($"/customers/{c1}/balance"), HttpStatusCode.NotFound, "NOT_FOUND");

        // The finance manager records FIB (reference needed): confirmed at once
        As(finance);
        var fibResponse = await Post("/payments", new { CustomerId = c1, OrderId = orderId, Method = "fib", Currency = "IQD", Amount = 4000m, Reference = " TX-77 ", ClientRef = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.Created, fibResponse.StatusCode);
        var fib = (await fibResponse.Content.ReadFromJsonAsync<LifeDrop.Application.Payments.CreatedPaymentDto>())!;
        Assert.Equal(("P-000002", "confirmed"), (fib.Number, fib.Status));
        var fibDetail = await Get(fib.Id);
        Assert.Equal(("fib", "TX-77", "confirmed"), (fibDetail.Method, fibDetail.Reference, fibDetail.Status));
        Assert.Equal(2, (await client.GetFromJsonAsync<List<LifeDrop.Application.Payments.PaymentDto>>("/payments"))!.Count);
        Assert.Equal(new[] { fib.Id }, (await client.GetFromJsonAsync<List<LifeDrop.Application.Payments.PaymentDto>>("/payments?status=confirmed"))!.Select(p => p.Id).ToArray());
        await AssertProblemAsync(await client.GetAsync("/payments?status=weird"), HttpStatusCode.BadRequest, "INVALID_DATA");

        // Balance: delivered 14000; the cash is only collected, so 4000 is confirmed
        var balance = (await client.GetFromJsonAsync<LifeDrop.Application.Payments.CustomerBalanceDto>($"/customers/{c1}/balance"))!;
        Assert.Equal(new[] { ("IQD", 14000m, 4000m, 5000m, 10000m) }, balance.Items.Select(i => (i.Currency, i.Delivered, i.Confirmed, i.Collected, i.Balance)).ToArray());

        // Confirming: only the finance manager, only a collected payment
        As(rep);
        await AssertProblemAsync(await Post($"/payments/{firstPayment.Id}/confirm"), HttpStatusCode.Forbidden, "FORBIDDEN");
        As(finance);
        Assert.Equal(HttpStatusCode.NoContent, (await Post($"/payments/{firstPayment.Id}/confirm")).StatusCode);
        await AssertProblemAsync(await Post($"/payments/{firstPayment.Id}/confirm"), HttpStatusCode.Conflict, "CONFLICT");
        await AssertProblemAsync(await Post($"/payments/{Guid.NewGuid()}/confirm"), HttpStatusCode.NotFound, "NOT_FOUND");
        var confirmed = await Get(firstPayment.Id);
        Assert.Equal(("confirmed", "P-000001"), (confirmed.Status, confirmed.Number));
        Assert.NotNull(confirmed.ConfirmedAt);
        Assert.Equal(new[] { "collected", "confirmed" }, confirmed.History!.Select(h => h.Status).ToArray());
        balance = (await client.GetFromJsonAsync<LifeDrop.Application.Payments.CustomerBalanceDto>($"/customers/{c1}/balance"))!;
        Assert.Equal((9000m, 0m, 5000m), (balance.Items[0].Confirmed, balance.Items[0].Collected, balance.Items[0].Balance));

        // A visitor takes back only their own payment that is still collected, with a reason
        As(rep);
        var third = (await (await Post("/payments", Cash(700m, Guid.NewGuid()))).Content.ReadFromJsonAsync<LifeDrop.Application.Payments.CreatedPaymentDto>())!;
        await AssertProblemAsync(await Post($"/payments/{third.Id}/void", new { Reason = " " }), HttpStatusCode.BadRequest, "INVALID_DATA");
        await AssertProblemAsync(await Post($"/payments/{firstPayment.Id}/void", new { Reason = "oops" }), HttpStatusCode.Forbidden, "FORBIDDEN");   // already confirmed
        As(rep2);
        await AssertProblemAsync(await Post($"/payments/{third.Id}/void", new { Reason = "not mine" }), HttpStatusCode.NotFound, "NOT_FOUND");
        As(rep);
        Assert.Equal(HttpStatusCode.NoContent, (await Post($"/payments/{third.Id}/void", new { Reason = "typed twice" })).StatusCode);
        await AssertProblemAsync(await Post($"/payments/{third.Id}/void", new { Reason = "again" }), HttpStatusCode.Conflict, "CONFLICT");
        var voided = await Get(third.Id);
        Assert.Equal(("voided", "typed twice"), (voided.Status, voided.ClosedReason));
        Assert.Equal(new[] { "collected", "voided" }, voided.History!.Select(h => h.Status).ToArray());
        balance = (await client.GetFromJsonAsync<LifeDrop.Application.Payments.CustomerBalanceDto>($"/customers/{c1}/balance"))!;
        Assert.Equal(0m, balance.Items[0].Collected);   // a voided payment counts nowhere

        // Correcting: only the finance manager; the old payment stays as "superseded", a new confirmed one replaces it
        await AssertProblemAsync(await Post($"/payments/{firstPayment.Id}/correct", new { Method = "cash", Currency = "IQD", Amount = 4500m, Reason = "counted again" }), HttpStatusCode.Forbidden, "FORBIDDEN");
        As(finance);
        await AssertProblemAsync(await Post($"/payments/{firstPayment.Id}/correct", new { Method = "cash", Currency = "IQD", Amount = 4500m }), HttpStatusCode.BadRequest, "INVALID_DATA");
        await AssertProblemAsync(await Post($"/payments/{firstPayment.Id}/correct", new { Method = "fib", Currency = "IQD", Amount = 4500m, Reason = "x" }), HttpStatusCode.BadRequest, "INVALID_DATA");
        await AssertProblemAsync(await Post($"/payments/{third.Id}/correct", new { Method = "cash", Currency = "IQD", Amount = 1m, Reason = "x" }), HttpStatusCode.Conflict, "CONFLICT");
        var fix = await Post($"/payments/{firstPayment.Id}/correct", new { OrderId = orderId, Method = "cash", Currency = "IQD", Amount = 4500m, Reason = "counted again" });
        Assert.Equal(HttpStatusCode.Created, fix.StatusCode);
        var fixPayment = (await fix.Content.ReadFromJsonAsync<LifeDrop.Application.Payments.CreatedPaymentDto>())!;
        Assert.Equal(("P-000004", "confirmed"), (fixPayment.Number, fixPayment.Status));
        var old = await Get(firstPayment.Id);
        Assert.Equal(("superseded", "counted again", fixPayment.Id), (old.Status, old.ClosedReason, old.ReplacedByPaymentId));
        Assert.Equal(new[] { "collected", "confirmed", "superseded" }, old.History!.Select(h => h.Status).ToArray());
        var replacement = await Get(fixPayment.Id);
        Assert.Equal((firstPayment.Id, 4500m, c1), (replacement.ReplacesPaymentId, replacement.Amount, replacement.CustomerId));
        await AssertProblemAsync(await Post($"/payments/{firstPayment.Id}/correct", new { Method = "cash", Currency = "IQD", Amount = 1m, Reason = "again" }), HttpStatusCode.Conflict, "CONFLICT");
        balance = (await client.GetFromJsonAsync<LifeDrop.Application.Payments.CustomerBalanceDto>($"/customers/{c1}/balance"))!;
        Assert.Equal((14000m, 8500m, 5500m), (balance.Items[0].Delivered, balance.Items[0].Confirmed, balance.Items[0].Balance));

        // The finance manager voids a confirmed payment
        Assert.Equal(HttpStatusCode.NoContent, (await Post($"/payments/{fib.Id}/void", new { Reason = "bank reversed it" })).StatusCode);
        balance = (await client.GetFromJsonAsync<LifeDrop.Application.Payments.CustomerBalanceDto>($"/customers/{c1}/balance"))!;
        Assert.Equal((4500m, 9500m), (balance.Items[0].Confirmed, balance.Items[0].Balance));

        // Every step is in the audit log
        await using var verify = new NpgsqlConnection(schemaBlock.ConnectionString);
        await verify.OpenAsync();
        await using var audit = new NpgsqlCommand("SELECT action, count(*) FROM audit_logs WHERE action LIKE 'payment.%' GROUP BY action ORDER BY action", verify);
        var actions = new List<string>();
        await using (var reader = await audit.ExecuteReaderAsync())
            while (await reader.ReadAsync()) actions.Add($"{reader.GetString(0)}={reader.GetInt64(1)}");
        Assert.Equal(new[] { "payment.confirmed=1", "payment.corrected=1", "payment.recorded=2", "payment.recorded_confirmed=1", "payment.voided=2" }, actions);
    }

    [SkippableFact]
    public async Task Delivery_DriverSeesOnlyAssignedOrdersAndRecordsPickupDeliveryOrFailureWithEvidence()
    {
        var url = GetRequiredEnvVar("TEST_DATABASE_URL");
        await VerifyRoleAsync(url, expectElevated: false);

        var (schemaBlock, factory) = await StartFlexiblePlanHostAsync(url);
        await using var _ = schemaBlock;
        await using var __ = factory;
        var conf = factory.Services.GetRequiredService<IConfiguration>();
        var client = factory.CreateClient();

        var t1 = Guid.NewGuid();
        Guid manager = Guid.NewGuid(), worker = Guid.NewGuid(), driver1 = Guid.NewGuid(), driver2 = Guid.NewGuid(), rep = Guid.NewGuid();
        Guid w1 = Guid.NewGuid(), c1 = Guid.NewGuid(), p1 = Guid.NewGuid(), p2 = Guid.NewGuid();
        await using (var conn = new NpgsqlConnection(schemaBlock.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(@"
                INSERT INTO tenants (id, name, code) VALUES (@t1, 'T1', 'T1');
                SELECT seed_default_roles(@t1);
                INSERT INTO users (id, tenant_id) VALUES (@m, @t1), (@w, @t1), (@d1, @t1), (@d2, @t1), (@rep, @t1);
                INSERT INTO user_roles (tenant_id, user_id, role_id)
                SELECT @t1, @m, id FROM roles WHERE tenant_id = @t1 AND name = 'SalesManager'
                UNION ALL SELECT @t1, @w, id FROM roles WHERE tenant_id = @t1 AND name = 'WarehouseManager'
                UNION ALL SELECT @t1, @d1, id FROM roles WHERE tenant_id = @t1 AND name = 'Driver'
                UNION ALL SELECT @t1, @d2, id FROM roles WHERE tenant_id = @t1 AND name = 'Driver'
                UNION ALL SELECT @t1, @rep, id FROM roles WHERE tenant_id = @t1 AND name = 'SalesRep';
                SELECT set_config('app.current_tenant', @t1::text, false);
                INSERT INTO warehouses (id, tenant_id, code, name) VALUES (@w1, @t1, 'W1', 'Erbil warehouse');
                INSERT INTO warehouse_staff (tenant_id, warehouse_id, user_id) VALUES (@t1, @w1, @w);
                INSERT INTO customers (id, tenant_id, type, code, name, phone, address, geom, status)
                    VALUES (@c1, @t1, 'Pharmacy', 'C1', 'Customer 1', '0750 000 0000', 'Ankawa street 5', ST_SetSRID(ST_MakePoint(44.0092, 36.1911), 4326)::geography, 'located');
                INSERT INTO products (id, tenant_id, code, name, unit, currency, base_price) VALUES
                    (@p1, @t1, 'P1', 'Product 1', 'box', 'IQD', 1000), (@p2, @t1, 'P2', 'Product 2', 'box', 'IQD', 2000);", conn);
            foreach (var (name, value) in new[] { ("t1", t1), ("m", manager), ("w", worker), ("d1", driver1), ("d2", driver2), ("rep", rep), ("w1", w1), ("c1", c1), ("p1", p1), ("p2", p2) })
                cmd.Parameters.AddWithValue(name, value);
            await cmd.ExecuteNonQueryAsync();
        }

        void As(Guid user) => client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            GenerateToken(t1, user, conf["Jwt:SigningKey"]!, conf["Jwt:Issuer"]!, conf["Jwt:Audience"]!));
        Task<HttpResponseMessage> Post(string path, object? body = null) => client.PostAsJsonAsync(path, body ?? new { });
        // An order that the warehouse prepared (line 2: 3 of 4) and handed to the driver
        async Task<Guid> HandedOver(Guid driver)
        {
            As(manager);
            var response = await client.PostAsJsonAsync("/orders", new { CustomerId = c1, WarehouseId = w1, ClientRef = Guid.NewGuid(), Items = new[] { new { ProductId = p1, Quantity = 10m }, new { ProductId = p2, Quantity = 4m } } });
            var id = (await response.Content.ReadFromJsonAsync<LifeDrop.Application.Orders.CreatedOrderDto>())!.Id;
            As(worker);
            Assert.Equal(HttpStatusCode.NoContent, (await Post($"/warehouse/orders/{id}/start")).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await Post($"/warehouse/orders/{id}/ready", new { Lines = new object[] { new { LineNo = 1, Quantity = 10m }, new { LineNo = 2, Quantity = 3m, Reason = "stock" } } })).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await Post($"/warehouse/orders/{id}/handover", new { DriverId = driver })).StatusCode);
            return id;
        }
        var near = new { Latitude = 36.1912, Longitude = 44.0093, AccuracyMeters = 8.0, IsMocked = false };
        var order1 = await HandedOver(driver1);

        // Only the assigned driver; nobody else, whatever their role
        As(driver2);
        Assert.Empty((await client.GetFromJsonAsync<List<LifeDrop.Application.Orders.OrderDto>>("/deliveries"))!);
        await AssertProblemAsync(await client.GetAsync($"/deliveries/{order1}"), HttpStatusCode.NotFound, "NOT_FOUND");
        await AssertProblemAsync(await Post($"/deliveries/{order1}/pickup"), HttpStatusCode.NotFound, "NOT_FOUND");
        await AssertProblemAsync(await Post($"/deliveries/{order1}/fail", new { Reason = "no" }), HttpStatusCode.NotFound, "NOT_FOUND");
        foreach (var other in new[] { rep, worker, manager })
        {
            As(other);
            await AssertProblemAsync(await client.GetAsync("/deliveries"), HttpStatusCode.Forbidden, "FORBIDDEN");
            await AssertProblemAsync(await Post($"/deliveries/{order1}/pickup"), HttpStatusCode.Forbidden, "FORBIDDEN");
        }

        As(driver1);
        var mine = (await client.GetFromJsonAsync<List<LifeDrop.Application.Orders.OrderDto>>("/deliveries"))!;
        Assert.Equal(new[] { order1 }, mine.Select(o => o.Id).ToArray());
        var detail = (await client.GetFromJsonAsync<LifeDrop.Application.Orders.OrderDto>($"/deliveries/{order1}"))!;
        Assert.Equal(("handed_over", "0750 000 0000", "Ankawa street 5"), (detail.Status, detail.Delivery!.Phone, detail.Delivery.Address));
        Assert.Equal((36.1911, 44.0092), (Math.Round(detail.Delivery.Latitude!.Value, 4), Math.Round(detail.Delivery.Longitude!.Value, 4)));
        Assert.Equal(new decimal?[] { 10m, 3m }, detail.Items.Select(i => i.PreparedQuantity).ToArray());

        // The steps come in order; a position is evidence only, but a fake or impossible one is refused
        await AssertProblemAsync(await Post($"/deliveries/{order1}/depart"), HttpStatusCode.Conflict, "CONFLICT");
        await AssertProblemAsync(await Post($"/deliveries/{order1}/deliver", new { ReceiverName = "Ali", Lines = new[] { new { LineNo = 1, Quantity = 10m }, new { LineNo = 2, Quantity = 3m } } }), HttpStatusCode.Conflict, "CONFLICT");
        await AssertProblemAsync(await Post($"/deliveries/{order1}/pickup", new { Location = new { Latitude = 36.19, Longitude = 44.0, IsMocked = true } }), HttpStatusCode.UnprocessableEntity, "GPS_MOCKED");
        await AssertProblemAsync(await Post($"/deliveries/{order1}/pickup", new { Location = new { Latitude = 100.0, Longitude = 44.0 } }), HttpStatusCode.BadRequest, "INVALID_DATA");
        Assert.Equal("handed_over", (await client.GetFromJsonAsync<LifeDrop.Application.Orders.OrderDto>($"/deliveries/{order1}"))!.Status);
        Assert.Equal(HttpStatusCode.NoContent, (await Post($"/deliveries/{order1}/pickup", new { Location = near })).StatusCode);
        await AssertProblemAsync(await Post($"/deliveries/{order1}/pickup"), HttpStatusCode.Conflict, "CONFLICT");
        Assert.Equal(HttpStatusCode.NoContent, (await Post($"/deliveries/{order1}/depart")).StatusCode);   // without a position is fine

        // Delivering: the receiver, every line once, never more than prepared, less needs a reason, at least one line above 0
        object Lines(decimal q1, decimal q2, string? reason = null) => new[] { new { LineNo = 1, Quantity = q1, Reason = (string?)null }, new { LineNo = 2, Quantity = q2, Reason = reason } };
        foreach (var bad in new object[]
        {
            new { ReceiverName = " ", Lines = Lines(10m, 3m) },
            new { Lines = Lines(10m, 3m) },
            new { ReceiverName = "Ali", Lines = new[] { new { LineNo = 1, Quantity = 10m, Reason = (string?)null } } },                     // line 2 missing
            new { ReceiverName = "Ali", Lines = Lines(10m, 4m) },                                                                              // more than prepared (3)
            new { ReceiverName = "Ali", Lines = Lines(10m, 2m) },                                                                              // less, no reason
            new { ReceiverName = "Ali", Lines = Lines(0m, 0m) },                                                                               // nothing delivered: that is a failure
            new { ReceiverName = "Ali", Lines = new[] { new { LineNo = 1, Quantity = 10m, Reason = (string?)null }, new { LineNo = 1, Quantity = 10m, Reason = (string?)null } } },
            new { ReceiverName = new string('x', 101), Lines = Lines(10m, 3m) }
        })
            await AssertProblemAsync(await Post($"/deliveries/{order1}/deliver", bad), HttpStatusCode.BadRequest, "INVALID_DATA");
        Assert.Equal("on_the_way", (await client.GetFromJsonAsync<LifeDrop.Application.Orders.OrderDto>($"/deliveries/{order1}"))!.Status);
        Assert.Equal(HttpStatusCode.NoContent, (await Post($"/deliveries/{order1}/deliver", new
        {
            ReceiverName = " Ali Hassan ", Note = " at the pharmacy door ", Lines = Lines(10m, 2m, " customer refused one box "),
            Location = new { Latitude = 36.2, Longitude = 44.0092, AccuracyMeters = 12.0 }
        })).StatusCode);

        var done = (await client.GetFromJsonAsync<LifeDrop.Application.Orders.OrderDto>($"/deliveries/{order1}"))!;
        Assert.Equal(("delivered", "Ali Hassan", "at the pharmacy door"), (done.Status, done.Delivery!.ReceiverName, done.Delivery.DeliveryNote));
        Assert.Equal(new decimal?[] { 10m, 2m }, done.Items.Select(i => i.DeliveredQuantity).ToArray());
        Assert.Equal(new string?[] { null, "customer refused one box" }, done.Items.Select(i => i.DeliveryReason).ToArray());
        Assert.Equal(new[] { "placed", "preparing", "ready", "handed_over", "picked_up", "on_the_way", "delivered" }, done.History!.Select(h => h.Status).ToArray());
        // Evidence: near (about 15 m) at pickup, none on the way, about one kilometre away at the delivery
        Assert.InRange(done.History.Single(h => h.Status == "picked_up").DistanceMeters!.Value, 1, 40);
        Assert.Null(done.History.Single(h => h.Status == "on_the_way").DistanceMeters);
        Assert.InRange(done.History.Single(h => h.Status == "delivered").DistanceMeters!.Value, 800, 1000);
        Assert.Equal(18000m, done.Total);   // 10 x 1000 + 4 x 2000: neither the shortage nor the delivered quantities change the total
        Assert.Empty((await client.GetFromJsonAsync<List<LifeDrop.Application.Orders.OrderDto>>("/deliveries"))!);   // no open work left
        Assert.Single((await client.GetFromJsonAsync<List<LifeDrop.Application.Orders.OrderDto>>("/deliveries?status=delivered"))!);
        await AssertProblemAsync(await client.GetAsync("/deliveries?status=placed"), HttpStatusCode.BadRequest, "INVALID_DATA");
        await AssertProblemAsync(await Post($"/deliveries/{order1}/fail", new { Reason = "too late" }), HttpStatusCode.Conflict, "CONFLICT");
        As(manager);
        await AssertProblemAsync(await Post($"/orders/{order1}/cancel", new { Reason = "too late" }), HttpStatusCode.Conflict, "CONFLICT");
        var seen = (await client.GetFromJsonAsync<LifeDrop.Application.Orders.OrderDto>($"/orders/{order1}"))!;   // the sellers see the delivery too
        Assert.Equal(("delivered", 2m), (seen.Status, seen.Items[1].DeliveredQuantity!.Value));
        Assert.Equal(("Ali Hassan", "at the pharmacy door", driver1), (seen.Delivery!.ReceiverName, seen.Delivery.DeliveryNote, seen.DriverId));

        // A delivery that fails: a reason is needed; it can fail before pickup; afterwards nothing moves
        var order2 = await HandedOver(driver1);
        As(driver1);
        await AssertProblemAsync(await Post($"/deliveries/{order2}/fail", new { Reason = " " }), HttpStatusCode.BadRequest, "INVALID_DATA");
        await AssertProblemAsync(await Post($"/deliveries/{order2}/fail", new { }), HttpStatusCode.BadRequest, "INVALID_DATA");
        Assert.Equal(HttpStatusCode.NoContent, (await Post($"/deliveries/{order2}/fail", new { Reason = " Pharmacy closed ", Location = near })).StatusCode);
        var failed = (await client.GetFromJsonAsync<LifeDrop.Application.Orders.OrderDto>($"/deliveries/{order2}"))!;
        Assert.Equal(("delivery_failed", "Pharmacy closed"), (failed.Status, failed.Delivery!.FailureReason));
        await AssertProblemAsync(await Post($"/deliveries/{order2}/pickup"), HttpStatusCode.Conflict, "CONFLICT");

        await using (var conn = new NpgsqlConnection(schemaBlock.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(@"SELECT set_config('app.current_tenant', @t::text, false);
                SELECT (SELECT string_agg(action || '=' || n, ',' ORDER BY action COLLATE ""C"")
                        FROM (SELECT action, count(*) n FROM audit_logs WHERE action IN ('order.picked_up', 'order.on_the_way', 'order.delivered', 'order.delivery_failed', 'order.delivery_shortage') GROUP BY action) a)", conn);
            cmd.Parameters.AddWithValue("t", t1);
            await using var reader = await cmd.ExecuteReaderAsync();
            await reader.NextResultAsync();
            await reader.ReadAsync();
            Assert.Equal("order.delivered=1,order.delivery_failed=1,order.delivery_shortage=1,order.on_the_way=1,order.picked_up=1", reader.GetString(0));
        }
    }

    // Phase 2 exit test (plan step 2.15, PRD §14): one order from the seller to the delivery, the payment and the finance numbers,
    // with the role, region and company limits checked along the way.
    [SkippableFact]
    public async Task Phase2Exit_OrderToDeliveryPaymentAndReportRespectRoleRegionAndCompany()
    {
        var url = GetRequiredEnvVar("TEST_DATABASE_URL");
        await VerifyRoleAsync(url, expectElevated: false);

        var (schemaBlock, factory) = await StartFlexiblePlanHostAsync(url);
        await using var _ = schemaBlock;
        await using var __ = factory;
        var conf = factory.Services.GetRequiredService<IConfiguration>();
        var client = factory.CreateClient();

        Guid t1 = Guid.NewGuid(), t2 = Guid.NewGuid();
        Guid manager = Guid.NewGuid(), worker = Guid.NewGuid(), driver = Guid.NewGuid(), finance = Guid.NewGuid(), rep = Guid.NewGuid(), sup = Guid.NewGuid();
        Guid manager2 = Guid.NewGuid(), finance2 = Guid.NewGuid(), worker2 = Guid.NewGuid();
        Guid w1 = Guid.NewGuid(), w2 = Guid.NewGuid(), c1 = Guid.NewGuid(), c2 = Guid.NewGuid(), p1 = Guid.NewGuid(), p2 = Guid.NewGuid();
        Guid region1 = Guid.NewGuid(), team1 = Guid.NewGuid();
        await using (var conn = new NpgsqlConnection(schemaBlock.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(@"
                INSERT INTO tenants (id, name, code) VALUES (@t1, 'T1', 'T1'), (@t2, 'T2', 'T2');
                SELECT seed_default_roles(@t1);
                SELECT seed_default_roles(@t2);
                INSERT INTO users (id, tenant_id) VALUES (@m, @t1), (@w, @t1), (@d, @t1), (@f, @t1), (@rep, @t1), (@sup, @t1), (@m2, @t2), (@f2, @t2), (@w2, @t2);
                INSERT INTO user_roles (tenant_id, user_id, role_id)
                SELECT @t1, @m, id FROM roles WHERE tenant_id = @t1 AND name = 'SalesManager'
                UNION ALL SELECT @t1, @w, id FROM roles WHERE tenant_id = @t1 AND name = 'WarehouseManager'
                UNION ALL SELECT @t1, @d, id FROM roles WHERE tenant_id = @t1 AND name = 'Driver'
                UNION ALL SELECT @t1, @f, id FROM roles WHERE tenant_id = @t1 AND name = 'FinanceManager'
                UNION ALL SELECT @t1, @rep, id FROM roles WHERE tenant_id = @t1 AND name = 'SalesRep'
                UNION ALL SELECT @t1, @sup, id FROM roles WHERE tenant_id = @t1 AND name = 'Supervisor'
                UNION ALL SELECT @t2, @m2, id FROM roles WHERE tenant_id = @t2 AND name = 'SalesManager'
                UNION ALL SELECT @t2, @f2, id FROM roles WHERE tenant_id = @t2 AND name = 'FinanceManager'
                UNION ALL SELECT @t2, @w2, id FROM roles WHERE tenant_id = @t2 AND name = 'WarehouseManager';
                SELECT set_config('app.current_tenant', @t1::text, false);
                INSERT INTO regions (id, tenant_id, name) VALUES (@r1, @t1, 'Erbil');
                INSERT INTO teams (id, tenant_id, region_id, name) VALUES (@tm1, @t1, @r1, 'Team Erbil');
                INSERT INTO team_members (tenant_id, user_id, team_id) VALUES (@t1, @sup, @tm1), (@t1, @rep, @tm1);
                INSERT INTO warehouses (id, tenant_id, code, name) VALUES (@w1, @t1, 'W1', 'Erbil warehouse');
                INSERT INTO warehouse_staff (tenant_id, warehouse_id, user_id) VALUES (@t1, @w1, @w);
                INSERT INTO customers (id, tenant_id, type, code, name, geom, status, owner_user_id) VALUES
                    (@c1, @t1, 'Pharmacy', 'C1', 'Customer 1', ST_SetSRID(ST_MakePoint(44.0092, 36.1911), 4326)::geography, 'located', @rep);
                INSERT INTO products (id, tenant_id, code, name, unit, currency, base_price) VALUES
                    (@p1, @t1, 'P1', 'Product 1', 'box', 'IQD', 1000), (@p2, @t1, 'P2', 'Product 2', 'box', 'IQD', 2000);
                SELECT set_config('app.current_tenant', @t2::text, false);
                INSERT INTO warehouses (id, tenant_id, code, name) VALUES (@w2x, @t2, 'W1', 'Other warehouse');
                INSERT INTO warehouse_staff (tenant_id, warehouse_id, user_id) VALUES (@t2, @w2x, @w2);
                INSERT INTO customers (id, tenant_id, type, code, name, geom, status) VALUES
                    (@c2, @t2, 'Pharmacy', 'C1', 'Other customer', ST_SetSRID(ST_MakePoint(44.0092, 36.1911), 4326)::geography, 'located');", conn);
            foreach (var (name, value) in new[] { ("t1", t1), ("t2", t2), ("m", manager), ("w", worker), ("d", driver), ("f", finance), ("rep", rep), ("sup", sup), ("m2", manager2), ("f2", finance2), ("w2", worker2),
                ("w2x", w2), ("w1", w1), ("c1", c1), ("c2", c2), ("p1", p1), ("p2", p2), ("r1", region1), ("tm1", team1) })
                cmd.Parameters.AddWithValue(name, value);
            await cmd.ExecuteNonQueryAsync();
        }

        void As(Guid tenant, Guid user) => client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            GenerateToken(tenant, user, conf["Jwt:SigningKey"]!, conf["Jwt:Issuer"]!, conf["Jwt:Audience"]!));
        Task<HttpResponseMessage> Post(string path, object? body = null) => client.PostAsJsonAsync(path, body ?? new { });
        // Another company never gets the data: refused as not found or as forbidden, whichever the role's own permissions decide first.
        static void Hidden(HttpResponseMessage response) => Assert.Contains(response.StatusCode, new[] { HttpStatusCode.NotFound, HttpStatusCode.Forbidden });
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        string Range() => $"/reports/kpis?from={today.AddDays(-1):yyyy-MM-dd}&to={today.AddDays(1):yyyy-MM-dd}";

        // 1. The seller places the order; the server prices it (10 x 1000 + 4 x 2000 = 18000 IQD)
        As(t1, manager);
        var created = await client.PostAsJsonAsync("/orders", new { CustomerId = c1, WarehouseId = w1, ClientRef = Guid.NewGuid(), Items = new[] { new { ProductId = p1, Quantity = 10m }, new { ProductId = p2, Quantity = 4m } } });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var orderId = (await created.Content.ReadFromJsonAsync<LifeDrop.Application.Orders.CreatedOrderDto>())!.Id;
        Assert.Equal(18000m, (await client.GetFromJsonAsync<LifeDrop.Application.Orders.OrderDto>($"/orders/{orderId}"))!.Total);

        // Another company sees nothing of it, with any role
        foreach (var other in new[] { manager2, finance2, worker2 })
        {
            As(t2, other);
            Hidden(await client.GetAsync($"/orders/{orderId}"));
        }
        As(t2, manager2);
        await AssertProblemAsync(await client.PostAsJsonAsync("/orders", new { CustomerId = c1, WarehouseId = w1, ClientRef = Guid.NewGuid(), Items = new[] { new { ProductId = p1, Quantity = 1m } } }), HttpStatusCode.NotFound, "NOT_FOUND");   // not its customer, warehouse or product

        // 2. Roles: the driver and the finance manager do not do the warehouse work; the warehouse works only on its own orders
        foreach (var other in new[] { driver, finance })
        {
            As(t1, other);
            await AssertProblemAsync(await Post($"/warehouse/orders/{orderId}/start"), HttpStatusCode.Forbidden, "FORBIDDEN");
        }
        As(t2, worker2);
        Assert.NotEqual(HttpStatusCode.NoContent, (await Post($"/warehouse/orders/{orderId}/start")).StatusCode);
        As(t1, worker);
        Assert.Equal(HttpStatusCode.NoContent, (await Post($"/warehouse/orders/{orderId}/start")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Post($"/warehouse/orders/{orderId}/ready", new { Lines = new object[] { new { LineNo = 1, Quantity = 10m }, new { LineNo = 2, Quantity = 3m, Reason = "stock" } } })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Post($"/warehouse/orders/{orderId}/handover", new { DriverId = driver })).StatusCode);

        // 3. Delivery: only the assigned driver
        As(t1, rep);
        await AssertProblemAsync(await Post($"/deliveries/{orderId}/pickup"), HttpStatusCode.Forbidden, "FORBIDDEN");
        As(t1, driver);
        Assert.Equal(HttpStatusCode.NoContent, (await Post($"/deliveries/{orderId}/pickup")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Post($"/deliveries/{orderId}/depart")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Post($"/deliveries/{orderId}/deliver", new
        {
            ReceiverName = "Ali", Lines = new object[] { new { LineNo = 1, Quantity = 10m }, new { LineNo = 2, Quantity = 2m, Reason = "refused one" } }
        })).StatusCode);   // delivered value 10 x 1000 + 2 x 2000 = 14000

        // 4. Payment: the visitor collects, the finance manager confirms; the driver cannot touch money; another company sees nothing
        As(t1, driver);
        await AssertProblemAsync(await Post("/payments", new { CustomerId = c1, OrderId = orderId, Method = "cash", Currency = "IQD", Amount = 1m, ClientRef = Guid.NewGuid() }), HttpStatusCode.Forbidden, "FORBIDDEN");
        As(t1, rep);
        var paid = await Post("/payments", new { CustomerId = c1, OrderId = orderId, Method = "cash", Currency = "IQD", Amount = 6000m, ClientRef = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.Created, paid.StatusCode);
        var paymentId = (await paid.Content.ReadFromJsonAsync<LifeDrop.Application.Payments.CreatedPaymentDto>())!.Id;
        await AssertProblemAsync(await Post($"/payments/{paymentId}/confirm"), HttpStatusCode.Forbidden, "FORBIDDEN");
        As(t2, finance2);
        Hidden(await client.GetAsync($"/payments/{paymentId}"));
        Hidden(await Post($"/payments/{paymentId}/confirm"));
        Hidden(await client.GetAsync($"/customers/{c1}/balance"));
        Assert.Empty((await client.GetFromJsonAsync<List<LifeDrop.Application.Payments.PaymentDto>>("/payments"))!);
        As(t1, finance);
        Assert.Equal(HttpStatusCode.NoContent, (await Post($"/payments/{paymentId}/confirm")).StatusCode);
        var balance = (await client.GetFromJsonAsync<LifeDrop.Application.Payments.CustomerBalanceDto>($"/customers/{c1}/balance"))!;
        Assert.Equal(new[] { ("IQD", 14000m, 6000m, 0m, 8000m) }, balance.Items.Select(i => (i.Currency, i.Delivered, i.Confirmed, i.Collected, i.Balance)).ToArray());

        // 5. The report counts the order and its delivery from the real data; every reader sees only their own area and company
        As(t1, manager);
        var report = await client.GetFromJsonAsync<LifeDrop.Application.Reports.KpiReportDto>(Range());
        var row = report!.Users.Single(u => u.UserId == manager).Counts;
        Assert.Equal((1, 1, 0), (row.Orders, row.OrdersDelivered, row.OrdersDeliveryFailed));
        Assert.Equal(new[] { ("IQD", 18000m) }, row.OrderTotals.Select(t => (t.Currency, t.Total)).ToArray());
        Assert.Equal(100.0, report.Users.Single(u => u.UserId == manager).Rates.OrderDelivery);
        As(t1, sup);
        var regional = (await client.GetFromJsonAsync<LifeDrop.Application.Reports.KpiReportDto>(Range()))!;
        Assert.DoesNotContain(regional.Users, u => u.UserId == manager);   // the seller is not in the supervisor's region: neither the row nor the order count is shown
        Assert.Equal(0, regional.Total.Counts.Orders);
        As(t1, finance);
        await AssertProblemAsync(await client.GetAsync(Range()), HttpStatusCode.Forbidden, "FORBIDDEN");
        As(t2, manager2);
        Assert.Empty((await client.GetFromJsonAsync<LifeDrop.Application.Reports.KpiReportDto>(Range()))!.Users);   // another company: nothing of the first one

        // 6. Every step left its audit row
        await using (var conn = new NpgsqlConnection(schemaBlock.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(@"SELECT set_config('app.current_tenant', @t::text, false);
                SELECT string_agg(DISTINCT action, ',' ORDER BY action) FROM audit_logs WHERE tenant_id = @t AND action IN ('order.delivered', 'order.picked_up', 'order.on_the_way')", conn);
            cmd.Parameters.AddWithValue("t", t1);
            await using var reader = await cmd.ExecuteReaderAsync();
            await reader.NextResultAsync();
            await reader.ReadAsync();
            Assert.Equal("order.delivered,order.on_the_way,order.picked_up", reader.GetString(0));
        }
    }

    [SkippableFact]
    public async Task Platform_SuperAdminManagesCompaniesAndEveryCrossCompanyReadIsAudited()
    {
        var url = GetRequiredEnvVar("TEST_DATABASE_URL");
        await VerifyRoleAsync(url, expectElevated: false);

        var (schemaBlock, factory) = await StartFlexiblePlanHostAsync(url);
        await using var _ = schemaBlock;
        await using var __ = factory;
        var conf = factory.Services.GetRequiredService<IConfiguration>();
        var client = factory.CreateClient();

        var platform = LifeDrop.Application.Platform.PlatformTenant.Id.Value;
        Guid t1 = Guid.NewGuid(), admin = Guid.NewGuid(), t1Manager = Guid.NewGuid(), t1Rep = Guid.NewGuid();
        await using (var conn = new NpgsqlConnection(schemaBlock.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(@"
                INSERT INTO tenants (id, name, code) VALUES (@t1, 'Company One', 'one');
                SELECT seed_default_roles(@t1);
                INSERT INTO users (id, tenant_id, email) VALUES (@admin, @p, 'root@platform'), (@m, @t1, 'm@one'), (@r, @t1, 'r@one');
                INSERT INTO user_roles (tenant_id, user_id, role_id)
                SELECT @p, @admin, id FROM roles WHERE tenant_id = @p AND name = 'SuperAdmin'
                UNION ALL SELECT @t1, @m, id FROM roles WHERE tenant_id = @t1 AND name = 'SalesManager'
                UNION ALL SELECT @t1, @r, id FROM roles WHERE tenant_id = @t1 AND name = 'SalesRep';
                -- A role of an ordinary company that carries the platform permission keys still gives nothing.
                INSERT INTO role_permissions (tenant_id, role_id, permission_key)
                SELECT @t1, id, k FROM roles, (VALUES ('platform.tenants.manage'), ('platform.tenants.access')) AS x(k) WHERE tenant_id = @t1 AND name = 'SalesManager';
                INSERT INTO audit_logs (id, tenant_id, actor_id, action, entity_id, occurred_at) VALUES
                    (gen_random_uuid(), @t1, @m, 'customer.created', 'c-1', now() - interval '2 minutes'), (gen_random_uuid(), @t1, @r, 'payment.recorded', 'p-1', now() - interval '1 minute');", conn);
            cmd.Parameters.AddWithValue("t1", t1); cmd.Parameters.AddWithValue("p", platform); cmd.Parameters.AddWithValue("admin", admin);
            cmd.Parameters.AddWithValue("m", t1Manager); cmd.Parameters.AddWithValue("r", t1Rep);
            await cmd.ExecuteNonQueryAsync();
        }
        void As(Guid tenant, Guid user) => client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            GenerateToken(tenant, user, conf["Jwt:SigningKey"]!, conf["Jwt:Issuer"]!, conf["Jwt:Audience"]!));
        object NewTenant(string code = "acme", string password = "a-long-password-1") => new { Code = code, Name = "Acme Pharma", ManagerEmail = "boss@acme.test", ManagerPassword = password };
        async Task<List<LifeDrop.Application.Platform.TenantSummaryDto>> Tenants() => (await client.GetFromJsonAsync<List<LifeDrop.Application.Platform.TenantSummaryDto>>("/platform/tenants"))!;
        async Task<List<LifeDrop.Application.Platform.PlatformAuditEntryDto>> PlatformAudit(string query = "") =>
            (await client.GetFromJsonAsync<List<LifeDrop.Application.Platform.PlatformAuditEntryDto>>("/platform/audit-logs" + query))!;

        // Only the platform company's Super Admin: not an ordinary company's manager, even with the same permission keys; not another platform-less user
        foreach (var other in new[] { t1Manager, t1Rep })
        {
            As(t1, other);
            await AssertProblemAsync(await client.GetAsync("/platform/tenants"), HttpStatusCode.Forbidden, "FORBIDDEN");
            await AssertProblemAsync(await client.PostAsJsonAsync("/platform/tenants", NewTenant("sneaky")), HttpStatusCode.Forbidden, "FORBIDDEN");
            await AssertProblemAsync(await client.PutAsJsonAsync($"/platform/tenants/{t1}", new { IsActive = false, Reason = "just because" }), HttpStatusCode.Forbidden, "FORBIDDEN");
            await AssertProblemAsync(await client.GetAsync($"/platform/tenants/{t1}/audit-logs?reason=curiosity+about+others"), HttpStatusCode.Forbidden, "FORBIDDEN");
            await AssertProblemAsync(await client.GetAsync("/platform/audit-logs"), HttpStatusCode.Forbidden, "FORBIDDEN");
        }

        As(platform, admin);
        Assert.Equal(new[] { "one", "platform" }, (await Tenants()).Select(t => t.Code).ToArray());
        Assert.Empty(await PlatformAudit());   // nothing was done yet; the refused calls above changed nothing

        // Creating a company: validated, seeded with roles and a first account with the CompanyManager and SalesManager roles, audited; the code is unique
        foreach (var bad in new object[]
        {
            NewTenant("platform"), NewTenant("A"), NewTenant("has space"), NewTenant("UPPER_case"), NewTenant("acme", "short"),
            new { Code = "acme", Name = " ", ManagerEmail = "boss@acme.test", ManagerPassword = "a-long-password-1" },
            new { Code = "acme", Name = "Acme", ManagerEmail = "not-an-email", ManagerPassword = "a-long-password-1" }
        })
            await AssertProblemAsync(await client.PostAsJsonAsync("/platform/tenants", bad), HttpStatusCode.BadRequest, "INVALID_DATA");
        var created = await client.PostAsJsonAsync("/platform/tenants", NewTenant());
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var acme = (await created.Content.ReadFromJsonAsync<LifeDrop.Application.Platform.CreatedTenantDto>())!;
        await AssertProblemAsync(await client.PostAsJsonAsync("/platform/tenants", NewTenant("acme")), HttpStatusCode.Conflict, "CONFLICT");
        var list = await Tenants();
        Assert.Equal(new[] { "acme", "one", "platform" }, list.Select(t => t.Code).ToArray());
        Assert.Equal((true, 1, 1), (list[0].IsActive, list[0].Users, list[0].ActiveUsers));
        await using (var conn = new NpgsqlConnection(schemaBlock.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(@"SELECT (SELECT count(*) FROM roles WHERE tenant_id = @t), (SELECT string_agg(r.name, ',' ORDER BY r.name) FROM user_roles ur JOIN roles r ON r.tenant_id = ur.tenant_id AND r.id = ur.role_id WHERE ur.tenant_id = @t AND ur.user_id = @u),
                (SELECT password_hash FROM users WHERE id = @u)", conn);
            cmd.Parameters.AddWithValue("t", acme.Id); cmd.Parameters.AddWithValue("u", acme.ManagerId);
            await using var reader = await cmd.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(8L, reader.GetInt64(0));
            Assert.Equal("CompanyManager,SalesManager", reader.GetString(1));   // the first account can set the company up (live map, devices, users, roles)
            Assert.StartsWith("pbkdf2-sha256$", reader.GetString(2));   // the password is stored hashed
        }

        // Rename and deactivate need a reason; the platform company never goes off; unknown companies are 404
        await AssertProblemAsync(await client.PutAsJsonAsync($"/platform/tenants/{acme.Id}", new { Name = "Acme 2" }), HttpStatusCode.BadRequest, "INVALID_DATA");
        await AssertProblemAsync(await client.PutAsJsonAsync($"/platform/tenants/{acme.Id}", new { Reason = "nothing to change" }), HttpStatusCode.BadRequest, "INVALID_DATA");
        await AssertProblemAsync(await client.PutAsJsonAsync($"/platform/tenants/{platform}", new { IsActive = false, Reason = "switch off" }), HttpStatusCode.Conflict, "CONFLICT");
        await AssertProblemAsync(await client.PutAsJsonAsync($"/platform/tenants/{Guid.NewGuid()}", new { Name = "x", Reason = "does not exist" }), HttpStatusCode.NotFound, "NOT_FOUND");
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync($"/platform/tenants/{acme.Id}", new { Name = " Acme Pharma Iraq ", Reason = "legal name changed" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync($"/platform/tenants/{acme.Id}", new { IsActive = false, Reason = "unpaid invoice" })).StatusCode);
        var acmeNow = (await Tenants()).Single(t => t.Code == "acme");
        Assert.Equal(("Acme Pharma Iraq", false), (acmeNow.Name, acmeNow.IsActive));
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync($"/platform/tenants/{acme.Id}", new { IsActive = true, Reason = "invoice paid" })).StatusCode);
        Assert.True((await Tenants()).Single(t => t.Code == "acme").IsActive);

        // The explicit cross-company read: a reason is needed, the audit row is written with it, an unknown company is 404 and writes nothing
        await AssertProblemAsync(await client.GetAsync($"/platform/tenants/{t1}/audit-logs"), HttpStatusCode.BadRequest, "INVALID_DATA");
        await AssertProblemAsync(await client.GetAsync($"/platform/tenants/{t1}/audit-logs?reason=short"), HttpStatusCode.BadRequest, "INVALID_DATA");
        await AssertProblemAsync(await client.GetAsync($"/platform/tenants/{t1}/audit-logs?reason=support+ticket+4711&limit=0"), HttpStatusCode.BadRequest, "INVALID_DATA");
        await AssertProblemAsync(await client.GetAsync($"/platform/tenants/{Guid.NewGuid()}/audit-logs?reason=support+ticket+4711"), HttpStatusCode.NotFound, "NOT_FOUND");
        var read = (await client.GetFromJsonAsync<List<LifeDrop.Application.Platform.TenantAuditEntryDto>>($"/platform/tenants/{t1}/audit-logs?reason=support+ticket+4711"))!;
        Assert.Equal(new[] { "payment.recorded", "customer.created" }, read.Select(r => r.Action).ToArray());   // newest first, only that company
        Assert.Equal("r@one", read[0].ActorEmail);
        var filtered = (await client.GetFromJsonAsync<List<LifeDrop.Application.Platform.TenantAuditEntryDto>>($"/platform/tenants/{t1}/audit-logs?reason=support+ticket+4711&action=customer.created"))!;
        Assert.Equal(new[] { "customer.created" }, filtered.Select(r => r.Action).ToArray());

        var audit = await PlatformAudit();
        Assert.Equal(new[] { "platform.tenant_audit_read", "platform.tenant_audit_read", "platform.tenant_activated", "platform.tenant_deactivated", "platform.tenant_renamed", "platform.tenant_created" },
            audit.Select(a => a.Action).ToArray());   // two reads, then the changes; the refused and invalid calls left nothing
        Assert.All(audit, a => Assert.Equal(("root@platform", admin), (a.ActorEmail, a.ActorId)));
        Assert.Equal(("one", "support ticket 4711"), (audit[0].TargetTenantCode, audit[0].Reason));
        Assert.Equal("action=customer.created", audit[0].Detail);
        Assert.Equal("unpaid invoice", audit.Single(a => a.Action == "platform.tenant_deactivated").Reason);
        Assert.Equal(new[] { "platform.tenant_audit_read", "platform.tenant_audit_read" }, (await PlatformAudit($"?tenantId={t1}")).Select(a => a.Action).ToArray());
        await AssertProblemAsync(await client.GetAsync("/platform/audit-logs?limit=500"), HttpStatusCode.BadRequest, "INVALID_DATA");

        // The platform audit cannot be changed or removed, not even by the application's own database role
        await using (var conn = new NpgsqlConnection(schemaBlock.ConnectionString))
        {
            await conn.OpenAsync();
            await Assert.ThrowsAsync<PostgresException>(async () => { await using var cmd = new NpgsqlCommand("DELETE FROM platform_audit_logs", conn); await cmd.ExecuteNonQueryAsync(); });
            await Assert.ThrowsAsync<PostgresException>(async () => { await using var cmd = new NpgsqlCommand("UPDATE platform_audit_logs SET reason = 'x'", conn); await cmd.ExecuteNonQueryAsync(); });
        }
    }

    [SkippableFact]
    public async Task SavedReports_OwnerSavesRunsAndSchedulesWithTheirOwnAreaAndFailedRunsAreKept()
    {
        var url = GetRequiredEnvVar("TEST_DATABASE_URL");
        await VerifyRoleAsync(url, expectElevated: false);

        var (schemaBlock, factory) = await StartFlexiblePlanHostAsync(url);   // clock: Monday 5 October 2026, 09:00 in Baghdad
        await using var _ = schemaBlock;
        await using var __ = factory;
        var conf = factory.Services.GetRequiredService<IConfiguration>();
        var client = factory.CreateClient();

        Guid t1 = Guid.NewGuid(), t2 = Guid.NewGuid(), manager = Guid.NewGuid(), manager2 = Guid.NewGuid(), temp = Guid.NewGuid(), rep = Guid.NewGuid(), other2 = Guid.NewGuid();
        await using (var conn = new NpgsqlConnection(schemaBlock.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(@"
                INSERT INTO tenants (id, name, code) VALUES (@t1, 'T1', 'T1'), (@t2, 'T2', 'T2');
                SELECT seed_default_roles(@t1);
                SELECT seed_default_roles(@t2);
                INSERT INTO users (id, tenant_id, email) VALUES (@m, @t1, 'manager@t1'), (@m2, @t1, 'manager2@t1'), (@tmp, @t1, 'temp@t1'), (@rep, @t1, 'rep@t1'), (@o2, @t2, 'manager@t2');
                INSERT INTO user_roles (tenant_id, user_id, role_id)
                SELECT @t1, u.id, r.id FROM (VALUES (@m, 'SalesManager'), (@m2, 'SalesManager'), (@tmp, 'SalesManager'), (@rep, 'SalesRep')) AS u(id, role)
                JOIN roles r ON r.tenant_id = @t1 AND r.name = u.role
                UNION ALL SELECT @t2, @o2, id FROM roles WHERE tenant_id = @t2 AND name = 'SalesManager';", conn);
            cmd.Parameters.AddWithValue("t1", t1); cmd.Parameters.AddWithValue("t2", t2); cmd.Parameters.AddWithValue("m", manager); cmd.Parameters.AddWithValue("m2", manager2);
            cmd.Parameters.AddWithValue("tmp", temp); cmd.Parameters.AddWithValue("rep", rep); cmd.Parameters.AddWithValue("o2", other2);
            await cmd.ExecuteNonQueryAsync();
        }
        void As(Guid tenant, Guid user) => client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            GenerateToken(tenant, user, conf["Jwt:SigningKey"]!, conf["Jwt:Issuer"]!, conf["Jwt:Audience"]!));
        object Weekly(string name = "Weekly KPIs") => new { Name = name, Range = "last_7_days", CustomerType = "Pharmacy", Schedule = new { Frequency = "weekly", Weekday = 1, Hour = 8 } };
        async Task<List<LifeDrop.Application.Reports.SavedReportDto>> Mine() => (await client.GetFromJsonAsync<List<LifeDrop.Application.Reports.SavedReportDto>>("/saved-reports"))!;
        async Task<List<LifeDrop.Application.Reports.ReportRunDto>> Runs(Guid id) => (await client.GetFromJsonAsync<List<LifeDrop.Application.Reports.ReportRunDto>>($"/saved-reports/{id}/runs"))!;
        async Task<int> Due(Guid tenant)
        {
            using var scope = factory.Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<LifeDrop.Application.Reports.SavedReportService>().RunDueAsync(new LifeDrop.Domain.Common.TenantId(tenant), CancellationToken.None);
        }
        async Task Sql(string sql, params (string, object)[] ps)
        {
            await using var conn = new NpgsqlConnection(schemaBlock.ConnectionString);
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand("SELECT set_config('app.current_tenant', @t::text, false); " + sql, conn);
            cmd.Parameters.AddWithValue("t", t1);
            foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v);
            await cmd.ExecuteNonQueryAsync();
        }

        // reports.read is needed
        As(t1, rep);
        await AssertProblemAsync(await client.GetAsync("/saved-reports"), HttpStatusCode.Forbidden, "FORBIDDEN");
        await AssertProblemAsync(await client.PostAsJsonAsync("/saved-reports", Weekly()), HttpStatusCode.Forbidden, "FORBIDDEN");

        // Validation
        As(t1, manager);
        foreach (var bad in new object[]
        {
            new { Name = " ", Range = "last_7_days" }, new { Name = "x", Range = "last_year" }, new { Name = "x" },
            new { Name = "x", Range = "fixed" }, new { Name = "x", Range = "fixed", From = "2026-10-05", To = "2026-10-01" },
            new { Name = "x", Range = "yesterday", From = "2026-10-01", To = "2026-10-02" },
            new { Name = "x", Range = "yesterday", Schedule = new { Frequency = "weekly", Hour = 8 } },
            new { Name = "x", Range = "yesterday", Schedule = new { Frequency = "daily", Weekday = 1, Hour = 8 } },
            new { Name = "x", Range = "yesterday", Schedule = new { Frequency = "daily", Hour = 24 } },
            new { Name = "x", Range = "yesterday", Schedule = new { Frequency = "monthly", DayOfMonth = 29, Hour = 8 } },
            new { Name = "x", Range = "yesterday", Schedule = new { Frequency = "hourly", Hour = 8 } },
            new { Name = "x", Range = "yesterday", RegionId = Guid.Empty }
        })
            await AssertProblemAsync(await client.PostAsJsonAsync("/saved-reports", bad), HttpStatusCode.BadRequest, "INVALID_DATA");

        // Save with a weekly schedule: the next run is Monday 12 October 08:00 Iraq time (05:00 UTC), because this Monday's 08:00 has passed
        var created = await client.PostAsJsonAsync("/saved-reports", Weekly());
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var reportId = (await created.Content.ReadFromJsonAsync<CreatedResponseDto>())!.Id;
        await AssertProblemAsync(await client.PostAsJsonAsync("/saved-reports", Weekly()), HttpStatusCode.Conflict, "CONFLICT");   // same name for the same owner
        var report = Assert.Single(await Mine());
        Assert.Equal(("Weekly KPIs", "last_7_days", "Pharmacy"), (report.Name, report.Range, report.CustomerType));
        Assert.Equal(("weekly", (int?)1, 8, true, (DateTimeOffset?)new DateTimeOffset(2026, 10, 12, 5, 0, 0, TimeSpan.Zero)), (report.Schedule!.Frequency, report.Schedule.Weekday, report.Schedule.Hour, report.Schedule.IsActive, report.Schedule.NextRunAt));
        As(t1, manager2);
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/saved-reports", Weekly())).StatusCode);   // another owner may use the same name

        // Only the owner: another user of the company and another company get 404 for everything
        foreach (var (tenant, user) in new[] { (t1, manager2), (t2, other2) })
        {
            As(tenant, user);
            Assert.DoesNotContain(await Mine(), r => r.Id == reportId);
            await AssertProblemAsync(await client.PostAsync($"/saved-reports/{reportId}/run", null), HttpStatusCode.NotFound, "NOT_FOUND");
            await AssertProblemAsync(await client.GetAsync($"/saved-reports/{reportId}/runs"), HttpStatusCode.NotFound, "NOT_FOUND");
            await AssertProblemAsync(await client.PutAsJsonAsync($"/saved-reports/{reportId}", Weekly("Renamed")), HttpStatusCode.NotFound, "NOT_FOUND");
            await AssertProblemAsync(await client.DeleteAsync($"/saved-reports/{reportId}"), HttpStatusCode.NotFound, "NOT_FOUND");
        }

        // Run now: the range is worked out in Iraq days, the result is kept, the schedule does not move
        As(t1, manager);
        var run = (await (await client.PostAsync($"/saved-reports/{reportId}/run", null)).Content.ReadFromJsonAsync<LifeDrop.Application.Reports.ReportRunDetailDto>())!;
        Assert.Equal(("manual", "ok", new DateOnly(2026, 9, 28), new DateOnly(2026, 10, 4)), (run.Run.Trigger, run.Run.Status, run.Run.From, run.Run.To));
        Assert.NotNull(run.Report);
        Assert.Equal(new[] { run.Run.Id }, (await Runs(reportId)).Select(r => r.Id).ToArray());
        var stored = (await client.GetFromJsonAsync<LifeDrop.Application.Reports.ReportRunDetailDto>($"/saved-reports/{reportId}/runs/{run.Run.Id}"))!;
        Assert.Equal((new DateOnly(2026, 9, 28), new DateOnly(2026, 10, 4)), (stored.Report!.From, stored.Report.To));
        Assert.Equal(new DateTimeOffset(2026, 10, 12, 5, 0, 0, TimeSpan.Zero), (await Mine()).Single().Schedule!.NextRunAt);
        await AssertProblemAsync(await client.GetAsync($"/saved-reports/{reportId}/runs?limit=31"), HttpStatusCode.BadRequest, "INVALID_DATA");
        await AssertProblemAsync(await client.GetAsync($"/saved-reports/{reportId}/runs/{Guid.NewGuid()}"), HttpStatusCode.NotFound, "NOT_FOUND");

        // The schedule: nothing is due yet; when due, it runs once as a scheduled run and moves to the next Monday
        Assert.Equal(0, await Due(t1));
        await Sql("UPDATE saved_reports SET next_run_at = '2026-10-01 00:00+00' WHERE id = @id", ("id", reportId));
        Assert.Equal(1, await Due(t1));
        Assert.Equal(0, await Due(t1));
        var after = await Runs(reportId);
        Assert.Equal(new[] { "manual", "schedule" }, after.Select(r => r.Trigger).OrderBy(t => t, StringComparer.Ordinal).ToArray());
        Assert.All(after, r => Assert.Equal("ok", r.Status));
        Assert.Equal(new DateTimeOffset(2026, 10, 12, 5, 0, 0, TimeSpan.Zero), (await Mine()).Single().Schedule!.NextRunAt);

        // Pause, remove and change the schedule
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync($"/saved-reports/{reportId}", new { Name = "Weekly KPIs", Range = "fixed", From = "2026-10-01", To = "2026-10-03",
            Schedule = new { Frequency = "daily", Hour = 12, IsActive = false } })).StatusCode);
        var paused = (await Mine()).Single();
        Assert.Equal(("fixed", (DateOnly?)new DateOnly(2026, 10, 1), false, (DateTimeOffset?)null), (paused.Range, paused.From, paused.Schedule!.IsActive, paused.Schedule.NextRunAt));
        await Sql("UPDATE saved_reports SET next_run_at = '2026-10-01 00:00+00' WHERE id = @id", ("id", reportId));
        Assert.Equal(0, await Due(t1));   // paused: never due
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync($"/saved-reports/{reportId}", new { Name = "Weekly KPIs", Range = "yesterday" })).StatusCode);
        Assert.Null((await Mine()).Single().Schedule);
        await AssertProblemAsync(await client.PutAsJsonAsync($"/saved-reports/{reportId}", new { Name = " " }), HttpStatusCode.BadRequest, "INVALID_DATA");

        // An owner who lost reports.read: the scheduled run is kept as failed and the schedule moves on
        As(t1, temp);
        var tempReport = (await (await client.PostAsJsonAsync("/saved-reports", new { Name = "Daily", Range = "yesterday", Schedule = new { Frequency = "daily", Hour = 7 } })).Content.ReadFromJsonAsync<CreatedResponseDto>())!.Id;
        await Sql("DELETE FROM user_roles WHERE user_id = @u; UPDATE saved_reports SET next_run_at = '2026-10-01 00:00+00' WHERE id = @id", ("u", temp), ("id", tempReport));
        Assert.Equal(1, await Due(t1));
        await using (var conn = new NpgsqlConnection(schemaBlock.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(@"SELECT set_config('app.current_tenant', @t::text, false);
                SELECT r.status, r.error_code, r.result IS NULL, s.next_run_at FROM report_runs r JOIN saved_reports s ON s.id = r.report_id WHERE r.report_id = @id", conn);
            cmd.Parameters.AddWithValue("t", t1); cmd.Parameters.AddWithValue("id", tempReport);
            await using var reader = await cmd.ExecuteReaderAsync();
            await reader.NextResultAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(("failed", "FORBIDDEN", true), (reader.GetString(0), reader.GetString(1), reader.GetBoolean(2)));
            Assert.Equal(new DateTimeOffset(2026, 10, 6, 4, 0, 0, TimeSpan.Zero), reader.GetFieldValue<DateTimeOffset>(3));   // tomorrow 07:00 Iraq time
        }

        // Deleting removes the runs too
        As(t1, manager);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/saved-reports/{reportId}")).StatusCode);
        await AssertProblemAsync(await client.DeleteAsync($"/saved-reports/{reportId}"), HttpStatusCode.NotFound, "NOT_FOUND");
        await AssertProblemAsync(await client.GetAsync($"/saved-reports/{reportId}/runs"), HttpStatusCode.NotFound, "NOT_FOUND");
        Assert.Empty(await Mine());
    }

    [SkippableFact]
    public async Task FormFields_ManagersDefineFieldsAndTheAssigneeAnswersThemWhileInVisit()
    {
        var url = GetRequiredEnvVar("TEST_DATABASE_URL");
        await VerifyRoleAsync(url, expectElevated: false);

        var (schemaBlock, factory) = await StartFlexiblePlanHostAsync(url);
        await using var _ = schemaBlock;
        await using var __ = factory;
        var conf = factory.Services.GetRequiredService<IConfiguration>();
        var client = factory.CreateClient();

        Guid t1 = Guid.NewGuid(), t2 = Guid.NewGuid(), rep = Guid.NewGuid(), rep2 = Guid.NewGuid(), manager = Guid.NewGuid(), manager2 = Guid.NewGuid(), c1 = Guid.NewGuid();
        Guid vDoctor = Guid.NewGuid(), vPharmacy = Guid.NewGuid(), vPlanned = Guid.NewGuid();
        await using (var conn = new NpgsqlConnection(schemaBlock.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(@"
                INSERT INTO tenants (id, name, code) VALUES (@t1, 'T1', 'T1'), (@t2, 'T2', 'T2');
                SELECT seed_default_roles(@t1);
                SELECT seed_default_roles(@t2);
                INSERT INTO users (id, tenant_id) VALUES (@rep, @t1), (@rep2, @t1), (@m, @t1), (@m2, @t2);
                INSERT INTO user_roles (tenant_id, user_id, role_id)
                SELECT @t1, @rep, id FROM roles WHERE tenant_id = @t1 AND name = 'SalesRep'
                UNION ALL SELECT @t1, @rep2, id FROM roles WHERE tenant_id = @t1 AND name = 'SalesRep'
                UNION ALL SELECT @t1, @m, id FROM roles WHERE tenant_id = @t1 AND name = 'SalesManager'
                UNION ALL SELECT @t2, @m2, id FROM roles WHERE tenant_id = @t2 AND name = 'SalesManager';
                SELECT set_config('app.current_tenant', @t1::text, false);
                INSERT INTO customers (id, tenant_id, type, code, name, geom, status, owner_user_id) VALUES
                    (@c1, @t1, 'Pharmacy', 'C1', 'Customer 1', ST_SetSRID(ST_MakePoint(44.0092, 36.1911), 4326)::geography, 'located', @rep);
                INSERT INTO visits (id, tenant_id, user_id, customer_id, status, schedule_date, visit_type, created_at) VALUES
                    (@vd, @t1, @rep, @c1, 'InVisit', '2026-10-05', 'doctor', now()), (@vp, @t1, @rep, @c1, 'InVisit', '2026-10-05', 'pharmacy', now()),
                    (@vpl, @t1, @rep, @c1, 'Planned', '2026-10-05', 'doctor', now());", conn);
            foreach (var (name, value) in new[] { ("t1", t1), ("t2", t2), ("rep", rep), ("rep2", rep2), ("m", manager), ("m2", manager2), ("c1", c1), ("vd", vDoctor), ("vp", vPharmacy), ("vpl", vPlanned) })
                cmd.Parameters.AddWithValue(name, value);
            await cmd.ExecuteNonQueryAsync();
        }
        void As(Guid tenant, Guid user) => client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            GenerateToken(tenant, user, conf["Jwt:SigningKey"]!, conf["Jwt:Issuer"]!, conf["Jwt:Audience"]!));
        object Field(string key, string type, string appliesTo = "all", string[]? options = null, bool required = false, int sort = 0) =>
            new { Key = key, Type = type, AppliesTo = appliesTo, LabelEn = "Label " + key, Options = options, IsRequired = required, SortOrder = sort };
        async Task<Guid> CreateField(object body)
        {
            var response = await client.PostAsJsonAsync("/form-fields", body);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<CreatedResponseDto>())!.Id;
        }
        async Task<List<LifeDrop.Application.Forms.FormFieldDto>> Fields(string query = "") =>
            (await client.GetFromJsonAsync<List<LifeDrop.Application.Forms.FormFieldDto>>("/form-fields" + query))!;
        Task<HttpResponseMessage> Answer(Guid visit, params (Guid Field, string? Value)[] answers) =>
            client.PutAsJsonAsync($"/visits/{visit}/report/custom", new { Answers = answers.Select(a => new { FieldId = a.Field, a.Value }).ToArray() });
        async Task<LifeDrop.Application.Forms.CustomAnswersDto> Saved(Guid visit) =>
            (await client.GetFromJsonAsync<LifeDrop.Application.Forms.CustomAnswersDto>($"/visits/{visit}/report/custom"))!;

        // Only forms.manage defines fields; everybody else just reads what they can answer
        As(t1, rep);
        await AssertProblemAsync(await client.PostAsJsonAsync("/form-fields", Field("notes", "text")), HttpStatusCode.Forbidden, "FORBIDDEN");
        Assert.Empty(await Fields());

        // Validation of a definition
        As(t1, manager);
        foreach (var bad in new object[]
        {
            Field("Bad Key", "text"), Field("1abc", "text"), Field("a", "text"), Field("ok_key", "file"), Field("ok_key", "text", "vet"),
            new { Key = "ok_key", Type = "text", AppliesTo = "all" },                                                    // no label
            new { Key = "ok_key", Type = "text", AppliesTo = "all", LabelEn = new string('x', 101) },
            Field("ok_key", "choice", options: new[] { "only" }), Field("ok_key", "choice", options: new[] { "a", "a" }), Field("ok_key", "choice"),
            Field("ok_key", "text", options: new[] { "a", "b" }), Field("ok_key", "text", sort: 1001)
        })
            await AssertProblemAsync(await client.PostAsJsonAsync("/form-fields", bad), HttpStatusCode.BadRequest, "INVALID_DATA");

        var chairs = await CreateField(new { Key = "chairs", Type = "number", AppliesTo = "doctor", LabelCkb = "ژمارەی کورسی", IsRequired = true });
        var rating = await CreateField(Field("rating", "choice", "pharmacy", new[] { "good", "bad" }));
        var open = await CreateField(Field("open_late", "yes_no", required: true));
        var next = await CreateField(Field("next_visit", "date"));
        var notes = await CreateField(Field("notes", "text"));
        await AssertProblemAsync(await client.PostAsJsonAsync("/form-fields", Field("notes", "number")), HttpStatusCode.Conflict, "CONFLICT");   // the key is unique in a company
        As(t2, manager2);
        await CreateField(Field("notes", "text"));   // another company may use it
        Assert.Single(await Fields());
        await AssertProblemAsync(await client.PutAsJsonAsync($"/form-fields/{notes}", new { IsRequired = true }), HttpStatusCode.NotFound, "NOT_FOUND");

        // What a rep sees: the fields of the visit type and of all types
        As(t1, rep);
        Assert.Equal(new[] { "chairs", "next_visit", "notes", "open_late" }, (await Fields("?visitType=doctor")).Select(f => f.Key).ToArray());
        Assert.Equal(new[] { "next_visit", "notes", "open_late", "rating" }, (await Fields("?visitType=pharmacy")).Select(f => f.Key).ToArray());
        await AssertProblemAsync(await client.GetAsync("/form-fields?visitType=vet"), HttpStatusCode.BadRequest, "INVALID_DATA");
        var chairsField = (await Fields("?visitType=doctor")).Single(f => f.Key == "chairs");
        Assert.Equal(("number", "doctor", true, "ژمارەی کورسی", (string?)null), (chairsField.Type, chairsField.AppliesTo, chairsField.IsRequired, chairsField.LabelCkb, chairsField.LabelEn));

        // Answers: required fields, the visit's type only, valid values, no duplicates
        foreach (var bad in new[]
        {
            new[] { (open, (string?)"true") },                                                                               // chairs is required for a doctor visit
            new[] { (chairs, (string?)"4"), (open, (string?)"true"), (rating, (string?)"good") },                           // rating is a pharmacy field
            new[] { (chairs, (string?)"abc"), (open, (string?)"true") }, new[] { (chairs, (string?)"1.2345"), (open, (string?)"true") },
            new[] { (chairs, (string?)"4"), (open, (string?)"yes") }, new[] { (chairs, (string?)"4"), (open, (string?)"true"), (next, (string?)"2026-13-01") },
            new[] { (chairs, (string?)"4"), (open, (string?)"true"), (notes, (string?)new string('x', 1001)) },
            new[] { (chairs, (string?)"4"), (chairs, (string?)"5"), (open, (string?)"true") }, new[] { (Guid.NewGuid(), (string?)"x"), (chairs, (string?)"4"), (open, (string?)"true") }
        })
            await AssertProblemAsync(await Answer(vDoctor, bad), HttpStatusCode.BadRequest, "INVALID_DATA");
        Assert.Equal(HttpStatusCode.NoContent, (await Answer(vDoctor, (chairs, "4.50"), (open, "true"), (notes, "  hello  "), (next, "2026-10-20"))).StatusCode);
        var answers = await Saved(vDoctor);
        Assert.Equal(new[] { "chairs=4.5", "next_visit=2026-10-20", "notes=hello", "open_late=true" }, answers.Answers.Select(a => $"{a.Key}={a.Value}").ToArray());
        As(t1, manager);
        Assert.Equal(4, (await Saved(vDoctor)).Answers.Count);   // the sales manager reads it
        As(t1, rep2);
        await AssertProblemAsync(await client.GetAsync($"/visits/{vDoctor}/report/custom"), HttpStatusCode.NotFound, "NOT_FOUND");
        await AssertProblemAsync(await Answer(vDoctor, (chairs, "1"), (open, "true")), HttpStatusCode.Forbidden, "FORBIDDEN");   // not the assignee

        // The set is replaced; a blank value removes an answer; a visit that is not in progress takes nothing
        As(t1, rep);
        Assert.Equal(HttpStatusCode.NoContent, (await Answer(vDoctor, (chairs, "5"), (open, "false"), (notes, ""))).StatusCode);
        Assert.Equal(new[] { "chairs=5", "open_late=false" }, (await Saved(vDoctor)).Answers.Select(a => $"{a.Key}={a.Value}").ToArray());
        await AssertProblemAsync(await Answer(vPlanned, (chairs, "1"), (open, "true")), HttpStatusCode.Conflict, "CONFLICT");
        await AssertProblemAsync(await Answer(Guid.NewGuid(), (open, "true")), HttpStatusCode.NotFound, "NOT_FOUND");
        Assert.Equal(HttpStatusCode.NoContent, (await Answer(vPharmacy, (open, "true"), (rating, "good"))).StatusCode);   // the pharmacy visit has no required doctor field
        await AssertProblemAsync(await Answer(vPharmacy, (open, "true"), (rating, "great")), HttpStatusCode.BadRequest, "INVALID_DATA");   // not one of the choices

        // Changing a definition: labels, choices, required, order; never the key, type or visit type; a retired field is hidden and no longer required
        await AssertProblemAsync(await client.PutAsJsonAsync($"/form-fields/{notes}", new { IsRequired = true }), HttpStatusCode.Forbidden, "FORBIDDEN");
        As(t1, manager);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync($"/form-fields/{rating}", new { LabelCkb = "هەڵسەنگاندن", Options = new[] { "good", "bad", "neutral" }, SortOrder = 7 })).StatusCode);
        var updated = (await Fields("?visitType=pharmacy")).Single(f => f.Key == "rating");
        Assert.Equal(new[] { "good", "bad", "neutral" }, updated.Options.ToArray());
        Assert.Equal(("Label rating", "هەڵسەنگاندن", 7), (updated.LabelEn, updated.LabelCkb, updated.SortOrder));   // a label that is not given keeps its value
        await AssertProblemAsync(await client.PutAsJsonAsync($"/form-fields/{notes}", new { Options = new[] { "a", "b" } }), HttpStatusCode.BadRequest, "INVALID_DATA");
        await AssertProblemAsync(await client.PutAsJsonAsync($"/form-fields/{notes}", new { LabelEn = " " }), HttpStatusCode.BadRequest, "INVALID_DATA");   // the only label cannot go
        await AssertProblemAsync(await client.PutAsJsonAsync($"/form-fields/{Guid.NewGuid()}", new { IsRequired = true }), HttpStatusCode.NotFound, "NOT_FOUND");
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync($"/form-fields/{open}", new { IsActive = false })).StatusCode);
        Assert.DoesNotContain(await Fields(), f => f.Key == "open_late");
        Assert.Contains(await Fields("?includeInactive=true"), f => f.Key == "open_late" && !f.IsActive);
        As(t1, rep);
        Assert.DoesNotContain(await Fields("?includeInactive=true"), f => f.Key == "open_late");   // inactive definitions are for the managers
        Assert.Equal(HttpStatusCode.NoContent, (await Answer(vDoctor, (chairs, "6"))).StatusCode);   // open_late is no longer required

        // Audit
        await using (var conn = new NpgsqlConnection(schemaBlock.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(@"SELECT string_agg(action || '=' || n, ',' ORDER BY action COLLATE ""C"") FROM (SELECT action, count(*) n FROM audit_logs WHERE tenant_id = @t AND action LIKE 'form_field.%' OR action = 'visit.custom_answers_saved' GROUP BY action) a", conn);
            cmd.Parameters.AddWithValue("t", t1);
            Assert.Equal("form_field.created=5,form_field.deactivated=1,form_field.updated=1,visit.custom_answers_saved=4", (string)(await cmd.ExecuteScalarAsync())!);
        }
    }

    [Fact]
    public void Observability_JobsGoStaleAfterThreeIntervalsAndTheHistogramIsCumulativeAndEscaped()
    {
        var jobs = new LifeDrop.API.Observability.JobHealth();
        var t0 = new DateTimeOffset(2026, 10, 5, 6, 0, 0, TimeSpan.Zero);
        var interval = TimeSpan.FromMinutes(5);
        jobs.Report("scan", true, interval, t0);
        jobs.Report("broken", false, interval, t0);
        Assert.False(jobs.Snapshot(t0.AddMinutes(14)).Single(j => j.Name == "scan").Stale);
        Assert.True(jobs.Snapshot(t0.AddMinutes(16)).Single(j => j.Name == "scan").Stale);
        Assert.True(jobs.Snapshot(t0).Single(j => j.Name == "broken").Stale);   // only ever failed
        jobs.Report("broken", true, interval, t0.AddMinutes(1));
        Assert.False(jobs.Snapshot(t0.AddMinutes(2)).Single(j => j.Name == "broken").Stale);   // one success clears it
        Assert.Equal((2L, 1L), (jobs.Snapshot(t0).Single(j => j.Name == "broken").Runs, jobs.Snapshot(t0).Single(j => j.Name == "broken").Failures));

        var metrics = new LifeDrop.API.Observability.RequestMetrics();
        metrics.Record("/x/{id}", "GET", 200, 0.04);
        metrics.Record("/x/{id}", "GET", 200, 0.3);
        metrics.Record("/x/{id}", "GET", 200, 20);
        metrics.Record("/we\"ird\\route", "POST", 500, 0.01);
        var text = metrics.Render(jobs, t0);
        Assert.Contains("lifedrop_http_requests_total{route=\"/x/{id}\",method=\"GET\",status=\"200\"} 3", text);
        Assert.Contains("lifedrop_http_request_duration_seconds_bucket{route=\"/x/{id}\",method=\"GET\",status=\"200\",le=\"0.05\"} 1", text);
        Assert.Contains("lifedrop_http_request_duration_seconds_bucket{route=\"/x/{id}\",method=\"GET\",status=\"200\",le=\"0.25\"} 1", text);
        Assert.Contains("lifedrop_http_request_duration_seconds_bucket{route=\"/x/{id}\",method=\"GET\",status=\"200\",le=\"0.5\"} 2", text);
        Assert.Contains("lifedrop_http_request_duration_seconds_bucket{route=\"/x/{id}\",method=\"GET\",status=\"200\",le=\"10\"} 2", text);
        Assert.Contains("lifedrop_http_request_duration_seconds_bucket{route=\"/x/{id}\",method=\"GET\",status=\"200\",le=\"+Inf\"} 3", text);
        Assert.Contains("lifedrop_http_request_duration_seconds_sum{route=\"/x/{id}\",method=\"GET\",status=\"200\"} 20.34", text);
        Assert.Contains("route=\"/we\\\"ird\\\\route\",method=\"POST\",status=\"500\"", text);
        Assert.Contains("lifedrop_job_failures_total{job=\"broken\"} 1", text);
        Assert.Contains($"lifedrop_job_last_success_timestamp_seconds{{job=\"scan\"}} {t0.ToUnixTimeSeconds()}", text);
    }

    [SkippableFact]
    public async Task Observability_HealthAndTokenProtectedMetricsCarryNoBusinessData()
    {
        var url = GetRequiredEnvVar("TEST_DATABASE_URL");
        await VerifyRoleAsync(url, expectElevated: false);

        var (schemaBlock, factory) = await StartFlexiblePlanHostAsync(url);
        await using var _ = schemaBlock;
        await using var __ = factory;
        await using var secured = factory.WithWebHostBuilder(b => b.UseSetting("Metrics:Token", "secret-token"));
        var open = factory.CreateClient();
        var client = secured.CreateClient();

        // Without a token configured the metrics do not exist
        Assert.Equal(HttpStatusCode.NotFound, (await open.GetAsync("/metrics")).StatusCode);

        // Liveness and readiness (database up) are open and are not rate limited
        for (var i = 0; i < 3; i++) Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
        var ready = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        using (var body = System.Text.Json.JsonDocument.Parse(await ready.Content.ReadAsStringAsync()))
            Assert.Equal(("ready", "up"), (body.RootElement.GetProperty("status").GetString(), body.RootElement.GetProperty("database").GetString()));
        for (var batch = 0; batch < 6; batch++)   // 120 requests in six groups of 20 at once: far above the 100 per minute of the API, and the connection pool is not the test
        {
            var burst = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => client.GetAsync("/health/ready")));
            Assert.All(burst, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));   // monitors are never refused
        }

        // A business route and a path with an id: only the route template is a label
        var secret = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/notifications/unread-count")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync($"/orders/{secret}")).StatusCode);

        // The token is needed
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/metrics")).StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "wrong");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/metrics")).StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "secret-token");

        // Jobs report into readiness and the metrics; a job that only ever failed makes readiness "degraded" (still 200)
        var jobs = secured.Services.GetRequiredService<LifeDrop.API.Observability.JobHealth>();
        var now = secured.Services.GetRequiredService<TimeProvider>().GetUtcNow();
        jobs.Report("test_job", true, TimeSpan.FromMinutes(5), now);
        jobs.Report("bad_job", false, TimeSpan.FromMinutes(5), now);
        using (var body = System.Text.Json.JsonDocument.Parse(await (await client.GetAsync("/health/ready")).Content.ReadAsStringAsync()))
        {
            Assert.Equal("degraded", body.RootElement.GetProperty("status").GetString());
            var list = body.RootElement.GetProperty("jobs").EnumerateArray().ToDictionary(j => j.GetProperty("name").GetString()!, j => j.GetProperty("stale").GetBoolean());
            Assert.Equal((false, true), (list["test_job"], list["bad_job"]));
        }

        var metrics = await client.GetAsync("/metrics");
        Assert.Equal(HttpStatusCode.OK, metrics.StatusCode);
        Assert.StartsWith("text/plain", metrics.Content.Headers.ContentType!.MediaType);
        var text = await metrics.Content.ReadAsStringAsync();
        Assert.Contains("lifedrop_http_requests_total{route=\"/health/live\",method=\"GET\",status=\"200\"} 3", text);
        Assert.Contains("lifedrop_http_requests_total{route=\"/health/ready\",method=\"GET\",status=\"200\"} 122", text);   // 1 + 120 + the one with the jobs
        Assert.Contains("lifedrop_http_requests_total{route=\"/notifications/unread-count\",method=\"GET\",status=\"401\"} 1", text);
        Assert.Contains("lifedrop_http_requests_total{route=\"/metrics\",method=\"GET\",status=\"401\"} 2", text);
        Assert.Contains("method=\"GET\",status=\"401\"} 1", text);
        Assert.DoesNotContain(secret.ToString(), text);   // ids of the path never reach the metrics
        Assert.Contains("lifedrop_job_runs_total{job=\"test_job\"} 1", text);
        Assert.Contains("lifedrop_job_failures_total{job=\"bad_job\"} 1", text);
        Assert.Contains($"lifedrop_job_last_success_timestamp_seconds{{job=\"test_job\"}} {now.ToUnixTimeSeconds()}", text);
        Assert.DoesNotContain("lifedrop_job_last_success_timestamp_seconds{job=\"bad_job\"}", text);   // it never succeeded
    }

    [SkippableFact]
    public async Task CompanyConfig_ReturnsPublicFactsOnlyForActiveCompanies()
    {
        var url = GetRequiredEnvVar("TEST_DATABASE_URL");
        await VerifyRoleAsync(url, expectElevated: false);

        var (schemaBlock, factory) = await StartFlexiblePlanHostAsync(url);
        await using var _ = schemaBlock;
        await using var __ = factory;
        await using (var conn = new NpgsqlConnection(schemaBlock.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(@"
                INSERT INTO tenants (id, name, code, is_active) VALUES
                  (gen_random_uuid(), 'Acme Trading', 'acme', true),
                  (gen_random_uuid(), 'Moved Co', 'moved', true),
                  (gen_random_uuid(), 'Bad Url Co', 'badurl', true),
                  (gen_random_uuid(), 'Closed Co', 'closed', false);", conn);
            await cmd.ExecuteNonQueryAsync();
        }
        await using var configured = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("CompanyDirectory:moved:ApiBaseUrl", "https://api.moved.example/");
            b.UseSetting("CompanyDirectory:badurl:ApiBaseUrl", "http://insecure.example");
            b.UseSetting("Branding:SupportPhone", "+9647510686828");
            b.UseSetting("Company:Industry", "food");
        });
        var client = configured.CreateClient();

        using (var acme = await (await client.GetAsync("/company-config/ACME")).Content.ReadFromJsonAsync<System.Text.Json.JsonDocument>())
        {
            Assert.Equal("acme", acme!.RootElement.GetProperty("tenantCode").GetString());
            Assert.Equal("Acme Trading", acme.RootElement.GetProperty("name").GetString());
            Assert.Equal(System.Text.Json.JsonValueKind.Null, acme.RootElement.GetProperty("apiBaseUrl").ValueKind);   // default: this server
            Assert.Equal("+9647510686828", acme.RootElement.GetProperty("supportPhone").GetString());
            Assert.Equal("food", acme.RootElement.GetProperty("industry").GetString());
        }
        using (var moved = await (await client.GetAsync("/company-config/moved")).Content.ReadFromJsonAsync<System.Text.Json.JsonDocument>())
            Assert.Equal("https://api.moved.example", moved!.RootElement.GetProperty("apiBaseUrl").GetString());
        using (var bad = await (await client.GetAsync("/company-config/badurl")).Content.ReadFromJsonAsync<System.Text.Json.JsonDocument>())
            Assert.Equal(System.Text.Json.JsonValueKind.Null, bad!.RootElement.GetProperty("apiBaseUrl").ValueKind);   // an insecure entry is ignored

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/company-config/closed")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/company-config/nobody")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/company-config/a%27%3B--")).StatusCode);   // not a company code
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










