using LifeDrop.Mobile.Auth.Models;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace LifeDrop.Mobile.Auth;

public interface ILoginCoordinator
{
    Task<AuthResult<StoredSession>> LoginAsync(string tenantCode, string email, string password, CancellationToken cancellationToken = default);
}

public class LoginCoordinator : ILoginCoordinator
{
    private readonly IAuthApiClient _apiClient;
    private readonly ISecureCredentialStore _store;

    public LoginCoordinator(IAuthApiClient apiClient, ISecureCredentialStore store)
    {
        _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<AuthResult<StoredSession>> LoginAsync(string tenantCode, string email, string password, CancellationToken cancellationToken = default)
    {
        var device = await _store.LoadDeviceAsync(cancellationToken);
        
        bool isDeviceBound = false;
        Guid? deviceId = null;
        string? deviceSecret = null;

        if (device != null)
        {
            if (!string.Equals(device.TenantCode, tenantCode, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(device.Email, email, StringComparison.OrdinalIgnoreCase))
            {
                return new AuthResult<StoredSession>(AuthStatus.LocalStateError);
            }

            if (device.DeviceId == Guid.Empty || string.IsNullOrWhiteSpace(device.DeviceSecret))
            {
                return new AuthResult<StoredSession>(AuthStatus.LocalStateError);
            }

            isDeviceBound = true;
            deviceId = device.DeviceId;
            deviceSecret = device.DeviceSecret;
        }

        var request = new LoginRequest(tenantCode, email, password, deviceId, deviceSecret);

        var response = await _apiClient.LoginAsync(request, cancellationToken);
        if (response.Status != AuthStatus.Success)
        {
            return new AuthResult<StoredSession>(response.Status, ErrorCode: response.ErrorCode, ErrorMessage: response.ErrorMessage, HttpStatusCode: response.HttpStatusCode);
        }
        if (response.Data == null)
        {
            return new AuthResult<StoredSession>(AuthStatus.NetworkOrServerError, ErrorMessage: "Invalid server response: token data missing.");
        }

        var tokens = response.Data;

        StoredSession session;
        try
        {
            session = new StoredSession(
                tokens,
                tenantCode,
                email,
                isDeviceBound,
                deviceId,
                SessionStatus.Active,
                Guid.NewGuid(),
                Guid.Empty
            );
        }
        catch (ArgumentException)
        {
            return new AuthResult<StoredSession>(AuthStatus.LocalStateError);
        }

        if (!session.IsValid())
        {
            return new AuthResult<StoredSession>(AuthStatus.LocalStateError);
        }

        try
        {
            await _store.SaveSessionAsync(session, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new AuthResult<StoredSession>(AuthStatus.LocalStateError);
        }

        return new AuthResult<StoredSession>(AuthStatus.Success, session);
    }
}
