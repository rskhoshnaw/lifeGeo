using LifeDrop.Application.Roles;
using LifeDrop.Domain.Common;

namespace LifeDrop.Application.Users;

// Read models for the web panel (plan step 1.17): the signed-in user, users, roles and devices. Every change
// (approve/block device, kill sessions, assign role) stays on its own route with its own server-side check;
// the panel uses Permissions only to decide which buttons to show.
public sealed record CurrentUserDto(Guid Id, string? Email, IReadOnlyList<string> Roles, IReadOnlyList<string> Permissions);

public sealed record UserSummaryDto(Guid Id, string? Email, IReadOnlyList<string> Roles, Guid? TeamId, string? TeamName,
    Guid? RegionId, int ActiveSessions);

public sealed record RoleDto(Guid Id, string Name, string DataScope);

// Never includes the device secret.
public sealed record DeviceSummaryDto(Guid Id, Guid UserId, string? UserEmail, string Label, string Platform, string AppVersion,
    string Status, DateTimeOffset RegisteredAt);

public enum DirectoryResult { Success, Forbidden, InvalidData }

public interface IUserDirectoryStore
{
    Task<CurrentUserDto?> GetCurrentAsync(TenantId tenantId, UserId userId, CancellationToken cancellationToken);
    // Active users; Region scope = members of teams in scope.RegionId, Own = only the actor.
    Task<IReadOnlyList<UserSummaryDto>> ListUsersAsync(TenantId tenantId, AccessScope scope, CancellationToken cancellationToken);
    Task<IReadOnlyList<RoleDto>> ListRolesAsync(TenantId tenantId, CancellationToken cancellationToken);
    Task<IReadOnlyList<DeviceSummaryDto>> ListDevicesAsync(TenantId tenantId, string? status, CancellationToken cancellationToken);
}

public sealed class UserDirectory(IUserDirectoryStore store, IPermissionStore permissions)
{
    // Same permission sets as the routes that change these records; visits.write lists the users a planner may
    // assign visits to (plan step 1.18), within its own scope.
    private static readonly string[] UserListPermissions = [PermissionKeys.SessionsKill, PermissionKeys.UserManageRoles, PermissionKeys.SystemAdmin, PermissionKeys.VisitsWrite];
    private static readonly string[] RolePermissions = [PermissionKeys.UserManageRoles, PermissionKeys.SystemAdmin];
    private static readonly string[] DevicePermissions = [PermissionKeys.DeviceApprove, PermissionKeys.DevicesManage, PermissionKeys.SystemAdmin];
    private static readonly string[] DeviceStatuses = ["pending", "approved", "blocked"];

    public Task<CurrentUserDto?> GetCurrentAsync(TenantId tenantId, UserId actorId, CancellationToken cancellationToken) =>
        tenantId.IsEmpty || actorId.IsEmpty ? Task.FromResult<CurrentUserDto?>(null) : store.GetCurrentAsync(tenantId, actorId, cancellationToken);

    public async Task<(DirectoryResult Result, IReadOnlyList<UserSummaryDto> Items)> ListUsersAsync(TenantId tenantId, UserId actorId, CancellationToken cancellationToken)
    {
        if (tenantId.IsEmpty || actorId.IsEmpty) return (DirectoryResult.Forbidden, []);
        AccessScope? widest = null;
        foreach (var key in UserListPermissions)
        {
            var scope = await permissions.GetAccessScopeAsync(tenantId, actorId, key, cancellationToken);
            if (scope is not null && (widest is null || scope.Scope > widest.Scope)) widest = scope;
        }
        if (widest is null) return (DirectoryResult.Forbidden, []);
        return (DirectoryResult.Success, await store.ListUsersAsync(tenantId, widest, cancellationToken));
    }

    public async Task<(DirectoryResult Result, IReadOnlyList<RoleDto> Items)> ListRolesAsync(TenantId tenantId, UserId actorId, CancellationToken cancellationToken)
    {
        if (!await HasAnyAsync(tenantId, actorId, RolePermissions, cancellationToken)) return (DirectoryResult.Forbidden, []);
        return (DirectoryResult.Success, await store.ListRolesAsync(tenantId, cancellationToken));
    }

    public async Task<(DirectoryResult Result, IReadOnlyList<DeviceSummaryDto> Items)> ListDevicesAsync(TenantId tenantId, UserId actorId, string? status, CancellationToken cancellationToken)
    {
        if (!await HasAnyAsync(tenantId, actorId, DevicePermissions, cancellationToken)) return (DirectoryResult.Forbidden, []);
        if (status is not null && !DeviceStatuses.Contains(status)) return (DirectoryResult.InvalidData, []);
        return (DirectoryResult.Success, await store.ListDevicesAsync(tenantId, status, cancellationToken));
    }

    private async Task<bool> HasAnyAsync(TenantId tenantId, UserId actorId, string[] keys, CancellationToken cancellationToken)
    {
        if (tenantId.IsEmpty || actorId.IsEmpty) return false;
        foreach (var key in keys)
            if (await permissions.HasPermissionAsync(tenantId, actorId, key, cancellationToken)) return true;
        return false;
    }
}
