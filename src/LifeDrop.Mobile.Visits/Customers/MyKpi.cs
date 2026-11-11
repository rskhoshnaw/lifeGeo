using System.Globalization;

namespace LifeDrop.Mobile.Visits;

// The visitor's own KPIs for a month (plan step 5.29): what GET /reports/kpi-compare answers. A month with no data has zero counts and no rates.
public sealed record MyKpiMoney(string Currency, decimal Total);

public sealed record MyKpiCounts(int VisitsPlanned, int VisitsCompleted, int VisitsFailed, int DelayBase, int DelayedVisits, int NewCustomers, int ApprovedNewCustomers, int Orders,
    int OrdersDelivered, int OrdersDeliveryFailed, List<MyKpiMoney>? OrderTotals, int FollowUpBase, int FollowUpsOverdue);

public sealed record MyKpiRates(double? VisitCompletion, double? VisitFailure, double? Delay, double? OrderDelivery, double? FollowUpOverdue);

public sealed record MyKpiPeriod(string Month, MyKpiCounts Counts, MyKpiRates Rates);

// Month is "yyyy-MM"; Trend is the 12 months up to it, oldest first.
public sealed record MyKpiData(string Month, MyKpiPeriod Current, MyKpiPeriod Previous, MyKpiPeriod LastYear, List<MyKpiPeriod>? Trend);

public enum KpiKind { Count, Rate }

// One line of the comparison: its text key (app.kpi.*), whether it is a count or a rate, and whether more is better (completion yes, failures and delays no).
public sealed record KpiMetric(string Id, string TextKey, KpiKind Kind, bool HigherIsBetter, Func<MyKpiPeriod, double?> Value);

public static class MyKpi
{
    public static readonly IReadOnlyList<KpiMetric> Metrics =
    [
        new("planned", "app.kpi.planned", KpiKind.Count, true, p => p.Counts.VisitsPlanned),
        new("completed", "app.kpi.completed", KpiKind.Count, true, p => p.Counts.VisitsCompleted),
        new("completion", "app.kpi.completion", KpiKind.Rate, true, p => p.Rates.VisitCompletion),
        new("failure", "app.kpi.failure", KpiKind.Rate, false, p => p.Rates.VisitFailure),
        new("delay", "app.kpi.delay", KpiKind.Rate, false, p => p.Rates.Delay),
        new("newCustomers", "app.kpi.newCustomers", KpiKind.Count, true, p => p.Counts.NewCustomers),
        new("orders", "app.kpi.orders", KpiKind.Count, true, p => p.Counts.Orders),
        new("delivery", "app.kpi.delivery", KpiKind.Rate, true, p => p.Rates.OrderDelivery),
        new("followups", "app.kpi.followups", KpiKind.Rate, false, p => p.Rates.FollowUpOverdue),
    ];

    // "12" for a count, "83.3%" for a rate, "—" when there is nothing to measure.
    public static string Format(KpiMetric metric, double? value) =>
        value is not { } v ? "—" : metric.Kind == KpiKind.Rate ? v.ToString("0.#", CultureInfo.InvariantCulture) + "%" : v.ToString("0", CultureInfo.InvariantCulture);

    // The change from `baseValue` to `current`: "+12.5%" for a count (of the earlier month), "-3.2 pt" for a rate (percentage points), "—" when it cannot be worked out (no earlier
    // value, or a count that was 0). Better is true when the change is the good direction, false when it is the bad one, null when there is none.
    public static (string Text, bool? Better) Change(KpiMetric metric, double? current, double? baseValue)
    {
        if (current is not { } now || baseValue is not { } before || (metric.Kind == KpiKind.Count && before == 0)) return ("—", null);
        var diff = metric.Kind == KpiKind.Count ? (now - before) / before * 100 : now - before;
        var rounded = Math.Round(diff, 1, MidpointRounding.AwayFromZero);
        var text = (rounded > 0 ? "+" : "") + rounded.ToString("0.#", CultureInfo.InvariantCulture) + (metric.Kind == KpiKind.Count ? "%" : " pt");
        return (text, rounded == 0 ? null : (rounded > 0) == metric.HigherIsBetter);
    }
}
