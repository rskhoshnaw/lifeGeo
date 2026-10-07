using System;
using System.Linq;
using LifeDrop.Application.Tracking;
using Xunit;

namespace LifeDrop.Application.Tests.Tracking;

// Coverage rules for the real-device tracking test: >= 90 % of the expected points (one per 3 minutes, owner decision 2026-10-07)
// and no gap over 15 minutes, counting from the shift start and up to its end.
public class ShiftSummaryTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 5, 5, 0, 0, TimeSpan.Zero);
    private static ShiftDto Shift => new(Guid.NewGuid(), Guid.NewGuid(), Start, Start.AddMinutes(60));

    private static DateTimeOffset[] Every3Min(int count) =>
        Enumerable.Range(1, count).Select(i => Start.AddMinutes(i * 3)).ToArray();

    [Fact]
    public void FullCoverageMeetsTarget()
    {
        var summary = ShiftTracking.Summarize(Shift, Every3Min(20), Start.AddMinutes(60));
        Assert.Equal((20, 20, 100.0, 180, true), (summary.PointCount, summary.ExpectedPoints, summary.CoveragePercent, summary.MaxGapSeconds, summary.MeetsTarget));
    }

    [Fact]
    public void NinetyPercentWithShortGapsMeetsTarget()
    {
        var points = Every3Min(20).Where((_, i) => i is not (5 or 12)).ToArray(); // 18 of 20, two 6-minute gaps
        var summary = ShiftTracking.Summarize(Shift, points, Start.AddMinutes(60));
        Assert.Equal((90.0, 360, true), (summary.CoveragePercent, summary.MaxGapSeconds, summary.MeetsTarget));
    }

    [Fact]
    public void LowCoverageFails()
    {
        var summary = ShiftTracking.Summarize(Shift, Every3Min(17), Start.AddMinutes(60)); // 85 %
        Assert.Equal(85.0, summary.CoveragePercent);
        Assert.False(summary.MeetsTarget);
    }

    [Fact]
    public void GapOfExactlyFifteenMinutesPassesAndLongerFails()
    {
        var ok = Every3Min(4).Concat(Enumerable.Range(0, 16).Select(i => Start.AddMinutes(27).AddSeconds(i * 120))).ToArray(); // 12 -> 27 min
        var okSummary = ShiftTracking.Summarize(Shift, ok, Start.AddMinutes(60));
        Assert.Equal(900, okSummary.MaxGapSeconds);
        Assert.True(okSummary.MeetsTarget);

        var bad = Every3Min(4).Concat(Enumerable.Range(0, 16).Select(i => Start.AddMinutes(28).AddSeconds(i * 120))).ToArray(); // 12 -> 28 min
        var badSummary = ShiftTracking.Summarize(Shift, bad, Start.AddMinutes(60));
        Assert.Equal(960, badSummary.MaxGapSeconds);
        Assert.Equal(100.0, badSummary.CoveragePercent);   // enough points, but the long gap alone fails it
        Assert.False(badSummary.MeetsTarget);
    }

    [Fact]
    public void GapsAtStartAndEndCount()
    {
        var late = ShiftTracking.Summarize(Shift, [Start.AddMinutes(16)], Start.AddMinutes(30));
        Assert.Equal(960, late.MaxGapSeconds);

        var silentEnd = ShiftTracking.Summarize(Shift, Every3Min(4), Start.AddMinutes(30)); // last point at 12:00
        Assert.Equal(1080, silentEnd.MaxGapSeconds);

        var none = ShiftTracking.Summarize(Shift, [], Start.AddMinutes(60));
        Assert.Equal((0, 0.0, 3600, false), (none.PointCount, none.CoveragePercent, none.MaxGapSeconds, none.MeetsTarget));
    }

    [Fact]
    public void VeryShortShiftExpectsAtLeastOnePoint()
    {
        var summary = ShiftTracking.Summarize(Shift, [Start.AddSeconds(5)], Start.AddSeconds(10));
        Assert.Equal((1, 100.0), (summary.ExpectedPoints, summary.CoveragePercent));
    }
}
