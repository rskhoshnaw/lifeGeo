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

        var existingDevice = await _store.LoadDeviceAsync(cancellationToken);
        if (existingDevice != null)
        {
            if (!string.Equals(existingDevice.TenantCode, request.TenantCode, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(existingDevice.Email, request.Email, StringComparison.OrdinalIgnoreCase))
            {
                return new AuthResult<RegisteredDevice>(AuthStatus.Conflict, ErrorMessage: "A device registration for a different account already exists on this device.");
            }

            if (existingDevice.DeviceId == Guid.Empty || string.IsNullOrWhiteSpace(existingDevice.DeviceSecret))
            {
                return new AuthResult<RegisteredDevice>(AuthStatus.LocalStateError, ErrorMessage: "Stored device registration is malformed.");
            }

            return new AuthResult<RegisteredDevice>(AuthStatus.Success, existingDevice);
        }

        var response = await _apiClient.RegisterDeviceAsync(request, cancellationToken);
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
            return new AuthResult<RegisteredDevice>(AuthStatus.LocalStateError, ErrorMessage: "Failed to save device registration securely.");
        }

        return new AuthResult<RegisteredDevice>(AuthStatus.Success, deviceToSave);
    }
}
