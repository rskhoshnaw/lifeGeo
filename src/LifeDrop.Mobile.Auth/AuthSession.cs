using LifeDrop.Mobile.Auth.Models;

namespace LifeDrop.Mobile.Auth;

public sealed class AuthSession : IAuthSession
{
    private readonly ISecureCredentialStore _store;
    private readonly IAuthApiClient _apiClient;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    public AuthSession(
        ISecureCredentialStore store,
        IAuthApiClient apiClient,
        TimeProvider timeProvider)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async Task<AuthResult<string>> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        var session = await _store.LoadSessionAsync(cancellationToken);
        if (session == null)
        {
            return AuthResult<string>.Unauthorized("No tokens found in storage.");
        }

        if (!session.IsValid())
        {
            if (session.Status == SessionStatus.RefreshPending)
            {
                // Fall through to wait on the lock. If it's a concurrent call in the same instance,
                // it will resolve. If it's a dead state, it will be caught inside the lock.
            }
            else
            {
                return AuthResult<string>.LocalStateError("Stored session metadata is invalid or corrupted.");
            }
        }
        else
        {
            var now = _timeProvider.GetUtcNow().UtcDateTime;
            var timeRemaining = session.Tokens.ExpiresAt - now;

            if (timeRemaining.TotalSeconds > 30)
            {
                return AuthResult<string>.Ok(session.Tokens.AccessToken);
            }
        }

        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            // Re-check after acquiring the lock in case another thread just refreshed it
            session = await _store.LoadSessionAsync(cancellationToken);
            if (session == null)
            {
                return AuthResult<string>.Unauthorized("Tokens were cleared by another operation.");
            }

            if (!session.IsValid())
            {
                if (session.Status == SessionStatus.RefreshPending)
                    return AuthResult<string>.LocalStateError("Session refresh is currently pending. Please try again or re-authenticate.");
                return AuthResult<string>.LocalStateError("Stored session metadata is invalid or corrupted.");
            }

            var now = _timeProvider.GetUtcNow().UtcDateTime;
            var timeRemaining = session.Tokens.ExpiresAt - now;

            if (timeRemaining.TotalSeconds > 30)
            {
                return AuthResult<string>.Ok(session.Tokens.AccessToken);
            }

            Guid? refreshDeviceId = null;
            string? refreshDeviceSecret = null;

            if (session.IsDeviceBound)
            {
                var device = await _store.LoadDeviceAsync(cancellationToken);
                
                if (device == null || 
                    device.DeviceId != session.BoundDeviceId ||
                    !string.Equals(device.TenantCode, session.TenantCode, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(device.Email, session.Email, StringComparison.OrdinalIgnoreCase))
                {
                    return AuthResult<string>.LocalStateError("Device binding information is missing or mismatched for this session.");
                }

                if (string.IsNullOrWhiteSpace(device.DeviceSecret))
                {
                    return AuthResult<string>.LocalStateError("Device secret is missing or whitespace.");
                }

                refreshDeviceId = device.DeviceId;
                refreshDeviceSecret = device.DeviceSecret;
            }

            var pendingSession = session with 
            { 
                Status = SessionStatus.RefreshPending,
                AttemptId = Guid.NewGuid()
            };

            // Pre-flight mark pending
            var marked = await _store.MarkSessionRefreshingAsync(session, pendingSession, cancellationToken);
            if (!marked)
            {
                // Concurrently modified, fail locally.
                return AuthResult<string>.LocalStateError("Failed to lock the session for refresh. Please try again.");
            }

            var request = new RefreshRequest(pendingSession.Tokens.RefreshToken, refreshDeviceId, refreshDeviceSecret);
            var refreshResult = await _apiClient.RefreshAsync(request, cancellationToken);

            if (refreshResult.IsSuccess && refreshResult.Data != null)
            {
                var newSession = new StoredSession(
                    refreshResult.Data,
                    pendingSession.TenantCode,
                    pendingSession.Email,
                    pendingSession.IsDeviceBound,
                    pendingSession.BoundDeviceId,
                    SessionStatus.Active,
                    pendingSession.SessionId,
                    Guid.Empty
                );

                var committed = await _store.CommitSessionRefreshAsync(pendingSession, newSession, cancellationToken);
                if (!committed)
                {
                    return AuthResult<string>.LocalStateError("Refresh completed but the session was superseded by a newer login or operation.");
                }
                return AuthResult<string>.Ok(newSession.Tokens.AccessToken);
            }
            else if (refreshResult.Status == AuthStatus.Unauthorized)
            {
                // On refresh 401, atomically conditionally clear
                var cleared = await _store.ClearSessionIfMatchesAsync(pendingSession, cancellationToken);
                if (!cleared)
                {
                    return AuthResult<string>.LocalStateError("Refresh failed (401) but the session was superseded by a newer login or operation.");
                }
                return AuthResult<string>.Unauthorized("Session refresh token expired or revoked.");
            }
            else
            {
                // On a network/server error, session remains RefreshPending to prevent replay
                return AuthResult<string>.NetworkOrServerError(
                    refreshResult.ErrorMessage ?? "Failed to refresh token due to a network or server error.",
                    refreshResult.HttpStatusCode);
            }
        }
        finally
        {
            _refreshLock.Release();
        }
    }
}
