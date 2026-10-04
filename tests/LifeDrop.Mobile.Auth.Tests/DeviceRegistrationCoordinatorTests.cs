using LifeDrop.Mobile.Auth.Models;
using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace LifeDrop.Mobile.Auth.Tests;

public class DeviceRegistrationCoordinatorTests
{
    private class MockAuthApiClient : IAuthApiClient
    {
        public Func<RegisterDeviceRequest, Task<AuthResult<RegisteredDevice>>>? RegisterDeviceHandler { get; set; }

        public Task<AuthResult<RegisteredDevice>> RegisterDeviceAsync(RegisterDeviceRequest request, CancellationToken cancellationToken = default)
        {
            if (RegisterDeviceHandler != null) return RegisterDeviceHandler(request);
            return Task.FromResult(new AuthResult<RegisteredDevice>(AuthStatus.Success, new RegisteredDevice(Guid.NewGuid(), "secret_token", request.TenantCode, request.Email)));
        }
        
        public Task<AuthResult<AuthTokens>> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<AuthResult<AuthTokens>> RefreshAsync(RefreshRequest request, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<AuthResult<bool>> LogoutAsync(string accessToken, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }

    private class MockSecureCredentialStore : ISecureCredentialStore
    {
        public RegisteredDevice? DeviceToReturn { get; set; }
        public bool SimulateSaveFailure { get; set; }
        public bool ThrowCancellationOnSave { get; set; }
        public RegisteredDevice? SavedDevice { get; private set; }

        public Task<RegisteredDevice?> LoadDeviceAsync(CancellationToken cancellationToken = default) => Task.FromResult(DeviceToReturn);
        
        public Task SaveDeviceAsync(RegisteredDevice device, CancellationToken cancellationToken = default)
        {
            if (ThrowCancellationOnSave) throw new OperationCanceledException();
            if (SimulateSaveFailure) throw new InvalidOperationException("Simulated save failure");
            SavedDevice = device;
            return Task.CompletedTask;
        }

        public Task SaveSessionAsync(StoredSession session, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task ClearDeviceAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<StoredSession?> LoadSessionAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<bool> MarkSessionRefreshingAsync(StoredSession expectedSession, StoredSession pendingSession, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<bool> CommitSessionRefreshAsync(StoredSession pendingSession, StoredSession newSession, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task ClearSessionAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<bool> ClearSessionIfMatchesAsync(StoredSession expectedSession, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task ClearAllAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }

    [Fact]
    public async Task RegisterDeviceAsync_Success_WhenNoExistingRegistration()
    {
        var apiClient = new MockAuthApiClient();
        var store = new MockSecureCredentialStore();
        var coordinator = new DeviceRegistrationCoordinator(apiClient, store);
        var request = new RegisterDeviceRequest("tenant", "user@test.com", "pass", "My Phone", "Android", "123", "1.0");

        var result = await coordinator.RegisterDeviceAsync(request);

        Assert.Equal(AuthStatus.Success, result.Status);
        Assert.NotNull(result.Data);
        Assert.Equal("tenant", result.Data.TenantCode);
        Assert.Equal("user@test.com", result.Data.Email);
        Assert.NotNull(store.SavedDevice);
        Assert.Equal(result.Data.DeviceId, store.SavedDevice.DeviceId);
    }

    [Fact]
    public async Task RegisterDeviceAsync_ReturnsExisting_WhenExistingRegistrationMatches_ZeroApiCallsAndZeroSaves()
    {
        var apiClient = new MockAuthApiClient();
        bool apiCalled = false;
        apiClient.RegisterDeviceHandler = req => { apiCalled = true; return Task.FromResult(new AuthResult<RegisteredDevice>(AuthStatus.Success)); };
        var store = new MockSecureCredentialStore { DeviceToReturn = new RegisteredDevice(Guid.NewGuid(), "old_secret", "tenant", "user@test.com") };
        var coordinator = new DeviceRegistrationCoordinator(apiClient, store);
        var request = new RegisterDeviceRequest("tenant", "user@test.com", "pass", "My Phone", "Android", "123", "1.0");

        var result = await coordinator.RegisterDeviceAsync(request);

        Assert.Equal(AuthStatus.Success, result.Status);
        Assert.NotNull(result.Data);
        Assert.Equal(store.DeviceToReturn.DeviceId, result.Data.DeviceId);
        Assert.False(apiCalled);
        Assert.Null(store.SavedDevice);
    }

    [Fact]
    public async Task RegisterDeviceAsync_ReturnsLocalError_WhenExistingRegistrationMalformed_ZeroApiCallsAndZeroSaves()
    {
        var apiClient = new MockAuthApiClient();
        bool apiCalled = false;
        apiClient.RegisterDeviceHandler = req => { apiCalled = true; return Task.FromResult(new AuthResult<RegisteredDevice>(AuthStatus.Success)); };
        var store = new MockSecureCredentialStore { DeviceToReturn = new RegisteredDevice(Guid.Empty, "", "tenant", "user@test.com") };
        var coordinator = new DeviceRegistrationCoordinator(apiClient, store);
        var request = new RegisterDeviceRequest("tenant", "user@test.com", "pass", "My Phone", "Android", "123", "1.0");

        var result = await coordinator.RegisterDeviceAsync(request);

        Assert.Equal(AuthStatus.LocalStateError, result.Status);
        Assert.False(apiCalled);
        Assert.Null(store.SavedDevice);
    }

    [Fact]
    public async Task RegisterDeviceAsync_ReturnsConflict_WhenAccountMismatches_ZeroApiCalls()
    {
        var apiClient = new MockAuthApiClient();
        bool apiCalled = false;
        apiClient.RegisterDeviceHandler = req => { apiCalled = true; return Task.FromResult(new AuthResult<RegisteredDevice>(AuthStatus.Success)); };
        var store = new MockSecureCredentialStore { DeviceToReturn = new RegisteredDevice(Guid.NewGuid(), "old_secret", "different", "user@test.com") };
        var coordinator = new DeviceRegistrationCoordinator(apiClient, store);
        var request = new RegisterDeviceRequest("tenant", "user@test.com", "pass", "My Phone", "Android", "123", "1.0");

        var result = await coordinator.RegisterDeviceAsync(request);

        Assert.Equal(AuthStatus.Conflict, result.Status);
        Assert.False(apiCalled);
        Assert.Null(store.SavedDevice);
    }

    [Fact]
    public async Task RegisterDeviceAsync_ReturnsNetworkOrServerError_WhenServerReturnsSuccessButNoData()
    {
        var apiClient = new MockAuthApiClient();
        apiClient.RegisterDeviceHandler = req => Task.FromResult(new AuthResult<RegisteredDevice>(AuthStatus.Success, null));
        var store = new MockSecureCredentialStore();
        var coordinator = new DeviceRegistrationCoordinator(apiClient, store);
        var request = new RegisterDeviceRequest("tenant", "user@test.com", "pass", "My Phone", "Android", "123", "1.0");

        var result = await coordinator.RegisterDeviceAsync(request);

        Assert.Equal(AuthStatus.NetworkOrServerError, result.Status);
        Assert.Null(store.SavedDevice);
    }

    [Fact]
    public async Task RegisterDeviceAsync_ReturnsNetworkOrServerError_WhenServerReturnsMalformedDevice_ZeroSaves()
    {
        var apiClient = new MockAuthApiClient();
        apiClient.RegisterDeviceHandler = req => Task.FromResult(new AuthResult<RegisteredDevice>(AuthStatus.Success, new RegisteredDevice(Guid.Empty, " ", req.TenantCode, req.Email)));
        var store = new MockSecureCredentialStore();
        var coordinator = new DeviceRegistrationCoordinator(apiClient, store);
        var request = new RegisterDeviceRequest("tenant", "user@test.com", "pass", "My Phone", "Android", "123", "1.0");

        var result = await coordinator.RegisterDeviceAsync(request);

        Assert.Equal(AuthStatus.NetworkOrServerError, result.Status);
        Assert.Null(store.SavedDevice);
    }

    [Fact]
    public async Task RegisterDeviceAsync_ReturnsLocalError_WhenSaveFails()
    {
        var apiClient = new MockAuthApiClient();
        var store = new MockSecureCredentialStore { SimulateSaveFailure = true };
        var coordinator = new DeviceRegistrationCoordinator(apiClient, store);
        var request = new RegisterDeviceRequest("tenant", "user@test.com", "pass", "My Phone", "Android", "123", "1.0");

        var result = await coordinator.RegisterDeviceAsync(request);

        Assert.Equal(AuthStatus.LocalStateError, result.Status);
        Assert.Null(result.Data);
        Assert.Null(store.SavedDevice);
    }

    [Fact]
    public async Task RegisterDeviceAsync_ThrowsOperationCanceledException_WhenCallerCancelsBeforeSaveThrows()
    {
        var apiClient = new MockAuthApiClient();
        var store = new MockSecureCredentialStore { ThrowCancellationOnSave = true };
        var coordinator = new DeviceRegistrationCoordinator(apiClient, store);
        var request = new RegisterDeviceRequest("tenant", "user@test.com", "pass", "My Phone", "Android", "123", "1.0");

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => coordinator.RegisterDeviceAsync(request, cts.Token));
        Assert.Null(store.SavedDevice);
    }

    [Fact]
    public async Task RegisterDeviceAsync_ReturnsLocalError_WhenStorageThrowsOperationCanceledUnexpectedly()
    {
        var apiClient = new MockAuthApiClient();
        var store = new MockSecureCredentialStore { ThrowCancellationOnSave = true };
        var coordinator = new DeviceRegistrationCoordinator(apiClient, store);
        var request = new RegisterDeviceRequest("tenant", "user@test.com", "pass", "My Phone", "Android", "123", "1.0");

        var result = await coordinator.RegisterDeviceAsync(request, CancellationToken.None);

        Assert.Equal(AuthStatus.LocalStateError, result.Status);
        Assert.Null(result.Data);
        Assert.Null(store.SavedDevice);
    }

    [Fact]
    public async Task RegisterDeviceAsync_ReturnsBadRequest_WhenInputInvalid()
    {
        var apiClient = new MockAuthApiClient();
        var store = new MockSecureCredentialStore();
        var coordinator = new DeviceRegistrationCoordinator(apiClient, store);
        
        var request = new RegisterDeviceRequest("", "user@test.com", "pass", "My Phone", "Android", "123", "1.0"); // Empty tenant
        var result = await coordinator.RegisterDeviceAsync(request);

        Assert.Equal(AuthStatus.BadRequest, result.Status);
        Assert.Null(store.SavedDevice);
    }
}
