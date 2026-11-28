using System.Collections.Generic;
using System.Linq;
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

    // Plan step 5.73: samples, money received and approved returns are summed per currency in the total, and a result saved before they existed (no such fields) still reads and adds up.
    [Fact]
    public void TheNewCountsAddUpPerCurrencyAndAnOlderSavedResultWithoutThemStillReads()
    {
        var a = Counts(0, 0, 0, 0, 0, 0, 0, 0, 0) with { Samples = 2, PaymentsConfirmed = [new("IQD", 700m), new("USD", 20m)], PaymentsWaiting = [new("IQD", 300m)], ReturnsApproved = 1, ReturnTotals = [new("IQD", 250m)] };
        var b = Counts(0, 0, 0, 0, 0, 0, 0, 0, 0) with { Samples = 1, PaymentsConfirmed = [new("IQD", 100m)], ReturnsApproved = 2, ReturnTotals = [new("IQD", 50m), new("USD", 5m)] };
        var oldResult = System.Text.Json.JsonSerializer.Deserialize<KpiCounts>("{\"visitsPlanned\":4,\"visitsCompleted\":2,\"visitsFailed\":0,\"delayBase\":0,\"delayedVisits\":0,\"newCustomers\":0,\"approvedNewCustomers\":0,\"orders\":0,\"ordersDelivered\":0,\"ordersDeliveryFailed\":0,\"orderTotals\":[],\"followUpBase\":0,\"followUpsOverdue\":0}",
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
        Assert.Equal((4, 0, 0), (oldResult.VisitsPlanned, oldResult.Samples, oldResult.ReturnsApproved));
        Assert.Null(oldResult.PaymentsConfirmed);
        var total = KpiReport.Sum(new List<KpiCounts> { a, b, oldResult });
        Assert.Equal((3, 3), (total.Samples, total.ReturnsApproved));
        Assert.Equal(new[] { ("IQD", 800m), ("USD", 20m) }, total.PaymentsConfirmed!.Select(t => (t.Currency, t.Total)).ToArray());
        Assert.Equal(new[] { ("IQD", 300m) }, total.PaymentsWaiting!.Select(t => (t.Currency, t.Total)).ToArray());
        Assert.Equal(new[] { ("IQD", 300m), ("USD", 5m) }, total.ReturnTotals!.Select(t => (t.Currency, t.Total)).ToArray());
    }

    // Plan step 5.74: net sales = orders minus approved returns per currency; a currency that has only returns is negative; a total adds the nets of its users.
    [Fact]
    public void NetSalesAreOrdersMinusApprovedReturnsPerCurrency()
    {
        var net = KpiReport.Net([new("IQD", 3000m), new("USD", 5m)], [new("IQD", 250m), new("EUR", 2m)]);
        Assert.Equal(new[] { ("EUR", -2m), ("IQD", 2750m), ("USD", 5m) }, net.Select(t => (t.Currency, t.Total)).ToArray());
        Assert.Equal(new[] { ("IQD", 3000m) }, KpiReport.Net([new("IQD", 3000m)], null).Select(t => (t.Currency, t.Total)).ToArray());
        var a = Counts(0, 0, 0, 0, 0, 0, 0, 0, 0) with { NetSales = [new("IQD", 100m)] };
        var b = Counts(0, 0, 0, 0, 0, 0, 0, 0, 0) with { NetSales = [new("IQD", -30m), new("USD", 1m)] };
        Assert.Equal(new[] { ("IQD", 70m), ("USD", 1m) }, KpiReport.Sum(new List<KpiCounts> { a, b }).NetSales!.Select(t => (t.Currency, t.Total)).ToArray());
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
