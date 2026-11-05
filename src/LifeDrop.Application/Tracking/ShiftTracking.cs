using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LifeDrop.Domain.Common;
using LifeDrop.Domain.Visits;

namespace LifeDrop.Application.Tracking;

public sealed record ShiftDto(Guid Id, Guid DeviceId, DateTimeOffset StartedAt, DateTimeOffset? EndedAt);

// One position from the phone during a shift. ClientPointId is created on the phone once per point, so a retried
// upload is recognised and not stored twice.
public sealed record TrackingPointRequest(Guid ClientPointId, double Latitude, double Longitude, double AccuracyMeters,
    DateTimeOffset CapturedAt, bool IsMocked = false);
// What the phone says about itself (plan step 5.17): permissions and settings that decide whether tracking can keep running, the app and phone, the battery. Every part is optional:
// an old app sends none. Values outside the allowed ones are dropped by the server, never trusted for anything but showing to a manager.
public sealed record ShiftHealthRequest(string? AppVersion = null, string? Platform = null, string? OsVersion = null, string? DeviceModel = null,
    string? LocationPermission = null, bool? PreciseLocation = null, bool? LocationServices = null, bool? Notifications = null, bool? BatteryExempt = null,
    string? BackgroundRefresh = null, int? BatteryPercent = null, bool? LowPower = null);
// A report may have no points (a phone that stands still, or has no fix yet) when it carries the health.
public sealed record UploadPointsRequest(List<TrackingPointRequest>? Points, ShiftHealthRequest? Health = null);
public sealed record UploadPointsResponse(int Accepted, int Duplicates, int Rejected);

// Coverage of a shift for the real-device acceptance test (plan step 1.13): points received against one point
// per expected interval, and the longest time without a point (including from the start and up to the end).
public sealed record ShiftSummaryDto(Guid ShiftId, DateTimeOffset StartedAt, DateTimeOffset? EndedAt, int PointCount,
    int ExpectedPoints, double CoveragePercent, int MaxGapSeconds, bool MeetsTarget);

public interface IShiftStore
{
    Task<ShiftDto?> GetOpenAsync(TenantId tenantId, UserId userId, CancellationToken cancellationToken);
    // False when the user already has an open shift (a concurrent start).
    Task<bool> TryStartAsync(TenantId tenantId, UserId userId, Guid deviceId, Guid shiftId, CancellationToken cancellationToken);
    Task<(ShiftDto Shift, Guid UserId)?> GetAsync(TenantId tenantId, Guid shiftId, CancellationToken cancellationToken);
    Task<bool> TryEndAsync(TenantId tenantId, UserId userId, Guid shiftId, CancellationToken cancellationToken);
    // Stores the points, ignoring any already stored for the shift; returns how many were new.
    Task<int> AddPointsAsync(TenantId tenantId, UserId userId, Guid deviceId, Guid shiftId, IReadOnlyList<TrackingPointRequest> points,
        CancellationToken cancellationToken);
    Task<IReadOnlyList<DateTimeOffset>> ListPointTimesAsync(TenantId tenantId, Guid shiftId, CancellationToken cancellationToken);
    // Replaces the state of the phone of this shift and sets the time it was last heard from (the server clock).
    Task RecordHealthAsync(TenantId tenantId, UserId userId, Guid shiftId, ShiftHealthRequest health, CancellationToken cancellationToken);
}

public enum ShiftResult { Success, Created, NotFound, DeviceRequired, DeviceMismatch, InvalidData, Conflict }

// Shift and tracking rules (plan step 1.13, PRD §6.2, decision D1). Tracking happens only inside an explicit shift
// of the caller, from the device bound to the access token.
public sealed class ShiftTracking(IShiftStore store, TimeProvider clock)
{
    public const int MaxPointsPerUpload = 200;
    // Owner decision 2026-10-07 (replaces the 30 s / 5 min of D1): a point about every 3 minutes saves battery on Android and fits iOS limits.
    // Trust comes from proofs at the moments that matter (check-in/out, orders, payments), not from a dense trail.
    public static readonly TimeSpan ExpectedInterval = TimeSpan.FromMinutes(3);
    public const double TargetCoveragePercent = 90;
    public static readonly TimeSpan TargetMaxGap = TimeSpan.FromMinutes(15);
    // A point may be up to this far ahead of the server clock (phone clock drift).
    public static readonly TimeSpan MaxFutureSkew = TimeSpan.FromSeconds(30);

    // Starts a shift, or returns the open one (a repeated tap or a retry does not open a second shift).
    public async Task<(ShiftResult Result, ShiftDto? Shift)> StartAsync(TenantId tenantId, UserId actorId, Guid? deviceId, CancellationToken cancellationToken)
    {
        if (deviceId is null || deviceId == Guid.Empty) return (ShiftResult.DeviceRequired, null);
        var open = await store.GetOpenAsync(tenantId, actorId, cancellationToken);
        if (open is not null) return (ShiftResult.Success, open);

        var id = Guid.NewGuid();
        if (!await store.TryStartAsync(tenantId, actorId, deviceId.Value, id, cancellationToken))
        {
            open = await store.GetOpenAsync(tenantId, actorId, cancellationToken);
            return open is null ? (ShiftResult.Conflict, null) : (ShiftResult.Success, open);
        }
        return (ShiftResult.Created, await store.GetOpenAsync(tenantId, actorId, cancellationToken));
    }

    public Task<ShiftDto?> GetCurrentAsync(TenantId tenantId, UserId actorId, CancellationToken cancellationToken) =>
        store.GetOpenAsync(tenantId, actorId, cancellationToken);

    // Ending an already ended shift succeeds again (a retry after a lost reply).
    public async Task<ShiftResult> EndAsync(TenantId tenantId, UserId actorId, Guid shiftId, CancellationToken cancellationToken)
    {
        var shift = await LoadOwnAsync(tenantId, actorId, shiftId, cancellationToken);
        if (shift is null) return ShiftResult.NotFound;
        if (shift.EndedAt is not null) return ShiftResult.Success;
        return await store.TryEndAsync(tenantId, actorId, shiftId, cancellationToken) ? ShiftResult.Success : ShiftResult.Conflict;
    }

    // Points must come from the shift's device and lie between the shift start and its end (or now). Points that
    // are malformed or outside the shift are counted as rejected; the others are stored once.
    public async Task<(ShiftResult Result, UploadPointsResponse? Response)> UploadAsync(TenantId tenantId, UserId actorId, Guid? deviceId,
        Guid shiftId, UploadPointsRequest request, CancellationToken cancellationToken)
    {
        if (deviceId is null || deviceId == Guid.Empty) return (ShiftResult.DeviceRequired, null);
        var points = request.Points ?? [];
        if (points.Count > MaxPointsPerUpload || (points.Count == 0 && request.Health is null)) return (ShiftResult.InvalidData, null);

        var shift = await LoadOwnAsync(tenantId, actorId, shiftId, cancellationToken);
        if (shift is null) return (ShiftResult.NotFound, null);
        if (shift.DeviceId != deviceId) return (ShiftResult.DeviceMismatch, null);

        // The phone is alive and says how it is, also when there is no new position.
        if (SanitizeHealth(request.Health) is { } health && shift.EndedAt is null)
            await store.RecordHealthAsync(tenantId, actorId, shiftId, health, cancellationToken);
        if (points.Count == 0) return (ShiftResult.Success, new UploadPointsResponse(0, 0, 0));

        var latest = shift.EndedAt ?? clock.GetUtcNow() + MaxFutureSkew;
        var valid = points
            .Where(p => p is not null && p.ClientPointId != Guid.Empty &&
                        new GpsFix(p.Latitude, p.Longitude, p.AccuracyMeters, p.CapturedAt, p.IsMocked).IsWellFormed &&
                        p.CapturedAt >= shift.StartedAt && p.CapturedAt <= latest)
            .ToList();
        // A point repeated in the same batch, or already stored, counts as a duplicate.
        var distinct = valid.GroupBy(p => p.ClientPointId).Select(g => g.First()).ToList();
        var accepted = distinct.Count == 0 ? 0 : await store.AddPointsAsync(tenantId, actorId, deviceId.Value, shiftId, distinct, cancellationToken);
        return (ShiftResult.Success, new UploadPointsResponse(accepted, valid.Count - accepted, points.Count - valid.Count));
    }

    // Only known values are kept; text is cut to the size the table accepts.
    public static ShiftHealthRequest? SanitizeHealth(ShiftHealthRequest? health)
    {
        if (health is null) return null;
        static string? Clip(string? value, int max)
        {
            value = value?.Trim();
            return string.IsNullOrEmpty(value) ? null : value.Length <= max ? value : value[..max];
        }
        static string? OneOf(string? value, params string[] allowed)
        {
            value = value?.Trim().ToLowerInvariant();
            return value is not null && allowed.Contains(value) ? value : null;
        }
        return new ShiftHealthRequest(
            Clip(health.AppVersion, 40), OneOf(health.Platform, "android", "ios"), Clip(health.OsVersion, 40), Clip(health.DeviceModel, 80),
            OneOf(health.LocationPermission, "always", "when_in_use", "denied") ?? "unknown",
            health.PreciseLocation, health.LocationServices, health.Notifications, health.BatteryExempt,
            OneOf(health.BackgroundRefresh, "available", "denied", "restricted"),
            health.BatteryPercent is >= 0 and <= 100 ? health.BatteryPercent : null, health.LowPower);
    }

    public async Task<(ShiftResult Result, ShiftSummaryDto? Summary)> SummarizeAsync(TenantId tenantId, UserId actorId, Guid shiftId,
        CancellationToken cancellationToken)
    {
        var shift = await LoadOwnAsync(tenantId, actorId, shiftId, cancellationToken);
        if (shift is null) return (ShiftResult.NotFound, null);
        var times = await store.ListPointTimesAsync(tenantId, shiftId, cancellationToken);
        return (ShiftResult.Success, Summarize(shift, times, shift.EndedAt ?? clock.GetUtcNow()));
    }

    public static ShiftSummaryDto Summarize(ShiftDto shift, IReadOnlyList<DateTimeOffset> pointTimes, DateTimeOffset end)
    {
        var duration = end - shift.StartedAt;
        var expected = Math.Max(1, (int)Math.Floor(duration / ExpectedInterval));
        var coverage = Math.Min(100, Math.Round(100.0 * pointTimes.Count / expected, 1));

        var maxGap = TimeSpan.Zero;
        var previous = shift.StartedAt;
        foreach (var time in pointTimes.OrderBy(t => t))
        {
            if (time - previous > maxGap) maxGap = time - previous;
            previous = time;
        }
        if (end - previous > maxGap) maxGap = end - previous;

        return new ShiftSummaryDto(shift.Id, shift.StartedAt, shift.EndedAt, pointTimes.Count, expected, coverage,
            (int)Math.Round(maxGap.TotalSeconds), coverage >= TargetCoveragePercent && maxGap <= TargetMaxGap);
    }

    // Only the caller's own shifts are visible here; another user's shift reads as not found.
    private async Task<ShiftDto?> LoadOwnAsync(TenantId tenantId, UserId actorId, Guid shiftId, CancellationToken cancellationToken)
    {
        if (shiftId == Guid.Empty) return null;
        var found = await store.GetAsync(tenantId, shiftId, cancellationToken);
        return found is { } f && f.UserId == actorId.Value ? f.Shift : null;
    }
}
