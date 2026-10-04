namespace LifeDrop.Application.Roles;

// Catalogue of permission keys (PRD §3, plan step 0.6). Keys are independent of role names;
// tenant, region and ownership rules are enforced separately.
public static class PermissionKeys
{
    public const string CustomersRead = "customers.read";
    public const string CustomersWrite = "customers.write";
    public const string CustomersLocationWrite = "customers.location.write";
    public const string CustomersGeofenceWrite = "customers.geofence.write";
    public const string VisitsWrite = "visits.write";
    public const string VisitsFail = "visits.fail";
    public const string VisitsCreateUnplanned = "visits.create_unplanned";
    public const string VisitsCancel = "visits.cancel";
    public const string DeviceApprove = "device.approve";
    public const string DevicesManage = "devices.manage";
    public const string UserManageRoles = "user.manageroles";
    public const string SystemAdmin = "system.admin";
    public const string SessionsKill = "sessions.kill";
    public const string AuditRead = "audit.read";

    // Regions and teams (decision D11): granted to the sales manager role.
    public const string RegionsRead = "regions.read";
    public const string RegionsManage = "regions.manage";
    public const string TeamsRead = "teams.read";
    public const string TeamsManage = "teams.manage";
}
