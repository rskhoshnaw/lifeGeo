using LifeDrop.Domain.Common;
using LifeDrop.Domain.Visits;
using System;
using Xunit;

namespace LifeDrop.Domain.Tests.Visits;

public class VisitStateMachineTests
{
    private readonly TenantId _tenantId = TenantId.New();
    private readonly UserId _userId = UserId.New();
    private readonly CustomerId _customerId = CustomerId.New();

    [Fact]
    public void Create_ValidInputs_ReturnsPlannedVisit()
    {
        var visit = Visit.Create(_tenantId, _userId, _customerId);

        Assert.Equal(VisitStatus.Planned, visit.Status);
        Assert.Equal(_tenantId, visit.TenantId);
        Assert.Equal(_userId, visit.UserId);
        Assert.Equal(_customerId, visit.CustomerId);
        Assert.Null(visit.ExceptionReason);
    }

    [Fact]
    public void FullHappyPath_TransitionsCorrectly()
    {
        var visit = Visit.Create(_tenantId, _userId, _customerId);
        
        visit.MarkEnRoute();
        Assert.Equal(VisitStatus.EnRoute, visit.Status);

        visit.MarkArrived();
        Assert.Equal(VisitStatus.Arrived, visit.Status);

        visit.StartVisit();
        Assert.Equal(VisitStatus.InVisit, visit.Status);

        visit.Complete();
        Assert.Equal(VisitStatus.Completed, visit.Status);
    }

    [Theory]
    [InlineData(VisitStatus.Planned, "MarkArrived")]
    [InlineData(VisitStatus.Planned, "StartVisit")]
    [InlineData(VisitStatus.Planned, "Complete")]
    [InlineData(VisitStatus.EnRoute, "StartVisit")]
    [InlineData(VisitStatus.EnRoute, "Complete")]
    [InlineData(VisitStatus.Arrived, "Complete")]
    public void InvalidJumps_ThrowInvalidOperationException(VisitStatus startStatus, string action)
    {
        // Arrange
        var visit = Visit.Create(_tenantId, _userId, _customerId);
        
        if (startStatus >= VisitStatus.EnRoute) visit.MarkEnRoute();
        if (startStatus >= VisitStatus.Arrived) visit.MarkArrived();
        if (startStatus >= VisitStatus.InVisit) visit.StartVisit();

        // Act & Assert
        Action act = action switch
        {
            "MarkEnRoute" => () => visit.MarkEnRoute(),
            "MarkArrived" => () => visit.MarkArrived(),
            "StartVisit" => () => visit.StartVisit(),
            "Complete" => () => visit.Complete(),
            _ => throw new NotImplementedException()
        };

        Assert.Throws<InvalidOperationException>(act);
    }

    [Fact]
    public void Cancel_WithValidReason_SetsStatusToCancelled()
    {
        var visit = Visit.Create(_tenantId, _userId, _customerId);
        
        visit.Cancel("Doctor unavailable");
        
        Assert.Equal(VisitStatus.Cancelled, visit.Status);
        Assert.Equal("Doctor unavailable", visit.ExceptionReason);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null)]
    public void Cancel_WithMissingReason_ThrowsArgumentException(string? reason)
    {
        var visit = Visit.Create(_tenantId, _userId, _customerId);
        
        Action act = () => visit.Cancel(reason);
        Assert.Throws<ArgumentException>(act);
    }

    [Fact]
    public void Fail_WithValidReason_SetsStatusToFailed()
    {
        var visit = Visit.Create(_tenantId, _userId, _customerId);
        visit.MarkEnRoute();
        
        visit.Fail("Car broke down");
        
        Assert.Equal(VisitStatus.Failed, visit.Status);
        Assert.Equal("Car broke down", visit.ExceptionReason);
    }

    [Theory]
    [InlineData(VisitStatus.Planned)]
    [InlineData(VisitStatus.EnRoute)]
    [InlineData(VisitStatus.Arrived)]
    [InlineData(VisitStatus.InVisit)]
    public void Cancel_FromAnyActiveStatus_IsAllowed(VisitStatus origin)
    {
        var visit = VisitAt(origin);

        visit.Cancel("Visit cancelled by an authorized actor");

        Assert.Equal(VisitStatus.Cancelled, visit.Status);
        Assert.Equal("Visit cancelled by an authorized actor", visit.ExceptionReason);
    }

    [Theory]
    [InlineData(VisitStatus.Planned)]
    [InlineData(VisitStatus.EnRoute)]
    [InlineData(VisitStatus.Arrived)]
    [InlineData(VisitStatus.InVisit)]
    public void Fail_FromAnyActiveStatus_IsAllowed(VisitStatus origin)
    {
        var visit = VisitAt(origin);

        visit.Fail("Visit could not be completed");

        Assert.Equal(VisitStatus.Failed, visit.Status);
        Assert.Equal("Visit could not be completed", visit.ExceptionReason);
    }

    private Visit VisitAt(VisitStatus origin)
    {
        var visit = Visit.Create(_tenantId, _userId, _customerId);
        if (origin is VisitStatus.EnRoute or VisitStatus.Arrived or VisitStatus.InVisit)
            visit.MarkEnRoute();
        if (origin is VisitStatus.Arrived or VisitStatus.InVisit)
            visit.MarkArrived();
        if (origin is VisitStatus.InVisit)
            visit.StartVisit();
        return visit;
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null)]
    public void Fail_WithMissingReason_ThrowsArgumentException(string? reason)
    {
        var visit = Visit.Create(_tenantId, _userId, _customerId);
        
        Action act = () => visit.Fail(reason);
        Assert.Throws<ArgumentException>(act);
    }

    [Fact]
    public void Cancel_WhenAlreadyCompleted_ThrowsInvalidOperationException()
    {
        var visit = Visit.Create(_tenantId, _userId, _customerId);
        visit.MarkEnRoute();
        visit.MarkArrived();
        visit.StartVisit();
        visit.Complete();

        Action act = () => visit.Cancel("Mistake");
        Assert.Throws<InvalidOperationException>(act);
    }

    [Fact]
    public void Fail_WhenAlreadyCancelled_ThrowsInvalidOperationException()
    {
        var visit = Visit.Create(_tenantId, _userId, _customerId);
        visit.Cancel("Patient postponed");

        Action act = () => visit.Fail("Could not reach");
        Assert.Throws<InvalidOperationException>(act);
    }
}
