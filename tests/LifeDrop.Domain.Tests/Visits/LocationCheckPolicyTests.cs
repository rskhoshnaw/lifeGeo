using LifeDrop.Domain.Visits;
using System;
using Xunit;

namespace LifeDrop.Domain.Tests.Visits;

public class LocationCheckPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 9, 0, 0, TimeSpan.Zero);
    private static GpsFix Fix(double accuracy = 8, int secondsAgo = 10, bool mocked = false) =>
        new(36.19, 44.01, accuracy, Now.AddSeconds(-secondsAgo), mocked);

    [Fact]
    public void Evaluate_GoodFixInsideRadius_IsAccepted() =>
        Assert.Null(LocationCheckPolicy.Evaluate(Fix(), Now, distanceMeters: 12, radiusMeters: 20));

    [Fact]
    public void Evaluate_ExactlyOnRadius_IsAccepted() =>
        Assert.Null(LocationCheckPolicy.Evaluate(Fix(), Now, 20, 20));

    [Theory]
    [InlineData(20.01, 20, LocationRejection.OutsideGeofence)]
    [InlineData(500, 20, LocationRejection.OutsideGeofence)]
    public void Evaluate_OutsideRadius_IsRejected(double distance, int radius, LocationRejection expected) =>
        Assert.Equal(expected, LocationCheckPolicy.Evaluate(Fix(), Now, distance, radius));

    [Fact]
    public void Evaluate_LowAccuracy_IsRejected() =>
        Assert.Equal(LocationRejection.LowAccuracy, LocationCheckPolicy.Evaluate(Fix(accuracy: 50.5), Now, 5, 20));

    [Fact]
    public void Evaluate_StaleFix_IsRejected() =>
        Assert.Equal(LocationRejection.StaleFix, LocationCheckPolicy.Evaluate(Fix(secondsAgo: 121), Now, 5, 20));

    [Fact]
    public void Evaluate_FixFromTheFuture_IsRejected() =>
        Assert.Equal(LocationRejection.ClockSkew, LocationCheckPolicy.Evaluate(Fix(secondsAgo: -31), Now, 5, 20));

    [Fact]
    public void Evaluate_MockedFix_IsRejectedEvenWhenOtherwiseValid() =>
        Assert.Equal(LocationRejection.MockLocation, LocationCheckPolicy.Evaluate(Fix(mocked: true), Now, 5, 20));

    [Theory]
    [InlineData(91, 44, 8)]
    [InlineData(36, 181, 8)]
    [InlineData(0, 0, 8)]
    [InlineData(36, 44, 0)]
    [InlineData(double.NaN, 44, 8)]
    public void GpsFix_Malformed_IsNotWellFormed(double lat, double lon, double accuracy) =>
        Assert.False(new GpsFix(lat, lon, accuracy, Now, false).IsWellFormed);
}
