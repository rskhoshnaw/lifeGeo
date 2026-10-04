using System;
using System.Linq;
using LifeDrop.Application.Tracking;
using Xunit;

namespace LifeDrop.Application.Tests.Tracking;

// Coverage rules for the real-device tracking test (plan step 1.13): >= 90 % of the expected 30 s points and no
// gap over 5 minutes, counting from the shift start and up to its end.
public class ShiftSummaryTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 5, 5, 0, 0, TimeSpan.Zero);
    private static ShiftDto Shift => new(Guid.NewGuid(), Guid.NewGuid(), Start, Start.AddMinutes(10));

    private static DateTimeOffset[] Every30s(int count, int offsetSeconds = 30) =>
        Enumerable.Range(0, count).Select(i => Start.AddSeconds(offsetSeconds + i * 30)).ToArray();

    [Fact]
    public void FullCoverageMeetsTarget()
    {
        var summary = ShiftTracking.Summarize(Shift, Every30s(20), Start.AddMinutes(10));
        Assert.Equal((20, 20, 100.0, 30, true), (summary.PointCount, summary.ExpectedPoints, summary.CoveragePercent, summary.MaxGapSeconds, summary.MeetsTarget));
    }

    [Fact]
    public void NinetyPercentWithShortGapsMeetsTarget()
    {
        var points = Every30s(20).Where((_, i) => i is not (5 or 12)).ToArray(); // 18 of 20, two 60 s gaps
        var summary = ShiftTracking.Summarize(Shift, points, Start.AddMinutes(10));
        Assert.Equal((90.0, 60, true), (summary.CoveragePercent, summary.MaxGapSeconds, summary.MeetsTarget));
    }

    [Fact]
    public void LowCoverageFails()
    {
        var summary = ShiftTracking.Summarize(Shift, Every30s(17), Start.AddMinutes(10)); // 85 %
        Assert.Equal(85.0, summary.CoveragePercent);
        Assert.False(summary.MeetsTarget);
    }

    [Fact]
    public void LongGapFailsEvenWithEnoughPoints()
    {
        // 20 points over 20 minutes, but none between minute 2 and minute 8
        var shift = new ShiftDto(Guid.NewGuid(), Guid.NewGuid(), Start, Start.AddMinutes(10));
        var points = Every30s(4).Concat(Enumerable.Range(0, 16).Select(i => Start.AddMinutes(8).AddSeconds(i * 7))).ToArray();
        var summary = ShiftTracking.Summarize(shift, points, Start.AddMinutes(10));
        Assert.Equal(360, summary.MaxGapSeconds);
        Assert.False(summary.MeetsTarget);
    }

    [Fact]
    public void GapsAtStartAndEndCount()
    {
        var late = ShiftTracking.Summarize(Shift, [Start.AddMinutes(6)], Start.AddMinutes(10));
        Assert.Equal(360, late.MaxGapSeconds);

        var silentEnd = ShiftTracking.Summarize(Shift, Every30s(8), Start.AddMinutes(10)); // last point at 4:00
        Assert.Equal(360, silentEnd.MaxGapSeconds);

        var none = ShiftTracking.Summarize(Shift, [], Start.AddMinutes(10));
        Assert.Equal((0, 0.0, 600, false), (none.PointCount, none.CoveragePercent, none.MaxGapSeconds, none.MeetsTarget));
    }

    [Fact]
    public void VeryShortShiftExpectsAtLeastOnePoint()
    {
        var summary = ShiftTracking.Summarize(Shift, [Start.AddSeconds(5)], Start.AddSeconds(10));
        Assert.Equal((1, 100.0), (summary.ExpectedPoints, summary.CoveragePercent));
    }
}
