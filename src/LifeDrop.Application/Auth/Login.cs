using LifeDrop.Domain.Common;

namespace LifeDrop.Application.Auth;

public sealed record LoginRequest(string TenantCode, string Email, string Password,
    Guid? DeviceId = null, string? DeviceSecret = null);
public sealed record LoginIdentity(TenantId TenantId, UserId UserId, bool RequiresDeviceBinding);

public interface ILoginStore
{
    Task<LoginIdentity?> AuthenticateAsync(LoginRequest request, CancellationToken cancellationToken);
}

public sealed class Login(ILoginStore store)
{
    public Task<LoginIdentity?> ExecuteAsync(LoginRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.TenantCode) ||
            string.IsNullOrWhiteSpace(request.Email) ||
            string.IsNullOrEmpty(request.Password) ||
            request.TenantCode.Length > 100 || request.Email.Length > 320 || request.Password.Length > 1024)
            throw new ArgumentException("Tenant code, email, and password are required within supported lengths.");
        return store.AuthenticateAsync(request with
        {
            TenantCode = request.TenantCode.Trim(),
            Email = request.Email.Trim().ToLowerInvariant()
        }, cancellationToken);
    }
}
