using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LifeDrop.Application.Roles;
using LifeDrop.Application.Visits;
using LifeDrop.Domain.Common;

namespace LifeDrop.Application.Reports;

// Reports and KPIs (plan step 2.12, PRD §10). Every KPI has a numerator and a denominator written in docs/KPI.md; a rate is only worked out from them, and a total is
// the sum of the numerators over the sum of the denominators of the users (never an average of rates). Days are Iraq days.
public sealed record KpiFilter(DateOnly From, DateOnly To, Guid? RegionId, Guid? TeamId, string? CustomerType);

public sealed record CurrencyTotal(string Currency, decimal Total);

// The counts of one user (or the total). Rates are in KpiRates.
public sealed record KpiCounts(int VisitsPlanned, int VisitsCompleted, int VisitsFailed, int DelayBase, int DelayedVisits, int NewCustomers, int ApprovedNewCustomers, int Orders,
    int OrdersDelivered, int OrdersDeliveryFailed, IReadOnlyList<CurrencyTotal> OrderTotals, int FollowUpBase, int FollowUpsOverdue);

// Null when the denominator is 0 (nothing to measure), never 0 %. Percent with one decimal.
public sealed record KpiRates(double? VisitCompletion, double? VisitFailure, double? Delay, double? OrderDelivery, double? FollowUpOverdue);

public sealed record KpiRow(Guid UserId, string? Email, string? TeamName, KpiCounts Counts, KpiRates Rates);

public sealed record AlertKpi(string Kind, string Category, int Total, int Handled, int Valid, int Suspicious, int Fraud);

public sealed record KpiReportDto(DateOnly From, DateOnly To, KpiRow Total, IReadOnlyList<KpiRow> Users, IReadOnlyList<AlertKpi> Alerts);

public enum ReportResult { Success, Forbidden, InvalidData }

public sealed record KpiUser(Guid UserId, string? Email, string? TeamName);

public interface IReportStore
{
    // Active users inside the scope that match the region and team filters.
    Task<IReadOnlyList<KpiUser>> ListUsersAsync(TenantId tenantId, AccessScope scope, KpiFilter filter, CancellationToken cancellationToken);
    // Counts per user for the given users; users without any data are missing from the result.
    Task<IReadOnlyDictionary<Guid, KpiCounts>> CountsAsync(TenantId tenantId, IReadOnlyList<Guid> userIds, KpiFilter filter, DateOnly today, DateTimeOffset now, CancellationToken cancellationToken);
    Task<IReadOnlyList<AlertKpi>> AlertsAsync(TenantId tenantId, IReadOnlyList<Guid> userIds, IReadOnlyList<string> categories, KpiFilter filter, CancellationToken cancellationToken);
}

public sealed class KpiReport(IReportStore store, IPermissionStore permissions, TimeProvider clock)
{
    public const int MaxDays = 366;

    public async Task<(ReportResult Result, KpiReportDto? Report)> GetAsync(TenantId tenantId, UserId actorId, KpiFilter filter, CancellationToken cancellationToken)
    {
        var scope = tenantId.IsEmpty || actorId.IsEmpty ? null : await permissions.GetAccessScopeAsync(tenantId, actorId, PermissionKeys.ReportsRead, cancellationToken);
        if (scope is null) return (ReportResult.Forbidden, null);
        var customerType = string.IsNullOrWhiteSpace(filter.CustomerType) ? null : filter.CustomerType.Trim();
        if (filter.To < filter.From || filter.To.DayNumber - filter.From.DayNumber >= MaxDays || filter.RegionId == Guid.Empty || filter.TeamId == Guid.Empty || customerType?.Length > 50)
            return (ReportResult.InvalidData, null);
        filter = filter with { CustomerType = customerType };

        var users = await store.ListUsersAsync(tenantId, scope, filter, cancellationToken);
        var ids = users.Select(u => u.UserId).ToList();
        var counts = ids.Count == 0 ? new Dictionary<Guid, KpiCounts>() : (Dictionary<Guid, KpiCounts>)await ToDictionary(store.CountsAsync(tenantId, ids, filter, IraqCalendar.Today(clock), clock.GetUtcNow(), cancellationToken));
        var rows = users.Where(u => counts.ContainsKey(u.UserId)).Select(u => new KpiRow(u.UserId, u.Email, u.TeamName, counts[u.UserId], Rates(counts[u.UserId])))
            .OrderBy(r => r.Email, StringComparer.Ordinal).ThenBy(r => r.UserId).ToList();
        var total = Sum(rows.Select(r => r.Counts).ToList());

        // Alerts: only the categories the reader may read (security alerts need alerts.security.read).
        var categories = new List<string>();
        if (await permissions.GetAccessScopeAsync(tenantId, actorId, PermissionKeys.AlertsRead, cancellationToken) is not null) categories.Add("operational");
        if (await permissions.GetAccessScopeAsync(tenantId, actorId, PermissionKeys.AlertsSecurityRead, cancellationToken) is not null) categories.Add("security");
        var alerts = ids.Count == 0 || categories.Count == 0 ? Array.Empty<AlertKpi>() : await store.AlertsAsync(tenantId, ids, categories, filter, cancellationToken);
        return (ReportResult.Success, new KpiReportDto(filter.From, filter.To, new KpiRow(Guid.Empty, null, null, total, Rates(total)), rows, alerts));
    }

    private static async Task<Dictionary<Guid, KpiCounts>> ToDictionary(Task<IReadOnlyDictionary<Guid, KpiCounts>> task) => (await task).ToDictionary(p => p.Key, p => p.Value);

    public static double? Percent(int numerator, int denominator) => denominator <= 0 ? null : Math.Round(100.0 * numerator / denominator, 1);

    public static KpiRates Rates(KpiCounts c) => new(
        Percent(c.VisitsCompleted, c.VisitsPlanned), Percent(c.VisitsFailed, c.VisitsPlanned), Percent(c.DelayedVisits, c.DelayBase),
        Percent(c.OrdersDelivered, c.OrdersDelivered + c.OrdersDeliveryFailed), Percent(c.FollowUpsOverdue, c.FollowUpBase));

    // Sum of the numerators and of the denominators; money is summed per currency, never across currencies.
    public static KpiCounts Sum(IReadOnlyList<KpiCounts> all) => new(
        all.Sum(c => c.VisitsPlanned), all.Sum(c => c.VisitsCompleted), all.Sum(c => c.VisitsFailed), all.Sum(c => c.DelayBase), all.Sum(c => c.DelayedVisits),
        all.Sum(c => c.NewCustomers), all.Sum(c => c.ApprovedNewCustomers), all.Sum(c => c.Orders), all.Sum(c => c.OrdersDelivered), all.Sum(c => c.OrdersDeliveryFailed),
        all.SelectMany(c => c.OrderTotals).GroupBy(t => t.Currency).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => new CurrencyTotal(g.Key, g.Sum(t => t.Total))).ToList(),
        all.Sum(c => c.FollowUpBase), all.Sum(c => c.FollowUpsOverdue));
}
