using LifeDrop.Application.Roles;
using LifeDrop.Domain.Common;

namespace LifeDrop.Application.Users;

public sealed record ChangeUserEmailRequest(string? Email);

public sealed record SetUserPasswordRequest(string? Password);

public sealed record SetUserCityRequest(string? City);

public sealed record ChangeOwnPasswordRequest(string? CurrentPassword, string? NewPassword);

// WrongPassword (plan step 4.46): the current password given for a change of one's own password is not the right one.
public enum ManageUserResult { Done, Forbidden, InvalidData, NotFound, Conflict, WrongPassword }

public interface IUserAccountStore
{
    // Every call is one transaction with its audit row: user.email_changed, user.password_changed, user.deactivated, user.activated.
    // Setting a password and deactivating also end every open session of that user. Only users of the actor's own company are reachable.
    Task<ManageUserResult> ChangeEmailAsync(TenantId tenantId, UserId actorId, UserId targetId, string email, CancellationToken cancellationToken);
    Task<ManageUserResult> SetPasswordAsync(TenantId tenantId, UserId actorId, UserId targetId, string password, CancellationToken cancellationToken);
    Task<ManageUserResult> SetActiveAsync(TenantId tenantId, UserId actorId, UserId targetId, bool active, CancellationToken cancellationToken);
    // Plan step 4.37: the city (null clears; audit user.city_changed) and taking one role away (NotFound when the user does not have it; audit user.role_removed).
    // Own password (plan step 4.46): checks the current one, sets the new one, and ends every session of the user except the one making the change (familyId). Audit user.password_changed_self.
    Task<ManageUserResult> ChangeOwnPasswordAsync(TenantId tenantId, UserId userId, Guid keepFamilyId, string currentPassword, string newPassword, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
    Task<ManageUserResult> SetCityAsync(TenantId tenantId, UserId actorId, UserId targetId, string? city, CancellationToken cancellationToken) => throw new NotSupportedException();
    Task<ManageUserResult> RemoveRoleAsync(TenantId tenantId, UserId actorId, UserId targetId, Guid roleId, CancellationToken cancellationToken) => throw new NotSupportedException();
}

// Changing, locking and unlocking accounts from the panel (plan step 4.10). Same permission as giving roles and adding accounts (user.manageroles or system.admin).
public sealed class ManageUsers(IUserAccountStore store, IPermissionStore permissions)
{
    public async Task<ManageUserResult> ChangeEmailAsync(TenantId tenantId, UserId actorId, UserId targetId, ChangeUserEmailRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!await AllowedAsync(tenantId, actorId, cancellationToken)) return ManageUserResult.Forbidden;
        var email = request.Email?.Trim().ToLowerInvariant();
        if (targetId.IsEmpty || email is null || email.Length > CreateStaffUser.MaxEmailLength || !CreateStaffUser.IsPlainEmail(email)) return ManageUserResult.InvalidData;
        return await store.ChangeEmailAsync(tenantId, actorId, targetId, email, cancellationToken);
    }

    public async Task<ManageUserResult> SetPasswordAsync(TenantId tenantId, UserId actorId, UserId targetId, SetUserPasswordRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!await AllowedAsync(tenantId, actorId, cancellationToken)) return ManageUserResult.Forbidden;
        if (targetId.IsEmpty || request.Password is null || request.Password.Length < CreateStaffUser.MinPasswordLength || request.Password.Length > CreateStaffUser.MaxPasswordLength)
            return ManageUserResult.InvalidData;
        return await store.SetPasswordAsync(tenantId, actorId, targetId, request.Password, cancellationToken);
    }

    // Nobody can lock their own account from the panel: a manager who locked themselves out could not undo it.
    public async Task<ManageUserResult> SetActiveAsync(TenantId tenantId, UserId actorId, UserId targetId, bool active, CancellationToken cancellationToken)
    {
        if (!await AllowedAsync(tenantId, actorId, cancellationToken)) return ManageUserResult.Forbidden;
        if (targetId.IsEmpty) return ManageUserResult.InvalidData;
        if (!active && targetId == actorId) return ManageUserResult.Conflict;
        return await store.SetActiveAsync(tenantId, actorId, targetId, active, cancellationToken);
    }

    public const int MaxCityLength = 80;

    // Anybody may change their own password; the current one must be known. The new one follows the rules of a new account and must differ.
    public async Task<ManageUserResult> ChangeOwnPasswordAsync(TenantId tenantId, UserId userId, Guid sessionFamilyId, ChangeOwnPasswordRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (tenantId.IsEmpty || userId.IsEmpty) return ManageUserResult.Forbidden;
        var current = request.CurrentPassword;
        var next = request.NewPassword;
        if (string.IsNullOrEmpty(current) || next is null || next.Length < CreateStaffUser.MinPasswordLength || next.Length > CreateStaffUser.MaxPasswordLength || next == current)
            return ManageUserResult.InvalidData;
        return await store.ChangeOwnPasswordAsync(tenantId, userId, sessionFamilyId, current, next, cancellationToken);
    }

    public async Task<ManageUserResult> SetCityAsync(TenantId tenantId, UserId actorId, UserId targetId, SetUserCityRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!await AllowedAsync(tenantId, actorId, cancellationToken)) return ManageUserResult.Forbidden;
        var city = string.IsNullOrWhiteSpace(request.City) ? null : request.City.Trim();
        if (targetId.IsEmpty || city?.Length > MaxCityLength) return ManageUserResult.InvalidData;
        return await store.SetCityAsync(tenantId, actorId, targetId, city, cancellationToken);
    }

    // Nobody takes a role away from themselves: a manager who removed the role that lets them manage roles could not undo it.
    public async Task<ManageUserResult> RemoveRoleAsync(TenantId tenantId, UserId actorId, UserId targetId, Guid roleId, CancellationToken cancellationToken)
    {
        if (!await AllowedAsync(tenantId, actorId, cancellationToken)) return ManageUserResult.Forbidden;
        if (targetId.IsEmpty || roleId == Guid.Empty) return ManageUserResult.InvalidData;
        if (targetId == actorId) return ManageUserResult.Conflict;
        return await store.RemoveRoleAsync(tenantId, actorId, targetId, roleId, cancellationToken);
    }

    private async Task<bool> AllowedAsync(TenantId tenantId, UserId actorId, CancellationToken cancellationToken)
    {
        if (tenantId.IsEmpty || actorId.IsEmpty) return false;
        return await permissions.HasPermissionAsync(tenantId, actorId, PermissionKeys.UserManageRoles, cancellationToken) ||
               await permissions.HasPermissionAsync(tenantId, actorId, PermissionKeys.SystemAdmin, cancellationToken);
    }
}
