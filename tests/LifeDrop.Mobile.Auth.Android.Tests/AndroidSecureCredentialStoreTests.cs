using LifeDrop.Mobile.Auth.Android;
using LifeDrop.Mobile.Auth.Models;
using Xunit;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;

namespace LifeDrop.Mobile.Auth.Android.Tests;

// A subclass that bypasses actual Android KeyStore for unit testing the logic.
public class TestableAndroidSecureCredentialStore : AndroidSecureCredentialStore
{
    public TestableAndroidSecureCredentialStore(Func<string, string?> getString, Func<string, string, bool> putString, Func<string, bool> removeString) 
        : base(getString, putString, removeString) 
    { 
    }

    protected override string Encrypt(string plaintext)
    {
        return $"fake_iv:{Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(plaintext))}";
    }

    protected override string Decrypt(string encryptedData)
    {
        var parts = encryptedData.Split(':');
        return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(parts[1]));
    }
}

public class AndroidSecureCredentialStoreTests
{
    private readonly Dictionary<string, string> _storage = new();
    private bool _simulateCommitFailure = false;
    private readonly TestableAndroidSecureCredentialStore _store;
    private static readonly DateTime _fixedTime = new DateTime(2025, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    public AndroidSecureCredentialStoreTests()
    {
        _store = new TestableAndroidSecureCredentialStore(
            key => _storage.TryGetValue(key, out var val) ? val : null,
            (key, val) => 
            {
                if (_simulateCommitFailure) return false;
                _storage[key] = val;
                return true;
            },
            key => 
            {
                if (_simulateCommitFailure) return false;
                _storage.Remove(key);
                return true;
            }
        );
    }

    private StoredSession CreateSession(SessionStatus status)
    {
        return new StoredSession(
            new AuthTokens("access", "refresh", "Bearer", _fixedTime.AddMinutes(15), _fixedTime.AddDays(7)),
            "tenant", "test@test.com", false, null, status, Guid.NewGuid(), status == SessionStatus.Active ? Guid.Empty : Guid.NewGuid()
        );
    }

    [Fact]
    public async Task SaveSessionAsync_WritesToStore()
    {
        var session = CreateSession(SessionStatus.Active);
        await _store.SaveSessionAsync(session);

        var loaded = await _store.LoadSessionAsync();
        Assert.NotNull(loaded);
        Assert.Equal(session.SessionId, loaded.SessionId);
    }

    [Fact]
    public async Task SaveSessionAsync_ReplacesAccountProperly()
    {
        var session1 = CreateSession(SessionStatus.Active);
        await _store.SaveSessionAsync(session1);

        var session2 = CreateSession(SessionStatus.Active);
        await _store.SaveSessionAsync(session2);

        var loaded = await _store.LoadSessionAsync();
        Assert.NotNull(loaded);
        Assert.Equal(session2.SessionId, loaded.SessionId);
    }

    [Fact]
    public async Task SaveSessionAsync_WhenCommitFails_ThrowsException()
    {
        var session = CreateSession(SessionStatus.Active);
        _simulateCommitFailure = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => _store.SaveSessionAsync(session));
    }

    // --- MARK TRANSITIONS ---

    [Fact]
    public async Task MarkSessionRefreshingAsync_WithMatchingState_SucceedsAndCommits()
    {
        var session = CreateSession(SessionStatus.Active);
        await _store.SaveSessionAsync(session);

        var pendingSession = session with { Status = SessionStatus.RefreshPending, AttemptId = Guid.NewGuid() };
        var result = await _store.MarkSessionRefreshingAsync(session, pendingSession);

        Assert.True(result);
        var loaded = await _store.LoadSessionAsync();
        Assert.Equal(SessionStatus.RefreshPending, loaded.Status);
        Assert.Equal(pendingSession.AttemptId, loaded.AttemptId);
    }

    [Fact]
    public async Task MarkSessionRefreshingAsync_WithGuidEmpty_Fails()
    {
        var session = CreateSession(SessionStatus.Active);
        await _store.SaveSessionAsync(session);

        var pendingSession = session with { Status = SessionStatus.RefreshPending, AttemptId = Guid.NewGuid(), SessionId = Guid.Empty };
        var result = await _store.MarkSessionRefreshingAsync(session, pendingSession);

        Assert.False(result);
        var loaded = await _store.LoadSessionAsync();
        Assert.Equal(SessionStatus.Active, loaded.Status);
    }

    [Fact]
    public async Task MarkSessionRefreshingAsync_WithEmptyAttemptId_Fails()
    {
        var session = CreateSession(SessionStatus.Active);
        await _store.SaveSessionAsync(session);

        var pendingSession = session with { Status = SessionStatus.RefreshPending, AttemptId = Guid.Empty };
        var result = await _store.MarkSessionRefreshingAsync(session, pendingSession);

        Assert.False(result);
        var loaded = await _store.LoadSessionAsync();
        Assert.Equal(SessionStatus.Active, loaded.Status);
    }

    [Fact]
    public async Task MarkSessionRefreshingAsync_WithMismatchedIdentity_FailsAndDoesNotCommit()
    {
        var session = CreateSession(SessionStatus.Active);
        await _store.SaveSessionAsync(session);

        var pendingSession = session with { Status = SessionStatus.RefreshPending, AttemptId = Guid.NewGuid(), Email = "different@test.com" };

        var result = await _store.MarkSessionRefreshingAsync(session, pendingSession);

        Assert.False(result);
        var loaded = await _store.LoadSessionAsync();
        Assert.Equal(SessionStatus.Active, loaded.Status);
        Assert.Equal("test@test.com", loaded.Email);
    }

    [Fact]
    public async Task MarkSessionRefreshingAsync_WithMismatchedExpectedIdentity_FailsAndDoesNotCommit()
    {
        var session = CreateSession(SessionStatus.Active);
        await _store.SaveSessionAsync(session);

        var staleExpected = session with { Email = "different@test.com" };
        var pendingSession = session with { Status = SessionStatus.RefreshPending, AttemptId = Guid.NewGuid() };

        var result = await _store.MarkSessionRefreshingAsync(staleExpected, pendingSession);

        Assert.False(result);
        var loaded = await _store.LoadSessionAsync();
        Assert.Equal(SessionStatus.Active, loaded.Status);
    }

    [Fact]
    public async Task MarkSessionRefreshingAsync_WithAlteredTokens_Fails()
    {
        var session = CreateSession(SessionStatus.Active);
        await _store.SaveSessionAsync(session);

        var newTokens = new AuthTokens("different", "refresh", "Bearer", _fixedTime, _fixedTime);
        var pendingSession = session with { Status = SessionStatus.RefreshPending, AttemptId = Guid.NewGuid(), Tokens = newTokens };

        var result = await _store.MarkSessionRefreshingAsync(session, pendingSession);

        Assert.False(result);
        var loaded = await _store.LoadSessionAsync();
        Assert.Equal(SessionStatus.Active, loaded.Status);
    }

    // --- COMMIT TRANSITIONS ---

    [Fact]
    public async Task CommitSessionRefreshAsync_WithCorrectState_Succeeds()
    {
        var pendingSession = CreateSession(SessionStatus.RefreshPending);
        await _store.SaveSessionAsync(pendingSession);

        var newTokens = new AuthTokens("new_access", "new_refresh", "Bearer", _fixedTime.AddMinutes(15), _fixedTime.AddDays(7));
        var newSession = pendingSession with { Status = SessionStatus.Active, AttemptId = Guid.Empty, Tokens = newTokens };
        
        var result = await _store.CommitSessionRefreshAsync(pendingSession, newSession);

        Assert.True(result);
        var loaded = await _store.LoadSessionAsync();
        Assert.Equal(SessionStatus.Active, loaded.Status);
        Assert.Equal("new_access", loaded.Tokens.AccessToken);
    }

    [Fact]
    public async Task CommitSessionRefreshAsync_WithNonEmptyAttemptId_Fails()
    {
        var pendingSession = CreateSession(SessionStatus.RefreshPending);
        await _store.SaveSessionAsync(pendingSession);

        var newTokens = new AuthTokens("new_access", "new_refresh", "Bearer", _fixedTime.AddMinutes(15), _fixedTime.AddDays(7));
        var newSession = pendingSession with { Status = SessionStatus.Active, AttemptId = Guid.NewGuid(), Tokens = newTokens };
        
        var result = await _store.CommitSessionRefreshAsync(pendingSession, newSession);

        Assert.False(result);
        var loaded = await _store.LoadSessionAsync();
        Assert.Equal(SessionStatus.RefreshPending, loaded.Status);
    }

    [Fact]
    public async Task CommitSessionRefreshAsync_WithAlteredIdentity_Fails()
    {
        var pendingSession = CreateSession(SessionStatus.RefreshPending);
        await _store.SaveSessionAsync(pendingSession);

        var newTokens = new AuthTokens("new_access", "new_refresh", "Bearer", _fixedTime.AddMinutes(15), _fixedTime.AddDays(7));
        var newSession = pendingSession with { Status = SessionStatus.Active, AttemptId = Guid.Empty, Tokens = newTokens, TenantCode = "different" };
        
        var result = await _store.CommitSessionRefreshAsync(pendingSession, newSession);

        Assert.False(result);
        var loaded = await _store.LoadSessionAsync();
        Assert.Equal(SessionStatus.RefreshPending, loaded.Status);
        Assert.Equal("tenant", loaded.TenantCode);
    }

    [Fact]
    public async Task CommitSessionRefreshAsync_WithUnsupportedTokenType_Fails()
    {
        var pendingSession = CreateSession(SessionStatus.RefreshPending);
        await _store.SaveSessionAsync(pendingSession);

        var newTokens = new AuthTokens("new_access", "new_refresh", "Basic", _fixedTime.AddMinutes(15), _fixedTime.AddDays(7));
        var newSession = pendingSession with { Status = SessionStatus.Active, AttemptId = Guid.Empty, Tokens = newTokens };
        
        var result = await _store.CommitSessionRefreshAsync(pendingSession, newSession);

        Assert.False(result);
        var loaded = await _store.LoadSessionAsync();
        Assert.Equal(SessionStatus.RefreshPending, loaded.Status);
    }

    [Fact]
    public async Task CommitSessionRefreshAsync_WithInvalidExpiry_Fails()
    {
        var pendingSession = CreateSession(SessionStatus.RefreshPending);
        await _store.SaveSessionAsync(pendingSession);

        // Access token expires after refresh token, which is invalid for StoredSession.IsValid()
        var newTokens = new AuthTokens("new_access", "new_refresh", "Bearer", _fixedTime.AddDays(10), _fixedTime.AddDays(7));
        var newSession = pendingSession with { Status = SessionStatus.Active, AttemptId = Guid.Empty, Tokens = newTokens };
        
        var result = await _store.CommitSessionRefreshAsync(pendingSession, newSession);

        Assert.False(result);
        var loaded = await _store.LoadSessionAsync();
        Assert.Equal(SessionStatus.RefreshPending, loaded.Status);
    }

    // --- CLEAR TRANSITIONS ---

    [Fact]
    public async Task ClearSessionIfMatchesAsync_OnlyClearsIfAttemptIdMatches()
    {
        var session = CreateSession(SessionStatus.Active);
        await _store.SaveSessionAsync(session);

        var mismatchedSession = session with { AttemptId = Guid.NewGuid() };
        var resultMismatch = await _store.ClearSessionIfMatchesAsync(mismatchedSession);
        Assert.False(resultMismatch);
        Assert.NotNull(await _store.LoadSessionAsync());

        var result = await _store.ClearSessionIfMatchesAsync(session);
        Assert.True(result);
        Assert.Null(await _store.LoadSessionAsync());
    }

    [Fact]
    public async Task ClearSessionIfMatchesAsync_FailsIfIdentityChanged()
    {
        var session = CreateSession(SessionStatus.Active);
        await _store.SaveSessionAsync(session);

        var mismatchedSession = session with { Email = "different@test.com" };
        var resultMismatch = await _store.ClearSessionIfMatchesAsync(mismatchedSession);
        Assert.False(resultMismatch);
        Assert.NotNull(await _store.LoadSessionAsync());
    }
}
