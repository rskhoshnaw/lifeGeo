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
        Assert.Equal(new double?[] { 10, 8, 80, null, null, 3, 4, null, null }, MyKpi.Metrics.Select(m => m.Value(period)).ToArray());
    }
}
