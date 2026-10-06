using System;
using LifeDrop.Application.Reports;
using Xunit;

namespace LifeDrop.Application.Tests;

// Relative report ranges and schedule times (plan step 3.2). Iraq time is UTC+3 all year. 5 October 2026 is a Monday.
public class SavedReportScheduleTests
{
    private static DateTimeOffset Utc(int month, int day, int hour, int minute = 0) => new(2026, month, day, hour, minute, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset MondayNineIraq = Utc(10, 5, 6);   // 09:00 in Baghdad

    [Theory]
    [InlineData(8, 10, 6, 5)]     // today's 08:00 is past: tomorrow 08:00 Iraq = 05:00 UTC
    [InlineData(12, 10, 5, 9)]    // today 12:00 Iraq = 09:00 UTC
    [InlineData(9, 10, 6, 6)]     // exactly now is not "after now": tomorrow
    public void DailyIsTheFirstTimeStrictlyAfterNow(int hour, int month, int day, int utcHour) =>
        Assert.Equal(Utc(month, day, utcHour), ReportSchedules.Next("daily", null, null, hour, MondayNineIraq));

    [Theory]
    [InlineData(1, 8, 10, 12, 5)]    // Monday 08:00 has passed: next Monday
    [InlineData(1, 10, 10, 5, 7)]    // Monday 10:00 is still ahead
    [InlineData(3, 8, 10, 7, 5)]     // Wednesday
    [InlineData(7, 8, 10, 11, 5)]    // Sunday (7)
    public void WeeklyUsesIsoWeekdays(int weekday, int hour, int month, int day, int utcHour) =>
        Assert.Equal(Utc(month, day, utcHour), ReportSchedules.Next("weekly", weekday, null, hour, MondayNineIraq));

    [Theory]
    [InlineData(5, 8, 11, 5)]     // the 5th at 08:00 has passed: next month
    [InlineData(20, 8, 10, 20)]
    [InlineData(1, 8, 11, 1)]
    public void MonthlyUsesTheDayOfMonthThisMonthOrTheNext(int dayOfMonth, int hour, int month, int day) =>
        Assert.Equal(Utc(month, day, 5), ReportSchedules.Next("monthly", null, dayOfMonth, hour, MondayNineIraq));

    [Fact]
    public void TheIraqDayDecidesNotTheUtcDay()
    {
        var oneAmIraq = Utc(10, 5, 22);   // 01:00 on 6 October in Baghdad
        Assert.Equal(Utc(10, 6, 5), ReportSchedules.Next("daily", null, null, 8, oneAmIraq));
        Assert.Equal(Utc(10, 6, 21), ReportSchedules.Next("daily", null, null, 0, oneAmIraq));   // midnight of 7 October Iraq time
    }

    [Fact]
    public void RelativeRangesAreIraqDaysEndingYesterdayExceptThisMonth()
    {
        var today = new DateOnly(2026, 10, 5);
        Assert.Equal((new DateOnly(2026, 10, 4), new DateOnly(2026, 10, 4)), ReportRanges.Resolve("yesterday", null, null, today));
        Assert.Equal((new DateOnly(2026, 9, 28), new DateOnly(2026, 10, 4)), ReportRanges.Resolve("last_7_days", null, null, today));
        Assert.Equal((new DateOnly(2026, 9, 5), new DateOnly(2026, 10, 4)), ReportRanges.Resolve("last_30_days", null, null, today));
        Assert.Equal((new DateOnly(2026, 10, 1), today), ReportRanges.Resolve("this_month", null, null, today));
        Assert.Equal((new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30)), ReportRanges.Resolve("last_month", null, null, today));
        Assert.Equal((new DateOnly(2025, 12, 1), new DateOnly(2025, 12, 31)), ReportRanges.Resolve("last_month", null, null, new DateOnly(2026, 1, 15)));
        Assert.Equal((new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 9)), ReportRanges.Resolve("fixed", new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 9), today));
    }
}
