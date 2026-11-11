using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LifeDrop.Application.Roles;
using LifeDrop.Application.Visits;
using LifeDrop.Domain.Common;

namespace LifeDrop.Application.Reports;

// A user's own KPIs for a month, next to the month before, the same month a year earlier and the last 12 months (plan step 5.29, owner request 2026-11-11). The numbers are the
// ones of docs/KPI.md for one user and one calendar month (Iraq days); no customer-type filter. A month with no data has zero counts and no rates (a dash), never 0 %.
public sealed record KpiPeriodDto(string Month, KpiCounts Counts, KpiRates Rates);

public sealed record KpiCompareDto(Guid UserId, string? Email, string Month, KpiPeriodDto Current, KpiPeriodDto Previous, KpiPeriodDto LastYear, IReadOnlyList<KpiPeriodDto> Trend);

// Everybody reads their own numbers (no permission needed: it is their own work). Another user's numbers need reports.read and the user inside the reader's area (a supervisor:
// the own region, like the report); outside it the answer is "not found". Months go from three years back up to the current month.
public sealed class KpiCompare(IReportStore store, IPermissionStore permissions, TimeProvider clock)
{
    public const int MaxMonthsBack = 36;
    public const int TrendMonths = 12;

    private static readonly KpiCounts NoData = new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, [], 0, 0);

    public async Task<(ReportResult Result, KpiCompareDto? Data)> GetAsync(TenantId tenantId, UserId actorId, Guid? userId, DateOnly month, CancellationToken cancellationToken)
    {
        if (tenantId.IsEmpty || actorId.IsEmpty) return (ReportResult.Forbidden, null);
        if (userId == Guid.Empty) return (ReportResult.InvalidData, null);
        var first = new DateOnly(month.Year, month.Month, 1);
        var today = IraqCalendar.Today(clock);
        var thisMonth = new DateOnly(today.Year, today.Month, 1);
        if (first > thisMonth || first < thisMonth.AddMonths(-MaxMonthsBack)) return (ReportResult.InvalidData, null);

        var target = userId ?? actorId.Value;
        AccessScope scope;
        if (target == actorId.Value) scope = new AccessScope(DataScope.Own, actorId, null);
        else
        {
            var wide = await permissions.GetAccessScopeAsync(tenantId, actorId, PermissionKeys.ReportsRead, cancellationToken);
            if (wide is null) return (ReportResult.Forbidden, null);
            scope = wide;
        }
        var users = await store.ListUsersAsync(tenantId, scope, new KpiFilter(first, first, null, null, null), cancellationToken);
        var user = users.FirstOrDefault(u => u.UserId == target);
        if (user is null) return (ReportResult.NotFound, null);

        var now = clock.GetUtcNow();
        var periods = new Dictionary<DateOnly, KpiPeriodDto>();
        async Task<KpiPeriodDto> PeriodAsync(DateOnly m)
        {
            if (periods.TryGetValue(m, out var known)) return known;
            var filter = new KpiFilter(m, m.AddMonths(1).AddDays(-1), null, null, null);
            var counts = await store.CountsAsync(tenantId, [target], filter, today, now, cancellationToken);
            var c = counts.TryGetValue(target, out var found) ? found : NoData;
            return periods[m] = new KpiPeriodDto(m.ToString("yyyy-MM", CultureInfo.InvariantCulture), c, KpiReport.Rates(c));
        }

        var trend = new List<KpiPeriodDto>();
        for (var back = TrendMonths - 1; back >= 0; back--) trend.Add(await PeriodAsync(first.AddMonths(-back)));
        var current = await PeriodAsync(first);
        var previous = await PeriodAsync(first.AddMonths(-1));
        var lastYear = await PeriodAsync(first.AddYears(-1));
        return (ReportResult.Success, new KpiCompareDto(target, user.Email, current.Month, current, previous, lastYear, trend));
    }
}
