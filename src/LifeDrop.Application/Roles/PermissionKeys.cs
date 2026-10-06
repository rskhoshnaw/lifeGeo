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
    // Plan step 1.23: a field user reorders and postpones visits of their own plan (managers use visits.write).
    public const string VisitsPlan = "visits.plan";
    public const string CustomersApprove = "customers.approve";
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

    // Reference list of cities, neighbourhoods and streets (plan step 1.23-b): add, rename, approve, merge, import.
    public const string PlacesManage = "places.manage";

    // Products and prices (plan step 2.1): read for sales, supervisors and finance; manage for the sales manager.
    public const string CatalogRead = "catalog.read";
    public const string CatalogManage = "catalog.manage";

    // Orders and warehouses (plan step 2.2).
    public const string OrdersCreate = "orders.create";
    public const string OrdersRead = "orders.read";
    public const string OrdersCancel = "orders.cancel";
    public const string WarehousesManage = "warehouses.manage";
    // Plan step 2.3: prepare orders and hand them to a driver, in the warehouses the user works in.
    public const string WarehouseWork = "warehouse.work";
    // Plan step 2.4: a driver picks up, delivers or fails the orders assigned to them.
    public const string DeliveryWork = "delivery.work";

    // Payments (plan step 2.5): record (finance manager, rep), manage (finance manager: confirm, void, correct), read.
    public const string PaymentsRecord = "payments.record";
    public const string PaymentsManage = "payments.manage";
    public const string PaymentsRead = "payments.read";
    public const string SamplesRecord = "samples.record";
    public const string SamplesManage = "samples.manage";
    public const string SamplesRead = "samples.read";
    public const string FollowUpsCreate = "followups.create";
    public const string FollowUpsManage = "followups.manage";
    public const string FollowUpsRead = "followups.read";
    public const string AlertsRead = "alerts.read";
    public const string AlertsSecurityRead = "alerts.security.read";
    public const string AlertsHandle = "alerts.handle";

    // Live map (plan step 1.19, decision D3): exact positions, or status without coordinates.
    public const string TrackingRead = "tracking.read";
    public const string TrackingSummary = "tracking.summary";
}
