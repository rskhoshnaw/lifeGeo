using LifeDrop.Mobile.Auth.Models;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace LifeDrop.Mobile.Auth;

public interface IDeviceRegistrationCoordinator
{
    Task<AuthResult<RegisteredDevice>> RegisterDeviceAsync(RegisterDeviceRequest request, CancellationToken cancellationToken = default);
}

public class DeviceRegistrationCoordinator : IDeviceRegistrationCoordinator
{
    private readonly IAuthApiClient _apiClient;
    private readonly ISecureCredentialStore _store;

    public DeviceRegistrationCoordinator(IAuthApiClient apiClient, ISecureCredentialStore store)
    {
        _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<AuthResult<RegisteredDevice>> RegisterDeviceAsync(RegisterDeviceRequest request, CancellationToken cancellationToken = default)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.TenantCode) || string.IsNullOrWhiteSpace(request.Email))
        {
            return new AuthResult<RegisteredDevice>(AuthStatus.BadRequest, ErrorMessage: "Missing required registration parameters.");
        }

        // Spaces around the e-mail (autofill, copy and paste) must not make the same person look like another account.
        request = request with { Email = request.Email.Trim() };
        var existingDevice = await _store.LoadDeviceAsync(cancellationToken);
        // Plan step 5.63: "Register" always asks the server. The phone's own memory of an earlier registration is not trusted: a manager may have deleted that device (then the old record
        // is useless and must be replaced), or it belongs to another company or e-mail, or it is damaged. Before, such a record ended the registration on the phone without any request,
        // and the user could never register again. The server decides; only when it says that this very phone is registered already (waiting for approval or approved) and the phone
        // holds that registration for the same company and e-mail, the stored registration stays.
        var sameRegistration = existingDevice is not null && existingDevice.DeviceId != Guid.Empty && !string.IsNullOrWhiteSpace(existingDevice.DeviceSecret) &&
            string.Equals(CompanyDirectory.Normalize(existingDevice.TenantCode), CompanyDirectory.Normalize(request.TenantCode), StringComparison.OrdinalIgnoreCase) &&
            string.Equals((existingDevice.Email ?? "").Trim(), request.Email, StringComparison.OrdinalIgnoreCase);

        var response = await _apiClient.RegisterDeviceAsync(request, cancellationToken);
        if (response.Status == AuthStatus.Conflict && sameRegistration && response.ErrorCode is null or "DEVICE_PENDING" or "DEVICE_ALREADY_APPROVED")
        {
            return new AuthResult<RegisteredDevice>(AuthStatus.Success, existingDevice);   // this phone is registered already, as it remembers
        }
        if (response.Status != AuthStatus.Success)
        {
            return response;
        }

        if (response.Data == null)
        {
            return new AuthResult<RegisteredDevice>(AuthStatus.NetworkOrServerError, ErrorMessage: "Invalid server response: device registration data missing.");
        }

        var deviceToSave = response.Data with { TenantCode = request.TenantCode, Email = request.Email };

        if (deviceToSave.DeviceId == Guid.Empty || string.IsNullOrWhiteSpace(deviceToSave.DeviceSecret))
        {
            return new AuthResult<RegisteredDevice>(AuthStatus.NetworkOrServerError, ErrorMessage: "Invalid server response: malformed device credentials.");
        }

        try
        {
            await _store.SaveDeviceAsync(deviceToSave, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new AuthResult<RegisteredDevice>(AuthStatus.LocalStateError, ErrorCode: "reg_save_failed", ErrorMessage: "Failed to save device registration securely.");
        }

        return new AuthResult<RegisteredDevice>(AuthStatus.Success, deviceToSave);
    }
}
