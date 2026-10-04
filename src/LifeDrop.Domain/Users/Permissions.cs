namespace LifeDrop.Domain.Users;

public static class Permissions
{
    // Tenant
    public const string Tenant_View = "tenant.view";
    public const string Tenant_Manage = "tenant.manage";

    // Users
    public const string User_View = "user.view";
    public const string User_Create = "user.create";
    public const string User_Update = "user.update";
    public const string User_Delete = "user.delete";
    public const string User_ManageRoles = "user.manageroles";

    // Roles
    public const string Role_View = "role.view";
    public const string Role_Create = "role.create";
    public const string Role_Update = "role.update";
    public const string Role_Delete = "role.delete";
    public const string Role_ManagePermissions = "role.managepermissions";

    // Devices
    public const string Device_View = "device.view";
    public const string Device_Register = "device.register";
    public const string Device_Approve = "device.approve";
    public const string Device_Revoke = "device.revoke";
    public const string Device_Delete = "device.delete";

    // Customers
    public const string Customer_Read = "customers.read";
    public const string Customer_Write = "customers.write";

    // Visits (for future use)
    public const string Visit_View = "visit.view";
    public const string Visit_Create = "visit.create";
    public const string Visit_Update = "visit.update";
    public const string Visit_Delete = "visit.delete";
    public const string Visit_CheckIn = "visit.checkin";
    public const string Visit_CheckOut = "visit.checkout";
    public const string Visit_ManageState = "visit.managestate";

    // Orders (for future use)
    public const string Order_View = "order.view";
    public const string Order_Create = "order.create";
    public const string Order_Update = "order.update";
    public const string Order_Delete = "order.delete";

    // Audit
    public const string Audit_View = "audit.view";

    // System
    public const string System_Admin = "system.admin";
}