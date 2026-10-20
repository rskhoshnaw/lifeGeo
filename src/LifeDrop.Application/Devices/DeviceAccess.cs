using LifeDrop.Domain.Common;

namespace LifeDrop.Application.Devices;

public sealed record RegisterDeviceRequest(string TenantCode, string Email, string Password,
    string DeviceLabel, string Platform, string InstallId, string AppVersion,
    // Plan step 4.39: sent by newer app builds; optional.
    string? Model = null, string? OsVersion = null);
public sealed record SetDeviceLabelRequest(string? Label);
public sealed record RegisteredDevice(Guid DeviceId, string DeviceSecret);

public enum DeviceChangeResult { Changed, NotFound, Forbidden, Conflict }

public interface IDeviceAccessStore
{
    Task<RegisteredDevice?> RegisterAsync(TenantId tenantId, UserId userId,
        RegisterDeviceRequest request, CancellationToken cancellationToken);
    Task<bool> IsApprovedAsync(TenantId tenantId, UserId userId, Guid deviceId,
        string deviceSecret, CancellationToken cancellationToken);
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
