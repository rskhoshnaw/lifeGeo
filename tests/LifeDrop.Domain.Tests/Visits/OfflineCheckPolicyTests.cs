using LifeDrop.Domain.Visits;
using Xunit;

namespace LifeDrop.Domain.Tests.Visits;

// Decision D5: offline check-in/out accepted up to 24 h late with the same position rules; the phone's monotonic
// clock must agree with the GPS capture time, otherwise a manager reviews it.
public class OfflineCheckPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

    private static GpsFix Fix(TimeSpan age, double accuracy = 10, bool mocked = false) =>
        new(36.19, 44.01, accuracy, Now - age, mocked);

    [Theory]
    [InlineData(1)]
    [InlineData(60)]
    [InlineData(24 * 60)]
    public void AcceptsInsideGeofenceUpTo24Hours(int minutesOld) =>
        Assert.Null(OfflineCheckPolicy.Evaluate(Fix(TimeSpan.FromMinutes(minutesOld)), Now, 10, 30));

    [Fact]
    public void OlderThan24HoursIsExpired() =>
        Assert.Equal(LocationRejection.OfflineExpired, OfflineCheckPolicy.Evaluate(Fix(TimeSpan.FromHours(24).Add(TimeSpan.FromSeconds(1))), Now, 10, 30));

    [Fact]
    public void SamePositionRulesAsOnline()
    {
        Assert.Equal(LocationRejection.MockLocation, OfflineCheckPolicy.Evaluate(Fix(TimeSpan.FromHours(1), mocked: true), Now, 10, 30));
        Assert.Equal(LocationRejection.LowAccuracy, OfflineCheckPolicy.Evaluate(Fix(TimeSpan.FromHours(1), accuracy: 80), Now, 10, 30));
        Assert.Equal(LocationRejection.OutsideGeofence, OfflineCheckPolicy.Evaluate(Fix(TimeSpan.FromHours(1)), Now, 31, 30));
        Assert.Equal(LocationRejection.ClockSkew, OfflineCheckPolicy.Evaluate(Fix(TimeSpan.FromMinutes(-5)), Now, 10, 30));
    }

    [Fact]
    public void OnlineStillRequiresAFreshFix() =>
        Assert.Equal(LocationRejection.StaleFix, LocationCheckPolicy.Evaluate(Fix(TimeSpan.FromHours(1)), Now, 10, 30));

    [Theory]
    [InlineData(60, 60, true)]     // monotonic clock agrees
    [InlineData(60, 61.5, true)]   // within 2 minutes
    [InlineData(60, 63, false)]    // phone clock or GPS time disagrees
    [InlineData(60, 30, false)]
    public void ClockAgreement(int capturedMinutesAgo, double elapsedMinutes, bool agrees) =>
        Assert.Equal(agrees, OfflineCheckPolicy.ClockAgrees(Now.AddMinutes(-capturedMinutesAgo), Now, TimeSpan.FromMinutes(elapsedMinutes)));

    [Fact]
    public void MissingOrNegativeElapsedNeverAgrees()
    {
        Assert.False(OfflineCheckPolicy.ClockAgrees(Now.AddMinutes(-1), Now, null));
        Assert.False(OfflineCheckPolicy.ClockAgrees(Now.AddMinutes(-1), Now, TimeSpan.FromMinutes(-1)));
    }
}
