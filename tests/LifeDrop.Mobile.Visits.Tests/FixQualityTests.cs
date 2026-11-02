using LifeDrop.Mobile.Tracking;
using Xunit;

namespace LifeDrop.Mobile.Visits.Tests;

public class FixQualityTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 2, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void KeepsAGoodFix()
    {
        Assert.True(new FixQuality().Keep(12, T0));
    }

    [Fact]
    public void DropsARoughFixWhileAGoodOneIsRecent()
    {
        var quality = new FixQuality();
        Assert.True(quality.Keep(10, T0));
        Assert.False(quality.Keep(400, T0.AddSeconds(60)));   // a tower position 400 m off: not sent
        Assert.True(quality.Keep(15, T0.AddSeconds(90)));
    }

    [Fact]
    public void KeepsRoughFixesWhenThereIsNothingBetter()
    {
        var quality = new FixQuality();
        Assert.True(quality.Keep(300, T0));                    // indoors: only rough positions exist
        Assert.True(quality.Keep(250, T0.AddSeconds(60)));
        Assert.True(quality.Keep(10, T0.AddSeconds(120)));
        Assert.True(quality.Keep(300, T0.AddMinutes(6)));      // the good fix is older than three minutes
    }

    [Fact]
    public void KeepsNoTwoFixesCloserThanTheMinimumGap()
    {
        var quality = new FixQuality();
        Assert.True(quality.Keep(10, T0));
        Assert.False(quality.Keep(8, T0.AddSeconds(5)));       // the same moment from a second source
        Assert.True(quality.Keep(8, T0.AddSeconds(25)));
    }

    [Fact]
    public void DropsAnInvalidAccuracyAndResetStartsOver()
    {
        var quality = new FixQuality();
        Assert.False(quality.Keep(0, T0));
        Assert.False(quality.Keep(double.NaN, T0));
        Assert.True(quality.Keep(10, T0));
        quality.Reset();
        Assert.True(quality.Keep(400, T0.AddSeconds(1)));
    }
}
