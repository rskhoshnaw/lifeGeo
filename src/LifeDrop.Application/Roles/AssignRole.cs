using LifeDrop.Domain.Common;
using LifeDrop.Domain.Users;

namespace LifeDrop.Application.Roles;

public enum AssignRoleResult { Assigned, AlreadyAssigned, UserNotFound, RoleNotFound, Forbidden }

public sealed record RoleAssignmentActor(TenantId TenantId, UserId UserId);

public interface IRoleAssignmentStore
{
    Task<AssignRoleResult> AssignAsync(TenantId tenantId, UserId actorId, UserId userId,
        RoleId roleId, CancellationToken cancellationToken);
}

public sealed class AssignRole(IRoleAssignmentStore store)
{
    public Task<AssignRoleResult> ExecuteAsync(RoleAssignmentActor actor, UserId userId,
        RoleId roleId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (actor.TenantId.IsEmpty || actor.UserId.IsEmpty)
            return Task.FromResult(AssignRoleResult.Forbidden);
        if (userId.IsEmpty || roleId.IsEmpty)
            throw new ArgumentException("UserId and RoleId are required.");

        return store.AssignAsync(actor.TenantId, actor.UserId, userId, roleId, cancellationToken);
    }
}
