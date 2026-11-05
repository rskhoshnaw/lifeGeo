using LifeDrop.Application.Roles;
using LifeDrop.Domain.Common;

namespace LifeDrop.Application.Tracking;

// Basic live map (plan step 1.19, PRD §6.2, decision D3): users with an open shift, the time of their last point
// and, with tracking.read, that point. tracking.summary shows status only and never coordinates. A point older
// than LiveWindow is returned with IsLive = false so the panel never shows it as live.
// The state of the phone as it last reported (plan step 5.17); no position in it, so it is also shown to users who only have tracking.summary.
public sealed record LiveMapHealthDto(string? AppVersion, string? Platform, string? OsVersion, string? DeviceModel, string? LocationPermission,
    bool? PreciseLocation, bool? LocationServices, bool? Notifications, bool? BatteryExempt, string? BackgroundRefresh, int? BatteryPercent, bool? LowPower);

// LastUpdateAt is the time of the last position; LastSeenAt the last time the phone reported at all (it reports every minute while the app runs, also without a new position).
// A user is live while either is recent.
public sealed record LiveMapEntryDto(Guid UserId, string? Email, string? TeamName, DateTimeOffset ShiftStartedAt,
    DateTimeOffset? LastUpdateAt, bool IsLive, bool Precise, double? Latitude, double? Longitude, double? AccuracyMeters,
    bool? IsMocked, string? LastCustomerName, DateTimeOffset? LastVisitAt, string? DeviceLabel = null,
    DateTimeOffset? LastSeenAt = null, LiveMapHealthDto? Health = null,
    // Plan step 5.22: the mobile number of the user, only for readers of exact positions (tracking.read).
    string? Phone = null);

public sealed record LiveMapRow(Guid UserId, string? Email, string? TeamName, Guid? RegionId, DateTimeOffset ShiftStartedAt,
    DateTimeOffset? LastPointAt, double? Latitude, double? Longitude, double? AccuracyMeters, bool? IsMocked,
    string? LastCustomerName, DateTimeOffset? LastVisitAt, string? DeviceLabel = null,
    DateTimeOffset? SeenAt = null, LiveMapHealthDto? Health = null, string? Phone = null);

public interface ILiveMapStore
{
    // Open shifts of active users inside the scope, with the last point of the shift and the last customer the
    // user checked in at since the shift started.
    Task<IReadOnlyList<LiveMapRow>> ListOpenShiftsAsync(TenantId tenantId, AccessScope scope, CancellationToken cancellationToken);
}

public sealed class LiveMap(ILiveMapStore store, IPermissionStore permissions, TimeProvider clock)
{
    // The phone reports about once a minute (3 minutes before step 5.12), so a user is "live" while the last position or report is at most 10 minutes old.
    public static readonly TimeSpan LiveWindow = TimeSpan.FromMinutes(10);

    public async Task<IReadOnlyList<LiveMapEntryDto>?> ListAsync(TenantId tenantId, UserId actorId, CancellationToken cancellationToken)
    {
        if (tenantId.IsEmpty || actorId.IsEmpty) return null;
        var precise = await permissions.GetAccessScopeAsync(tenantId, actorId, PermissionKeys.TrackingRead, cancellationToken);
        var summary = await permissions.GetAccessScopeAsync(tenantId, actorId, PermissionKeys.TrackingSummary, cancellationToken);
        var widest = precise is null ? summary : summary is null || precise.Scope >= summary.Scope ? precise : summary;
        if (widest is null) return null;

        var now = clock.GetUtcNow();
        var rows = await store.ListOpenShiftsAsync(tenantId, widest, cancellationToken);
        return rows.Select(row => ToEntry(row, precise?.Covers(new UserId(row.UserId), row.RegionId) == true, now)).ToList();
    }

    public static LiveMapEntryDto ToEntry(LiveMapRow row, bool precise, DateTimeOffset now)
    {
        DateTimeOffset? contact = row.LastPointAt is { } p ? (row.SeenAt is { } s && s > p ? s : p) : row.SeenAt;
        var isLive = contact is { } at && now - at <= LiveWindow;
        var hasPoint = precise && row.LastPointAt is not null;
        return new LiveMapEntryDto(row.UserId, row.Email, row.TeamName, row.ShiftStartedAt, row.LastPointAt, isLive, precise,
            hasPoint ? row.Latitude : null, hasPoint ? row.Longitude : null, hasPoint ? row.AccuracyMeters : null,
            hasPoint ? row.IsMocked : null, row.LastCustomerName, row.LastVisitAt, row.DeviceLabel, row.SeenAt, row.Health, precise ? row.Phone : null);
    }
}
