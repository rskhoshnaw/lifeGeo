using LifeDrop.Application.Roles;
using LifeDrop.Domain.Common;

namespace LifeDrop.Application.Tracking;

// Basic live map (plan step 1.19, PRD §6.2, decision D3): users with an open shift, the time of their last point
// and, with tracking.read, that point. tracking.summary shows status only and never coordinates. A point older
// than LiveWindow is returned with IsLive = false so the panel never shows it as live.
public sealed record LiveMapEntryDto(Guid UserId, string? Email, string? TeamName, DateTimeOffset ShiftStartedAt,
    DateTimeOffset? LastUpdateAt, bool IsLive, bool Precise, double? Latitude, double? Longitude, double? AccuracyMeters,
    bool? IsMocked, string? LastCustomerName, DateTimeOffset? LastVisitAt, string? DeviceLabel = null);

public sealed record LiveMapRow(Guid UserId, string? Email, string? TeamName, Guid? RegionId, DateTimeOffset ShiftStartedAt,
    DateTimeOffset? LastPointAt, double? Latitude, double? Longitude, double? AccuracyMeters, bool? IsMocked,
    string? LastCustomerName, DateTimeOffset? LastVisitAt, string? DeviceLabel = null);

public interface ILiveMapStore
{
    // Open shifts of active users inside the scope, with the last point of the shift and the last customer the
    // user checked in at since the shift started.
    Task<IReadOnlyList<LiveMapRow>> ListOpenShiftsAsync(TenantId tenantId, AccessScope scope, CancellationToken cancellationToken);
}

public sealed class LiveMap(ILiveMapStore store, IPermissionStore permissions, TimeProvider clock)
{
    // Points arrive about every 3 minutes (owner decision 2026-10-07), so a user is "live" while the last point is at most 10 minutes old.
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
        var isLive = row.LastPointAt is { } at && now - at <= LiveWindow;
        var hasPoint = precise && row.LastPointAt is not null;
        return new LiveMapEntryDto(row.UserId, row.Email, row.TeamName, row.ShiftStartedAt, row.LastPointAt, isLive, precise,
            hasPoint ? row.Latitude : null, hasPoint ? row.Longitude : null, hasPoint ? row.AccuracyMeters : null,
            hasPoint ? row.IsMocked : null, row.LastCustomerName, row.LastVisitAt, row.DeviceLabel);
    }
}
