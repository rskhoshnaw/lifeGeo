using LifeDrop.Domain.Common;
using LifeDrop.Domain.Visits;
using System;
using Xunit;

namespace LifeDrop.Domain.Tests.Visits;

public class VisitPlanningTests
{
    private readonly TenantId _tenantId = TenantId.New();
    private readonly UserId _userId = UserId.New();
    private readonly CustomerId _customerId = CustomerId.New();
    private readonly DateOnly _scheduleDate = new(2026, 1, 15);

    [Fact]
    public void Create_WithoutPlanningDetails_UsesDefaults()
    {
        var visit = Visit.Create(_tenantId, _userId, _customerId, _scheduleDate);

        Assert.Equal(VisitType.Sales, visit.Type);
        Assert.Equal(VisitPriority.Normal, visit.Priority);
        Assert.Null(visit.PlannedTime);
        Assert.False(visit.IsUnplanned);
        Assert.Null(visit.UnplannedReason);
    }

    [Fact]
    public void Create_WithPlanningDetails_StoresThem()
    {
        var visit = Visit.Create(_tenantId, _userId, _customerId, _scheduleDate, VisitType.Scientific, VisitPriority.High, new TimeOnly(9, 30));

        Assert.Equal(VisitType.Scientific, visit.Type);
        Assert.Equal(VisitPriority.High, visit.Priority);
        Assert.Equal(new TimeOnly(9, 30), visit.PlannedTime);
        Assert.Equal(VisitStatus.Planned, visit.Status);
    }

    [Fact]
    public void CreateUnplanned_WithReason_IsMarkedUnplannedWithTrimmedReason()
    {
        var visit = Visit.CreateUnplanned(_tenantId, _userId, _customerId, _scheduleDate, VisitType.FollowUp, "  Customer called  ");

        Assert.True(visit.IsUnplanned);
        Assert.Equal("Customer called", visit.UnplannedReason);
        Assert.Equal(VisitType.FollowUp, visit.Type);
        Assert.Equal(VisitStatus.Planned, visit.Status);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void CreateUnplanned_WithoutReason_Throws(string? reason)
    {
        Assert.Throws<ArgumentException>(() => Visit.CreateUnplanned(_tenantId, _userId, _customerId, _scheduleDate, VisitType.Sales, reason));
    }

    [Fact]
    public void CreateUnplanned_WithTooLongReason_Throws()
    {
        Assert.Throws<ArgumentException>(() => Visit.CreateUnplanned(_tenantId, _userId, _customerId, _scheduleDate, VisitType.Sales, new string('x', 501)));
    }

    [Theory]
    [InlineData("sales", VisitType.Sales)]
    [InlineData("scientific", VisitType.Scientific)]
    [InlineData("follow_up", VisitType.FollowUp)]
    [InlineData("other", VisitType.Other)]
    public void VisitType_ContractRoundTrips(string value, VisitType expected)
    {
        Assert.True(VisitPlanningContract.TryParseType(value, out var parsed));
        Assert.Equal(expected, parsed);
        Assert.Equal(value, parsed.ToContract());
    }

    [Theory]
    [InlineData("high", VisitPriority.High)]
    [InlineData("normal", VisitPriority.Normal)]
    [InlineData("low", VisitPriority.Low)]
    public void Priority_ContractRoundTrips(string value, VisitPriority expected)
    {
        Assert.True(VisitPlanningContract.TryParsePriority(value, out var parsed));
        Assert.Equal(expected, parsed);
        Assert.Equal(value, parsed.ToContract());
    }

    [Theory]
    [InlineData("Sales")]
    [InlineData("urgent")]
    [InlineData("")]
    public void Contract_RejectsUnknownValues(string value)
    {
        Assert.False(VisitPlanningContract.TryParseType(value, out _));
        Assert.False(VisitPlanningContract.TryParsePriority(value, out _));
    }
}
