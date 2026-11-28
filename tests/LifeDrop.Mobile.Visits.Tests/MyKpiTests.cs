using LifeDrop.Mobile.Visits;
using Xunit;

namespace LifeDrop.Mobile.Visits.Tests;

// How the app shows the visitor's KPIs side by side (plan step 5.29): values, changes and whether a change is good.
public class MyKpiTests
{
    private static KpiMetric Metric(string id) => MyKpi.Metrics.Single(m => m.Id == id);

    private static MyKpiPeriod Period(int planned, int completed, double? completionRate) =>
        new("2026-11", new MyKpiCounts(planned, completed, 0, 0, 0, 3, 3, 4, 2, 0, null, 0, 0), new MyKpiRates(completionRate, null, null, null, null));

    [Fact]
    public void Values_are_counts_or_percentages_and_a_dash_when_there_is_nothing_to_measure()
    {
        var period = Period(10, 8, 80);
        Assert.Equal("10", MyKpi.Format(Metric("planned"), Metric("planned").Value(period)));
        Assert.Equal("80%", MyKpi.Format(Metric("completion"), Metric("completion").Value(period)));
        Assert.Equal("83.3%", MyKpi.Format(Metric("completion"), 83.33));
        Assert.Equal("—", MyKpi.Format(Metric("failure"), Metric("failure").Value(period)));
    }

    [Fact]
    public void A_count_changes_by_a_percentage_of_the_earlier_month_and_a_rate_by_percentage_points()
    {
        Assert.Equal(("+25%", (bool?)true), MyKpi.Change(Metric("completed"), 10, 8));
        Assert.Equal(("-50%", (bool?)false), MyKpi.Change(Metric("completed"), 5, 10));
        Assert.Equal(("+3.5 pt", (bool?)true), MyKpi.Change(Metric("completion"), 83.5, 80));
        Assert.Equal(("0 pt", (bool?)null), MyKpi.Change(Metric("completion"), 80, 80));
    }

    [Fact]
    public void For_failures_delays_and_overdue_follow_ups_less_is_better()
    {
        Assert.Equal(("+5 pt", (bool?)false), MyKpi.Change(Metric("failure"), 15, 10));
        Assert.Equal(("-4 pt", (bool?)true), MyKpi.Change(Metric("delay"), 6, 10));
        Assert.Equal(("-2 pt", (bool?)true), MyKpi.Change(Metric("followups"), 8, 10));
    }

    [Fact]
    public void Nothing_to_compare_shows_a_dash_without_a_verdict()
    {
        Assert.Equal(("—", (bool?)null), MyKpi.Change(Metric("planned"), 5, 0));          // an earlier count of 0: no percentage
        Assert.Equal(("—", (bool?)null), MyKpi.Change(Metric("completion"), null, 50));     // a month without a rate
        Assert.Equal(("—", (bool?)null), MyKpi.Change(Metric("completion"), 50, null));
        Assert.Equal(("+100%", (bool?)true), MyKpi.Change(Metric("orders"), 2, 1));
    }

    [Fact]
    public void Every_metric_has_its_own_text_key_and_reads_the_period()
    {
        Assert.Equal(MyKpi.Metrics.Count, MyKpi.Metrics.Select(m => m.TextKey).Distinct().Count());
        var period = Period(10, 8, 80);
        Assert.Equal(new double?[] { 10, 8, 80, null, null, 3, 4, null, null, 0, 0, 0, 0 }, MyKpi.Metrics.Select(m => m.Value(period)).ToArray());
    }

    // Plan steps 5.73 / 5.74: samples, money received, approved returns and net sales (in dinars) are metrics of the page too; money has thousands separators; an answer from an older server (none of them) reads 0.
    [Fact]
    public void SamplesMoneyReturnsAndNetSalesAreMetricsAndAnOlderAnswerReadsZero()
    {
        var counts = new MyKpiCounts(10, 8, 0, 0, 0, 3, 3, 4, 2, 0, [new("IQD", 3000m), new("USD", 5m)], 0, 0, 3, [new("IQD", 1250000m), new("USD", 20m)], [new("IQD", 300m)], 2, [new("IQD", 250m)], [new("IQD", 2750m), new("USD", 0m)]);
        var period = new MyKpiPeriod("2026-11", counts, new MyKpiRates(80, null, null, null, null));
        Assert.Equal("3", MyKpi.Format(Metric("samples"), Metric("samples").Value(period)));
        Assert.Equal("1,250,000", MyKpi.Format(Metric("moneyIqd"), Metric("moneyIqd").Value(period)));   // only the dinars, with thousands separators
        Assert.Equal("2", MyKpi.Format(Metric("returns"), Metric("returns").Value(period)));
        Assert.Equal("2,750", MyKpi.Format(Metric("netIqd"), Metric("netIqd").Value(period)));
        Assert.False(Metric("returns").HigherIsBetter);                                                // more returns is worse
        // money changes like a count: a percentage of the earlier month, and no change can be worked out from 0
        Assert.Equal(("+10%", (bool?)true), MyKpi.Change(Metric("moneyIqd"), 1100, 1000));
        Assert.Equal(("—", (bool?)null), MyKpi.Change(Metric("moneyIqd"), 1100, 0));
        // an older server sends none of the new fields: they read as 0, and net sales fall back to the orders
        var old = new MyKpiPeriod("2026-11", new MyKpiCounts(10, 8, 0, 0, 0, 3, 3, 4, 2, 0, [new("IQD", 3000m)], 0, 0), new MyKpiRates(null, null, null, null, null));
        Assert.Equal(("0", "0", "0", "3,000"), (MyKpi.Format(Metric("samples"), Metric("samples").Value(old)), MyKpi.Format(Metric("moneyIqd"), Metric("moneyIqd").Value(old)),
            MyKpi.Format(Metric("returns"), Metric("returns").Value(old)), MyKpi.Format(Metric("netIqd"), Metric("netIqd").Value(old))));
        // every metric has its text in the language files (the tests run in English)
        foreach (var metric in MyKpi.Metrics) Assert.False(string.IsNullOrWhiteSpace(LifeDrop.Mobile.Localization.Texts.Get(metric.TextKey)), metric.Id);
    }
}
