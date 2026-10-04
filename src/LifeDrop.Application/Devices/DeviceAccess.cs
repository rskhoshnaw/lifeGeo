using LifeDrop.Domain.Common;

namespace LifeDrop.Application.Devices;

public sealed record RegisterDeviceRequest(string TenantCode, string Email, string Password,
    string DeviceLabel, string Platform, string InstallId, string AppVersion);
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
}
