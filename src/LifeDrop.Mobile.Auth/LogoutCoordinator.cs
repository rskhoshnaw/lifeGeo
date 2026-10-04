using LifeDrop.Mobile.Auth.Models;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace LifeDrop.Mobile.Auth;

public interface ILogoutCoordinator
{
    Task<AuthResult<bool>> LogoutAsync(CancellationToken cancellationToken = default);
}

public class LogoutCoordinator : ILogoutCoordinator
{
    private readonly IAuthApiClient _apiClient;
    private readonly ISecureCredentialStore _store;

    public LogoutCoordinator(IAuthApiClient apiClient, ISecureCredentialStore store)
    {
        _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<AuthResult<bool>> LogoutAsync(CancellationToken cancellationToken = default)
    {
        var session = await _store.LoadSessionAsync(cancellationToken);
        if (session == null || !session.IsValid())
        {
            return new AuthResult<bool>(AuthStatus.LocalStateError, ErrorMessage: "No valid active session found to log out.");
        }

        AuthResult<bool> serverResult;
        try
        {
            serverResult = await _apiClient.LogoutAsync(session.Tokens.AccessToken, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new AuthResult<bool>(AuthStatus.NetworkOrServerError, ErrorMessage: "An unexpected error occurred during server logout.");
        }

        if (serverResult.Status != AuthStatus.Success)
        {
            return serverResult;
        }

        try
        {
            var cleared = await _store.ClearSessionIfMatchesAsync(session, cancellationToken);
            if (!cleared)
            {
                return new AuthResult<bool>(AuthStatus.LocalStateError, ErrorMessage: "Session was modified concurrently and could not be safely cleared.");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new AuthResult<bool>(AuthStatus.LocalStateError, ErrorMessage: "Failed to safely clear session from local storage.");
        }

        return new AuthResult<bool>(AuthStatus.Success, true);
    }
}
