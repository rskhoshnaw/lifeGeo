using System.Reflection;
using LifeDrop.Domain.Common;

namespace LifeDrop.Application.Roles;

// Roles the panel can build itself (plan step 4.38): a name, how far the data reaches (tenant | region | own) and a list of permissions. The default roles (the ones every
// company starts with) keep their name and cannot be deleted, but their permissions and reach can be changed. A custom role can be deleted only when nobody has it.
// The change that would leave nobody able to manage roles is refused.
public sealed record SaveRoleRequest(string? Name, string? DataScope, bool? RequiresDeviceBinding, IReadOnlyList<string>? Permissions);

public sealed record PermissionDto(string Key, string Group);

public enum RoleResult { Success, Forbidden, InvalidData, NotFound, Conflict }

public interface IRoleDefinitionStore
{
    // Conflict: the name is taken. Audit role.created with the role id.
    Task<(RoleResult Result, Guid? Id)> CreateAsync(TenantId tenantId, UserId actorId, SaveRoleRequest role, CancellationToken cancellationToken);
    // NotFound: no such role in the company. InvalidData: a default role renamed. Conflict: the name is taken, or nobody would be left to manage roles. Audit role.updated.
    Task<RoleResult> UpdateAsync(TenantId tenantId, UserId actorId, Guid roleId, SaveRoleRequest role, CancellationToken cancellationToken);
    // NotFound; Conflict: a default role, a role somebody has, or the last one that manages roles. Audit role.deleted.
    Task<RoleResult> DeleteAsync(TenantId tenantId, UserId actorId, Guid roleId, CancellationToken cancellationToken);
}

public static class RoleDefaults
{
    public static readonly IReadOnlyList<string> Names = ["CompanyManager", "SalesManager", "Supervisor", "FinanceManager", "SalesRep", "ScientificRep", "WarehouseManager", "Driver", "WholesalerOfficer"];
    public static readonly IReadOnlyList<string> Scopes = ["tenant", "region", "own"];
}

public sealed class RoleManagement(IRoleDefinitionStore store, IPermissionStore permissions)
{
    public const int MaxNameLength = 60;

    // Every key of PermissionKeys except system.admin (the platform's own right is never given from the panel).
    public static readonly IReadOnlyList<PermissionDto> Catalogue = typeof(PermissionKeys).GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f.IsLiteral && f.FieldType == typeof(string)).Select(f => (string)f.GetRawConstantValue()!)
        .Where(k => k != PermissionKeys.SystemAdmin).Distinct().OrderBy(k => k, StringComparer.Ordinal)
        .Select(k => new PermissionDto(k, k[..k.IndexOf('.')])).ToList();

    public async Task<(RoleResult Result, IReadOnlyList<PermissionDto> Items)> ListPermissionsAsync(TenantId tenantId, UserId actorId, CancellationToken cancellationToken) =>
        await AllowedAsync(tenantId, actorId, cancellationToken) ? (RoleResult.Success, Catalogue) : (RoleResult.Forbidden, []);

    public async Task<(RoleResult Result, Guid? Id)> CreateAsync(TenantId tenantId, UserId actorId, SaveRoleRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!await AllowedAsync(tenantId, actorId, cancellationToken)) return (RoleResult.Forbidden, null);
        var clean = Clean(request);
        return clean is null ? (RoleResult.InvalidData, null) : await store.CreateAsync(tenantId, actorId, clean, cancellationToken);
    }

    public async Task<RoleResult> UpdateAsync(TenantId tenantId, UserId actorId, Guid roleId, SaveRoleRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!await AllowedAsync(tenantId, actorId, cancellationToken)) return RoleResult.Forbidden;
        var clean = Clean(request);
        return roleId == Guid.Empty || clean is null ? RoleResult.InvalidData : await store.UpdateAsync(tenantId, actorId, roleId, clean, cancellationToken);
    }

    public async Task<RoleResult> DeleteAsync(TenantId tenantId, UserId actorId, Guid roleId, CancellationToken cancellationToken)
    {
        if (!await AllowedAsync(tenantId, actorId, cancellationToken)) return RoleResult.Forbidden;
        return roleId == Guid.Empty ? RoleResult.InvalidData : await store.DeleteAsync(tenantId, actorId, roleId, cancellationToken);
    }

    // The name trimmed, the scope one of the three, and only known permission keys (no duplicates).
    private static SaveRoleRequest? Clean(SaveRoleRequest request)
    {
        var name = request.Name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > MaxNameLength || request.DataScope is null || !RoleDefaults.Scopes.Contains(request.DataScope) || request.Permissions is null)
            return null;
        var keys = request.Permissions.Select(k => k?.Trim()).ToList();
        if (keys.Any(k => k is null || !Catalogue.Any(p => p.Key == k))) return null;
        return new SaveRoleRequest(name, request.DataScope, request.RequiresDeviceBinding ?? false, keys.Distinct().OrderBy(k => k, StringComparer.Ordinal).ToList()!);
    }

    private async Task<bool> AllowedAsync(TenantId tenantId, UserId actorId, CancellationToken cancellationToken) =>
        !tenantId.IsEmpty && !actorId.IsEmpty &&
        (await permissions.HasPermissionAsync(tenantId, actorId, PermissionKeys.UserManageRoles, cancellationToken) ||
         await permissions.HasPermissionAsync(tenantId, actorId, PermissionKeys.SystemAdmin, cancellationToken));
}
