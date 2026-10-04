using LifeDrop.Mobile.Auth.Models;

namespace LifeDrop.Mobile.Auth;

public interface IAuthApiClient
{
    Task<AuthResult<RegisteredDevice>> RegisterDeviceAsync(
        RegisterDeviceRequest request,
        CancellationToken cancellationToken = default);

    Task<AuthResult<AuthTokens>> LoginAsync(
        LoginRequest request,
        CancellationToken cancellationToken = default);

    Task<AuthResult<AuthTokens>> RefreshAsync(
        RefreshRequest request,
        CancellationToken cancellationToken = default);

    Task<AuthResult<bool>> LogoutAsync(
        string accessToken,
        CancellationToken cancellationToken = default);
}
