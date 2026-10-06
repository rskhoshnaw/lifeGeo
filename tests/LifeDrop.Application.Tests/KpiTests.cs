using System.Collections.Generic;
using LifeDrop.Application.Reports;
using Xunit;

namespace LifeDrop.Application.Tests;

// KPI arithmetic (plan step 2.12): a rate is numerator over denominator, nothing to measure is "no value", totals add numerators and denominators.
public class KpiTests
{
    private static KpiCounts Counts(int planned, int completed, int failed, int delayBase, int delayed, int delivered, int deliveryFailed, int followBase, int overdue, params CurrencyTotal[] totals) =>
        new(planned, completed, failed, delayBase, delayed, 0, 0, delivered + deliveryFailed, delivered, deliveryFailed, totals, followBase, overdue);

    [Fact]
    public void RatesAreNumeratorOverDenominatorWithOneDecimalAndNullWhenThereIsNothingToMeasure()
    {
        var rates = KpiReport.Rates(Counts(6, 3, 1, 3, 2, 1, 1, 3, 1));
        Assert.Equal((50.0, 16.7, 66.7, 50.0, 33.3), (rates.VisitCompletion, rates.VisitFailure, rates.Delay, rates.OrderDelivery, rates.FollowUpOverdue));
        var none = KpiReport.Rates(Counts(0, 0, 0, 0, 0, 0, 0, 0, 0));
        Assert.Equal(new double?[] { null, null, null, null, null }, new[] { none.VisitCompletion, none.VisitFailure, none.Delay, none.OrderDelivery, none.FollowUpOverdue });
    }

    [Fact]
    public void ATotalAddsNumeratorsAndDenominatorsInsteadOfAveragingRates()
    {
        // 1 of 1 (100 %) and 1 of 3 (33 %): the total is 2 of 4 = 50 %, not 66.7 %
        var total = KpiReport.Sum(new List<KpiCounts> { Counts(1, 1, 0, 0, 0, 0, 0, 0, 0), Counts(3, 1, 0, 0, 0, 0, 0, 0, 0) });
        Assert.Equal((4, 2), (total.VisitsPlanned, total.VisitsCompleted));
        Assert.Equal(50.0, KpiReport.Rates(total).VisitCompletion);
    }

    [Fact]
    public void MoneyIsAddedPerCurrencyAndNeverAcrossCurrencies()
    {
        var total = KpiReport.Sum(new List<KpiCounts>
        {
            Counts(0, 0, 0, 0, 0, 0, 0, 0, 0, new CurrencyTotal("IQD", 1000m), new CurrencyTotal("USD", 5m)),
            Counts(0, 0, 0, 0, 0, 0, 0, 0, 0, new CurrencyTotal("IQD", 2500m))
        });
        Assert.Equal(new[] { new CurrencyTotal("IQD", 3500m), new CurrencyTotal("USD", 5m) }, total.OrderTotals);
    }
}
