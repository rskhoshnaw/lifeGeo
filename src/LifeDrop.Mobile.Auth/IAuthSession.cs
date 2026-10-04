using LifeDrop.Mobile.Auth.Models;

namespace LifeDrop.Mobile.Auth;

public interface IAuthSession
{
    Task<AuthResult<string>> GetAccessTokenAsync(CancellationToken cancellationToken = default);
}
