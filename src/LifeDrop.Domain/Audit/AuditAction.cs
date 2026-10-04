namespace LifeDrop.Domain.Audit;

public static class AuditActions
{
    // Auth
    public const string Login = "auth.login";
    public const string Logout = "auth.logout";
    public const string PasswordChange = "auth.password_change";
    public const string TokenRefresh = "auth.token_refresh";
    public const string TokenRevoked = "auth.token_revoked";

    // Tenant
    public const string TenantCreated = "tenant.created";
    public const string TenantUpdated = "tenant.updated";
    public const string TenantActivated = "tenant.activated";
    public const string TenantDeactivated = "tenant.deactivated";

    // User
    public const string UserCreated = "user.created";
    public const string UserUpdated = "user.updated";
    public const string UserDeleted = "user.deleted";
    public const string UserActivated = "user.activated";
    public const string UserDeactivated = "user.deactivated";
    public const string UserEmailChanged = "user.email_changed";
    public const string UserPasswordChanged = "user.password_changed";
    public const string UserRoleAssigned = "user.role_assigned";
    public const string UserRoleRemoved = "user.role_removed";

    // Role
    public const string RoleCreated = "role.created";
    public const string RoleUpdated = "role.updated";
    public const string RoleDeleted = "role.deleted";
    public const string RolePermissionAdded = "role.permission_added";
    public const string RolePermissionRemoved = "role.permission_removed";

    // Device
    public const string DeviceRegistered = "device.registered";
    public const string DeviceApproved = "device.approved";
    public const string DeviceRevoked = "device.revoked";
    public const string DeviceAssigned = "device.assigned";
    public const string DeviceUnassigned = "device.unassigned";
    public const string DeviceSecretRotated = "device.secret_rotated";
    public const string DeviceDeleted = "device.deleted";

    // Visit (for future)
    public const string VisitCreated = "visit.created";
    public const string VisitStateChanged = "visit.state_changed";
    public const string VisitCheckIn = "visit.checkin";
    public const string VisitCheckOut = "visit.checkout";
    public const string VisitCancelled = "visit.cancelled";
    public const string VisitFailed = "visit.failed";

    // Order (for future)
    public const string OrderCreated = "order.created";
    public const string OrderUpdated = "order.updated";
    public const string OrderDeleted = "order.deleted";

    // Payment (for future)
    public const string PaymentCreated = "payment.created";
    public const string PaymentUpdated = "payment.updated";

    // Backup (for future)
    public const string BackupCreated = "backup.created";
    public const string BackupDeleted = "backup.deleted";
}