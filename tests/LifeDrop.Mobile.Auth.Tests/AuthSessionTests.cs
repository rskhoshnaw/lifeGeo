using System.Diagnostics;
using LifeDrop.Mobile.Auth.Models;
using Xunit;

namespace LifeDrop.Mobile.Auth.Tests;

public class AuthSessionTests
{
    private sealed class FakeTimeProvider : TimeProvider
    {
        public DateTimeOffset CurrentTime { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => CurrentTime;
    }

    private sealed class FakeCredentialStore : ISecureCredentialStore
    {
        public StoredSession? Session { get; set; }
        public bool ThrowOnSave { get; set; }
        public int SaveCallCount { get; private set; }
        public int ClearCallCount { get; private set; }

        public Task SaveSessionAsync(StoredSession session, CancellationToken cancellationToken = default)
        {
            if (ThrowOnSave) throw new InvalidOperationException("Storage failed");
            Session = session;
            SaveCallCount++;
            return Task.CompletedTask;
        }

        public Task<StoredSession?> LoadSessionAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Session);
        }

        public Task ClearSessionAsync(CancellationToken cancellationToken = default)
        {
            Session = null;
            ClearCallCount++;
            return Task.CompletedTask;
        }

                public Task<bool> ClearSessionIfMatchesAsync(StoredSession expectedSession, CancellationToken cancellationToken = default)
        {
            if (Session == null || Session.SessionId != expectedSession.SessionId || Session.AttemptId != expectedSession.AttemptId)
                return Task.FromResult(false);
            
            Session = null;
            ClearCallCount++;
            return Task.FromResult(true);
        }

        public int MarkCallCount { get; private set; }
        public int CommitCallCount { get; private set; }
        public bool ThrowOnCommit { get; set; }
        public bool ThrowOnMark { get; set; }

                public Task<bool> MarkSessionRefreshingAsync(StoredSession expectedSession, StoredSession pendingSession, CancellationToken cancellationToken = default)
        {
            if (ThrowOnMark) return Task.FromResult(false);
            if (Session == null || Session.SessionId != expectedSession.SessionId || Session.AttemptId != expectedSession.AttemptId || Session.Status != SessionStatus.Active)
                return Task.FromResult(false);
            
            Session = pendingSession;
            MarkCallCount++;
            return Task.FromResult(true);
        }

                public Task<bool> CommitSessionRefreshAsync(StoredSession pendingSession, StoredSession newSession, CancellationToken cancellationToken = default)
        {
            if (ThrowOnCommit) throw new InvalidOperationException("Storage failed");
            if (Session == null || Session.SessionId != pendingSession.SessionId || Session.AttemptId != pendingSession.AttemptId || Session.Status != SessionStatus.RefreshPending)
                return Task.FromResult(false);
            
            Session = newSession;
            CommitCallCount++;
            return Task.FromResult(true);
        }

        public RegisteredDevice? Device { get; set; }

        public Task SaveDeviceAsync(RegisteredDevice device, CancellationToken cancellationToken = default) 
        {
            Device = device;
            return Task.CompletedTask;
        }
        
        public Task<RegisteredDevice?> LoadDeviceAsync(CancellationToken cancellationToken = default) => Task.FromResult(Device);
        
        public Task ClearDeviceAsync(CancellationToken cancellationToken = default)
        {
            Device = null;
            return Task.CompletedTask;
        }
        
        public Task ClearAllAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeAuthApiClient : IAuthApiClient
    {
        public Func<RefreshRequest, CancellationToken, Task<AuthResult<AuthTokens>>>? RefreshAsyncHandler { get; set; }
        public int RefreshCallCount { get; private set; }

        public Task<AuthResult<AuthTokens>> RefreshAsync(RefreshRequest request, CancellationToken cancellationToken = default)
        {
            RefreshCallCount++;
            if (RefreshAsyncHandler != null) return RefreshAsyncHandler(request, cancellationToken);
            return Task.FromResult(AuthResult<AuthTokens>.NetworkOrServerError("Not configured"));
        }

        public Task<AuthResult<RegisteredDevice>> RegisterDeviceAsync(RegisterDeviceRequest request, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<AuthResult<AuthTokens>> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<AuthResult<bool>> LogoutAsync(string accessToken, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }

    [Fact]
    public void StoredSession_Constructor_ThrowsOnInvalidMetadata()
    {
        var tokens = new AuthTokens("access", "refresh", "Bearer", DateTime.UtcNow, DateTime.UtcNow);

        Assert.Throws<ArgumentException>(() => new StoredSession(tokens, "", "email@domain.com", false, null));
        Assert.Throws<ArgumentException>(() => new StoredSession(tokens, "TENANT", "", false, null));
        Assert.Throws<ArgumentException>(() => new StoredSession(tokens, "TENANT", "email@domain.com", true, null)); // missing device ID
        Assert.Throws<ArgumentException>(() => new StoredSession(tokens, "TENANT", "email@domain.com", false, Guid.NewGuid())); // device ID when unbound
    }

    [Fact]
    public async Task GetAccessTokenAsync_ValidToken_ReturnsImmediately()
    {
        var store = new FakeCredentialStore();
        var api = new FakeAuthApiClient();
        var time = new FakeTimeProvider { CurrentTime = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero) };

        var tokens = new AuthTokens("valid_access", "valid_refresh", "Bearer", time.CurrentTime.UtcDateTime.AddMinutes(15), time.CurrentTime.UtcDateTime.AddDays(7));
        store.Session = new StoredSession(tokens, "TENANT", "user@domain.com", false, null);
        var session = new AuthSession(store, api, time);

        var result = await session.GetAccessTokenAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal("valid_access", result.Data);
        Assert.Equal(0, api.RefreshCallCount);
    }

    [Fact]
    public async Task GetAccessTokenAsync_NearExpiry_RefreshesAndSaves()
    {
        var store = new FakeCredentialStore();
        var api = new FakeAuthApiClient();
        var time = new FakeTimeProvider { CurrentTime = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero) };

        var tokens = new AuthTokens("old_access", "old_refresh", "Bearer", time.CurrentTime.UtcDateTime.AddSeconds(20), time.CurrentTime.UtcDateTime.AddDays(7));
        store.Session = new StoredSession(tokens, "TENANT", "user@domain.com", false, null);
        
        api.RefreshAsyncHandler = (req, ct) => Task.FromResult(AuthResult<AuthTokens>.Ok(
            new AuthTokens("new_access", "new_refresh", "Bearer", time.CurrentTime.UtcDateTime.AddMinutes(15), time.CurrentTime.UtcDateTime.AddDays(7))));

        var session = new AuthSession(store, api, time);

        var result = await session.GetAccessTokenAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal("new_access", result.Data);
        Assert.Equal(1, api.RefreshCallCount);
        Assert.Equal(1, store.CommitCallCount);
        Assert.NotNull(store.Session);
        Assert.Equal("new_access", store.Session.Tokens.AccessToken);
        Assert.Equal("TENANT", store.Session.TenantCode);
    }

    [Fact]
    public async Task GetAccessTokenAsync_ConcurrentCallers_RotatesOnlyOnce()
    {
        var store = new FakeCredentialStore();
        var api = new FakeAuthApiClient();
        var time = new FakeTimeProvider { CurrentTime = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero) };

        var tokens = new AuthTokens("old_access", "old_refresh", "Bearer", time.CurrentTime.UtcDateTime.AddSeconds(10), time.CurrentTime.UtcDateTime.AddDays(7));
        store.Session = new StoredSession(tokens, "TENANT", "user@domain.com", false, null);
        
        var refreshTcs = new TaskCompletionSource<AuthResult<AuthTokens>>();
        var firstCallEnteredTcs = new TaskCompletionSource();

        api.RefreshAsyncHandler = (req, ct) =>
        {
            firstCallEnteredTcs.SetResult();
            time.CurrentTime = time.CurrentTime.AddMinutes(1);
            return refreshTcs.Task;
        };

        var session = new AuthSession(store, api, time);

        var task1 = session.GetAccessTokenAsync();
        await firstCallEnteredTcs.Task;
        var task2 = session.GetAccessTokenAsync();
        
        var newTokens = new AuthTokens("new_access", "new_refresh", "Bearer", time.CurrentTime.UtcDateTime.AddMinutes(15), time.CurrentTime.UtcDateTime.AddDays(7));
        refreshTcs.SetResult(AuthResult<AuthTokens>.Ok(newTokens));

        var results = await Task.WhenAll(task1, task2);

        Assert.True(results[0].IsSuccess);
        Assert.Equal("new_access", results[0].Data);
        Assert.True(results[1].IsSuccess);
        Assert.Equal("new_access", results[1].Data);
        
        Assert.Equal(1, api.RefreshCallCount);
        Assert.Equal(1, store.CommitCallCount);
    }

    [Fact]
    public async Task GetAccessTokenAsync_RefreshReturns401_ClearsStoredTokens()
    {
        var store = new FakeCredentialStore();
        var api = new FakeAuthApiClient();
        var time = new FakeTimeProvider { CurrentTime = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero) };

        var tokens = new AuthTokens("old_access", "old_refresh", "Bearer", time.CurrentTime.UtcDateTime.AddSeconds(10), time.CurrentTime.UtcDateTime.AddDays(7));
        store.Session = new StoredSession(tokens, "TENANT", "user@domain.com", false, null);
        api.RefreshAsyncHandler = (req, ct) => Task.FromResult(AuthResult<AuthTokens>.Unauthorized());

        var session = new AuthSession(store, api, time);
        var result = await session.GetAccessTokenAsync();

        Assert.False(result.IsSuccess);
        Assert.Equal(AuthStatus.Unauthorized, result.Status);
        Assert.Equal(1, store.ClearCallCount);
        Assert.Null(store.Session);
    }

    [Fact]
    public async Task GetAccessTokenAsync_TransientFailure_PreservesTokensAndReturnsFailure()
    {
        var store = new FakeCredentialStore();
        var api = new FakeAuthApiClient();
        var time = new FakeTimeProvider { CurrentTime = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero) };

        var originalTokens = new AuthTokens("old_access", "old_refresh", "Bearer", time.CurrentTime.UtcDateTime.AddSeconds(10), time.CurrentTime.UtcDateTime.AddDays(7));
        store.Session = new StoredSession(originalTokens, "TENANT", "user@domain.com", false, null);
        
        api.RefreshAsyncHandler = (req, ct) => Task.FromResult(AuthResult<AuthTokens>.NetworkOrServerError("Timeout"));

        var session = new AuthSession(store, api, time);
        var result = await session.GetAccessTokenAsync();

        Assert.False(result.IsSuccess);
        Assert.Equal(AuthStatus.NetworkOrServerError, result.Status);
        Assert.Equal(0, store.ClearCallCount);
        Assert.Equal(0, store.CommitCallCount);
        Assert.NotNull(store.Session);
        Assert.Equal("old_access", store.Session.Tokens.AccessToken);
    }

    [Fact]
    public async Task GetAccessTokenAsync_SaveFailure_PropagatesException()
    {
        var store = new FakeCredentialStore { ThrowOnCommit = true };
        var api = new FakeAuthApiClient();
        var time = new FakeTimeProvider { CurrentTime = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero) };

        var tokens = new AuthTokens("old_access", "old_refresh", "Bearer", time.CurrentTime.UtcDateTime.AddSeconds(10), time.CurrentTime.UtcDateTime.AddDays(7));
        store.Session = new StoredSession(tokens, "TENANT", "user@domain.com", false, null);
        
        api.RefreshAsyncHandler = (req, ct) => Task.FromResult(AuthResult<AuthTokens>.Ok(
            new AuthTokens("new_access", "new_refresh", "Bearer", time.CurrentTime.UtcDateTime.AddMinutes(15), time.CurrentTime.UtcDateTime.AddDays(7))));

        var session = new AuthSession(store, api, time);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => session.GetAccessTokenAsync());
        Assert.Equal("Storage failed", ex.Message);
    }

    [Fact]
    public async Task GetAccessTokenAsync_Cancellation_PropagatesException()
    {
        var store = new FakeCredentialStore();
        var api = new FakeAuthApiClient();
        var time = new FakeTimeProvider { CurrentTime = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero) };

        var tokens = new AuthTokens("old_access", "old_refresh", "Bearer", time.CurrentTime.UtcDateTime.AddSeconds(10), time.CurrentTime.UtcDateTime.AddDays(7));
        store.Session = new StoredSession(tokens, "TENANT", "user@domain.com", false, null);
        
        api.RefreshAsyncHandler = async (req, ct) => 
        {
            await Task.Delay(5000, ct); 
            return AuthResult<AuthTokens>.Unauthorized();
        };

        var session = new AuthSession(store, api, time);
        using var cts = new CancellationTokenSource(10);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.GetAccessTokenAsync(cts.Token));
    }

    [Fact]
    public async Task GetAccessTokenAsync_BoundSession_MatchingDeviceAndAccount_AttachesCredentialsAndPreservesMetadata()
    {
        var deviceId = Guid.NewGuid();
        var store = new FakeCredentialStore();
        var api = new FakeAuthApiClient();
        var time = new FakeTimeProvider { CurrentTime = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero) };

        var tokens = new AuthTokens("bound_access", "bound_refresh", "Bearer", time.CurrentTime.UtcDateTime.AddSeconds(10), time.CurrentTime.UtcDateTime.AddDays(7));
        store.Session = new StoredSession(tokens, "TENANT1", "test@domain.com", true, deviceId);
            
        store.Device = new RegisteredDevice(deviceId, "device_secret_123", "TENANT1", "test@domain.com");
        
        RefreshRequest? capturedRequest = null;
        api.RefreshAsyncHandler = (req, ct) => 
        {
            capturedRequest = req;
            return Task.FromResult(AuthResult<AuthTokens>.Ok(
                new AuthTokens("new_access", "new_refresh", "Bearer", time.CurrentTime.UtcDateTime.AddMinutes(15), time.CurrentTime.UtcDateTime.AddDays(7))));
        };

        var session = new AuthSession(store, api, time);
        var result = await session.GetAccessTokenAsync();

        Assert.True(result.IsSuccess);
        Assert.NotNull(capturedRequest);
        Assert.Equal(deviceId, capturedRequest.DeviceId);
        Assert.Equal("device_secret_123", capturedRequest.DeviceSecret);
        
        Assert.Equal("TENANT1", store.Session!.TenantCode);
        Assert.Equal("test@domain.com", store.Session.Email);
        Assert.True(store.Session.IsDeviceBound);
        Assert.Equal(deviceId, store.Session.BoundDeviceId);
    }

    [Fact]
    public async Task GetAccessTokenAsync_UnboundSession_DoesNotAttachCredentials()
    {
        var store = new FakeCredentialStore();
        var api = new FakeAuthApiClient();
        var time = new FakeTimeProvider { CurrentTime = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero) };

        var tokens = new AuthTokens("unbound_access", "unbound_refresh", "Bearer", time.CurrentTime.UtcDateTime.AddSeconds(10), time.CurrentTime.UtcDateTime.AddDays(7));
        store.Session = new StoredSession(tokens, "TENANT1", "test@domain.com", false, null);
            
        store.Device = new RegisteredDevice(Guid.NewGuid(), "device_secret_123", "TENANT1", "test@domain.com"); 
        
        RefreshRequest? capturedRequest = null;
        api.RefreshAsyncHandler = (req, ct) => 
        {
            capturedRequest = req;
            return Task.FromResult(AuthResult<AuthTokens>.Ok(
                new AuthTokens("new_access", "new_refresh", "Bearer", time.CurrentTime.UtcDateTime.AddMinutes(15), time.CurrentTime.UtcDateTime.AddDays(7))));
        };

        var session = new AuthSession(store, api, time);
        var result = await session.GetAccessTokenAsync();

        Assert.True(result.IsSuccess);
        Assert.NotNull(capturedRequest);
        Assert.Null(capturedRequest.DeviceId);
        Assert.Null(capturedRequest.DeviceSecret);
    }

    [Fact]
    public async Task GetAccessTokenAsync_BoundSession_MismatchedDeviceId_ReturnsDistinctFailure()
    {
        var sessionDeviceId = Guid.NewGuid();
        var otherDeviceId = Guid.NewGuid();
        var store = new FakeCredentialStore();
        var api = new FakeAuthApiClient();
        var time = new FakeTimeProvider { CurrentTime = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero) };

        var tokens = new AuthTokens("bound_access", "bound_refresh", "Bearer", time.CurrentTime.UtcDateTime.AddSeconds(10), time.CurrentTime.UtcDateTime.AddDays(7));
        store.Session = new StoredSession(tokens, "TENANT1", "test@domain.com", true, sessionDeviceId);
            
        store.Device = new RegisteredDevice(otherDeviceId, "device_secret_123", "TENANT1", "test@domain.com"); 
        
        var session = new AuthSession(store, api, time);
        var result = await session.GetAccessTokenAsync();

        Assert.False(result.IsSuccess);
        Assert.Equal(AuthStatus.LocalStateError, result.Status);
        Assert.Equal("Device binding information is missing or mismatched for this session.", result.ErrorMessage);
        Assert.Equal(0, api.RefreshCallCount);
        Assert.Equal(0, store.ClearCallCount);
    }

    [Fact]
    public async Task GetAccessTokenAsync_BoundSession_MismatchedAccount_ReturnsDistinctFailure()
    {
        var deviceId = Guid.NewGuid();
        var store = new FakeCredentialStore();
        var api = new FakeAuthApiClient();
        var time = new FakeTimeProvider { CurrentTime = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero) };

        var tokens = new AuthTokens("bound_access", "bound_refresh", "Bearer", time.CurrentTime.UtcDateTime.AddSeconds(10), time.CurrentTime.UtcDateTime.AddDays(7));
        store.Session = new StoredSession(tokens, "TENANT1", "test@domain.com", true, deviceId);
            
        store.Device = new RegisteredDevice(deviceId, "device_secret_123", "TENANT2", "other@domain.com"); 
        
        var session = new AuthSession(store, api, time);
        var result = await session.GetAccessTokenAsync();

        Assert.False(result.IsSuccess);
        Assert.Equal(AuthStatus.LocalStateError, result.Status);
        Assert.Equal("Device binding information is missing or mismatched for this session.", result.ErrorMessage);
        Assert.Equal(0, api.RefreshCallCount);
        Assert.Equal(0, store.ClearCallCount);
    }

    [Fact]
    public async Task GetAccessTokenAsync_BoundSession_MissingCredentials_ReturnsDistinctFailure()
    {
        var sessionDeviceId = Guid.NewGuid();
        var store = new FakeCredentialStore();
        var api = new FakeAuthApiClient();
        var time = new FakeTimeProvider { CurrentTime = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero) };

        var tokens = new AuthTokens("bound_access", "bound_refresh", "Bearer", time.CurrentTime.UtcDateTime.AddSeconds(10), time.CurrentTime.UtcDateTime.AddDays(7));
        store.Session = new StoredSession(tokens, "TENANT1", "test@domain.com", true, sessionDeviceId);
            
        store.Device = null; 
        
        var session = new AuthSession(store, api, time);
        var result = await session.GetAccessTokenAsync();

        Assert.False(result.IsSuccess);
        Assert.Equal(AuthStatus.LocalStateError, result.Status);
        Assert.Equal("Device binding information is missing or mismatched for this session.", result.ErrorMessage);
        Assert.Equal(0, api.RefreshCallCount);
        Assert.Equal(0, store.ClearCallCount); 
    }

    [Fact]
    public async Task GetAccessTokenAsync_ValidToken_MalformedStoredState_ReturnsDistinctFailure()
    {
        var store = new FakeCredentialStore();
        var api = new FakeAuthApiClient();
        var time = new FakeTimeProvider { CurrentTime = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero) };

        var tokens = new AuthTokens("valid_access", "valid_refresh", "Bearer", time.CurrentTime.UtcDateTime.AddMinutes(15), time.CurrentTime.UtcDateTime.AddDays(7));
        // Bypass constructor validation using 'with' to simulate corrupted deserialization
        store.Session = new StoredSession(tokens, "TENANT", "user@domain.com", true, Guid.NewGuid()) with { BoundDeviceId = Guid.Empty };
        var session = new AuthSession(store, api, time);

        var result = await session.GetAccessTokenAsync();

        Assert.False(result.IsSuccess);
        Assert.Equal(AuthStatus.LocalStateError, result.Status);
        Assert.Equal("Stored session metadata is invalid or corrupted.", result.ErrorMessage);
        Assert.Equal(0, api.RefreshCallCount);
    }

    [Fact]
    public async Task GetAccessTokenAsync_NearExpiry_MalformedStoredState_ReturnsDistinctFailure()
    {
        var store = new FakeCredentialStore();
        var api = new FakeAuthApiClient();
        var time = new FakeTimeProvider { CurrentTime = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero) };

        var tokens = new AuthTokens("old_access", "old_refresh", "Bearer", time.CurrentTime.UtcDateTime.AddSeconds(20), time.CurrentTime.UtcDateTime.AddDays(7));
        // Bypass constructor validation using 'with' to simulate corrupted deserialization
        store.Session = new StoredSession(tokens, "TENANT", "user@domain.com", false, null) with { TenantCode = "" };
        
        var session = new AuthSession(store, api, time);

        var result = await session.GetAccessTokenAsync();

        Assert.False(result.IsSuccess);
        Assert.Equal(AuthStatus.LocalStateError, result.Status);
        Assert.Equal("Stored session metadata is invalid or corrupted.", result.ErrorMessage);
        Assert.Equal(0, api.RefreshCallCount);
    }

    [Fact]
    public async Task GetAccessTokenAsync_ValidToken_UnsupportedTokenType_ReturnsDistinctFailure()
    {
        var store = new FakeCredentialStore();
        var api = new FakeAuthApiClient();
        var time = new FakeTimeProvider { CurrentTime = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero) };

        var tokens = new AuthTokens("valid_access", "valid_refresh", "Basic", time.CurrentTime.UtcDateTime.AddMinutes(15), time.CurrentTime.UtcDateTime.AddDays(7));
        store.Session = new StoredSession(tokens, "TENANT", "user@domain.com", false, null);
        var session = new AuthSession(store, api, time);

        var result = await session.GetAccessTokenAsync();

        Assert.False(result.IsSuccess);
        Assert.Equal(AuthStatus.LocalStateError, result.Status);
    }

    [Fact]
    public async Task GetAccessTokenAsync_ValidToken_DefaultExpiry_ReturnsDistinctFailure()
    {
        var store = new FakeCredentialStore();
        var api = new FakeAuthApiClient();
        var time = new FakeTimeProvider { CurrentTime = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero) };

        var tokens = new AuthTokens("valid_access", "valid_refresh", "Bearer", default, time.CurrentTime.UtcDateTime.AddDays(7));
        store.Session = new StoredSession(tokens, "TENANT", "user@domain.com", false, null);
        var session = new AuthSession(store, api, time);

        var result = await session.GetAccessTokenAsync();

        Assert.False(result.IsSuccess);
        Assert.Equal(AuthStatus.LocalStateError, result.Status);
    }

    [Fact]
    public async Task GetAccessTokenAsync_NearExpiry_InconsistentExpiry_ReturnsDistinctFailure()
    {
        var store = new FakeCredentialStore();
        var api = new FakeAuthApiClient();
        var time = new FakeTimeProvider { CurrentTime = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero) };

        // ExpiresAt is technically near expiry or valid, but it is GREATER than RefreshExpiresAt which is invalid
        var tokens = new AuthTokens("old_access", "old_refresh", "Bearer", time.CurrentTime.UtcDateTime.AddSeconds(20), time.CurrentTime.UtcDateTime.AddSeconds(10));
        store.Session = new StoredSession(tokens, "TENANT", "user@domain.com", false, null);
        
        var session = new AuthSession(store, api, time);

        var result = await session.GetAccessTokenAsync();

        Assert.False(result.IsSuccess);
        Assert.Equal(AuthStatus.LocalStateError, result.Status);
        Assert.Equal(0, api.RefreshCallCount); // Refresh must not be called
    }

    [Fact]
    public async Task GetAccessTokenAsync_FailsPreflight_ReturnsDistinctFailure()
    {
        var store = new FakeCredentialStore();
        var api = new FakeAuthApiClient();
        var time = new FakeTimeProvider { CurrentTime = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero) };

        var tokens = new AuthTokens("old_access", "old_refresh", "Bearer", time.CurrentTime.UtcDateTime.AddSeconds(20), time.CurrentTime.UtcDateTime.AddDays(7));
        store.Session = new StoredSession(tokens, "TENANT", "user@domain.com", false, null, SessionStatus.RefreshPending);
        
        var session = new AuthSession(store, api, time);

        var result = await session.GetAccessTokenAsync();

        Assert.False(result.IsSuccess);
        Assert.Equal(AuthStatus.LocalStateError, result.Status);
        Assert.Equal(0, store.MarkCallCount);
        Assert.Equal(0, api.RefreshCallCount);
    }

    [Fact]
    public async Task GetAccessTokenAsync_FailedCommit_LeavesSessionPendingAndReturnsFailure()
    {
        var store = new FakeCredentialStore { ThrowOnCommit = true };
        var api = new FakeAuthApiClient();
        var time = new FakeTimeProvider { CurrentTime = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero) };

        var tokens = new AuthTokens("old_access", "old_refresh", "Bearer", time.CurrentTime.UtcDateTime.AddSeconds(20), time.CurrentTime.UtcDateTime.AddDays(7));
        store.Session = new StoredSession(tokens, "TENANT", "user@domain.com", false, null);
        
        api.RefreshAsyncHandler = (req, ct) => Task.FromResult(AuthResult<AuthTokens>.Ok(
            new AuthTokens("new_access", "new_refresh", "Bearer", time.CurrentTime.UtcDateTime.AddMinutes(15), time.CurrentTime.UtcDateTime.AddDays(7))));

        var session = new AuthSession(store, api, time);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => session.GetAccessTokenAsync());
        Assert.Equal("Storage failed", ex.Message);
        Assert.Equal(1, store.MarkCallCount);
        Assert.Equal(0, store.CommitCallCount);
        Assert.Equal(SessionStatus.RefreshPending, store.Session!.Status);
    }

    [Fact]
    public async Task GetAccessTokenAsync_NetworkTimeout_LeavesSessionPending()
    {
        var store = new FakeCredentialStore();
        var api = new FakeAuthApiClient();
        var time = new FakeTimeProvider { CurrentTime = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero) };

        var tokens = new AuthTokens("old_access", "old_refresh", "Bearer", time.CurrentTime.UtcDateTime.AddSeconds(20), time.CurrentTime.UtcDateTime.AddDays(7));
        store.Session = new StoredSession(tokens, "TENANT", "user@domain.com", false, null);
        
        api.RefreshAsyncHandler = (req, ct) => Task.FromResult(AuthResult<AuthTokens>.NetworkOrServerError("Timeout"));

        var session = new AuthSession(store, api, time);
        var result = await session.GetAccessTokenAsync();

        Assert.False(result.IsSuccess);
        Assert.Equal(AuthStatus.NetworkOrServerError, result.Status);
        Assert.Equal(1, store.MarkCallCount);
        Assert.Equal(0, store.CommitCallCount);
        Assert.Equal(0, store.ClearCallCount);
        Assert.Equal(SessionStatus.RefreshPending, store.Session!.Status);
    }

    [Fact]
    public async Task GetAccessTokenAsync_ConcurrentInstances_OnlyOneCallsApi()
    {
        var store = new FakeCredentialStore();
        var api = new FakeAuthApiClient();
        var time = new FakeTimeProvider { CurrentTime = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero) };

        var tokens = new AuthTokens("old_access", "old_refresh", "Bearer", time.CurrentTime.UtcDateTime.AddSeconds(20), time.CurrentTime.UtcDateTime.AddDays(7));
        store.Session = new StoredSession(tokens, "TENANT", "user@domain.com", false, null);
        
        var refreshTcs = new TaskCompletionSource<AuthResult<AuthTokens>>();
        var firstCallEnteredTcs = new TaskCompletionSource();

        api.RefreshAsyncHandler = (req, ct) =>
        {
            firstCallEnteredTcs.SetResult();
            return refreshTcs.Task;
        };

        var session1 = new AuthSession(store, api, time);
        var session2 = new AuthSession(store, api, time);

        var task1 = session1.GetAccessTokenAsync();
        await firstCallEnteredTcs.Task;
        
        // session1 is now hung inside RefreshAsync. The store has SessionStatus.RefreshPending.
        // session2 should see it's pending and fail fast.
        var task2 = session2.GetAccessTokenAsync();
        
        var result2 = await task2;
        Assert.False(result2.IsSuccess);
        Assert.Equal(AuthStatus.LocalStateError, result2.Status); // Fails closed due to pending state
        
        refreshTcs.SetResult(AuthResult<AuthTokens>.Ok(
            new AuthTokens("new_access", "new_refresh", "Bearer", time.CurrentTime.UtcDateTime.AddMinutes(15), time.CurrentTime.UtcDateTime.AddDays(7))));
            
        var result1 = await task1;
        Assert.True(result1.IsSuccess);
        
        Assert.Equal(1, store.MarkCallCount);
        Assert.Equal(1, api.RefreshCallCount);
        Assert.Equal(1, store.CommitCallCount);
    }

    [Fact]
    public async Task GetAccessTokenAsync_MarkPendingFails_ReturnsLocalStateError()
    {
        var store = new FakeCredentialStore { ThrowOnMark = true };
        var api = new FakeAuthApiClient();
        var time = new FakeTimeProvider { CurrentTime = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero) };

        var tokens = new AuthTokens("old_access", "old_refresh", "Bearer", time.CurrentTime.UtcDateTime.AddSeconds(20), time.CurrentTime.UtcDateTime.AddDays(7));
        store.Session = new StoredSession(tokens, "TENANT", "user@domain.com", false, null);
        
        var session = new AuthSession(store, api, time);
        var result = await session.GetAccessTokenAsync();

        Assert.False(result.IsSuccess);
        Assert.Equal(AuthStatus.LocalStateError, result.Status);
        Assert.Equal("Failed to lock the session for refresh. Please try again.", result.ErrorMessage);
        Assert.Equal(0, api.RefreshCallCount);
    }

    [Fact]
    public async Task GetAccessTokenAsync_AccountSwitchDuringRefresh_PreventsClearOn401()
    {
        var store = new FakeCredentialStore();
        var api = new FakeAuthApiClient();
        var time = new FakeTimeProvider { CurrentTime = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero) };

        var oldTokens = new AuthTokens("old_access", "old_refresh", "Bearer", time.CurrentTime.UtcDateTime.AddSeconds(20), time.CurrentTime.UtcDateTime.AddDays(7));
        store.Session = new StoredSession(oldTokens, "TENANT", "user1@domain.com", false, null);
        
        var firstCallEnteredTcs = new TaskCompletionSource();
        var refreshTcs = new TaskCompletionSource<AuthResult<AuthTokens>>();

        api.RefreshAsyncHandler = (req, ct) =>
        {
            firstCallEnteredTcs.SetResult();
            return refreshTcs.Task;
        };

        var session = new AuthSession(store, api, time);
        var task = session.GetAccessTokenAsync();

        await firstCallEnteredTcs.Task;

        // Simulate user logging in with a new account and replacing the stored session
        var newTokens = new AuthTokens("new_access", "new_refresh", "Bearer", time.CurrentTime.UtcDateTime.AddMinutes(60), time.CurrentTime.UtcDateTime.AddDays(7));
        store.Session = new StoredSession(newTokens, "TENANT", "user2@domain.com", false, null);

        // Old refresh returns 401
        refreshTcs.SetResult(AuthResult<AuthTokens>.Unauthorized());

        var result = await task;

        Assert.False(result.IsSuccess);
        Assert.Equal(AuthStatus.LocalStateError, result.Status);
        
        // The store MUST NOT be cleared because it holds the new user's tokens
        Assert.Equal(0, store.ClearCallCount);
        Assert.NotNull(store.Session);
        Assert.Equal("new_access", store.Session.Tokens.AccessToken);
        Assert.Equal("user2@domain.com", store.Session.Email);
    }

    [Fact]
    public async Task GetAccessTokenAsync_AccountSwitchDuringRefresh_PreventsOverwriteOn200()
    {
        var store = new FakeCredentialStore();
        var api = new FakeAuthApiClient();
        var time = new FakeTimeProvider { CurrentTime = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero) };

        var oldTokens = new AuthTokens("old_access", "old_refresh", "Bearer", time.CurrentTime.UtcDateTime.AddSeconds(20), time.CurrentTime.UtcDateTime.AddDays(7));
        store.Session = new StoredSession(oldTokens, "TENANT", "user1@domain.com", false, null);
        
        var firstCallEnteredTcs = new TaskCompletionSource();
        var refreshTcs = new TaskCompletionSource<AuthResult<AuthTokens>>();

        api.RefreshAsyncHandler = (req, ct) =>
        {
            firstCallEnteredTcs.SetResult();
            return refreshTcs.Task;
        };

        var session = new AuthSession(store, api, time);
        var task = session.GetAccessTokenAsync();

        await firstCallEnteredTcs.Task;

        // Simulate user logging in with a new account
        var loginTokens = new AuthTokens("login_access", "login_refresh", "Bearer", time.CurrentTime.UtcDateTime.AddMinutes(60), time.CurrentTime.UtcDateTime.AddDays(7));
        store.Session = new StoredSession(loginTokens, "TENANT", "user2@domain.com", false, null);

        // Old refresh returns 200
        var refreshedTokens = new AuthTokens("refreshed_access", "refreshed_refresh", "Bearer", time.CurrentTime.UtcDateTime.AddMinutes(15), time.CurrentTime.UtcDateTime.AddDays(7));
        refreshTcs.SetResult(AuthResult<AuthTokens>.Ok(refreshedTokens));

        var result = await task;

        // The commit should fail and result in LocalStateError
        Assert.False(result.IsSuccess);
        Assert.Equal(AuthStatus.LocalStateError, result.Status);
        
        // The store MUST NOT contain the refreshed tokens
        Assert.Equal(0, store.CommitCallCount);
        Assert.NotNull(store.Session);
        Assert.Equal("login_access", store.Session.Tokens.AccessToken);
        Assert.Equal("user2@domain.com", store.Session.Email);
    }
    [Fact]
    public async Task GetAccessTokenAsync_AccountSwitchWithIdenticalTokens_PreventsClearOn401()
    {
        var store = new FakeCredentialStore();
        var api = new FakeAuthApiClient();
        var time = new FakeTimeProvider { CurrentTime = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero) };

        var identicalToken = "synthetic_access_token";
        var oldTokens = new AuthTokens(identicalToken, "old_refresh", "Bearer", time.CurrentTime.UtcDateTime.AddSeconds(20), time.CurrentTime.UtcDateTime.AddDays(7));
        store.Session = new StoredSession(oldTokens, "TENANT", "user1@domain.com", false, null);
        
        var firstCallEnteredTcs = new TaskCompletionSource();
        var refreshTcs = new TaskCompletionSource<AuthResult<AuthTokens>>();

        api.RefreshAsyncHandler = (req, ct) =>
        {
            firstCallEnteredTcs.SetResult();
            return refreshTcs.Task;
        };

        var session = new AuthSession(store, api, time);
        var task = session.GetAccessTokenAsync();

        await firstCallEnteredTcs.Task;

        var newTokens = new AuthTokens(identicalToken, "new_refresh", "Bearer", time.CurrentTime.UtcDateTime.AddMinutes(60), time.CurrentTime.UtcDateTime.AddDays(7));
        store.Session = new StoredSession(newTokens, "TENANT", "user2@domain.com", false, null);

        refreshTcs.SetResult(AuthResult<AuthTokens>.Unauthorized());

        var result = await task;

        Assert.False(result.IsSuccess);
        Assert.Equal(AuthStatus.LocalStateError, result.Status);
        
        Assert.Equal(0, store.ClearCallCount);
        Assert.NotNull(store.Session);
        Assert.Equal("user2@domain.com", store.Session.Email);
    }

    [Fact]
    public async Task GetAccessTokenAsync_BoundSession_EmptyDeviceSecret_FailsPreflight()
    {
        var store = new FakeCredentialStore();
        var api = new FakeAuthApiClient();
        var time = new FakeTimeProvider { CurrentTime = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero) };

        var deviceId = Guid.NewGuid();
        var tokens = new AuthTokens("access", "refresh", "Bearer", time.CurrentTime.UtcDateTime.AddSeconds(20), time.CurrentTime.UtcDateTime.AddDays(7));
        
        store.Session = new StoredSession(tokens, "TENANT", "user@domain.com", true, deviceId);
        store.Device = new RegisteredDevice(deviceId, "  ", "TENANT", "user@domain.com");

        var session = new AuthSession(store, api, time);
        var result = await session.GetAccessTokenAsync();

        Assert.False(result.IsSuccess);
        Assert.Equal(AuthStatus.LocalStateError, result.Status);
        Assert.Equal("Device secret is missing or whitespace.", result.ErrorMessage);
        
        Assert.Equal(0, store.MarkCallCount);
        Assert.Equal(0, api.RefreshCallCount);
        Assert.Equal(SessionStatus.Active, store.Session.Status);
    }
}

