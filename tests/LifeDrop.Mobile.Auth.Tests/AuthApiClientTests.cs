using System.Net;
using System.Text;
using LifeDrop.Mobile.Auth.Models;
using LifeDrop.Mobile.Auth.Tests.Stubs;
using Xunit;

namespace LifeDrop.Mobile.Auth.Tests;

public sealed class AuthApiClientTests
{
    private static readonly Uri TestBaseUri = new("http://localhost:5256");

    private sealed class TrackableHttpResponseMessage : HttpResponseMessage
    {
        public bool IsDisposed { get; private set; }

        public TrackableHttpResponseMessage(HttpStatusCode statusCode, string content = "")
            : base(statusCode)
        {
            if (!string.IsNullOrEmpty(content))
            {
                Content = new StringContent(content, Encoding.UTF8, "application/json");
            }
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                IsDisposed = true;
            }
        }
    }

    [Fact]
    public async Task RegisterDeviceAsync_Success_ReturnsRegisteredDevice()
    {
        var expectedDeviceId = Guid.NewGuid();
        var expectedSecret = "sec_test_secret_12345";
        var jsonResponse = $"{{\"deviceId\":\"{expectedDeviceId}\",\"deviceSecret\":\"{expectedSecret}\"}}";

        var handler = StubHttpMessageHandler.FromResponse(HttpStatusCode.OK, jsonResponse);
        var httpClient = new HttpClient(handler) { BaseAddress = TestBaseUri };
        var client = new AuthApiClient(httpClient);

        var request = new RegisterDeviceRequest(
            TenantCode: "ACME",
            Email: "rep@acme.com",
            Password: "Password123!",
            DeviceLabel: "Pixel 8 Pro",
            Platform: "android",
            InstallId: "install-uuid-123",
            AppVersion: "1.0.0");

        var result = await client.RegisterDeviceAsync(request);

        Assert.True(result.IsSuccess);
        Assert.Equal(AuthStatus.Success, result.Status);
        Assert.Equal(200, result.HttpStatusCode);
        Assert.NotNull(result.Data);
        Assert.Equal(expectedDeviceId, result.Data.DeviceId);
        Assert.Equal(expectedSecret, result.Data.DeviceSecret);
        Assert.NotNull(handler.LastRequest);
        Assert.Equal(HttpMethod.Post, handler.LastRequest.Method);
        Assert.Equal("/auth/devices/register", handler.LastRequest.RequestUri?.AbsolutePath);
        Assert.Contains("\"tenantCode\":\"ACME\"", handler.LastRequestBody);
        Assert.Contains("\"platform\":\"android\"", handler.LastRequestBody);
    }

    [Fact]
    public async Task RegisterDeviceAsync_Conflict_ReturnsConflictStatus()
    {
        var handler = StubHttpMessageHandler.FromResponse(HttpStatusCode.Conflict);
        var httpClient = new HttpClient(handler) { BaseAddress = TestBaseUri };
        var client = new AuthApiClient(httpClient);

        var request = new RegisterDeviceRequest("ACME", "rep@acme.com", "Pass", "Label", "android", "id", "1.0");
        var result = await client.RegisterDeviceAsync(request);

        Assert.False(result.IsSuccess);
        Assert.Equal(AuthStatus.Conflict, result.Status);
        Assert.Equal(409, result.HttpStatusCode);
    }

    [Fact]
    public async Task RegisterDeviceAsync_InvalidCredentials_ReturnsInvalidCredentials()
    {
        var handler = StubHttpMessageHandler.FromResponse(HttpStatusCode.Unauthorized);
        var httpClient = new HttpClient(handler) { BaseAddress = TestBaseUri };
        var client = new AuthApiClient(httpClient);

        var request = new RegisterDeviceRequest("ACME", "rep@acme.com", "WrongPass", "Label", "ios", "id", "1.0");
        var result = await client.RegisterDeviceAsync(request);

        Assert.False(result.IsSuccess);
        Assert.Equal(AuthStatus.InvalidCredentials, result.Status);
        Assert.Equal(401, result.HttpStatusCode);
    }

    [Fact]
    public async Task RegisterDeviceAsync_BadRequest_ReturnsBadRequest()
    {
        var handler = StubHttpMessageHandler.FromResponse(HttpStatusCode.BadRequest);
        var httpClient = new HttpClient(handler) { BaseAddress = TestBaseUri };
        var client = new AuthApiClient(httpClient);

        var request = new RegisterDeviceRequest("ACME", "rep@acme.com", "Pass", "Label", "invalid_platform", "id", "1.0");
        var result = await client.RegisterDeviceAsync(request);

        Assert.False(result.IsSuccess);
        Assert.Equal(AuthStatus.BadRequest, result.Status);
        Assert.Equal(400, result.HttpStatusCode);
    }

    [Theory]
    [InlineData("{\"deviceId\":\"00000000-0000-0000-0000-000000000000\",\"deviceSecret\":\"secret\"}")]
    [InlineData("{\"deviceId\":\"11111111-1111-1111-1111-111111111111\",\"deviceSecret\":\"\"}")]
    [InlineData("{\"deviceId\":\"11111111-1111-1111-1111-111111111111\",\"deviceSecret\":\"   \"}")]
    [InlineData("not-a-valid-json")]
    public async Task RegisterDeviceAsync_MalformedOrIncomplete200_ReturnsNetworkOrServerError(string malformedJson)
    {
        var handler = StubHttpMessageHandler.FromResponse(HttpStatusCode.OK, malformedJson);
        var httpClient = new HttpClient(handler) { BaseAddress = TestBaseUri };
        var client = new AuthApiClient(httpClient);

        var request = new RegisterDeviceRequest("ACME", "rep@acme.com", "Pass", "Label", "android", "id", "1.0");
        var result = await client.RegisterDeviceAsync(request);

        Assert.False(result.IsSuccess);
        Assert.Equal(AuthStatus.NetworkOrServerError, result.Status);
        Assert.Equal(200, result.HttpStatusCode);
        Assert.Null(result.Data);
    }

    [Fact]
    public async Task LoginAsync_ValidCredentials_ReturnsTokens()
    {
        var expiresAt = DateTime.UtcNow.AddMinutes(15);
        var refreshExpiresAt = DateTime.UtcNow.AddDays(7);
        var jsonResponse = $@"{{
            ""accessToken"": ""jwt.sample.token"",
            ""refreshToken"": ""ref_token_opaque_xyz"",
            ""tokenType"": ""Bearer"",
            ""expiresAt"": ""{expiresAt:O}"",
            ""refreshExpiresAt"": ""{refreshExpiresAt:O}""
        }}";

        var handler = StubHttpMessageHandler.FromResponse(HttpStatusCode.OK, jsonResponse);
        var httpClient = new HttpClient(handler) { BaseAddress = TestBaseUri };
        var client = new AuthApiClient(httpClient);

        var request = new LoginRequest("ACME", "rep@acme.com", "Secret123!", Guid.NewGuid(), "dev_secret");
        var result = await client.LoginAsync(request);

        Assert.True(result.IsSuccess);
        Assert.Equal(AuthStatus.Success, result.Status);
        Assert.Equal(200, result.HttpStatusCode);
        Assert.NotNull(result.Data);
        Assert.Equal("jwt.sample.token", result.Data.AccessToken);
        Assert.Equal("ref_token_opaque_xyz", result.Data.RefreshToken);
        Assert.Equal("Bearer", result.Data.TokenType);
        Assert.NotNull(handler.LastRequest);
        Assert.Equal("/auth/login", handler.LastRequest.RequestUri?.AbsolutePath);
    }

    [Fact]
    public async Task LoginAsync_InvalidCredentials_ReturnsInvalidCredentials()
    {
        var handler = StubHttpMessageHandler.FromResponse(HttpStatusCode.Unauthorized);
        var httpClient = new HttpClient(handler) { BaseAddress = TestBaseUri };
        var client = new AuthApiClient(httpClient);

        var request = new LoginRequest("ACME", "rep@acme.com", "BadPassword");
        var result = await client.LoginAsync(request);

        Assert.False(result.IsSuccess);
        Assert.Equal(AuthStatus.InvalidCredentials, result.Status);
        Assert.Equal(401, result.HttpStatusCode);
    }

    [Fact]
    public async Task LoginAsync_DeviceNotApproved_ReturnsDeviceNotApprovedStatus()
    {
        var errorBody = "{\"Code\":\"DEVICE_NOT_APPROVED\"}";
        var handler = StubHttpMessageHandler.FromResponse(HttpStatusCode.Forbidden, errorBody);
        var httpClient = new HttpClient(handler) { BaseAddress = TestBaseUri };
        var client = new AuthApiClient(httpClient);

        var request = new LoginRequest("ACME", "rep@acme.com", "ValidPass", Guid.NewGuid(), "pending_sec");
        var result = await client.LoginAsync(request);

        Assert.False(result.IsSuccess);
        Assert.Equal(AuthStatus.DeviceNotApproved, result.Status);
        Assert.Equal("DEVICE_NOT_APPROVED", result.ErrorCode);
        Assert.Equal(403, result.HttpStatusCode);
    }

    [Fact]
    public async Task LoginAsync_DeviceNotApprovedProblemDetails_ReturnsDeviceNotApprovedStatus()
    {
        // Server error contract (plan step 0.5): RFC 9457 ProblemDetails with a "code" extension.
        var errorBody = "{\"type\":\"https://tools.ietf.org/html/rfc9110#section-15.5.4\",\"title\":\"Forbidden\",\"status\":403,\"code\":\"DEVICE_NOT_APPROVED\",\"traceId\":\"0HN-test\"}";
        var handler = StubHttpMessageHandler.FromResponse(HttpStatusCode.Forbidden, errorBody, "application/problem+json");
        var httpClient = new HttpClient(handler) { BaseAddress = TestBaseUri };
        var client = new AuthApiClient(httpClient);

        var request = new LoginRequest("ACME", "rep@acme.com", "ValidPass", Guid.NewGuid(), "pending_sec");
        var result = await client.LoginAsync(request);

        Assert.False(result.IsSuccess);
        Assert.Equal(AuthStatus.DeviceNotApproved, result.Status);
        Assert.Equal("DEVICE_NOT_APPROVED", result.ErrorCode);
        Assert.Equal(403, result.HttpStatusCode);
    }

    [Fact]
    public async Task LoginAsync_ForbiddenWithoutDeviceCode_ReturnsForbiddenStatus()
    {
        var handler = StubHttpMessageHandler.FromResponse(HttpStatusCode.Forbidden, "{}");
        var httpClient = new HttpClient(handler) { BaseAddress = TestBaseUri };
        var client = new AuthApiClient(httpClient);

        var request = new LoginRequest("ACME", "rep@acme.com", "ValidPass");
        var result = await client.LoginAsync(request);

        Assert.False(result.IsSuccess);
        Assert.Equal(AuthStatus.Forbidden, result.Status);
        Assert.Equal(403, result.HttpStatusCode);
    }

    [Theory]
    [InlineData("{\"accessToken\":\"\",\"refreshToken\":\"ref\",\"tokenType\":\"Bearer\",\"expiresAt\":\"2026-10-01T20:00:00Z\",\"refreshExpiresAt\":\"2026-10-08T20:00:00Z\"}")]
    [InlineData("{\"accessToken\":\"jwt\",\"refreshToken\":\"\",\"tokenType\":\"Bearer\",\"expiresAt\":\"2026-10-01T20:00:00Z\",\"refreshExpiresAt\":\"2026-10-08T20:00:00Z\"}")]
    [InlineData("{\"accessToken\":\"jwt\",\"refreshToken\":\"ref\",\"tokenType\":\"Basic\",\"expiresAt\":\"2026-10-01T20:00:00Z\",\"refreshExpiresAt\":\"2026-10-08T20:00:00Z\"}")]
    [InlineData("{\"accessToken\":\"jwt\",\"refreshToken\":\"ref\",\"tokenType\":\"Bearer\",\"expiresAt\":\"0001-01-01T00:00:00Z\",\"refreshExpiresAt\":\"2026-10-08T20:00:00Z\"}")]
    [InlineData("{\"accessToken\":\"jwt\",\"refreshToken\":\"ref\",\"tokenType\":\"Bearer\",\"expiresAt\":\"2026-10-08T20:00:00Z\",\"refreshExpiresAt\":\"2026-10-01T20:00:00Z\"}")]
    [InlineData("{\"accessToken\":\"jwt\",\"refreshToken\":\"ref\",\"tokenType\":\"Bearer\",\"expiresAt\":\"2026-10-08T20:00:00Z\",\"refreshExpiresAt\":\"2026-10-08T20:00:00Z\"}")]
    [InlineData("{invalid_json}")]
    public async Task LoginAsync_MalformedOrIncomplete200_ReturnsNetworkOrServerError(string malformedJson)
    {
        var handler = StubHttpMessageHandler.FromResponse(HttpStatusCode.OK, malformedJson);
        var httpClient = new HttpClient(handler) { BaseAddress = TestBaseUri };
        var client = new AuthApiClient(httpClient);

        var request = new LoginRequest("ACME", "rep@acme.com", "Pass");
        var result = await client.LoginAsync(request);

        Assert.False(result.IsSuccess);
        Assert.Equal(AuthStatus.NetworkOrServerError, result.Status);
        Assert.Equal(200, result.HttpStatusCode);
        Assert.Null(result.Data);
    }

    [Fact]
    public async Task RefreshAsync_ValidToken_ReturnsNewTokens()
    {
        var expiresAt = DateTime.UtcNow.AddMinutes(15);
        var refreshExpiresAt = DateTime.UtcNow.AddDays(7);
        var jsonResponse = $@"{{
            ""accessToken"": ""jwt.new.token"",
            ""refreshToken"": ""ref_new_opaque"",
            ""tokenType"": ""Bearer"",
            ""expiresAt"": ""{expiresAt:O}"",
            ""refreshExpiresAt"": ""{refreshExpiresAt:O}""
        }}";

        var handler = StubHttpMessageHandler.FromResponse(HttpStatusCode.OK, jsonResponse);
        var httpClient = new HttpClient(handler) { BaseAddress = TestBaseUri };
        var client = new AuthApiClient(httpClient);

        var request = new RefreshRequest("old_refresh_token", Guid.NewGuid(), "dev_secret");
        var result = await client.RefreshAsync(request);

        Assert.True(result.IsSuccess);
        Assert.Equal(AuthStatus.Success, result.Status);
        Assert.Equal(200, result.HttpStatusCode);
        Assert.NotNull(result.Data);
        Assert.Equal("jwt.new.token", result.Data.AccessToken);
        Assert.Equal("ref_new_opaque", result.Data.RefreshToken);
        Assert.NotNull(handler.LastRequest);
        Assert.Equal("/auth/refresh", handler.LastRequest.RequestUri?.AbsolutePath);
    }

    [Fact]
    public async Task RefreshAsync_RevokedOrExpiredFamily_ReturnsUnauthorized()
    {
        var handler = StubHttpMessageHandler.FromResponse(HttpStatusCode.Unauthorized);
        var httpClient = new HttpClient(handler) { BaseAddress = TestBaseUri };
        var client = new AuthApiClient(httpClient);

        var request = new RefreshRequest("expired_token");
        var result = await client.RefreshAsync(request);

        Assert.False(result.IsSuccess);
        Assert.Equal(AuthStatus.Unauthorized, result.Status);
        Assert.Equal(401, result.HttpStatusCode);
    }

    [Theory]
    [InlineData("{\"accessToken\":\"jwt\",\"refreshToken\":\"\",\"tokenType\":\"Bearer\",\"expiresAt\":\"2026-10-01T20:00:00Z\",\"refreshExpiresAt\":\"2026-10-08T20:00:00Z\"}")]
    [InlineData("not-json")]
    public async Task RefreshAsync_Malformed200_ReturnsNetworkOrServerError(string malformedJson)
    {
        var handler = StubHttpMessageHandler.FromResponse(HttpStatusCode.OK, malformedJson);
        var httpClient = new HttpClient(handler) { BaseAddress = TestBaseUri };
        var client = new AuthApiClient(httpClient);

        var request = new RefreshRequest("token");
        var result = await client.RefreshAsync(request);

        Assert.False(result.IsSuccess);
        Assert.Equal(AuthStatus.NetworkOrServerError, result.Status);
        Assert.Equal(200, result.HttpStatusCode);
    }

    [Fact]
    public async Task LogoutAsync_ValidSession_ReturnsSuccessWith204AndSendsAuthorizationHeader()
    {
        var handler = StubHttpMessageHandler.FromResponse(HttpStatusCode.NoContent);
        var httpClient = new HttpClient(handler) { BaseAddress = TestBaseUri };
        var client = new AuthApiClient(httpClient);

        var result = await client.LogoutAsync("my_sample_access_token");

        Assert.True(result.IsSuccess);
        Assert.Equal(AuthStatus.Success, result.Status);
        Assert.Equal(204, result.HttpStatusCode);
        Assert.NotNull(handler.LastRequest);
        Assert.Equal(HttpMethod.Post, handler.LastRequest.Method);
        Assert.Equal("/auth/logout", handler.LastRequest.RequestUri?.AbsolutePath);
        Assert.NotNull(handler.LastRequest.Headers.Authorization);
        Assert.Equal("Bearer", handler.LastRequest.Headers.Authorization.Scheme);
        Assert.Equal("my_sample_access_token", handler.LastRequest.Headers.Authorization.Parameter);
    }

    [Fact]
    public async Task LogoutAsync_EmptyToken_ThrowsArgumentException()
    {
        var handler = StubHttpMessageHandler.FromResponse(HttpStatusCode.NoContent);
        var httpClient = new HttpClient(handler) { BaseAddress = TestBaseUri };
        var client = new AuthApiClient(httpClient);

        await Assert.ThrowsAsync<ArgumentException>(() => client.LogoutAsync("   "));
    }

    [Fact]
    public async Task HttpRequestException_HidesRawMessage_ReturnsGenericSafeMessage()
    {
        var handler = new StubHttpMessageHandler(_ =>
            throw new HttpRequestException("Socket connection failed to 192.168.1.100:5256 / internal db error", null, HttpStatusCode.ServiceUnavailable));
        var httpClient = new HttpClient(handler) { BaseAddress = TestBaseUri };
        var client = new AuthApiClient(httpClient);

        var result = await client.LoginAsync(new LoginRequest("ACME", "user@acme.com", "Password"));

        Assert.False(result.IsSuccess);
        Assert.Equal(AuthStatus.NetworkOrServerError, result.Status);
        Assert.Equal(503, result.HttpStatusCode);
        Assert.DoesNotContain("192.168.1.100", result.ErrorMessage);
        Assert.DoesNotContain("Socket connection failed", result.ErrorMessage);
        Assert.Equal("A network error occurred while communicating with the server.", result.ErrorMessage);
    }

    [Fact]
    public async Task Cancellation_PreservesCallerCancellation_ThrowsOperationCanceledException()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var handler = StubHttpMessageHandler.FromResponse(HttpStatusCode.Forbidden, "{\"Code\":\"DEVICE_NOT_APPROVED\"}");
        var httpClient = new HttpClient(handler) { BaseAddress = TestBaseUri };
        var client = new AuthApiClient(httpClient);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await client.LoginAsync(new LoginRequest("ACME", "user@acme.com", "Password"), cts.Token);
        });
    }

    [Fact]
    public async Task Timeout_DuringSend_ReturnsNetworkOrServerError()
    {
        var handler = new StubHttpMessageHandler(_ => throw new TaskCanceledException());
        var httpClient = new HttpClient(handler) { BaseAddress = TestBaseUri };
        var client = new AuthApiClient(httpClient);

        var result = await client.LoginAsync(new LoginRequest("ACME", "user@acme.com", "Password"));

        Assert.False(result.IsSuccess);
        Assert.Equal(AuthStatus.NetworkOrServerError, result.Status);
        Assert.Null(result.HttpStatusCode);
        Assert.Equal("A network error occurred while communicating with the server.", result.ErrorMessage);
    }

    [Fact]
    public async Task Timeout_DuringResponseBodyRead_ReturnsNetworkOrServerError_NotForbidden()
    {
        var response = new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new TimeoutHttpContent()
        };
        var handler = new StubHttpMessageHandler(_ => response);
        var httpClient = new HttpClient(handler) { BaseAddress = TestBaseUri };
        var client = new AuthApiClient(httpClient);

        var result = await client.LoginAsync(new LoginRequest("ACME", "user@acme.com", "Password"));

        Assert.False(result.IsSuccess);
        Assert.Equal(AuthStatus.NetworkOrServerError, result.Status);
        Assert.Equal("A network error occurred while communicating with the server.", result.ErrorMessage);
    }

    private sealed class TimeoutHttpContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => throw new TaskCanceledException();
        protected override Task<Stream> CreateContentReadStreamAsync() => throw new TaskCanceledException();
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }

    [Fact]
    public async Task Responses_AreDisposed_OnAllPaths()
    {
        // 1. RegisterDevice success path
        var registerTrackable = new TrackableHttpResponseMessage(HttpStatusCode.OK, $"{{\"deviceId\":\"{Guid.NewGuid()}\",\"deviceSecret\":\"sec\"}}");
        var client1 = new AuthApiClient(new HttpClient(new StubHttpMessageHandler(_ => registerTrackable)) { BaseAddress = TestBaseUri });
        await client1.RegisterDeviceAsync(new RegisterDeviceRequest("A", "e@a.com", "P", "L", "android", "i", "1"));
        Assert.True(registerTrackable.IsDisposed);

        // 2. Login error path (403 with error body)
        var loginTrackable = new TrackableHttpResponseMessage(HttpStatusCode.Forbidden, "{\"Code\":\"DEVICE_NOT_APPROVED\"}");
        var client2 = new AuthApiClient(new HttpClient(new StubHttpMessageHandler(_ => loginTrackable)) { BaseAddress = TestBaseUri });
        await client2.LoginAsync(new LoginRequest("A", "e@a.com", "P"));
        Assert.True(loginTrackable.IsDisposed);

        // 3. Refresh error path (401)
        var refreshTrackable = new TrackableHttpResponseMessage(HttpStatusCode.Unauthorized);
        var client3 = new AuthApiClient(new HttpClient(new StubHttpMessageHandler(_ => refreshTrackable)) { BaseAddress = TestBaseUri });
        await client3.RefreshAsync(new RefreshRequest("token"));
        Assert.True(refreshTrackable.IsDisposed);

        // 4. Logout path (204)
        var logoutTrackable = new TrackableHttpResponseMessage(HttpStatusCode.NoContent);
        var client4 = new AuthApiClient(new HttpClient(new StubHttpMessageHandler(_ => logoutTrackable)) { BaseAddress = TestBaseUri });
        await client4.LogoutAsync("token");
        Assert.True(logoutTrackable.IsDisposed);
    }

    [Fact]
    public void SensitiveModels_ToString_RedactsSecrets()
    {
        var login = new LoginRequest("ACME", "rep@acme.com", "SuperSecretPassword123", Guid.NewGuid(), "dev_secret_xyz");
        var register = new RegisterDeviceRequest("ACME", "rep@acme.com", "SuperSecretPassword123", "Pixel", "android", "id", "1.0");
        var regDevice = new RegisteredDevice(Guid.NewGuid(), "dev_secret_xyz");
        var refresh = new RefreshRequest("refresh_token_abc", Guid.NewGuid(), "dev_secret_xyz");
        var tokens = new AuthTokens("access_jwt_123", "refresh_opaque_456", "Bearer", DateTime.UtcNow, DateTime.UtcNow.AddDays(7));

        var loginStr = login.ToString();
        var registerStr = register.ToString();
        var regDeviceStr = regDevice.ToString();
        var refreshStr = refresh.ToString();
        var tokensStr = tokens.ToString();

        Assert.DoesNotContain("SuperSecretPassword123", loginStr);
        Assert.DoesNotContain("dev_secret_xyz", loginStr);
        Assert.Contains("[REDACTED]", loginStr);

        Assert.DoesNotContain("SuperSecretPassword123", registerStr);
        Assert.Contains("[REDACTED]", registerStr);

        Assert.DoesNotContain("dev_secret_xyz", regDeviceStr);
        Assert.Contains("[REDACTED]", regDeviceStr);

        Assert.DoesNotContain("refresh_token_abc", refreshStr);
        Assert.DoesNotContain("dev_secret_xyz", refreshStr);
        Assert.Contains("[REDACTED]", refreshStr);

        Assert.DoesNotContain("access_jwt_123", tokensStr);
        Assert.DoesNotContain("refresh_opaque_456", tokensStr);
        Assert.Contains("[REDACTED]", tokensStr);
    }
}
