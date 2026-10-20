using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Xunit;
using LifeDrop.API;
using LifeDrop.Application.Auth;
using LifeDrop.Application.Customers;
using LifeDrop.Application.Devices;
using System.Text.Json;
using System.Runtime.ExceptionServices;

namespace LifeDrop.Database.Tests;

public class DeploymentRehearsalTests
{
    private string GetRequiredEnvVar(string key)
    {
        var val = Environment.GetEnvironmentVariable(key);
        if (string.IsNullOrWhiteSpace(val)) throw new SkipException($"{key} not set. Skipping test.");
        return val;
    }

    [SkippableFact]
    public async Task Rehearse_LeastPrivilege_Deployment()
    {
        var schemaId = Guid.NewGuid().ToString("N");
        await RunRehearsal(failIntentionally: false, schemaId: schemaId);
    }

    [SkippableFact]
    public async Task Rehearse_CleanupRunsOnFailure()
    {
        var schemaId = Guid.NewGuid().ToString("N");
        var ex = await Assert.ThrowsAsync<Exception>(() => RunRehearsal(failIntentionally: true, schemaId: schemaId));
        Assert.Equal("Intentional failure after resource creation", ex.Message);

        // Intentional-failure test asserts that the exact resources are absent.
        var superUrl = GetRequiredEnvVar("TEST_SUPERUSER_DATABASE_URL");
        await using var verifyConn = new NpgsqlConnection(superUrl);
        await verifyConn.OpenAsync();
        
        var schema = "rehearsal_" + schemaId;
        var appRole = "app_rehearsal_" + schemaId;

        await using var checkSchemaCmd = new NpgsqlCommand("SELECT COUNT(*) FROM information_schema.schemata WHERE schema_name = @s", verifyConn);
        checkSchemaCmd.Parameters.AddWithValue("s", schema);
        var schemaCount = Convert.ToInt64(await checkSchemaCmd.ExecuteScalarAsync());
        Assert.Equal(0, schemaCount);

        await using var checkRoleCmd = new NpgsqlCommand("SELECT COUNT(*) FROM pg_roles WHERE rolname = @r", verifyConn);
        checkRoleCmd.Parameters.AddWithValue("r", appRole);
        var roleCount = Convert.ToInt64(await checkRoleCmd.ExecuteScalarAsync());
        Assert.Equal(0, roleCount);
    }

    private async Task RunRehearsal(bool failIntentionally, string schemaId)
    {
        var superUrl = GetRequiredEnvVar("TEST_SUPERUSER_DATABASE_URL");
        var builder = new NpgsqlConnectionStringBuilder(superUrl);
        var dbName = builder.Database;
        if (!dbName.StartsWith("lifedrop_test_")) throw new Exception("Must use a test database!");

        var schema = "rehearsal_" + schemaId;
        var appRole = "app_rehearsal_" + schemaId;
        var appPassword = "rehearsalpass";
        
        Exception? testException = null;
        Exception? cleanupException = null;
        
        try
        {
            await using var setupConn = new NpgsqlConnection(superUrl);
            await setupConn.OpenAsync();
            
            // 1. CREATE SCHEMA
            await using (var cmd = new NpgsqlCommand($"CREATE SCHEMA {schema}", setupConn))
                await cmd.ExecuteNonQueryAsync();

            // 2. RUN MIGRATIONS
            string[] migrationFiles = { "0001_role_assignment.sql", "0002_login.sql", "0003_sessions_devices.sql", "0004_customers.sql", "0005_dev_admin_customer_permissions.sql", "0006_visits.sql", "0007_regions_teams.sql", "0008_role_data_scope.sql", "0009_default_roles.sql", "0010_visit_planning.sql", "0011_visit_cancel_permission.sql", "0012_customer_location.sql", "0013_visit_reports.sql", "0014_visit_location_events.sql", "0015_visit_status_history.sql", "0016_shifts_tracking.sql", "0017_offline_checkpoints.sql", "0018_customer_approval.sql", "0019_live_map.sql", "0020_flexible_planning.sql", "0021_places_customer_profile.sql", "0022_region_rep_notifications.sql", "0023_customer_priority.sql", "0024_catalog_prices.sql", "0025_city_prices_discounts.sql", "0026_orders_warehouses.sql", "0027_warehouse_work.sql", "0028_delivery.sql", "0029_payments.sql", "0030_samples.sql", "0031_visit_report_details.sql", "0032_follow_ups.sql", "0033_alerts.sql", "0034_antifraud.sql", "0035_route_history.sql", "0036_reports.sql", "0037_push.sql", "0038_platform.sql", "0039_saved_reports.sql", "0040_custom_fields.sql", "0041_antifraud_visits.sql", "0042_channels.sql", "0043_customer_archive.sql", "0044_manager_records_samples.sql", "0045_customer_contact.sql", "0046_manager_messages.sql", "0047_customer_doctor_fields.sql" };
            var migSql = $"SET search_path TO {schema}, public; ";
            foreach(var f in migrationFiles) {
                migSql += await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, f)) + "\n";
            }
            await using (var cmd = new NpgsqlCommand(migSql, setupConn))
                await cmd.ExecuteNonQueryAsync();

            // 3. CREATE ROLE & GRANTS
            await using (var cmd = new NpgsqlCommand($@"
                CREATE ROLE {appRole} WITH LOGIN PASSWORD '{appPassword}' NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE;
                GRANT USAGE ON SCHEMA {schema} TO {appRole};
                GRANT USAGE ON SCHEMA public TO {appRole};
                SET search_path TO {schema};
                {(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "lifedrop_app_grants.sql"))).Replace("lifedrop_app", appRole)}
            ", setupConn))
            {
                await cmd.ExecuteNonQueryAsync();
            }

            // 4. SEED BOOTSTRAP DATA
            var tenantId = Guid.NewGuid();
            var adminId = Guid.NewGuid();
            var adminRole = Guid.NewGuid();
            var fieldRole = Guid.NewGuid();
            string realHash = LifeDrop.Infrastructure.Auth.PasswordHash.Create("password123");

            await using (var cmd = new NpgsqlCommand($@"
                SET search_path TO {schema}, public;
                INSERT INTO tenants (id, name, code) VALUES (@t, 'Dev Tenant', 'dev');
                INSERT INTO users (id, tenant_id, email, password_hash) VALUES (@a, @t, 'admin@rehearsal.local', @h);
                INSERT INTO roles (id, tenant_id, name) VALUES (@r, @t, 'DevAdmin'), (@f, @t, 'FieldRep');
                INSERT INTO role_permissions (tenant_id, role_id, permission_key) VALUES 
                    (@t, @r, 'device.approve'), (@t, @r, 'user.manageroles'), 
                    (@t, @r, 'customers.write'), (@t, @r, 'customers.read'),
                    (@t, @r, 'visits.write'), (@t, @r, 'visits.fail'), (@t, @r, 'regions.manage'), (@t, @r, 'regions.read'),
                    (@t, @r, 'teams.manage'), (@t, @r, 'teams.read'), (@t, @r, 'audit.read'), (@t, @r, 'sessions.kill'), (@t, @r, 'customers.location.write'), (@t, @r, 'customers.geofence.write');
                INSERT INTO user_roles (tenant_id, user_id, role_id) VALUES (@t, @a, @r);
            ", setupConn))
            {
                cmd.Parameters.AddWithValue("t", tenantId);
                cmd.Parameters.AddWithValue("a", adminId);
                cmd.Parameters.AddWithValue("r", adminRole);
                cmd.Parameters.AddWithValue("f", fieldRole);
                cmd.Parameters.AddWithValue("h", realHash);
                await cmd.ExecuteNonQueryAsync();
            }

            if (failIntentionally) throw new Exception("Intentional failure after resource creation");

            // 5. BOOT HOST WITH RUNTIME ROLE
            var appBuilder = new NpgsqlConnectionStringBuilder(superUrl)
            {
                Username = appRole,
                Password = appPassword,
                SearchPath = $"{schema},public" // PostGIS types and functions live in public
            };
            var appConnString = appBuilder.ConnectionString;

            var logs = new CapturingLoggerProvider();
            using (var factory = new WebApplicationFactory<Program>()
                .WithWebHostBuilder(webBuilder =>
                {
                    webBuilder.UseSetting("ConnectionStrings:LifeDrop", appConnString);
                    TestHostSettings.UseTestJwt(webBuilder);
                    webBuilder.ConfigureLogging(logging => logging.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Debug).AddProvider(logs));
                }))
            {
                using var client = factory.CreateClient();

                // Note on PostGIS Verification: 
                // This test verifies API routing, isolation, and schema access. It does NOT set or read the PostGIS `geom` field on customers.

                var loginReq = new LoginRequest("dev", "admin@rehearsal.local", "password123");
                var loginResp = await client.PostAsJsonAsync("/auth/login", loginReq);
                Assert.Equal(HttpStatusCode.OK, loginResp.StatusCode);
                var tokens = await loginResp.Content.ReadFromJsonAsync<LoginResponse>();

                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens!.AccessToken);

                var refreshReq = new RefreshRequest(tokens.RefreshToken);
                var refreshResp = await client.PostAsJsonAsync("/auth/refresh", refreshReq);
                Assert.Equal(HttpStatusCode.OK, refreshResp.StatusCode);

                var regReq = new RegisterDeviceRequest("dev", "admin@rehearsal.local", "password123", "My Phone", "android", "inst123", "1.0.0");
                var regResp = await client.PostAsJsonAsync("/auth/devices/register", regReq);
                Assert.Equal(HttpStatusCode.OK, regResp.StatusCode);
                var deviceData = await regResp.Content.ReadFromJsonAsync<RegisterDeviceResponse>();

                var approveResp = await client.PostAsync($"/devices/{deviceData!.DeviceId}/approve", null);
                Assert.Equal(HttpStatusCode.NoContent, approveResp.StatusCode);

                var assignResp = await client.PostAsync($"/users/{adminId}/roles/{fieldRole}", null);
                Assert.Equal(HttpStatusCode.NoContent, assignResp.StatusCode);

                var custReq = new CreateCustomerRequest("Clinic", "C999", "Test Clinic");
                var custResp = await client.PostAsJsonAsync("/customers", custReq);
                Assert.Equal(HttpStatusCode.Created, custResp.StatusCode);
                
                var location = custResp.Headers.Location!.ToString();
                var custIdString = location.Split('/')[2];
                var custId = Guid.Parse(custIdString);

                var getResp = await client.GetAsync($"/customers/{custId}");
                Assert.Equal(HttpStatusCode.OK, getResp.StatusCode);
                
                var json = await getResp.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                Assert.Equal("C999", doc.RootElement.GetProperty("code").GetString());
                Assert.Equal("Test Clinic", doc.RootElement.GetProperty("name").GetString());

                // Every endpoint group through the restricted role and db/grants/lifedrop_app_grants.sql (plan step 0.9)
                async Task<Guid> CreatedIdAsync(HttpResponseMessage response)
                {
                    Assert.Equal(HttpStatusCode.Created, response.StatusCode);
                    using var body = await response.Content.ReadFromJsonAsync<JsonDocument>();
                    return body!.RootElement.GetProperty("id").GetGuid();
                }
                var regionId = await CreatedIdAsync(await client.PostAsJsonAsync("/regions", new { Name = "Erbil" }));
                var teamId = await CreatedIdAsync(await client.PostAsJsonAsync("/teams", new { Name = "Team A", RegionId = regionId }));
                Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsync($"/teams/{teamId}/members/{adminId}", null)).StatusCode);
                Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/teams")).StatusCode);
                var visitId = await CreatedIdAsync(await client.PostAsJsonAsync("/visits", new { AssignedUserId = adminId, CustomerId = custId, ScheduleDate = "2026-10-05" }));
                Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/visits?scheduleDate=2026-10-05")).StatusCode);
                Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync($"/visits/{visitId}/en-route", null)).StatusCode);
                Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync($"/visits/{visitId}/fail", new { Reason = "Closed" })).StatusCode);
                Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync($"/customers/{custId}/location", new { Latitude = 36.19, Longitude = 44.01, Source = "gps", AccuracyMeters = 8.5 })).StatusCode);
                Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync($"/customers/{custId}/geofence", new { RadiusMeters = 50 })).StatusCode);
                var reportVisit = await CreatedIdAsync(await client.PostAsJsonAsync("/visits", new { AssignedUserId = adminId, CustomerId = custId, ScheduleDate = "2026-10-05" }));
                foreach (var transition in new[] { "en-route", "arrive" })
                    Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync($"/visits/{reportVisit}/{transition}", null)).StatusCode);
                // Check-in needs a token bound to the approved device and a fresh fix inside the 50 m radius set above.
                var deviceLogin = await client.PostAsJsonAsync("/auth/login", new { TenantCode = "dev", Email = "admin@rehearsal.local", Password = "password123", DeviceId = deviceData.DeviceId, DeviceSecret = deviceData.DeviceSecret });
                Assert.Equal(HttpStatusCode.OK, deviceLogin.StatusCode);
                var deviceTokens = await deviceLogin.Content.ReadFromJsonAsync<LoginResponse>();
                using (var checkIn = new HttpRequestMessage(HttpMethod.Post, $"/visits/{reportVisit}/start")
                {
                    Content = JsonContent.Create(new { Latitude = 36.19005, Longitude = 44.01005, AccuracyMeters = 6, CapturedAt = DateTimeOffset.UtcNow })
                })
                {
                    checkIn.Headers.Authorization = new AuthenticationHeaderValue("Bearer", deviceTokens!.AccessToken);
                    Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(checkIn)).StatusCode);
                }
                Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync($"/visits/{reportVisit}/report", new { Outcome = "products_presented", Note = "Presented the new range" })).StatusCode);
                Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/visits/{reportVisit}/report")).StatusCode);
                using (var checkOut = new HttpRequestMessage(HttpMethod.Post, $"/visits/{reportVisit}/complete")
                {
                    Content = JsonContent.Create(new { Latitude = 36.19005, Longitude = 44.01005, AccuracyMeters = 6, CapturedAt = DateTimeOffset.UtcNow })
                })
                {
                    checkOut.Headers.Authorization = new AuthenticationHeaderValue("Bearer", deviceTokens!.AccessToken);
                    Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(checkOut)).StatusCode);
                }
                // Shift and tracking (step 1.13) through the restricted role
                async Task<HttpResponseMessage> AsDevice(HttpMethod method, string path, object? body = null)
                {
                    using var request = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body) };
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", deviceTokens!.AccessToken);
                    return await client.SendAsync(request);
                }
                var shiftStart = await AsDevice(HttpMethod.Post, "/shifts");
                Assert.Equal(HttpStatusCode.Created, shiftStart.StatusCode);
                var shiftId = (await shiftStart.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("id").GetGuid();
                var uploaded = await AsDevice(HttpMethod.Post, $"/shifts/{shiftId}/points", new
                {
                    Points = new[] { new { ClientPointId = Guid.NewGuid(), Latitude = 36.19005, Longitude = 44.01005, AccuracyMeters = 9, CapturedAt = DateTimeOffset.UtcNow } }
                });
                Assert.Equal(HttpStatusCode.OK, uploaded.StatusCode);
                Assert.Equal(1, (await uploaded.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("accepted").GetInt32());
                Assert.Equal(HttpStatusCode.OK, (await AsDevice(HttpMethod.Get, $"/shifts/{shiftId}/summary")).StatusCode);
                Assert.Equal(HttpStatusCode.NoContent, (await AsDevice(HttpMethod.Post, $"/shifts/{shiftId}/end")).StatusCode);
                Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/audit-logs?limit=5")).StatusCode);
                Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/teams/{teamId}/members/{adminId}")).StatusCode);
                Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync($"/users/{adminId}/sessions/revoke", null)).StatusCode);
                Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/teams")).StatusCode);

                // No secret reaches the application logs (PRD §2)
                Assert.NotEmpty(logs.Messages);
                var refreshed = await refreshResp.Content.ReadFromJsonAsync<LoginResponse>();
                foreach (var secret in new[] { "password123", tokens.AccessToken, tokens.RefreshToken, refreshed!.AccessToken, refreshed.RefreshToken, deviceData.DeviceSecret })
                    Assert.DoesNotContain(logs.Messages, message => message.Contains(secret, StringComparison.Ordinal));
            } 
        }
        catch (Exception ex)
        {
            testException = ex;
        }
        finally
        {
            try
            {
                await using var cleanupConn = new NpgsqlConnection(superUrl);
                await cleanupConn.OpenAsync();

                // Strict guards for exact names before issuing DROP
                if (schema == "rehearsal_" + schemaId && appRole == "app_rehearsal_" + schemaId && dbName.StartsWith("lifedrop_test_"))
                {
                    await using (var cmd = new NpgsqlCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE;", cleanupConn))
                        await cmd.ExecuteNonQueryAsync();
                        
                    await using (var cmd = new NpgsqlCommand($"REVOKE USAGE ON SCHEMA public FROM {appRole};", cleanupConn))
                        await cmd.ExecuteNonQueryAsync();

                    await using (var cmd = new NpgsqlCommand($"DROP ROLE IF EXISTS {appRole};", cleanupConn))
                        await cmd.ExecuteNonQueryAsync();
                }
            }
            catch (Exception ex)
            {
                cleanupException = ex;
            }
            
            Exception? verifyException = null;
            try
            {
                await using var verifyConn = new NpgsqlConnection(superUrl);
                await verifyConn.OpenAsync();
                
                await using var checkSchemaCmd = new NpgsqlCommand("SELECT COUNT(*) FROM information_schema.schemata WHERE schema_name = @s", verifyConn);
                checkSchemaCmd.Parameters.AddWithValue("s", schema);
                var schemaCount = Convert.ToInt64(await checkSchemaCmd.ExecuteScalarAsync());
                if (schemaCount != 0) throw new Exception("Schema was not cleaned up.");

                await using var checkRoleCmd = new NpgsqlCommand("SELECT COUNT(*) FROM pg_roles WHERE rolname = @r", verifyConn);
                checkRoleCmd.Parameters.AddWithValue("r", appRole);
                var roleCount = Convert.ToInt64(await checkRoleCmd.ExecuteScalarAsync());
                if (roleCount != 0) throw new Exception("Role was not cleaned up.");
            }
            catch (Exception ex)
            {
                verifyException = ex;
            }

            var exceptions = new System.Collections.Generic.List<Exception>();
            if (testException != null) exceptions.Add(testException);
            if (cleanupException != null) exceptions.Add(new Exception("Cleanup failed.", cleanupException));
            if (verifyException != null) exceptions.Add(new Exception("Verification failed.", verifyException));

            if (exceptions.Count == 1 && testException != null)
            {
                ExceptionDispatchInfo.Capture(testException).Throw();
            }
            else if (exceptions.Count > 0)
            {
                throw new AggregateException("Multiple failures occurred.", exceptions);
            }
        }
    }
}

public class LoginResponse { public string AccessToken { get; set; } = ""; public string RefreshToken { get; set; } = ""; }
public class RegisterDeviceResponse { public Guid DeviceId { get; set; } public string DeviceSecret { get; set; } = ""; }

// Collects every log message (including exceptions) written by the API host.
public sealed class CapturingLoggerProvider : Microsoft.Extensions.Logging.ILoggerProvider
{
    public System.Collections.Concurrent.ConcurrentQueue<string> Messages { get; } = new();
    public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) => new Logger(Messages);
    public void Dispose() { }

    private sealed class Logger(System.Collections.Concurrent.ConcurrentQueue<string> messages) : Microsoft.Extensions.Logging.ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            messages.Enqueue(formatter(state, exception) + (exception is null ? "" : " " + exception));
    }
}


