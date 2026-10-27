namespace LifeDrop.Application.Roles;

// Catalogue of permission keys (PRD §3, plan step 0.6). Keys are independent of role names;
// tenant, region and ownership rules are enforced separately.
public static class PermissionKeys
{
    public const string CustomersRead = "customers.read";
    // Plan step 4.35: customers of type Doctor are only for roles that have this (not the sales rep).
    // Plan step 4.36: a restriction, not a right: with it the customer writes (create, edit, location) work on customers of type Doctor only. The scientific rep has it.
    public const string CustomersDoctorsOnly = "customers.doctors.only";
    public const string CustomersDoctorsRead = "customers.doctors.read";
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
    // Custom visit-form fields (plan step 3.2, part 2).
    public const string FormsManage = "forms.manage";

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
    // Plan step 5.6: the officer of the wholesalers sees the orders of the wholesalers they work with and marks them dispatched.
    public const string WholesalerWork = "wholesaler.work";
    // Plan step 5.7: marketing messages. use = build a campaign from the customers inside one's scope and work its queue; manage = the message templates.
    public const string MarketingUse = "marketing.use";
    public const string MarketingManage = "marketing.manage";

    // Payments (plan step 2.5): record (finance manager, rep), manage (finance manager: confirm, void, correct), read.
    public const string PaymentsRecord = "payments.record";
    public const string PaymentsManage = "payments.manage";
    public const string PaymentsRead = "payments.read";
    public const string SamplesRecord = "samples.record";
    public const string NotificationsSend = "notifications.send";
    public const string SamplesManage = "samples.manage";
    public const string SamplesRead = "samples.read";
    public const string FollowUpsCreate = "followups.create";
    public const string FollowUpsManage = "followups.manage";
    public const string FollowUpsRead = "followups.read";
    public const string AlertsRead = "alerts.read";
    public const string AlertsSecurityRead = "alerts.security.read";
    public const string AlertsHandle = "alerts.handle";
    public const string ReportsRead = "reports.read";

    // Sales targets per visitor, product and month (plan step 5.5): set by managers inside their area, read for the report.
    public const string TargetsManage = "targets.manage";
    public const string TargetsRead = "targets.read";

    // Live map (plan step 1.19, decision D3): exact positions, or status without coordinates.
    public const string TrackingRead = "tracking.read";
    public const string TrackingSummary = "tracking.summary";
}
