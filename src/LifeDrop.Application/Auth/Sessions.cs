using LifeDrop.Domain.Common;

namespace LifeDrop.Application.Auth;

public sealed record SessionIdentity(TenantId TenantId, UserId UserId, Guid FamilyId, Guid? DeviceId);
public sealed record SessionTokens(SessionIdentity Identity, string RefreshToken, DateTime RefreshExpiresAt);
public sealed record RefreshRequest(string RefreshToken, Guid? DeviceId = null, string? DeviceSecret = null);

public interface ISessionStore
{
    Task<SessionTokens> IssueAsync(LoginIdentity identity, Guid? deviceId,
        CancellationToken cancellationToken);
    Task<SessionTokens?> RotateAsync(RefreshRequest request, CancellationToken cancellationToken);
    Task<bool> IsAccessActiveAsync(SessionIdentity identity, CancellationToken cancellationToken);
    Task RevokeFamilyAsync(SessionIdentity identity, CancellationToken cancellationToken);
}
