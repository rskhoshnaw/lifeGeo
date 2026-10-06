using System.Text.RegularExpressions;
using LifeDrop.Application.Roles;
using LifeDrop.Domain.Common;

namespace LifeDrop.Application.Platform;

// Tenant management and explicit, audited cross-tenant access for the Super Admin (plan step 3.1). Only users of the platform company
// with the permission may call these; every action writes a row in platform_audit_logs, a cross-company read in the same transaction.
public static class PlatformTenant
{
    public static readonly TenantId Id = new(Guid.Parse("00000000-0000-0000-0000-00000000f001"));
    public const string Code = "platform";
}

public sealed record TenantSummaryDto(Guid Id, string Code, string Name, bool IsActive, int Users, int ActiveUsers);
public sealed record CreateTenantRequest(string? Code, string? Name, string? ManagerEmail, string? ManagerPassword);
public sealed record CreatedTenantDto(Guid Id, string Code, Guid ManagerId);
public sealed record UpdateTenantRequest(string? Name, bool? IsActive, string? Reason);
public sealed record TenantAuditEntryDto(Guid Id, Guid ActorId, string? ActorEmail, string Action, string EntityId, DateTimeOffset OccurredAt);
public sealed record PlatformAuditEntryDto(Guid Id, Guid ActorId, string? ActorEmail, string Action, Guid? TargetTenantId, string? TargetTenantCode, string? Reason, string? Detail,
    DateTimeOffset OccurredAt);

public enum PlatformResult { Success, Forbidden, InvalidData, NotFound, Conflict }

public interface IPlatformStore
{
    Task<IReadOnlyList<TenantSummaryDto>> ListTenantsAsync(CancellationToken cancellationToken);
    // Creates the company, its default roles and its first CompanyManager; false when the code or the email is taken.
    Task<CreatedTenantDto?> CreateTenantAsync(UserId actor, string code, string name, string managerEmail, string managerPassword, CancellationToken cancellationToken);
    // Null when the company does not exist.
    Task<bool> UpdateTenantAsync(UserId actor, Guid tenantId, string? name, bool? isActive, string reason, CancellationToken cancellationToken);
    // Reads the company's audit log and writes the platform audit row in the same transaction; null when the company does not exist.
    Task<IReadOnlyList<TenantAuditEntryDto>?> ReadTenantAuditAsync(UserId actor, Guid tenantId, string reason, string? action, int limit, CancellationToken cancellationToken);
    Task<IReadOnlyList<PlatformAuditEntryDto>> ListPlatformAuditAsync(Guid? tenantId, int limit, CancellationToken cancellationToken);
}

public sealed partial class PlatformService(IPlatformStore store, IPermissionStore permissions)
{
    public const string ManagePermission = "platform.tenants.manage";
    public const string AccessPermission = "platform.tenants.access";
    public const int DefaultLimit = 50;
    public const int MaxLimit = 200;
    public const int MinPasswordLength = 12;

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{1,31}$")]
    private static partial Regex CodePattern();

    public async Task<(PlatformResult Result, IReadOnlyList<TenantSummaryDto> Items)> ListTenantsAsync(TenantId tenantId, UserId actorId, CancellationToken cancellationToken) =>
        await AllowedAsync(tenantId, actorId, ManagePermission, cancellationToken) ? (PlatformResult.Success, await store.ListTenantsAsync(cancellationToken)) : (PlatformResult.Forbidden, []);

    public async Task<(PlatformResult Result, CreatedTenantDto? Created)> CreateTenantAsync(TenantId tenantId, UserId actorId, CreateTenantRequest request, CancellationToken cancellationToken)
    {
        if (!await AllowedAsync(tenantId, actorId, ManagePermission, cancellationToken)) return (PlatformResult.Forbidden, null);
        var code = request.Code?.Trim().ToLowerInvariant();
        var name = request.Name?.Trim();
        var email = request.ManagerEmail?.Trim().ToLowerInvariant();
        if (code is null || !CodePattern().IsMatch(code) || code == PlatformTenant.Code || string.IsNullOrEmpty(name) || name.Length > 100 ||
            email is null || email.Length > 200 || !email.Contains('@') || email.StartsWith('@') || email.EndsWith('@') ||
            request.ManagerPassword is null || request.ManagerPassword.Length < MinPasswordLength || request.ManagerPassword.Length > 200)
            return (PlatformResult.InvalidData, null);
        var created = await store.CreateTenantAsync(actorId, code, name, email, request.ManagerPassword, cancellationToken);
        return created is null ? (PlatformResult.Conflict, null) : (PlatformResult.Success, created);
    }

    public async Task<PlatformResult> UpdateTenantAsync(TenantId tenantId, UserId actorId, Guid targetId, UpdateTenantRequest request, CancellationToken cancellationToken)
    {
        if (!await AllowedAsync(tenantId, actorId, ManagePermission, cancellationToken)) return PlatformResult.Forbidden;
        var name = request.Name?.Trim();
        var reason = request.Reason?.Trim();
        if (targetId == Guid.Empty || (name is null && request.IsActive is null) || (name is not null && (name.Length == 0 || name.Length > 100)) ||
            reason is null || reason.Length is < 5 or > 500)
            return PlatformResult.InvalidData;
        if (targetId == PlatformTenant.Id.Value && request.IsActive == false) return PlatformResult.Conflict;   // the platform company never goes off
        return await store.UpdateTenantAsync(actorId, targetId, name, request.IsActive, reason, cancellationToken) ? PlatformResult.Success : PlatformResult.NotFound;
    }

    // The explicit cross-company read: a reason is mandatory and is written to the platform audit before anything is returned.
    public async Task<(PlatformResult Result, IReadOnlyList<TenantAuditEntryDto> Items)> ReadTenantAuditAsync(TenantId tenantId, UserId actorId, Guid targetId, string? reason,
        string? action, int? limit, CancellationToken cancellationToken)
    {
        if (!await AllowedAsync(tenantId, actorId, AccessPermission, cancellationToken)) return (PlatformResult.Forbidden, []);
        reason = reason?.Trim();
        if (targetId == Guid.Empty || reason is null || reason.Length is < 10 or > 500 || limit is < 1 or > MaxLimit) return (PlatformResult.InvalidData, []);
        var items = await store.ReadTenantAuditAsync(actorId, targetId, reason, string.IsNullOrWhiteSpace(action) ? null : action.Trim(), limit ?? DefaultLimit, cancellationToken);
        return items is null ? (PlatformResult.NotFound, []) : (PlatformResult.Success, items);
    }

    public async Task<(PlatformResult Result, IReadOnlyList<PlatformAuditEntryDto> Items)> ListPlatformAuditAsync(TenantId tenantId, UserId actorId, Guid? targetId, int? limit,
        CancellationToken cancellationToken)
    {
        if (!await AllowedAsync(tenantId, actorId, ManagePermission, cancellationToken)) return (PlatformResult.Forbidden, []);
        if (limit is < 1 or > MaxLimit) return (PlatformResult.InvalidData, []);
        return (PlatformResult.Success, await store.ListPlatformAuditAsync(targetId, limit ?? DefaultLimit, cancellationToken));
    }

    // The platform company and the permission: a role with the same permission key in an ordinary company gives nothing.
    private async Task<bool> AllowedAsync(TenantId tenantId, UserId actorId, string permission, CancellationToken cancellationToken) =>
        !tenantId.IsEmpty && !actorId.IsEmpty && tenantId == PlatformTenant.Id && await permissions.HasPermissionAsync(tenantId, actorId, permission, cancellationToken);
}
