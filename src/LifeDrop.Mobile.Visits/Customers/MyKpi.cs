using System.Globalization;

namespace LifeDrop.Mobile.Visits;

// The visitor's own KPIs for a month (plan step 5.29): what GET /reports/kpi-compare answers. A month with no data has zero counts and no rates.
public sealed record MyKpiMoney(string Currency, decimal Total);

public sealed record MyKpiCounts(int VisitsPlanned, int VisitsCompleted, int VisitsFailed, int DelayBase, int DelayedVisits, int NewCustomers, int ApprovedNewCustomers, int Orders,
    int OrdersDelivered, int OrdersDeliveryFailed, List<MyKpiMoney>? OrderTotals, int FollowUpBase, int FollowUpsOverdue,
    // Plan steps 5.73 and 5.74 (the server adds them; an older answer has none): samples given, money received (confirmed), approved returns and net sales (orders minus approved returns).
    int Samples = 0, List<MyKpiMoney>? PaymentsConfirmed = null, List<MyKpiMoney>? PaymentsWaiting = null, int ReturnsApproved = 0, List<MyKpiMoney>? ReturnTotals = null, List<MyKpiMoney>? NetSales = null);

public sealed record MyKpiRates(double? VisitCompletion, double? VisitFailure, double? Delay, double? OrderDelivery, double? FollowUpOverdue);

public sealed record MyKpiPeriod(string Month, MyKpiCounts Counts, MyKpiRates Rates);

// Month is "yyyy-MM"; Trend is the 12 months up to it, oldest first.
public sealed record MyKpiData(string Month, MyKpiPeriod Current, MyKpiPeriod Previous, MyKpiPeriod LastYear, List<MyKpiPeriod>? Trend);

// Money is a count of dinars: shown with thousands separators and compared like a count (a percentage change).
// MoneyCents (dollars) shows two decimals.
public enum KpiKind { Count, Rate, Money, MoneyCents }

// One line of the comparison: its text key (app.kpi.*), whether it is a count or a rate, and whether more is better (completion yes, failures and delays no).
// OnlyWhenUsed: the line is shown only when some month of the page has a value (dollars: a visitor who works in dinars never sees them).
public sealed record KpiMetric(string Id, string TextKey, KpiKind Kind, bool HigherIsBetter, Func<MyKpiPeriod, double?> Value, bool OnlyWhenUsed = false);

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
        new("samples", "app.kpi.samples", KpiKind.Count, true, p => p.Counts.Samples),
        new("moneyIqd", "app.kpi.moneyIqd", KpiKind.Money, true, p => Of(p.Counts.PaymentsConfirmed, "IQD")),
        new("returns", "app.kpi.returns", KpiKind.Count, false, p => p.Counts.ReturnsApproved),
        new("netIqd", "app.kpi.netIqd", KpiKind.Money, true, p => Of(p.Counts.NetSales ?? p.Counts.OrderTotals, "IQD")),
        // Dollars (plan step 5.75): only for a visitor who has some.
        new("moneyUsd", "app.kpi.moneyUsd", KpiKind.MoneyCents, true, p => Of(p.Counts.PaymentsConfirmed, "USD"), true),
        new("netUsd", "app.kpi.netUsd", KpiKind.MoneyCents, true, p => Of(p.Counts.NetSales ?? p.Counts.OrderTotals, "USD"), true),
    ];

    // The total of one currency in a money list (0 when there is none).
    private static double Of(List<MyKpiMoney>? list, string currency) => (double)(list?.Where(m => m.Currency == currency).Sum(m => m.Total) ?? 0m);

    // The lines of the page for this data: every metric, except the ones that are only for a visitor who has a value (dollars) in some month of the page.
    public static IEnumerable<KpiMetric> Visible(MyKpiData data)
    {
        var periods = new[] { data.Current, data.Previous, data.LastYear }.Concat(data.Trend ?? []).ToList();
        return Metrics.Where(m => !m.OnlyWhenUsed || periods.Any(p => (m.Value(p) ?? 0) != 0));
    }

    // "12" for a count, "83.3%" for a rate, "—" when there is nothing to measure.
    public static string Format(KpiMetric metric, double? value) =>
        value is not { } v ? "—" : metric.Kind == KpiKind.Rate ? v.ToString("0.#", CultureInfo.InvariantCulture) + "%" : metric.Kind == KpiKind.Money ? v.ToString("N0", CultureInfo.InvariantCulture) : metric.Kind == KpiKind.MoneyCents ? v.ToString("N2", CultureInfo.InvariantCulture) : v.ToString("0", CultureInfo.InvariantCulture);

    // The change from `baseValue` to `current`: "+12.5%" for a count (of the earlier month), "-3.2 pt" for a rate (percentage points), "—" when it cannot be worked out (no earlier
    // value, or a count that was 0). Better is true when the change is the good direction, false when it is the bad one, null when there is none.
    public static (string Text, bool? Better) Change(KpiMetric metric, double? current, double? baseValue)
    {
        if (current is not { } now || baseValue is not { } before || (metric.Kind != KpiKind.Rate && before == 0)) return ("—", null);
        var diff = metric.Kind != KpiKind.Rate ? (now - before) / before * 100 : now - before;
        var rounded = Math.Round(diff, 1, MidpointRounding.AwayFromZero);
        var text = (rounded > 0 ? "+" : "") + rounded.ToString("0.#", CultureInfo.InvariantCulture) + (metric.Kind != KpiKind.Rate ? "%" : " pt");
        return (text, rounded == 0 ? null : (rounded > 0) == metric.HigherIsBetter);
    }
}
