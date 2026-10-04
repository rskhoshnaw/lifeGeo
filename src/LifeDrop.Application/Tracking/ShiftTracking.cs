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
public sealed record UploadPointsRequest(List<TrackingPointRequest>? Points);
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
}

public enum ShiftResult { Success, Created, NotFound, DeviceRequired, DeviceMismatch, InvalidData, Conflict }

// Shift and tracking rules (plan step 1.13, PRD §6.2, decision D1). Tracking happens only inside an explicit shift
// of the caller, from the device bound to the access token.
public sealed class ShiftTracking(IShiftStore store, TimeProvider clock)
{
    public const int MaxPointsPerUpload = 200;
    public static readonly TimeSpan ExpectedInterval = TimeSpan.FromSeconds(30);
    public const double TargetCoveragePercent = 90;
    public static readonly TimeSpan TargetMaxGap = TimeSpan.FromMinutes(5);
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
        var points = request.Points;
        if (points is null || points.Count == 0 || points.Count > MaxPointsPerUpload) return (ShiftResult.InvalidData, null);

        var shift = await LoadOwnAsync(tenantId, actorId, shiftId, cancellationToken);
        if (shift is null) return (ShiftResult.NotFound, null);
        if (shift.DeviceId != deviceId) return (ShiftResult.DeviceMismatch, null);

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
