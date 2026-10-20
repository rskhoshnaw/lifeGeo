using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LifeDrop.Application.Roles;
using LifeDrop.Application.Visits;
using LifeDrop.Domain.Common;

namespace LifeDrop.Application.Alerts;

// Alerts in the panel (plan step 2.9). An alert is about one event (the dedupe key is unique, so one event never makes two alerts). Status new -> seen -> handled
// (a note is needed). Operational and security alerts are separated by permission.
public sealed record AlertDto(Guid Id, string Kind, string Category, string Severity, Guid? SubjectUserId, string? SubjectEmail, Guid? CustomerId, string? CustomerCode, string? CustomerName,
    string EntityId, string? Detail, string Status, DateTimeOffset OccurredAt, DateTimeOffset CreatedAt, DateTimeOffset? SeenAt, string? SeenByEmail, DateTimeOffset? HandledAt,
    string? HandledByEmail, string? HandledNote, string? Evidence = null, string? HandledOutcome = null);

// Outcome of the review: valid | suspicious | fraud. Needed for the security alerts about behaviour (AlertRules.ReviewedKinds).
public sealed record HandleAlertRequest(string? Note, string? Outcome = null);
public sealed record AlertCountDto(int New);
public sealed record ScanResultDto(int Created);

// Plan step 4.33: the first ShiftWindowHours hours of a shift are watched; staying in one place for more than StationaryMinutes there, or ending the shift
// before the window is over, makes an alert for the sales manager and the rep's supervisor.
public sealed record ShiftAlertSettingsDto(int ShiftWindowHours, int StationaryMinutes);

public enum AlertResult { Success, Forbidden, NotFound, InvalidData, Conflict }

// Which alerts a user may see in one category: everything, or the alerts about users of one region.
public sealed record AlertAudience(string Category, Guid? RegionId);

public static class AlertRules
{
    public static readonly TimeSpan VisitDelay = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan LongStay = TimeSpan.FromMinutes(90);
    public static readonly TimeSpan TrackingGap = TimeSpan.FromMinutes(15);
    public static readonly string[] Categories = ["operational", "security"];
    public static readonly string[] Statuses = ["new", "seen", "handled"];
    public static readonly string[] Outcomes = ["valid", "suspicious", "fraud"];
    // Anti-fraud thresholds (plan step 2.10; see docs/ANTIFRAUD.md). Only tracking points of shifts that started in the last 48 hours are looked at.
    public const double MaxSpeedKmh = 180;
    public const double MinJumpMeters = 1000;
    public static readonly TimeSpan ClockAhead = TimeSpan.FromMinutes(5);
    public const double AreaRadiusMeters = 30_000;
    public const double PoorAccuracyMeters = 200;
    public const int PoorAccuracyCount = 5;
    public static readonly TimeSpan ScanWindow = TimeSpan.FromHours(48);
    // Anti-fraud from visit events and shift summaries (plan step 4.8).
    public static readonly TimeSpan MinStay = TimeSpan.FromMinutes(2);
    // Shift alerts (plan step 4.33): defaults, limits and the radius that counts as "in one place" (phone positions indoors drift by tens of metres).
    public const int DefaultShiftWindowHours = 3;
    public const int DefaultStationaryMinutes = 20;
    public const double StationaryRadiusMeters = 50;
    public const double NearCustomerMeters = 100;
    public static readonly TimeSpan CoverageMinShift = TimeSpan.FromHours(1);
    public const double LowCoveragePercent = 50;
    // A manager who handles one of these must say what the review found.
    public static readonly string[] ReviewedKinds = ["gps_mocked", "impossible_speed", "clock_tamper", "outside_area", "poor_accuracy", "device_mismatch", "impossible_travel", "short_stay"];
}

public interface IAlertStore
{
    Task<IReadOnlyList<AlertDto>> ListAsync(TenantId tenantId, IReadOnlyList<AlertAudience> audiences, string? category, string? status, string? kind, int limit, CancellationToken cancellationToken);
    Task<int> CountNewAsync(TenantId tenantId, IReadOnlyList<AlertAudience> audiences, CancellationToken cancellationToken);
    Task<AlertDto?> GetAsync(TenantId tenantId, Guid alertId, IReadOnlyList<AlertAudience> audiences, CancellationToken cancellationToken);
    // new -> seen; Conflict when it is not new.
    Task<AlertResult> MarkSeenAsync(TenantId tenantId, UserId actorId, Guid alertId, DateTimeOffset now, CancellationToken cancellationToken);
    // new or seen -> handled; Conflict when it is handled already.
    Task<AlertResult> HandleAsync(TenantId tenantId, UserId actorId, Guid alertId, string note, string? outcome, DateTimeOffset now, CancellationToken cancellationToken);
    // Looks at the data of one company and makes the alerts that are missing; returns how many were made.
    Task<int> ScanAsync(TenantId tenantId, DateTimeOffset now, DateOnly today, TimeOnly localTime, CancellationToken cancellationToken);
    // The shift alert settings of a company (the defaults when never saved) and saving them (audit shift_alert_settings.changed).
    Task<ShiftAlertSettingsDto> GetShiftSettingsAsync(TenantId tenantId, CancellationToken cancellationToken) => throw new NotSupportedException();
    Task SetShiftSettingsAsync(TenantId tenantId, UserId actorId, ShiftAlertSettingsDto settings, CancellationToken cancellationToken) => throw new NotSupportedException();
    Task<IReadOnlyList<TenantId>> ListActiveTenantsAsync(CancellationToken cancellationToken);
}

public sealed class AlertService(IAlertStore store, IPermissionStore permissions, TimeProvider clock)
{
    public async Task<(AlertResult Result, IReadOnlyList<AlertDto> Items)> ListAsync(TenantId tenantId, UserId actorId, string? category, string? status, string? kind, int? limit,
        CancellationToken cancellationToken)
    {
        var (audiences, _) = await AudiencesAsync(tenantId, actorId, cancellationToken);
        if (audiences.Count == 0) return (AlertResult.Forbidden, Array.Empty<AlertDto>());
        var take = limit ?? 100;
        if (take is < 1 or > 200 || (category is not null && !AlertRules.Categories.Contains(category)) || (status is not null && !AlertRules.Statuses.Contains(status)) || kind?.Length > 50)
            return (AlertResult.InvalidData, Array.Empty<AlertDto>());
        return (AlertResult.Success, await store.ListAsync(tenantId, audiences, category, status, kind, take, cancellationToken));
    }

    public async Task<(AlertResult Result, AlertCountDto? Count)> CountAsync(TenantId tenantId, UserId actorId, CancellationToken cancellationToken)
    {
        var (audiences, _) = await AudiencesAsync(tenantId, actorId, cancellationToken);
        return audiences.Count == 0 ? (AlertResult.Forbidden, null) : (AlertResult.Success, new AlertCountDto(await store.CountNewAsync(tenantId, audiences, cancellationToken)));
    }

    public async Task<(AlertResult Result, AlertDto? Alert)> GetAsync(TenantId tenantId, UserId actorId, Guid alertId, CancellationToken cancellationToken)
    {
        var (audiences, _) = await AudiencesAsync(tenantId, actorId, cancellationToken);
        if (audiences.Count == 0) return (AlertResult.Forbidden, null);
        var alert = alertId == Guid.Empty ? null : await store.GetAsync(tenantId, alertId, audiences, cancellationToken);
        return alert is null ? (AlertResult.NotFound, null) : (AlertResult.Success, alert);
    }

    // Seeing and handling need alerts.handle and the right to read the category of that alert.
    public async Task<AlertResult> MarkSeenAsync(TenantId tenantId, UserId actorId, Guid alertId, CancellationToken cancellationToken)
    {
        var (check, _) = await LoadAsync(tenantId, actorId, alertId, cancellationToken);
        return check != AlertResult.Success ? check : await store.MarkSeenAsync(tenantId, actorId, alertId, clock.GetUtcNow(), cancellationToken);
    }

    public async Task<AlertResult> HandleAsync(TenantId tenantId, UserId actorId, Guid alertId, HandleAlertRequest request, CancellationToken cancellationToken)
    {
        var (check, alert) = await LoadAsync(tenantId, actorId, alertId, cancellationToken);
        if (check != AlertResult.Success) return check;
        var note = request.Note?.Trim();
        // A review of behaviour needs its result; for other alerts a result is optional (and only "valid" makes sense).
        var needsOutcome = AlertRules.ReviewedKinds.Contains(alert!.Kind);
        if (string.IsNullOrEmpty(note) || note.Length > 500 || (needsOutcome && request.Outcome is null) || (request.Outcome is not null && !AlertRules.Outcomes.Contains(request.Outcome)) ||
            (!needsOutcome && request.Outcome is not null))
            return AlertResult.InvalidData;
        return await store.HandleAsync(tenantId, actorId, alertId, note, request.Outcome, clock.GetUtcNow(), cancellationToken);
    }

    public async Task<(AlertResult Result, ScanResultDto? Scan)> ScanAsync(TenantId tenantId, UserId actorId, CancellationToken cancellationToken)
    {
        if (await ScopeAsync(tenantId, actorId, PermissionKeys.AlertsHandle, cancellationToken) is null) return (AlertResult.Forbidden, null);
        return (AlertResult.Success, new ScanResultDto(await ScanTenantAsync(tenantId, cancellationToken)));
    }

    // Seen by whoever reads alerts; changed only by a manager with tenant-wide alert rights (the sales manager). Hours 1-12, minutes 5-120.
    public async Task<(AlertResult Result, ShiftAlertSettingsDto? Settings)> GetShiftSettingsAsync(TenantId tenantId, UserId actorId, CancellationToken cancellationToken)
    {
        var (audiences, _) = await AudiencesAsync(tenantId, actorId, cancellationToken);
        return audiences.Count == 0 ? (AlertResult.Forbidden, null) : (AlertResult.Success, await store.GetShiftSettingsAsync(tenantId, cancellationToken));
    }

    public async Task<AlertResult> SetShiftSettingsAsync(TenantId tenantId, UserId actorId, ShiftAlertSettingsDto settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var scope = await ScopeAsync(tenantId, actorId, PermissionKeys.AlertsHandle, cancellationToken);
        if (scope is null || scope.Scope != DataScope.Tenant) return AlertResult.Forbidden;
        if (settings.ShiftWindowHours is < 1 or > 12 || settings.StationaryMinutes is < 5 or > 120) return AlertResult.InvalidData;
        await store.SetShiftSettingsAsync(tenantId, actorId, settings, cancellationToken);
        return AlertResult.Success;
    }

    // Used by the background scan and by the manual scan.
    public Task<int> ScanTenantAsync(TenantId tenantId, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        return store.ScanAsync(tenantId, now, IraqCalendar.Today(clock), TimeOnly.FromDateTime(now.ToOffset(TimeSpan.FromHours(3)).DateTime), cancellationToken);
    }

    private async Task<(AlertResult Result, AlertDto? Alert)> LoadAsync(TenantId tenantId, UserId actorId, Guid alertId, CancellationToken cancellationToken)
    {
        if (await ScopeAsync(tenantId, actorId, PermissionKeys.AlertsHandle, cancellationToken) is null) return (AlertResult.Forbidden, null);
        var (audiences, _) = await AudiencesAsync(tenantId, actorId, cancellationToken);
        var alert = alertId == Guid.Empty || audiences.Count == 0 ? null : await store.GetAsync(tenantId, alertId, audiences, cancellationToken);
        return alert is null ? (AlertResult.NotFound, null) : (AlertResult.Success, alert);
    }

    private async Task<(IReadOnlyList<AlertAudience> Audiences, bool Any)> AudiencesAsync(TenantId tenantId, UserId actorId, CancellationToken cancellationToken)
    {
        var list = new List<AlertAudience>();
        foreach (var (category, permission) in new[] { ("operational", PermissionKeys.AlertsRead), ("security", PermissionKeys.AlertsSecurityRead) })
        {
            var scope = await ScopeAsync(tenantId, actorId, permission, cancellationToken);
            if (scope is null) continue;
            if (scope.Scope == DataScope.Tenant) list.Add(new AlertAudience(category, null));
            else if (scope.RegionId is { } region) list.Add(new AlertAudience(category, region));
            // A scoped reader without a region sees nothing.
        }
        return (list, list.Count > 0);
    }

    private Task<AccessScope?> ScopeAsync(TenantId tenantId, UserId actorId, string permission, CancellationToken cancellationToken) =>
        tenantId.IsEmpty || actorId.IsEmpty ? Task.FromResult<AccessScope?>(null) : permissions.GetAccessScopeAsync(tenantId, actorId, permission, cancellationToken);
}
