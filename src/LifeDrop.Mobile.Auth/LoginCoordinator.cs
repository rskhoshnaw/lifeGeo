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
        email = (email ?? "").Trim();   // spaces from autofill or paste must not look like another account
        var device = await _store.LoadDeviceAsync(cancellationToken);
        
        bool isDeviceBound = false;
        Guid? deviceId = null;
        var deviceEmailChanged = false;
        string? deviceSecret = null;

        if (device != null)
        {
            if (!string.Equals(CompanyDirectory.Normalize(device.TenantCode), CompanyDirectory.Normalize(tenantCode), StringComparison.OrdinalIgnoreCase))
            {
                return new AuthResult<StoredSession>(AuthStatus.LocalStateError, ErrorCode: "device_mismatch");
            }
            // Another e-mail on a phone registered for this company (the device was moved to this user, e.g. two accounts merged): the server decides, it
            // accepts the device only for its own user. On success the stored device gets the new e-mail (below).
            deviceEmailChanged = !string.Equals((device.Email ?? "").Trim(), email, StringComparison.OrdinalIgnoreCase);

            if (device.DeviceId == Guid.Empty || string.IsNullOrWhiteSpace(device.DeviceSecret))
            {
                return new AuthResult<StoredSession>(AuthStatus.LocalStateError, ErrorCode: "device_malformed");
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

        if (deviceEmailChanged && device is not null)
        {
            try
            {
                await _store.SaveDeviceAsync(device with { Email = email }, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return new AuthResult<StoredSession>(AuthStatus.LocalStateError, ErrorCode: "device_save_failed");
            }
        }

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
            return new AuthResult<StoredSession>(AuthStatus.LocalStateError, ErrorCode: "session_build");
        }

        if (!session.IsValid())
        {
            return new AuthResult<StoredSession>(AuthStatus.LocalStateError, ErrorCode: "session_invalid");
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
            return new AuthResult<StoredSession>(AuthStatus.LocalStateError, ErrorCode: "save_failed");
        }

        return new AuthResult<StoredSession>(AuthStatus.Success, session);
    }
}
