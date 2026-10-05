using LifeDrop.Domain.Common;
using LifeDrop.Domain.Visits;
using System;
using Xunit;

namespace LifeDrop.Domain.Tests.Visits;

public class VisitPostponeTests
{
    private static readonly DateOnly Today = new(2026, 10, 5);

    private static Visit NewVisit() => Visit.Create(TenantId.New(), UserId.New(), CustomerId.New(), Today.AddDays(1));

    [Fact]
    public void Postpone_PlannedVisit_MovesToTheNewDayAndStaysPlanned()
    {
        var visit = NewVisit();
        visit.Postpone(Today.AddDays(3), Today);
        Assert.Equal(Today.AddDays(3), visit.ScheduleDate);
        Assert.Equal(VisitStatus.Planned, visit.Status);
    }

    [Fact]
    public void Postpone_EnRouteVisit_GoesBackToPlanned()
    {
        var visit = NewVisit();
        visit.MarkEnRoute();
        visit.Postpone(Today, Today);
        Assert.Equal((Today, VisitStatus.Planned), (visit.ScheduleDate, visit.Status));
    }

    [Theory]
    [InlineData(-1)]   // a past day
    [InlineData(1)]    // the day it is already planned for (Today + 1)
    public void Postpone_PastOrSameDay_IsRejected(int offsetFromToday)
    {
        var visit = NewVisit();
        Assert.Throws<ArgumentException>(() => visit.Postpone(Today.AddDays(offsetFromToday), Today));
        Assert.Equal(Today.AddDays(1), visit.ScheduleDate);
    }

    [Fact]
    public void Postpone_AfterArrival_IsRejected()
    {
        var visit = NewVisit();
        visit.MarkEnRoute();
        visit.MarkArrived();
        Assert.Throws<InvalidOperationException>(() => visit.Postpone(Today.AddDays(5), Today));
        Assert.Equal(VisitStatus.Arrived, visit.Status);
    }

    [Fact]
    public void Postpone_FinishedVisit_IsRejected()
    {
        var visit = NewVisit();
        visit.Cancel("Not needed");
        Assert.Throws<InvalidOperationException>(() => visit.Postpone(Today.AddDays(5), Today));
    }
}
