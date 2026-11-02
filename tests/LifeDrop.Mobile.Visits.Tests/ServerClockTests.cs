using LifeDrop.Mobile.Tracking;
using Xunit;

namespace LifeDrop.Mobile.Visits.Tests;

public class ServerClockTests
{
    private static readonly DateTimeOffset Phone = new(2026, 11, 2, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ACorrectPhoneIsNeverChanged()
    {
        var clock = new ServerClock();
        clock.Observe(Phone.AddSeconds(1), Phone);   // the header has a resolution of one second
        Assert.False(clock.IsSkewed);
        Assert.Equal(Phone, clock.ToServerTime(Phone));
    }

    [Fact]
    public void APhoneThatRunsBehindIsMovedForward()
    {
        var clock = new ServerClock();
        clock.Observe(Phone.AddMinutes(7), Phone);   // the phone is seven minutes slow
        Assert.True(clock.IsSkewed);
        Assert.Equal(Phone.AddMinutes(7), clock.ToServerTime(Phone));
    }

    [Fact]
    public void APhoneThatRunsAheadIsMovedBack()
    {
        var clock = new ServerClock();
        clock.Observe(Phone, Phone.AddHours(1));   // the phone is one hour fast
        Assert.Equal(Phone.AddMinutes(10), clock.ToServerTime(Phone.AddHours(1).AddMinutes(10)));
    }

    [Fact]
    public void AnAnswerWithoutADateChangesNothing()
    {
        var clock = new ServerClock();
        clock.Observe(Phone.AddMinutes(7), Phone);
        clock.Observe(null, Phone.AddMinutes(1));
        Assert.Equal(TimeSpan.FromMinutes(7), clock.Offset);
    }
}
