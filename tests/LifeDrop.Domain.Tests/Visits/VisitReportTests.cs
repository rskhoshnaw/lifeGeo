using LifeDrop.Domain.Visits;
using Xunit;

namespace LifeDrop.Domain.Tests.Visits;

public class VisitReportTests
{
    [Theory]
    [InlineData("products_presented", VisitOutcome.ProductsPresented)]
    [InlineData("order_intent", VisitOutcome.OrderIntent)]
    [InlineData("follow_up_needed", VisitOutcome.FollowUpNeeded)]
    [InlineData("no_interest", VisitOutcome.NoInterest)]
    [InlineData("other", VisitOutcome.Other)]
    [InlineData("order_placed", VisitOutcome.OrderPlaced)]
    public void TryCreate_ValidOutcome_RoundTripsContract(string outcome, VisitOutcome expected)
    {
        Assert.True(VisitReport.TryCreate(outcome, "  Met the pharmacist  ", null, out var report));
        Assert.Equal(expected, report!.Outcome);
        Assert.Equal(outcome, report.OutcomeContract);
        Assert.Equal("Met the pharmacist", report.Note);
    }

    [Theory]
    [InlineData(null, "Valid note")]
    [InlineData("order_confirmed", "Valid note")]
    [InlineData("Products_Presented", "Valid note")]
    [InlineData("other", null)]
    [InlineData("other", "  ")]
    [InlineData("other", "ok")]
    [InlineData("other", "...!!!")]
    public void TryCreate_InvalidOutcomeOrNote_Fails(string? outcome, string? note)
    {
        Assert.False(VisitReport.TryCreate(outcome, note, null, out var report));
        Assert.Null(report);
    }

    [Fact]
    public void TryCreate_TooLongNote_Fails() =>
        Assert.False(VisitReport.TryCreate("other", new string('a', 2001), null, out _));

    [Fact]
    public void TryCreate_UnknownContractVersion_Fails() =>
        Assert.False(VisitReport.TryCreate("other", "Valid note", 2, out _));

    [Fact]
    public void TryCreate_ArabicOrKurdishNote_IsAccepted() =>
        Assert.True(VisitReport.TryCreate("other", "زيارة ناجحة", 1, out _));
}
