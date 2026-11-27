using LifeDrop.Domain.Common;

namespace LifeDrop.Application.Devices;

public sealed record RegisterDeviceRequest(string TenantCode, string Email, string Password,
    string DeviceLabel, string Platform, string InstallId, string AppVersion,
    // Plan step 4.39: sent by newer app builds; optional.
    string? Model = null, string? OsVersion = null);
public sealed record SetDeviceLabelRequest(string? Label);
public sealed record RegisteredDevice(Guid DeviceId, string DeviceSecret);

public enum DeviceChangeResult { Changed, NotFound, Forbidden, Conflict }

// Plan step 5.62: why a registration was refused with a conflict: the phone is already registered (and in what state), or the account / company is not active.
public enum RegistrationBlock { Unknown, Pending, Approved, Blocked, AccountInactive }

public interface IDeviceAccessStore
{
    Task<RegisteredDevice?> RegisterAsync(TenantId tenantId, UserId userId,
        RegisterDeviceRequest request, CancellationToken cancellationToken);
    // Called when RegisterAsync refused: tells the phone the real reason instead of a bare conflict.
    Task<RegistrationBlock> WhyNotRegisteredAsync(TenantId tenantId, UserId userId, string installId, CancellationToken cancellationToken) => Task.FromResult(RegistrationBlock.Unknown);
    Task<bool> IsApprovedAsync(TenantId tenantId, UserId userId, Guid deviceId,
        string deviceSecret, CancellationToken cancellationToken);
    // True while the device exists for this user and is not deleted (any status). A phone holding an id for which this is false has stale local credentials.
    Task<bool> ExistsAsync(TenantId tenantId, UserId userId, Guid deviceId, CancellationToken cancellationToken) => Task.FromResult(true);
    Task RecordDeniedAsync(TenantId tenantId, UserId userId,
        CancellationToken cancellationToken);
    Task<DeviceChangeResult> ApproveAsync(TenantId tenantId, UserId actorId, Guid deviceId,
        CancellationToken cancellationToken);
    Task<DeviceChangeResult> BlockAsync(TenantId tenantId, UserId actorId, Guid deviceId,
        CancellationToken cancellationToken);
    // Plan step 4.39: the manager's label (null clears; audit device.label_changed) and the delete (blocks, ends its sessions, hides it, frees its install id; audit device.deleted).
    // Forbidden without device.approve / devices.manage / system.admin; NotFound for a device of another company or one already deleted.
    Task<DeviceChangeResult> SetLabelAsync(TenantId tenantId, UserId actorId, Guid deviceId, string? label, CancellationToken cancellationToken) => throw new NotSupportedException();
    Task<DeviceChangeResult> DeleteAsync(TenantId tenantId, UserId actorId, Guid deviceId, CancellationToken cancellationToken) => throw new NotSupportedException();
}
