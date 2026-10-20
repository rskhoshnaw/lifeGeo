using LifeDrop.Domain.Common;

namespace LifeDrop.Application.Roles;

// Ordered from narrowest to widest; a user's effective scope is the widest among roles granting the permission.
public enum DataScope { Own = 0, Region = 1, Tenant = 2 }

// What an actor may reach with one permission (plan step 0.6b). RegionId is the region of the actor's team, if any.
// HideDoctors (plan step 4.35): the user lacks customers.doctors.read (the sales rep), so customers of type Doctor are not theirs to see.
// DoctorsOnly (plan step 4.36): for the customer write permissions, the user has the restriction customers.doctors.only (the scientific rep), so only customers of type Doctor can be changed.
public sealed record AccessScope(DataScope Scope, UserId ActorId, Guid? RegionId, bool HideDoctors = false, bool DoctorsOnly = false)
{
    // " AND lower(<column>) <> 'doctor'" for a customer type column when doctors are hidden; nothing otherwise.
    // or " AND lower(<column>) = 'doctor'" when only doctors may be touched.
    public string DoctorFilter(string typeColumn) => HideDoctors ? $" AND lower({typeColumn}) <> 'doctor'" : DoctorsOnly ? $" AND lower({typeColumn}) = 'doctor'" : "";

    public static AccessScope WholeTenant(UserId actorId) => new(DataScope.Tenant, actorId, null);

    // True when this scope reaches the user (userRegion = region of the user's team).
    public bool Covers(UserId userId, Guid? userRegion) => Scope switch
    {
        DataScope.Tenant => true,
        DataScope.Region => userId == ActorId || (RegionId is not null && userRegion == RegionId),
        _ => userId == ActorId
    };
}

public interface IPermissionStore
{
    Task<bool> HasPermissionAsync(TenantId tenantId, UserId userId, string permissionKey, CancellationToken cancellationToken);

    // Null when the user lacks the permission.
    async Task<AccessScope?> GetAccessScopeAsync(TenantId tenantId, UserId userId, string permissionKey, CancellationToken cancellationToken) =>
        await HasPermissionAsync(tenantId, userId, permissionKey, cancellationToken) ? AccessScope.WholeTenant(userId) : null;

    // Exists is false when the user is not an active user of the tenant.
    Task<(bool Exists, Guid? RegionId)> GetUserRegionAsync(TenantId tenantId, UserId userId, CancellationToken cancellationToken) =>
        Task.FromResult<(bool, Guid?)>((true, null));
}
