using Xunit;

namespace LifeDrop.Mobile.Visits.Tests;

public class TargetProgressTests
{
    [Theory]
    [InlineData(100, 80, 80)]
    [InlineData(100, 0, 0)]
    [InlineData(100, 130, 130)]
    [InlineData(3, 1, 33)]
    [InlineData(8, 5, 63)]
    public void Percent_IsTheWholePercentOfTheTarget(int target, int actual, int expected) => Assert.Equal(expected, TargetProgress.Percent(target, actual));

    [Fact]
    public void Percent_IsNullWithoutATarget()
    {
        Assert.Null(TargetProgress.Percent(null, 10));
        Assert.Null(TargetProgress.Percent(0, 10));
    }

    [Fact]
    public void Fill_NeverPassesTheBar()
    {
        Assert.Equal(0.5, TargetProgress.Fill(100, 50));
        Assert.Equal(1.0, TargetProgress.Fill(100, 250));
        Assert.Equal(0.0, TargetProgress.Fill(null, 50));
    }

    [Fact]
    public void Months_GoBackTwoYearsAndForwardToThisMonth()
    {
        var today = new DateOnly(2026, 10, 20);
        Assert.False(TargetProgress.CanGoNext(new DateOnly(2026, 10, 1), today));
        Assert.True(TargetProgress.CanGoNext(new DateOnly(2026, 9, 1), today));
        Assert.True(TargetProgress.CanGoBack(new DateOnly(2024, 11, 1), today));
        Assert.False(TargetProgress.CanGoBack(new DateOnly(2024, 10, 1), today));
        Assert.Equal(new DateOnly(2026, 9, 1), TargetProgress.Shift(new DateOnly(2026, 10, 17), -1));
        Assert.Equal("2026-01-01", TargetProgress.MonthPath(new DateOnly(2026, 1, 31)));
    }
}
