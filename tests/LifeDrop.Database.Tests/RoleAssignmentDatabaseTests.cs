using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using LifeDrop.Application.Roles;
using LifeDrop.Domain.Common;
using LifeDrop.Infrastructure.Roles;
using LifeDrop.Infrastructure.Auth;
using Npgsql;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace LifeDrop.Database.Tests;

public class RoleAssignmentDatabaseTests
{
    [Fact]
    public async Task MigrationEnforcesPermissionsTenantBoundaryAndAudit()
    {
        var url = Environment.GetEnvironmentVariable("TEST_DATABASE_URL")
            ?? throw new InvalidOperationException("TEST_DATABASE_URL must name an isolated lifedrop_test_* database.");
        var builder = new NpgsqlConnectionStringBuilder(url);
        if (builder.Database is null || !builder.Database.StartsWith("lifedrop_test_", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Refusing to test a database outside lifedrop_test_*.");

        var schema = "role_test_" + Guid.NewGuid().ToString("N");
        await using var setup = new NpgsqlConnection(url);
        await setup.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE SCHEMA {schema}", setup))
            await create.ExecuteNonQueryAsync();

        try
        {
            builder.SearchPath = schema;
            await using var dataSource = NpgsqlDataSource.Create(builder.ConnectionString);
            await using var connection = await dataSource.OpenConnectionAsync();
            var migration = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "0001_role_assignment.sql"));
            await using (var migrate = new NpgsqlCommand(migration, connection))
                await migrate.ExecuteNonQueryAsync();
            var loginMigration = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "0002_login.sql"));
            await using (var migrate = new NpgsqlCommand(loginMigration, connection))
                await migrate.ExecuteNonQueryAsync();
            var sessionMigration = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "0003_sessions_devices.sql"));
            await using (var migrate = new NpgsqlCommand(sessionMigration, connection))
                await migrate.ExecuteNonQueryAsync();

            var tenant = Guid.NewGuid();
            var otherTenant = Guid.NewGuid();
            var actor = Guid.NewGuid();
            var unprivileged = Guid.NewGuid();
            var target = Guid.NewGuid();
            var managerRole = Guid.NewGuid();
            var targetRole = Guid.NewGuid();
            var foreignRole = Guid.NewGuid();
            var foreignUser = Guid.NewGuid();
            var foreignDevice = Guid.NewGuid();
            var passwordHash = PasswordHash.Create("correct-password-for-test");
            var fieldPasswordHash = PasswordHash.Create("field-password-for-test");
            await using (var seed = new NpgsqlCommand("""
                INSERT INTO tenants(id,name,code) VALUES
                  (@tenant,'Tenant A','a'),(@other,'Tenant B','b');
                INSERT INTO users(id,tenant_id) VALUES
                  (@actor,@tenant),(@unprivileged,@tenant),(@target,@tenant),
                  (@foreignUser,@other);
                UPDATE users SET email='manager@example.test', password_hash=@password_hash
                  WHERE id=@actor;
                UPDATE users SET email='ordinary@example.test', password_hash=@password_hash
                  WHERE id=@unprivileged;
                UPDATE users SET email='field@example.test', password_hash=@field_hash
                  WHERE id=@target;
                INSERT INTO roles(id,tenant_id,name) VALUES
                  (@managerRole,@tenant,'Manager'),(@targetRole,@tenant,'Target'),
                  (@foreignRole,@other,'Foreign');
                INSERT INTO role_permissions(tenant_id,role_id,permission_key)
                  VALUES (@tenant,@managerRole,'user.manageroles'),
                         (@tenant,@managerRole,'device.approve');
                INSERT INTO user_roles(tenant_id,user_id,role_id)
                  VALUES (@tenant,@actor,@managerRole);
                UPDATE roles SET requires_device_binding=true WHERE id=@targetRole;
                INSERT INTO devices(id,tenant_id,user_id,device_label,platform,install_id,
                                    app_version,secret_hash)
                  VALUES (@foreignDevice,@other,@foreignUser,'Other device','android',
                          'other-install','1.0',@foreignHash);
                """, connection))
            {
                seed.Parameters.AddWithValue("tenant", tenant);
                seed.Parameters.AddWithValue("other", otherTenant);
                seed.Parameters.AddWithValue("actor", actor);
                seed.Parameters.AddWithValue("unprivileged", unprivileged);
                seed.Parameters.AddWithValue("target", target);
                seed.Parameters.AddWithValue("managerRole", managerRole);
                seed.Parameters.AddWithValue("targetRole", targetRole);
                seed.Parameters.AddWithValue("foreignRole", foreignRole);
                seed.Parameters.AddWithValue("foreignUser", foreignUser);
                seed.Parameters.AddWithValue("foreignDevice", foreignDevice);
                seed.Parameters.AddWithValue("foreignHash", System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
                seed.Parameters.AddWithValue("password_hash", passwordHash);
                seed.Parameters.AddWithValue("field_hash", fieldPasswordHash);
                await seed.ExecuteNonQueryAsync();
            }

            const string signingKey = "lifedrop-isolated-test-signing-key-32-bytes-minimum";
            var settings = new Dictionary<string, string>
            {
                ["ConnectionStrings__LifeDrop"] = builder.ConnectionString,
                ["Jwt__Issuer"] = "lifedrop-test",
                ["Jwt__Audience"] = "lifedrop-api-test",
                ["Jwt__SigningKey"] = signingKey
            };
            var prior = settings.Keys.ToDictionary(k => k, Environment.GetEnvironmentVariable);
            foreach (var (name, value) in settings) Environment.SetEnvironmentVariable(name, value);
            try
            {
            using var factory = new WebApplicationFactory<LifeDrop.API.Program>();
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
                { BaseAddress = new Uri("https://localhost") });
            var path = $"/users/{target}/roles/{targetRole}";
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync(path, null)).StatusCode);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
                Token(actor, tenant, "incorrect-signing-key-that-is-long-enough", "lifedrop-test", "lifedrop-api-test"));
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync(path, null)).StatusCode);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
                Token(unprivileged, tenant, signingKey, "lifedrop-test", "lifedrop-api-test"));
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync(path, null)).StatusCode);
            var ordinaryLogin = await client.PostAsJsonAsync("/auth/login", new
                { TenantCode = "a", Email = "ordinary@example.test", Password = "correct-password-for-test" });
            Assert.Equal(HttpStatusCode.OK, ordinaryLogin.StatusCode);
            using var ordinaryBody = await ordinaryLogin.Content.ReadFromJsonAsync<System.Text.Json.JsonDocument>();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
                ordinaryBody!.RootElement.GetProperty("accessToken").GetString());
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync(path, null)).StatusCode);
            var badLogin = await client.PostAsJsonAsync("/auth/login", new
                { TenantCode = "a", Email = "manager@example.test", Password = "wrong-password" });
            Assert.Equal(HttpStatusCode.Unauthorized, badLogin.StatusCode);
            var wrongTenantLogin = await client.PostAsJsonAsync("/auth/login", new
                { TenantCode = "b", Email = "manager@example.test", Password = "correct-password-for-test" });
            Assert.Equal(HttpStatusCode.Unauthorized, wrongTenantLogin.StatusCode);
            var loginResponse = await client.PostAsJsonAsync("/auth/login", new
                { TenantCode = "a", Email = "manager@example.test", Password = "correct-password-for-test" });
            Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);
            using var loginBody = await loginResponse.Content.ReadFromJsonAsync<System.Text.Json.JsonDocument>();
            var issuedToken = loginBody!.RootElement.GetProperty("accessToken").GetString();
            Assert.False(string.IsNullOrWhiteSpace(issuedToken));
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
                issuedToken);
            Assert.Equal(HttpStatusCode.NotFound,
                (await client.PostAsync($"/users/{target}/roles/{foreignRole}", null)).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync(path, null)).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync(path, null)).StatusCode);

            var firstRefresh = loginBody.RootElement.GetProperty("refreshToken").GetString();
            var rotatedResponse = await client.PostAsJsonAsync("/auth/refresh", new { RefreshToken = firstRefresh });
            Assert.Equal(HttpStatusCode.OK, rotatedResponse.StatusCode);
            using var rotatedBody = await rotatedResponse.Content.ReadFromJsonAsync<System.Text.Json.JsonDocument>();
            Assert.Equal(HttpStatusCode.Unauthorized,
                (await client.PostAsJsonAsync("/auth/refresh", new { RefreshToken = firstRefresh })).StatusCode);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
                rotatedBody!.RootElement.GetProperty("accessToken").GetString());
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync(path, null)).StatusCode);

            var nextManagerLogin = await client.PostAsJsonAsync("/auth/login", new
                { TenantCode = "a", Email = "manager@example.test", Password = "correct-password-for-test" });
            Assert.Equal(HttpStatusCode.OK, nextManagerLogin.StatusCode);
            using var nextManagerBody = await nextManagerLogin.Content.ReadFromJsonAsync<System.Text.Json.JsonDocument>();
            var managerToken = nextManagerBody!.RootElement.GetProperty("accessToken").GetString();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", managerToken);
            Assert.Equal(HttpStatusCode.NotFound,
                (await client.PostAsync($"/devices/{foreignDevice}/approve", null)).StatusCode);

            var registration = await client.PostAsJsonAsync("/auth/devices/register", new
            {
                TenantCode = "a", Email = "field@example.test", Password = "field-password-for-test",
                DeviceLabel = "Test Android", Platform = "android", InstallId = "install-1",
                AppVersion = "1.0"
            });
            Assert.Equal(HttpStatusCode.OK, registration.StatusCode);
            using var registered = await registration.Content.ReadFromJsonAsync<System.Text.Json.JsonDocument>();
            var deviceId = registered!.RootElement.GetProperty("deviceId").GetGuid();
            var deviceSecret = registered.RootElement.GetProperty("deviceSecret").GetString();
            var fieldLogin = new { TenantCode = "a", Email = "field@example.test",
                Password = "field-password-for-test" };
            Assert.Equal(HttpStatusCode.Forbidden,
                (await client.PostAsJsonAsync("/auth/login", fieldLogin)).StatusCode);
            var pendingDeviceLogin = await client.PostAsJsonAsync("/auth/login", new
            {
                fieldLogin.TenantCode, fieldLogin.Email, fieldLogin.Password,
                DeviceId = deviceId, DeviceSecret = deviceSecret
            });
            Assert.Equal(HttpStatusCode.Forbidden, pendingDeviceLogin.StatusCode);
            Assert.Equal("application/problem+json", pendingDeviceLogin.Content.Headers.ContentType?.MediaType);
            using (var problem = await pendingDeviceLogin.Content.ReadFromJsonAsync<System.Text.Json.JsonDocument>())
                Assert.Equal("DEVICE_NOT_APPROVED", problem!.RootElement.GetProperty("code").GetString());
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", managerToken);
            Assert.Equal(HttpStatusCode.NoContent,
                (await client.PostAsync($"/devices/{deviceId}/approve", null)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden,
                (await client.PostAsJsonAsync("/auth/login", new
                {
                    fieldLogin.TenantCode, fieldLogin.Email, fieldLogin.Password,
                    DeviceId = deviceId, DeviceSecret = "wrong-secret"
                })).StatusCode);
            var boundLogin = await client.PostAsJsonAsync("/auth/login", new
            {
                fieldLogin.TenantCode, fieldLogin.Email, fieldLogin.Password,
                DeviceId = deviceId, DeviceSecret = deviceSecret
            });
            Assert.Equal(HttpStatusCode.OK, boundLogin.StatusCode);
            using var boundBody = await boundLogin.Content.ReadFromJsonAsync<System.Text.Json.JsonDocument>();
            var boundRefresh = boundBody!.RootElement.GetProperty("refreshToken").GetString();
            Assert.Equal(HttpStatusCode.Unauthorized,
                (await client.PostAsJsonAsync("/auth/refresh", new { RefreshToken = boundRefresh })).StatusCode);
            var boundRotation = await client.PostAsJsonAsync("/auth/refresh", new
                { RefreshToken = boundRefresh, DeviceId = deviceId, DeviceSecret = deviceSecret });
            Assert.Equal(HttpStatusCode.OK, boundRotation.StatusCode);
            using var boundRotated = await boundRotation.Content.ReadFromJsonAsync<System.Text.Json.JsonDocument>();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
                boundRotated!.RootElement.GetProperty("accessToken").GetString());
            var unauthorizedBlockResp = await client.PostAsync($"/devices/{deviceId}/block", null);
            Assert.Equal(HttpStatusCode.Forbidden, unauthorizedBlockResp.StatusCode);

            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", managerToken);
            var foreignBlockResp = await client.PostAsync($"/devices/{foreignDevice}/block", null);
            Assert.Equal(HttpStatusCode.NotFound, foreignBlockResp.StatusCode);

            await using (var foreignCheckCmd = new NpgsqlCommand("SELECT status FROM devices WHERE id = @id", connection))
            {
                foreignCheckCmd.Parameters.AddWithValue("id", foreignDevice);
                var foreignStatus = Convert.ToString(await foreignCheckCmd.ExecuteScalarAsync());
                Assert.Equal("pending", foreignStatus);
            }

            await using (var auditCheckCmd = new NpgsqlCommand("SELECT COUNT(*) FROM audit_logs WHERE action = 'device.blocked'", connection))
            {
                var blockedAuditCount = Convert.ToInt64(await auditCheckCmd.ExecuteScalarAsync());
                Assert.Equal(0, blockedAuditCount);
            }

            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", managerToken);
            Assert.Equal(HttpStatusCode.NoContent,
                (await client.PostAsync($"/devices/{deviceId}/block", null)).StatusCode);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
                boundRotated!.RootElement.GetProperty("accessToken").GetString());
            Assert.Equal(HttpStatusCode.Unauthorized,
                (await client.PostAsync("/auth/logout", null)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized,
                (await client.PostAsJsonAsync("/auth/refresh", new
                {
                    RefreshToken = boundRotated.RootElement.GetProperty("refreshToken").GetString(),
                    DeviceId = deviceId, DeviceSecret = deviceSecret
                })).StatusCode);

            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", managerToken);
            Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/auth/logout", null)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized,
                (await client.PostAsync($"/devices/{deviceId}/approve", null)).StatusCode);
            }
            finally
            {
                foreach (var (name, value) in prior) Environment.SetEnvironmentVariable(name, value);
            }

            await using var verify = new NpgsqlCommand(
                "SELECT (SELECT count(*) FROM user_roles WHERE user_id=@target AND role_id=@role), (SELECT count(*) FROM audit_logs WHERE action='user.role_assigned')", connection);
            verify.Parameters.AddWithValue("target", target);
            verify.Parameters.AddWithValue("role", targetRole);
            await using var reader = await verify.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(1, reader.GetInt64(0));
            Assert.Equal(1, reader.GetInt64(1));
            await reader.CloseAsync();

            await using var authAudit = new NpgsqlCommand(
                "SELECT count(*), count(*) FILTER (WHERE succeeded) FROM auth_login_audit", connection);
            await using var auditReader = await authAudit.ExecuteReaderAsync();
            Assert.True(await auditReader.ReadAsync());
            Assert.True(auditReader.GetInt64(0) >= 6);
            Assert.True(auditReader.GetInt64(1) >= 4);
            await auditReader.CloseAsync();

            await using var securityAudit = new NpgsqlCommand("""
                SELECT
                  count(*) FILTER (WHERE action='auth.refresh_replay'),
                  count(*) FILTER (WHERE action='device.approved'),
                  count(*) FILTER (WHERE action='device.blocked'),
                  count(*) FILTER (WHERE action='auth.device_rejected')
                FROM audit_logs
                """, connection);
            await using var securityReader = await securityAudit.ExecuteReaderAsync();
            Assert.True(await securityReader.ReadAsync());
            Assert.Equal(1, securityReader.GetInt64(0));
            Assert.Equal(1, securityReader.GetInt64(1));
            Assert.Equal(1, securityReader.GetInt64(2));
            Assert.Equal(3, securityReader.GetInt64(3));
            await securityReader.CloseAsync();

            await using var mutateAudit = new NpgsqlCommand("DELETE FROM audit_logs", connection);
            var error = await Assert.ThrowsAsync<PostgresException>(() => mutateAudit.ExecuteNonQueryAsync());
            Assert.Equal("P0001", error.SqlState);
            await using var mutateAuthAudit = new NpgsqlCommand("DELETE FROM auth_login_audit", connection);
            var authError = await Assert.ThrowsAsync<PostgresException>(() => mutateAuthAudit.ExecuteNonQueryAsync());
            Assert.Equal("P0001", authError.SqlState);
        }
        finally
        {
            await using var cleanup = new NpgsqlCommand($"DROP SCHEMA {schema} CASCADE", setup);
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    private static string Token(Guid actor, Guid tenant, string key, string issuer, string audience)
    {
        var token = new JwtSecurityToken(issuer, audience,
            [new Claim(JwtRegisteredClaimNames.Sub, actor.ToString()),
             new Claim("tenant_id", tenant.ToString())],
            expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)), SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}


