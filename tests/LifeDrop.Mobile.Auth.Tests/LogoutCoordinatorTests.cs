using LifeDrop.Mobile.Auth.Models;
using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace LifeDrop.Mobile.Auth.Tests;

public class LogoutCoordinatorTests
{
    private static readonly DateTime _fixedTime = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private class MockAuthApiClient : IAuthApiClient
    {
        public bool SimulateServerFailure { get; set; }
        public bool ThrowCancellation { get; set; }
        public int LogoutCallCount { get; private set; }
        public string? LastAccessToken { get; private set; }
        public Func<Task>? OnBeforeServerReturn { get; set; }
        
        public async Task<AuthResult<bool>> LogoutAsync(string accessToken, CancellationToken cancellationToken = default)
        {
            LogoutCallCount++;
            LastAccessToken = accessToken;

            if (ThrowCancellation) throw new OperationCanceledException();
            if (OnBeforeServerReturn != null) await OnBeforeServerReturn();
            
            if (SimulateServerFailure) return new AuthResult<bool>(AuthStatus.Forbidden, false);
            return new AuthResult<bool>(AuthStatus.Success, true);
        }

        public Task<AuthResult<AuthTokens>> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<AuthResult<AuthTokens>> RefreshAsync(RefreshRequest request, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<AuthResult<RegisteredDevice>> RegisterDeviceAsync(RegisterDeviceRequest request, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }

    private class MockSecureCredentialStore : ISecureCredentialStore
    {
        public StoredSession? CurrentSession { get; set; }
        public bool ClearCalled { get; private set; }
        public bool DeviceClearCalled { get; private set; }

        public Task<StoredSession?> LoadSessionAsync(CancellationToken cancellationToken = default) => Task.FromResult(CurrentSession);

        public Task<bool> ClearSessionIfMatchesAsync(StoredSession expectedSession, CancellationToken cancellationToken = default)
        {
            ClearCalled = true;
            if (CurrentSession != null && CurrentSession.SessionId == expectedSession.SessionId)
            {
                CurrentSession = null;
                return Task.FromResult(true);
            }
            return Task.FromResult(false);
        }

        public Task ClearDeviceAsync(CancellationToken cancellationToken = default)
        {
            DeviceClearCalled = true;
            return Task.CompletedTask;
        }

        public Task SaveDeviceAsync(RegisteredDevice device, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<RegisteredDevice?> LoadDeviceAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task SaveSessionAsync(StoredSession session, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<bool> MarkSessionRefreshingAsync(StoredSession expectedSession, StoredSession pendingSession, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<bool> CommitSessionRefreshAsync(StoredSession pendingSession, StoredSession newSession, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task ClearSessionAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task ClearAllAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }

    private static StoredSession CreateValidSession(Guid? sessionId = null) =>
        new StoredSession(
            new AuthTokens("access_token_123", "refresh_token_123", "Bearer", _fixedTime.AddHours(1), _fixedTime.AddDays(1)),
            "tenant",
            "user@test.com",
            true,
            Guid.NewGuid(),
            SessionStatus.Active,
            sessionId ?? Guid.NewGuid()
        );

    [Fact]
    public async Task LogoutAsync_Success_CallsServerExactlyOnceWithCorrectToken_AndClearsSession_AndPreservesDevice()
    {
        var session = CreateValidSession();
        var store = new MockSecureCredentialStore { CurrentSession = session };
        var apiClient = new MockAuthApiClient();
        var coordinator = new LogoutCoordinator(apiClient, store);

        var result = await coordinator.LogoutAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(1, apiClient.LogoutCallCount);
        Assert.Equal(session.Tokens.AccessToken, apiClient.LastAccessToken);
        Assert.True(store.ClearCalled);
        Assert.Null(store.CurrentSession); // Successfully cleared
        Assert.False(store.DeviceClearCalled);
    }

    [Fact]
    public async Task LogoutAsync_ReturnsLocalStateError_WhenNoValidSession()
    {
        var store = new MockSecureCredentialStore { CurrentSession = null };
        var apiClient = new MockAuthApiClient();
        var coordinator = new LogoutCoordinator(apiClient, store);

        var result = await coordinator.LogoutAsync();

        Assert.Equal(AuthStatus.LocalStateError, result.Status);
        Assert.Equal(0, apiClient.LogoutCallCount);
        Assert.False(store.ClearCalled);
    }

    [Fact]
    public async Task LogoutAsync_ReturnsServerError_AndMakesZeroClearAttempts_WhenServerFails()
    {
        var session = CreateValidSession();
        var store = new MockSecureCredentialStore { CurrentSession = session };
        var apiClient = new MockAuthApiClient { SimulateServerFailure = true };
        var coordinator = new LogoutCoordinator(apiClient, store);

        var result = await coordinator.LogoutAsync();

        Assert.Equal(AuthStatus.Forbidden, result.Status);
        Assert.Equal(1, apiClient.LogoutCallCount);
        Assert.False(store.ClearCalled);
        Assert.NotNull(store.CurrentSession); // Session should still be intact
        Assert.Equal(session.SessionId, store.CurrentSession.SessionId);
        Assert.False(store.DeviceClearCalled);
    }

    [Fact]
    public async Task LogoutAsync_ReturnsLocalStateError_WhenSessionReplacedConcurrently_AndLeavesNewerSessionIntact()
    {
        var originalSession = CreateValidSession();
        var newSession = CreateValidSession(); // Has a different Guid automatically
        var store = new MockSecureCredentialStore { CurrentSession = originalSession };
        
        var apiClient = new MockAuthApiClient();
        apiClient.OnBeforeServerReturn = () => 
        {
            // Mutate the store concurrently before the server call completes
            store.CurrentSession = newSession;
            return Task.CompletedTask;
        };

        var coordinator = new LogoutCoordinator(apiClient, store);

        var result = await coordinator.LogoutAsync();

        Assert.Equal(AuthStatus.LocalStateError, result.Status);
        Assert.True(store.ClearCalled); // It attempted to clear
        Assert.NotNull(store.CurrentSession); // But failed, leaving current intact
        Assert.Equal(newSession.SessionId, store.CurrentSession.SessionId); // The newer session survives
        Assert.False(store.DeviceClearCalled);
    }

    [Fact]
    public async Task LogoutAsync_ThrowsOperationCanceledException_WhenCancelled()
    {
        var store = new MockSecureCredentialStore { CurrentSession = CreateValidSession() };
        var apiClient = new MockAuthApiClient { ThrowCancellation = true };
        var coordinator = new LogoutCoordinator(apiClient, store);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => coordinator.LogoutAsync(cts.Token));
        Assert.False(store.ClearCalled);
    }
}
