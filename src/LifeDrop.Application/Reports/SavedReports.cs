using LifeDrop.Application.Roles;
using LifeDrop.Application.Visits;
using LifeDrop.Domain.Common;

namespace LifeDrop.Application.Reports;

// Saved and scheduled reports (plan step 3.2, part 1). A report is the filter of the KPI report (docs/KPI.md) under a name. Only its owner
// reads, runs, changes or deletes it; a run always works with the owner's current area (reports.read). A scheduled run stores its result.
public static class ReportRanges
{
    public static readonly string[] Relative = ["yesterday", "last_7_days", "last_30_days", "this_month", "last_month"];
    public const string Fixed = "fixed";

    // Iraq days; "today" is not in the past ranges except this_month.
    public static (DateOnly From, DateOnly To) Resolve(string kind, DateOnly? from, DateOnly? to, DateOnly today) => kind switch
    {
        "yesterday" => (today.AddDays(-1), today.AddDays(-1)),
        "last_7_days" => (today.AddDays(-7), today.AddDays(-1)),
        "last_30_days" => (today.AddDays(-30), today.AddDays(-1)),
        "this_month" => (new DateOnly(today.Year, today.Month, 1), today),
        "last_month" => (new DateOnly(today.Year, today.Month, 1).AddMonths(-1), new DateOnly(today.Year, today.Month, 1).AddDays(-1)),
        _ => (from!.Value, to!.Value)
    };
}

public static class ReportSchedules
{
    private static readonly TimeSpan Iraq = TimeSpan.FromHours(3);   // Asia/Baghdad, no daylight saving

    // The first run strictly after `now` (UTC), at `hour` Iraq time. Weekday: 1 = Monday ... 7 = Sunday; day of month 1-28.
    public static DateTimeOffset Next(string frequency, int? weekday, int? dayOfMonth, int hour, DateTimeOffset now)
    {
        var local = now.ToOffset(Iraq).DateTime;
        var date = DateOnly.FromDateTime(local);
        DateTime At(DateOnly d) => d.ToDateTime(new TimeOnly(hour, 0));
        DateOnly pick = frequency switch
        {
            "daily" => date,
            "weekly" => date.AddDays(((weekday!.Value - IsoWeekday(date)) + 7) % 7),
            _ => date.Day <= dayOfMonth!.Value ? new DateOnly(date.Year, date.Month, dayOfMonth.Value) : new DateOnly(date.Year, date.Month, 1).AddMonths(1).AddDays(dayOfMonth.Value - 1)
        };
        while (At(pick) <= local)
            pick = frequency switch { "daily" => pick.AddDays(1), "weekly" => pick.AddDays(7), _ => new DateOnly(pick.Year, pick.Month, 1).AddMonths(1).AddDays(dayOfMonth!.Value - 1) };
        return new DateTimeOffset(At(pick), Iraq).ToUniversalTime();
    }

    private static int IsoWeekday(DateOnly d) => d.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)d.DayOfWeek;
}

public sealed record ScheduleRequest(string? Frequency, int? Weekday, int? DayOfMonth, int? Hour, bool? IsActive);
public sealed record SaveReportRequest(string? Name, string? Range, DateOnly? From, DateOnly? To, Guid? RegionId, Guid? TeamId, string? CustomerType, ScheduleRequest? Schedule);
public sealed record ScheduleDto(string Frequency, int? Weekday, int? DayOfMonth, int Hour, bool IsActive, DateTimeOffset? NextRunAt, DateTimeOffset? LastRunAt);
public sealed record SavedReportDto(Guid Id, string Name, string Range, DateOnly? From, DateOnly? To, Guid? RegionId, Guid? TeamId, string? CustomerType, ScheduleDto? Schedule,
    DateTimeOffset CreatedAt);
public sealed record ReportRunDto(Guid Id, Guid ReportId, string Trigger, string Status, string? ErrorCode, DateOnly From, DateOnly To, DateTimeOffset RanAt);
public sealed record ReportRunDetailDto(ReportRunDto Run, KpiReportDto? Report);

// What the store keeps; the schedule is already worked out (NextRunAt).
public sealed record SavedReportData(string Name, string Range, DateOnly? From, DateOnly? To, Guid? RegionId, Guid? TeamId, string? CustomerType,
    string? Frequency, int? Weekday, int? DayOfMonth, int? Hour, bool IsActive, DateTimeOffset? NextRunAt);
public sealed record DueReport(Guid Id, UserId OwnerId, string Range, DateOnly? From, DateOnly? To, Guid? RegionId, Guid? TeamId, string? CustomerType,
    string Frequency, int? Weekday, int? DayOfMonth, int Hour);

public interface ISavedReportStore
{
    Task<IReadOnlyList<SavedReportDto>> ListAsync(TenantId tenantId, UserId owner, CancellationToken cancellationToken);
    Task<SavedReportDto?> GetAsync(TenantId tenantId, UserId owner, Guid id, CancellationToken cancellationToken);
    Task<int> CountAsync(TenantId tenantId, UserId owner, CancellationToken cancellationToken);
    // Null when the owner already has a report with this name.
    Task<Guid?> CreateAsync(TenantId tenantId, UserId owner, SavedReportData data, CancellationToken cancellationToken);
    // False when it is not the owner's; null result of the name check is the same as create (Conflict).
    Task<(bool Found, bool NameTaken)> UpdateAsync(TenantId tenantId, UserId owner, Guid id, SavedReportData data, CancellationToken cancellationToken);
    Task<bool> DeleteAsync(TenantId tenantId, UserId owner, Guid id, CancellationToken cancellationToken);
    Task<IReadOnlyList<ReportRunDto>> ListRunsAsync(TenantId tenantId, UserId owner, Guid reportId, int limit, CancellationToken cancellationToken);
    Task<ReportRunDetailDto?> GetRunAsync(TenantId tenantId, UserId owner, Guid reportId, Guid runId, CancellationToken cancellationToken);
    // Stores the run (keeping the newest MaxRuns of the report) and marks the report as run at `ranAt`.
    Task<Guid> AddRunAsync(TenantId tenantId, Guid reportId, string trigger, string status, string? errorCode, DateOnly from, DateOnly to, DateTimeOffset ranAt, KpiReportDto? result,
        DateTimeOffset? nextRunAt, CancellationToken cancellationToken);
    Task<IReadOnlyList<DueReport>> ListDueAsync(TenantId tenantId, DateTimeOffset now, int limit, CancellationToken cancellationToken);
}

public enum SavedReportResult { Success, Forbidden, InvalidData, NotFound, Conflict }

public sealed class SavedReportService(ISavedReportStore store, KpiReport kpi, IPermissionStore permissions, TimeProvider clock)
{
    public const int MaxPerUser = 20;
    public const int MaxRuns = 30;
    public const int DefaultLimit = 30;
    public const int DueBatch = 20;

    public async Task<(SavedReportResult Result, IReadOnlyList<SavedReportDto> Items)> ListAsync(TenantId tenantId, UserId actorId, CancellationToken cancellationToken) =>
        await CanAsync(tenantId, actorId, cancellationToken) ? (SavedReportResult.Success, await store.ListAsync(tenantId, actorId, cancellationToken)) : (SavedReportResult.Forbidden, []);

    public async Task<(SavedReportResult Result, Guid? Id)> CreateAsync(TenantId tenantId, UserId actorId, SaveReportRequest request, CancellationToken cancellationToken)
    {
        if (!await CanAsync(tenantId, actorId, cancellationToken)) return (SavedReportResult.Forbidden, null);
        if (!TryBuild(request, out var data)) return (SavedReportResult.InvalidData, null);
        if (await store.CountAsync(tenantId, actorId, cancellationToken) >= MaxPerUser) return (SavedReportResult.Conflict, null);
        var id = await store.CreateAsync(tenantId, actorId, data!, cancellationToken);
        return id is null ? (SavedReportResult.Conflict, null) : (SavedReportResult.Success, id);
    }

    public async Task<SavedReportResult> UpdateAsync(TenantId tenantId, UserId actorId, Guid id, SaveReportRequest request, CancellationToken cancellationToken)
    {
        if (!await CanAsync(tenantId, actorId, cancellationToken)) return SavedReportResult.Forbidden;
        if (id == Guid.Empty) return SavedReportResult.NotFound;
        if (!TryBuild(request, out var data)) return SavedReportResult.InvalidData;
        var (found, taken) = await store.UpdateAsync(tenantId, actorId, id, data!, cancellationToken);
        return !found ? SavedReportResult.NotFound : taken ? SavedReportResult.Conflict : SavedReportResult.Success;
    }

    public async Task<SavedReportResult> DeleteAsync(TenantId tenantId, UserId actorId, Guid id, CancellationToken cancellationToken)
    {
        if (!await CanAsync(tenantId, actorId, cancellationToken)) return SavedReportResult.Forbidden;
        return id != Guid.Empty && await store.DeleteAsync(tenantId, actorId, id, cancellationToken) ? SavedReportResult.Success : SavedReportResult.NotFound;
    }

    // Runs now with the owner's area and keeps the result; the schedule is not moved.
    public async Task<(SavedReportResult Result, ReportRunDetailDto? Run)> RunNowAsync(TenantId tenantId, UserId actorId, Guid id, CancellationToken cancellationToken)
    {
        if (!await CanAsync(tenantId, actorId, cancellationToken)) return (SavedReportResult.Forbidden, null);
        var report = id == Guid.Empty ? null : await store.GetAsync(tenantId, actorId, id, cancellationToken);
        if (report is null) return (SavedReportResult.NotFound, null);
        var now = clock.GetUtcNow();
        var (from, to) = ReportRanges.Resolve(report.Range, report.From, report.To, IraqCalendar.Today(clock));
        var (result, kpis) = await kpi.GetAsync(tenantId, actorId, new KpiFilter(from, to, report.RegionId, report.TeamId, report.CustomerType), cancellationToken);
        if (result != ReportResult.Success) return (result == ReportResult.Forbidden ? SavedReportResult.Forbidden : SavedReportResult.InvalidData, null);
        var runId = await store.AddRunAsync(tenantId, id, "manual", "ok", null, from, to, now, kpis, null, cancellationToken);
        return (SavedReportResult.Success, new ReportRunDetailDto(new ReportRunDto(runId, id, "manual", "ok", null, from, to, now), kpis));
    }

    public async Task<(SavedReportResult Result, IReadOnlyList<ReportRunDto> Items)> ListRunsAsync(TenantId tenantId, UserId actorId, Guid id, int? limit, CancellationToken cancellationToken)
    {
        if (!await CanAsync(tenantId, actorId, cancellationToken)) return (SavedReportResult.Forbidden, []);
        if (limit is < 1 or > MaxRuns) return (SavedReportResult.InvalidData, []);
        if (id == Guid.Empty || await store.GetAsync(tenantId, actorId, id, cancellationToken) is null) return (SavedReportResult.NotFound, []);
        return (SavedReportResult.Success, await store.ListRunsAsync(tenantId, actorId, id, limit ?? DefaultLimit, cancellationToken));
    }

    public async Task<(SavedReportResult Result, ReportRunDetailDto? Run)> GetRunAsync(TenantId tenantId, UserId actorId, Guid id, Guid runId, CancellationToken cancellationToken)
    {
        if (!await CanAsync(tenantId, actorId, cancellationToken)) return (SavedReportResult.Forbidden, null);
        var run = id == Guid.Empty || runId == Guid.Empty ? null : await store.GetRunAsync(tenantId, actorId, id, runId, cancellationToken);
        return run is null ? (SavedReportResult.NotFound, null) : (SavedReportResult.Success, run);
    }

    // The background job: runs every due scheduled report of one company as its owner and moves the schedule on. A report whose owner can no
    // longer read reports (or whose filter became invalid) is stored as a failed run, never skipped silently.
    public async Task<int> RunDueAsync(TenantId tenantId, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var done = 0;
        foreach (var due in await store.ListDueAsync(tenantId, now, DueBatch, cancellationToken))
        {
            var (from, to) = ReportRanges.Resolve(due.Range, due.From, due.To, IraqCalendar.Today(clock));
            var next = ReportSchedules.Next(due.Frequency, due.Weekday, due.DayOfMonth, due.Hour, now);
            var (result, kpis) = await kpi.GetAsync(tenantId, due.OwnerId, new KpiFilter(from, to, due.RegionId, due.TeamId, due.CustomerType), cancellationToken);
            var ok = result == ReportResult.Success;
            await store.AddRunAsync(tenantId, due.Id, "schedule", ok ? "ok" : "failed", ok ? null : result == ReportResult.Forbidden ? "FORBIDDEN" : "INVALID_DATA", from, to, now, kpis, next, cancellationToken);
            done++;
        }
        return done;
    }

    private bool TryBuild(SaveReportRequest request, out SavedReportData? data)
    {
        data = null;
        var name = request.Name?.Trim();
        var range = request.Range?.Trim();
        var customerType = string.IsNullOrWhiteSpace(request.CustomerType) ? null : request.CustomerType.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 100 || customerType?.Length > 50 || request.RegionId == Guid.Empty || request.TeamId == Guid.Empty) return false;
        DateOnly? from = null, to = null;
        if (range == ReportRanges.Fixed)
        {
            if (request.From is null || request.To is null || request.To < request.From || request.To.Value.DayNumber - request.From.Value.DayNumber >= KpiReport.MaxDays) return false;
            from = request.From; to = request.To;
        }
        else if (range is null || !ReportRanges.Relative.Contains(range) || request.From is not null || request.To is not null) return false;

        string? frequency = null; int? weekday = null, dayOfMonth = null, hour = null; DateTimeOffset? next = null; var active = true;
        if (request.Schedule is { } s)
        {
            frequency = s.Frequency?.Trim().ToLowerInvariant();
            if (frequency is not ("daily" or "weekly" or "monthly") || s.Hour is null or < 0 or > 23) return false;
            if (frequency == "weekly" ? s.Weekday is null or < 1 or > 7 : s.Weekday is not null) return false;
            if (frequency == "monthly" ? s.DayOfMonth is null or < 1 or > 28 : s.DayOfMonth is not null) return false;
            weekday = s.Weekday; dayOfMonth = s.DayOfMonth; hour = s.Hour; active = s.IsActive ?? true;
            next = active ? ReportSchedules.Next(frequency, weekday, dayOfMonth, hour.Value, clock.GetUtcNow()) : null;
        }
        data = new SavedReportData(name, range!, from, to, request.RegionId, request.TeamId, customerType, frequency, weekday, dayOfMonth, hour, active, next);
        return true;
    }

    private async Task<bool> CanAsync(TenantId tenantId, UserId actorId, CancellationToken cancellationToken) =>
        !tenantId.IsEmpty && !actorId.IsEmpty && await permissions.HasPermissionAsync(tenantId, actorId, PermissionKeys.ReportsRead, cancellationToken);
}
