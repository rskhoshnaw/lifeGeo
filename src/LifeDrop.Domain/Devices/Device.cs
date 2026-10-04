namespace LifeDrop.Domain.Devices;

using LifeDrop.Domain.Common;
using LifeDrop.Domain.Tenants;
using LifeDrop.Domain.Users;

public class Device : AggregateRoot<DeviceId>
{
    public TenantId TenantId { get; private set; }
    public UserId? UserId { get; private set; }
    public string DeviceName { get; private set; }
    public string DeviceModel { get; private set; }
    public string OsVersion { get; private set; }
    public string AppVersion { get; private set; }
    public string DeviceToken { get; private set; }
    public string DeviceSecretHash { get; private set; }
    public DeviceStatus Status { get; private set; }
    public DateTime RegisteredAt { get; private set; }
    public DateTime? ApprovedAt { get; private set; }
    public UserId? ApprovedBy { get; private set; }
    public DateTime? LastSeenAt { get; private set; }
    public DateTime? RevokedAt { get; private set; }
    public UserId? RevokedBy { get; private set; }
    public string RevocationReason { get; private set; } = string.Empty;

    private Device(DeviceId id, TenantId tenantId, string deviceName, string deviceModel, string osVersion, string appVersion, string deviceToken, string deviceSecretHash) : base(id)
    {
        TenantId = tenantId;
        DeviceName = deviceName;
        DeviceModel = deviceModel;
        OsVersion = osVersion;
        AppVersion = appVersion;
        DeviceToken = deviceToken;
        DeviceSecretHash = deviceSecretHash;
        Status = DeviceStatus.PendingApproval;
        RegisteredAt = DateTime.UtcNow;
    }

    public static Device Register(TenantId tenantId, string deviceName, string deviceModel, string osVersion, string appVersion, string deviceToken, string deviceSecretHash)
    {
        if (tenantId.IsEmpty)
            throw new ArgumentException("TenantId is required", nameof(tenantId));
        if (string.IsNullOrWhiteSpace(deviceName))
            throw new ArgumentException("Device name is required", nameof(deviceName));
        if (string.IsNullOrWhiteSpace(deviceToken))
            throw new ArgumentException("Device token is required", nameof(deviceToken));
        if (string.IsNullOrWhiteSpace(deviceSecretHash))
            throw new ArgumentException("Device secret hash is required", nameof(deviceSecretHash));

        return new Device(DeviceId.New(), tenantId, deviceName.Trim(), deviceModel?.Trim() ?? string.Empty, osVersion?.Trim() ?? string.Empty, appVersion?.Trim() ?? string.Empty, deviceToken.Trim(), deviceSecretHash);
    }

    /// <summary>
    /// Assigns a device to a user with tenant validation. The user must belong to the same tenant as the device.
    /// </summary>
    /// <param name="userId">The user to assign the device to</param>
    /// <param name="approvedBy">The user approving the assignment</param>
    /// <param name="userTenantId">The tenant ID of the user (for cross-tenant validation)</param>
    /// <exception cref="ArgumentException">Thrown when user belongs to a different tenant</exception>
    public void AssignToUser(UserId userId, UserId approvedBy, TenantId userTenantId)
    {
        if (Status != DeviceStatus.PendingApproval && Status != DeviceStatus.Approved)
            throw new InvalidOperationException("Device must be in pending or approved status to assign");
        if (userId.IsEmpty)
            throw new ArgumentException("UserId is required", nameof(userId));
        if (approvedBy.IsEmpty)
            throw new ArgumentException("ApprovedBy is required", nameof(approvedBy));
        if (userTenantId != TenantId)
            throw new ArgumentException("Cannot assign device to user from a different tenant", nameof(userTenantId));

        UserId = userId;
        Status = DeviceStatus.Approved;
        ApprovedAt = DateTime.UtcNow;
        ApprovedBy = approvedBy;
    }

    public void Approve(UserId approvedBy)
    {
        if (Status != DeviceStatus.PendingApproval)
            throw new InvalidOperationException("Only pending devices can be approved");
        if (approvedBy.IsEmpty)
            throw new ArgumentException("ApprovedBy is required", nameof(approvedBy));

        Status = DeviceStatus.Approved;
        ApprovedAt = DateTime.UtcNow;
        ApprovedBy = approvedBy;
    }

    public void Revoke(UserId revokedBy, string reason)
    {
        if (Status == DeviceStatus.Revoked)
            throw new InvalidOperationException("Device is already revoked");
        if (revokedBy.IsEmpty)
            throw new ArgumentException("RevokedBy is required", nameof(revokedBy));
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("Revocation reason is required", nameof(reason));

        Status = DeviceStatus.Revoked;
        RevokedAt = DateTime.UtcNow;
        RevokedBy = revokedBy;
        RevocationReason = reason.Trim();
        UserId = null;
    }

    public void UpdateLastSeen()
    {
        LastSeenAt = DateTime.UtcNow;
    }

    public void UpdateInfo(string deviceName, string deviceModel, string osVersion, string appVersion)
    {
        DeviceName = deviceName?.Trim() ?? DeviceName;
        DeviceModel = deviceModel?.Trim() ?? DeviceModel;
        OsVersion = osVersion?.Trim() ?? OsVersion;
        AppVersion = appVersion?.Trim() ?? AppVersion;
    }

    public void RotateSecret(string newDeviceSecretHash)
    {
        if (string.IsNullOrWhiteSpace(newDeviceSecretHash))
            throw new ArgumentException("Device secret hash is required", nameof(newDeviceSecretHash));
        DeviceSecretHash = newDeviceSecretHash;
    }

    public bool IsActive => Status == DeviceStatus.Approved && UserId.HasValue;
}
