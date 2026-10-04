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
    // Kill Session (plan step 0.7): revokes every active session of the target and audits the action
    // under the actor, in one transaction. Returns the number of revoked refresh tokens.
    Task<int> RevokeAllForUserAsync(TenantId tenantId, UserId actorId, UserId targetUserId, CancellationToken cancellationToken);
}

public enum KillSessionsResult { Success, Forbidden, UserNotFound }

// An administrator ends all sessions of a user (PRD §4). sessions.kill's data scope limits the targets:
// tenant = any user, region = users in the actor's region, own = only the actor.
public sealed class KillUserSessions(ISessionStore sessions, LifeDrop.Application.Roles.IPermissionStore permissions)
{
    public async Task<KillSessionsResult> ExecuteAsync(TenantId tenantId, UserId actorId, UserId targetUserId, CancellationToken cancellationToken)
    {
        if (tenantId.IsEmpty || actorId.IsEmpty) return KillSessionsResult.Forbidden;
        if (targetUserId.IsEmpty) return KillSessionsResult.UserNotFound;

        var scope = await permissions.GetAccessScopeAsync(tenantId, actorId, LifeDrop.Application.Roles.PermissionKeys.SessionsKill, cancellationToken);
        if (scope is null) return KillSessionsResult.Forbidden;

        var (exists, targetRegion) = await permissions.GetUserRegionAsync(tenantId, targetUserId, cancellationToken);
        if (!exists) return KillSessionsResult.UserNotFound;
        if (targetUserId != actorId)
        {
            if (scope.Scope == LifeDrop.Application.Roles.DataScope.Own) return KillSessionsResult.Forbidden;
            if (scope.Scope == LifeDrop.Application.Roles.DataScope.Region && (scope.RegionId is null || targetRegion != scope.RegionId))
                return KillSessionsResult.Forbidden;
        }

        await sessions.RevokeAllForUserAsync(tenantId, actorId, targetUserId, cancellationToken);
        return KillSessionsResult.Success;
    }
}
