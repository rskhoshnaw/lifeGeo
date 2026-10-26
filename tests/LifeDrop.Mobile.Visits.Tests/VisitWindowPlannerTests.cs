using Xunit;

namespace LifeDrop.Mobile.Visits.Tests;

public class VisitWindowPlannerTests
{
    private static readonly TimeSpan Nine = TimeSpan.FromHours(9);
    private static readonly TimeSpan Five = TimeSpan.FromHours(17);

    [Fact]
    public void AnyDayAndAnyHour_SendsNothing() =>
        Assert.Empty(VisitWindowPlanner.Build(true, Array.Empty<int>(), true, Nine, Five).Windows);

    [Fact]
    public void ChosenDaysWithAnyHour_AreWholeDays()
    {
        var (result, windows) = VisitWindowPlanner.Build(false, new[] { 6, 0, 6 }, true, Nine, Five);
        Assert.Equal(VisitWindowPlanResult.Ok, result);
        Assert.Equal(new[] { new VisitWindowItem(0, "00:00:00", "23:59:00"), new VisitWindowItem(6, "00:00:00", "23:59:00") }, windows);
    }

    [Fact]
    public void AnyDayWithHours_IsTheSameHoursOnAllSevenDays()
    {
        var (result, windows) = VisitWindowPlanner.Build(true, Array.Empty<int>(), false, Nine, Five);
        Assert.Equal(VisitWindowPlanResult.Ok, result);
        Assert.Equal(7, windows.Count);
        Assert.All(windows, w => Assert.Equal(("09:00:00", "17:00:00"), (w.OpensAt, w.ClosesAt)));
        Assert.Equal(Enumerable.Range(0, 7), windows.Select(w => w.Weekday));
    }

    [Fact]
    public void ChosenDaysAndHours_AreSortedByDay()
    {
        var (_, windows) = VisitWindowPlanner.Build(false, new[] { 3, 1 }, false, Nine, Five);
        Assert.Equal(new[] { 1, 3 }, windows.Select(w => w.Weekday));
    }

    [Fact]
    public void NoDayChosen_IsRefused() =>
        Assert.Equal(VisitWindowPlanResult.NoDays, VisitWindowPlanner.Build(false, Array.Empty<int>(), true, Nine, Five).Result);

    [Theory]
    [InlineData(9, 9)]
    [InlineData(17, 9)]
    public void ClosingNotAfterOpening_IsRefused(int from, int to) =>
        Assert.Equal(VisitWindowPlanResult.BadHours, VisitWindowPlanner.Build(true, Array.Empty<int>(), false, TimeSpan.FromHours(from), TimeSpan.FromHours(to)).Result);
}
