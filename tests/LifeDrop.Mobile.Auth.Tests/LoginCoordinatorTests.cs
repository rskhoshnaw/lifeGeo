using LifeDrop.Mobile.Auth.Models;
using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace LifeDrop.Mobile.Auth.Tests;

public class LoginCoordinatorTests
{
    private static readonly DateTime _fixedTime = new DateTime(2025, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private class MockAuthApiClient : IAuthApiClient
    {
        public Func<LoginRequest, Task<AuthResult<AuthTokens>>>? LoginAsyncHandler { get; set; }

        public Task<AuthResult<AuthTokens>> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default)
        {
            if (LoginAsyncHandler != null) return LoginAsyncHandler(request);
            return Task.FromResult(new AuthResult<AuthTokens>(AuthStatus.Success, new AuthTokens("access", "refresh", "Bearer", _fixedTime.AddMinutes(15), _fixedTime.AddDays(7))));
        }

        public Task<AuthResult<RegisteredDevice>> RegisterDeviceAsync(RegisterDeviceRequest request, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<AuthResult<AuthTokens>> RefreshAsync(RefreshRequest request, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<AuthResult<bool>> LogoutAsync(string accessToken, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }

    private class MockSecureCredentialStore : ISecureCredentialStore
    {
        public RegisteredDevice? DeviceToReturn { get; set; }
        public bool SimulateSaveFailure { get; set; }
        public bool ThrowCancellationOnSave { get; set; }
        public StoredSession? SavedSession { get; private set; }

        public Task<RegisteredDevice?> LoadDeviceAsync(CancellationToken cancellationToken = default) => Task.FromResult(DeviceToReturn);
        
        public Task SaveSessionAsync(StoredSession session, CancellationToken cancellationToken = default)
        {
            if (ThrowCancellationOnSave) throw new OperationCanceledException();
            if (SimulateSaveFailure) throw new InvalidOperationException("Simulated save failure");
            SavedSession = session;
            return Task.CompletedTask;
        }

        public Task SaveDeviceAsync(RegisteredDevice device, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task ClearDeviceAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<StoredSession?> LoadSessionAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<bool> MarkSessionRefreshingAsync(StoredSession expectedSession, StoredSession pendingSession, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<bool> CommitSessionRefreshAsync(StoredSession pendingSession, StoredSession newSession, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task ClearSessionAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<bool> ClearSessionIfMatchesAsync(StoredSession expectedSession, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task ClearAllAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }

    [Fact]
    public async Task LoginAsync_UnboundSuccess_WhenNoDeviceRegistered_SendsNoDeviceSecrets()
    {
        var apiClient = new MockAuthApiClient();
        LoginRequest? capturedRequest = null;
        apiClient.LoginAsyncHandler = req => {
            capturedRequest = req;
            return Task.FromResult(new AuthResult<AuthTokens>(AuthStatus.Success, new AuthTokens("access", "refresh", "Bearer", _fixedTime.AddMinutes(15), _fixedTime.AddDays(7))));
        };
        var store = new MockSecureCredentialStore();
        var coordinator = new LoginCoordinator(apiClient, store);

        var result = await coordinator.LoginAsync("tenant", "user@test.com", "password");

        Assert.Equal(AuthStatus.Success, result.Status);
        Assert.NotNull(result.Data);
        Assert.False(result.Data.IsDeviceBound);
        Assert.Null(result.Data.BoundDeviceId);
        Assert.NotNull(store.SavedSession);

        Assert.NotNull(capturedRequest);
        Assert.Null(capturedRequest.DeviceId);
        Assert.Null(capturedRequest.DeviceSecret);
    }

    [Fact]
    public async Task LoginAsync_BoundSuccess_WhenValidDeviceRegistered()
    {
        var apiClient = new MockAuthApiClient();
        var store = new MockSecureCredentialStore { DeviceToReturn = new RegisteredDevice(Guid.NewGuid(), "secret", "tenant", "user@test.com") };
        var coordinator = new LoginCoordinator(apiClient, store);

        LoginRequest? capturedRequest = null;
        apiClient.LoginAsyncHandler = req => {
            capturedRequest = req;
            return Task.FromResult(new AuthResult<AuthTokens>(AuthStatus.Success, new AuthTokens("access", "refresh", "Bearer", _fixedTime.AddMinutes(15), _fixedTime.AddDays(7))));
        };

        var result = await coordinator.LoginAsync("tenant", "user@test.com", "password");

        Assert.Equal(AuthStatus.Success, result.Status);
        Assert.NotNull(result.Data);
        Assert.True(result.Data.IsDeviceBound);
        Assert.Equal(store.DeviceToReturn.DeviceId, result.Data.BoundDeviceId);
        
        Assert.NotNull(capturedRequest);
        Assert.Equal(store.DeviceToReturn.DeviceId, capturedRequest.DeviceId);
        Assert.Equal("secret", capturedRequest.DeviceSecret);
    }

    [Fact]
    public async Task LoginAsync_ReturnsLocalError_WhenDeviceIdentityMismatched_NeverCallsLogin()
    {
        var apiClient = new MockAuthApiClient();
        LoginRequest? capturedRequest = null;
        apiClient.LoginAsyncHandler = req => { capturedRequest = req; return Task.FromResult(new AuthResult<AuthTokens>(AuthStatus.Success, null)); };
        var store = new MockSecureCredentialStore { DeviceToReturn = new RegisteredDevice(Guid.NewGuid(), "secret", "different", "user@test.com") };
        var coordinator = new LoginCoordinator(apiClient, store);

        var result = await coordinator.LoginAsync("tenant", "user@test.com", "password");

        Assert.Equal(AuthStatus.LocalStateError, result.Status);
        Assert.Null(store.SavedSession);
        Assert.Null(capturedRequest);
    }

    [Fact]
    public async Task LoginAsync_ReturnsLocalError_WhenDeviceMalformed_NeverCallsLogin()
    {
        var apiClient = new MockAuthApiClient();
        LoginRequest? capturedRequest = null;
        apiClient.LoginAsyncHandler = req => { capturedRequest = req; return Task.FromResult(new AuthResult<AuthTokens>(AuthStatus.Success, null)); };
        var store = new MockSecureCredentialStore { DeviceToReturn = new RegisteredDevice(Guid.Empty, "", "tenant", "user@test.com") };
        var coordinator = new LoginCoordinator(apiClient, store);

        var result = await coordinator.LoginAsync("tenant", "user@test.com", "password");

        Assert.Equal(AuthStatus.LocalStateError, result.Status);
        Assert.Null(store.SavedSession);
        Assert.Null(capturedRequest);
    }

    [Fact]
    public async Task LoginAsync_ReturnsNetworkOrServerError_WhenServerReturnsSuccessButNoData()
    {
        var apiClient = new MockAuthApiClient();
        apiClient.LoginAsyncHandler = req => Task.FromResult(new AuthResult<AuthTokens>(AuthStatus.Success, null));
        var store = new MockSecureCredentialStore();
        var coordinator = new LoginCoordinator(apiClient, store);

        var result = await coordinator.LoginAsync("tenant", "user@test.com", "password");

        Assert.Equal(AuthStatus.NetworkOrServerError, result.Status);
        Assert.Null(store.SavedSession);
    }

    [Fact]
    public async Task LoginAsync_ReturnsApiError_WhenServerRejects()
    {
        var apiClient = new MockAuthApiClient();
        apiClient.LoginAsyncHandler = req => Task.FromResult(new AuthResult<AuthTokens>(AuthStatus.Unauthorized));
        var store = new MockSecureCredentialStore();
        var coordinator = new LoginCoordinator(apiClient, store);

        var result = await coordinator.LoginAsync("tenant", "user@test.com", "password");

        Assert.Equal(AuthStatus.Unauthorized, result.Status);
        Assert.Null(store.SavedSession);
    }

    [Fact]
    public async Task LoginAsync_ReturnsLocalError_WhenTokenInvalid()
    {
        var apiClient = new MockAuthApiClient();
        apiClient.LoginAsyncHandler = req => Task.FromResult(new AuthResult<AuthTokens>(AuthStatus.Success, new AuthTokens("access", "refresh", "", _fixedTime.AddMinutes(15), _fixedTime.AddDays(7))));
        var store = new MockSecureCredentialStore();
        var coordinator = new LoginCoordinator(apiClient, store);

        var result = await coordinator.LoginAsync("tenant", "user@test.com", "password");

        Assert.Equal(AuthStatus.LocalStateError, result.Status);
        Assert.Null(store.SavedSession);
    }

    [Fact]
    public async Task LoginAsync_ReturnsLocalError_WhenSaveFails_NoSessionReported()
    {
        var apiClient = new MockAuthApiClient();
        var store = new MockSecureCredentialStore { SimulateSaveFailure = true };
        var coordinator = new LoginCoordinator(apiClient, store);

        var result = await coordinator.LoginAsync("tenant", "user@test.com", "password");

        Assert.Equal(AuthStatus.LocalStateError, result.Status);
        Assert.Null(result.Data);
        Assert.Null(store.SavedSession);
    }

    [Fact]
    public async Task LoginAsync_ThrowsOperationCanceledException_WhenApiCancelled()
    {
        var apiClient = new MockAuthApiClient();
        var store = new MockSecureCredentialStore();
        var coordinator = new LoginCoordinator(apiClient, store);
        
        apiClient.LoginAsyncHandler = req => throw new OperationCanceledException();

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => coordinator.LoginAsync("tenant", "user@test.com", "password", cts.Token));
        Assert.Null(store.SavedSession);
    }

    [Fact]
    public async Task LoginAsync_ThrowsOperationCanceledException_WhenCallerCancelsBeforeSaveThrows()
    {
        var apiClient = new MockAuthApiClient();
        var store = new MockSecureCredentialStore { ThrowCancellationOnSave = true };
        var coordinator = new LoginCoordinator(apiClient, store);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => coordinator.LoginAsync("tenant", "user@test.com", "password", cts.Token));
        Assert.Null(store.SavedSession);
    }

    [Fact]
    public async Task LoginAsync_ReturnsLocalError_WhenStorageThrowsOperationCanceledUnexpectedly()
    {
        var apiClient = new MockAuthApiClient();
        var store = new MockSecureCredentialStore { ThrowCancellationOnSave = true };
        var coordinator = new LoginCoordinator(apiClient, store);

        var result = await coordinator.LoginAsync("tenant", "user@test.com", "password", CancellationToken.None);

        Assert.Equal(AuthStatus.LocalStateError, result.Status);
        Assert.Null(result.Data);
        Assert.Null(store.SavedSession);
    }
}
