using System;
using System.Threading;
using System.Threading.Tasks;
using LifeDrop.Domain.Common;
using LifeDrop.Domain.Visits;

namespace LifeDrop.Application.Visits;

public enum CreateVisitResult { Created, Forbidden, InvalidData, UserNotFound, CustomerNotFound }

// VisitType: pharmacy | doctor | wholesaler | other (default other). Priority: high | normal | low (default normal).
// PlannedTime: local time of day in Asia/Baghdad, optional.
public sealed record CreateVisitRequest(Guid AssignedUserId, Guid CustomerId, DateOnly ScheduleDate,
    string? VisitType = null, string? Priority = null, TimeOnly? PlannedTime = null);

public sealed record CreateUnplannedVisitRequest(Guid CustomerId, string Reason, string? VisitType = null);

public interface IVisitStore
{
    Task<(CreateVisitResult Result, Visit? Visit)> CreateAsync(TenantId tenantId, UserId actorId, Visit visit, CancellationToken cancellationToken);
    Task<System.Collections.Generic.List<VisitDto>> ListAssignedVisitsAsync(TenantId tenantId, UserId assignedUserId, DateOnly scheduleDate, CancellationToken cancellationToken);
    Task<Visit?> GetAsync(TenantId tenantId, VisitId visitId, CancellationToken cancellationToken);
    Task<bool> TryUpdateStatusAsync(TenantId tenantId, VisitId visitId, UserId expectedUserId, VisitStatus expectedStatus, VisitStatus newStatus, string? exceptionReason, CancellationToken cancellationToken);
}

public sealed class CreateVisit(IVisitStore store, LifeDrop.Application.Customers.ICustomerStore customers, LifeDrop.Application.Roles.IPermissionStore permissions)
{
    public async Task<(CreateVisitResult Result, Guid? VisitId)> ExecuteAsync(TenantId tenantId, UserId actorId, CreateVisitRequest request, CancellationToken cancellationToken)
    {
        if (tenantId.IsEmpty || actorId.IsEmpty) return (CreateVisitResult.Forbidden, null);
        if (request.AssignedUserId == Guid.Empty || request.CustomerId == Guid.Empty || request.ScheduleDate == default)
            return (CreateVisitResult.InvalidData, null);

        // Data scope of visits.write (plan step 0.6b): own = only for oneself; region = only for users in the
        // actor's region; the customer must be visible within the same scope.
        var scope = await permissions.GetAccessScopeAsync(tenantId, actorId, LifeDrop.Application.Roles.PermissionKeys.VisitsWrite, cancellationToken);
        if (scope is null) return (CreateVisitResult.Forbidden, null);

        var assigneeId = new UserId(request.AssignedUserId);
        if (scope.Scope != LifeDrop.Application.Roles.DataScope.Tenant && assigneeId != actorId)
        {
            if (scope.Scope == LifeDrop.Application.Roles.DataScope.Own) return (CreateVisitResult.Forbidden, null);
            var (assigneeExists, assigneeRegion) = await permissions.GetUserRegionAsync(tenantId, assigneeId, cancellationToken);
            if (!assigneeExists) return (CreateVisitResult.UserNotFound, null);
            if (scope.RegionId is null || assigneeRegion != scope.RegionId) return (CreateVisitResult.Forbidden, null);
        }

        if (await customers.GetByIdAsync(tenantId, new CustomerId(request.CustomerId), scope, cancellationToken) is null)
            return (CreateVisitResult.CustomerNotFound, null);

        if (!VisitPlanningContract.TryParseType(request.VisitType, out var visitType) ||
            !VisitPlanningContract.TryParsePriority(request.Priority, out var priority))
            return (CreateVisitResult.InvalidData, null);

        var visit = Visit.Create(tenantId, new UserId(request.AssignedUserId), new CustomerId(request.CustomerId), request.ScheduleDate,
            visitType, priority, request.PlannedTime);
        
        var (result, storedVisit) = await store.CreateAsync(tenantId, actorId, visit, cancellationToken);
        
        if (result == CreateVisitResult.Created && storedVisit is not null)
        {
            return (result, storedVisit.Id.Value);
        }
        
        return (result, null);
    }
}

// Unplanned visit (PRD §5.1): the field user records a visit for themself, today (Iraq local date),
// only with visits.create_unplanned and a reason; the customer must be visible within that permission's scope.
public sealed class CreateUnplannedVisit(IVisitStore store, LifeDrop.Application.Customers.ICustomerStore customers,
    LifeDrop.Application.Roles.IPermissionStore permissions, TimeProvider clock)
{
    public async Task<(CreateVisitResult Result, Guid? VisitId)> ExecuteAsync(TenantId tenantId, UserId actorId, CreateUnplannedVisitRequest request, CancellationToken cancellationToken)
    {
        if (tenantId.IsEmpty || actorId.IsEmpty) return (CreateVisitResult.Forbidden, null);
        var scope = await permissions.GetAccessScopeAsync(tenantId, actorId, LifeDrop.Application.Roles.PermissionKeys.VisitsCreateUnplanned, cancellationToken);
        if (scope is null) return (CreateVisitResult.Forbidden, null);

        if (request.CustomerId == Guid.Empty || !VisitPlanningContract.TryParseType(request.VisitType, out var visitType))
            return (CreateVisitResult.InvalidData, null);

        Visit visit;
        try
        {
            visit = Visit.CreateUnplanned(tenantId, actorId, new CustomerId(request.CustomerId), IraqCalendar.Today(clock), visitType, request.Reason);
        }
        catch (ArgumentException)
        {
            return (CreateVisitResult.InvalidData, null);
        }

        if (await customers.GetByIdAsync(tenantId, new CustomerId(request.CustomerId), scope, cancellationToken) is null)
            return (CreateVisitResult.CustomerNotFound, null);

        var (result, stored) = await store.CreateAsync(tenantId, actorId, visit, cancellationToken);
        return (result, result == CreateVisitResult.Created ? stored?.Id.Value : null);
    }
}

// "Today" for Iraqi plans is the local date in Asia/Baghdad (UTC+3, no daylight saving), not the UTC date (PRD §11).
public static class IraqCalendar
{
    private static readonly TimeZoneInfo Zone = Resolve();

    public static DateOnly Today(TimeProvider clock) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), Zone).DateTime);

    private static TimeZoneInfo Resolve()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Asia/Baghdad"); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.CreateCustomTimeZone("Asia/Baghdad", TimeSpan.FromHours(3), "Asia/Baghdad", "Asia/Baghdad"); }
    }
}

// Daily plan of the caller (plan step 1.3, PRD §5.1, §11): only the caller's visits; without a date the day is
// "today" in Asia/Baghdad; a day without visits is an empty list. Ordered by planned time (unscheduled last),
// then priority, then creation.
public sealed class ListAssignedVisits(IVisitStore store, TimeProvider clock)
{
    public async Task<System.Collections.Generic.List<VisitDto>?> ExecuteAsync(TenantId tenantId, UserId actorId, DateOnly? scheduleDate, CancellationToken cancellationToken)
    {
        if (tenantId.IsEmpty || actorId.IsEmpty) return null;
        return await store.ListAssignedVisitsAsync(tenantId, actorId, scheduleDate ?? IraqCalendar.Today(clock), cancellationToken);
    }
}

public enum MarkVisitResult { Success, NotFound, Forbidden, InvalidState, Conflict, InvalidData }

public sealed class MarkVisitEnRoute(IVisitStore store)
{
    public async Task<MarkVisitResult> ExecuteAsync(TenantId tenantId, UserId actorId, Guid visitId, CancellationToken cancellationToken)
    {
        if (tenantId.IsEmpty || actorId.IsEmpty || visitId == Guid.Empty) return MarkVisitResult.NotFound;
        
        var visit = await store.GetAsync(tenantId, new VisitId(visitId), cancellationToken);
        if (visit is null) return MarkVisitResult.NotFound;

        if (visit.UserId != actorId) return MarkVisitResult.Forbidden; // Employee can only mark their own visit

        var expectedStatus = visit.Status;
        try
        {
            visit.MarkEnRoute();
        }
        catch (InvalidOperationException)
        {
            return MarkVisitResult.InvalidState;
        }

        var success = await store.TryUpdateStatusAsync(tenantId, visit.Id, actorId, expectedStatus, visit.Status, visit.ExceptionReason, cancellationToken);
        if (!success) return MarkVisitResult.Conflict;

        return MarkVisitResult.Success;
    }
}

public sealed class MarkVisitArrived(IVisitStore store)
{
    public async Task<MarkVisitResult> ExecuteAsync(TenantId tenantId, UserId actorId, Guid visitId, CancellationToken cancellationToken)
    {
        if (tenantId.IsEmpty || actorId.IsEmpty || visitId == Guid.Empty) return MarkVisitResult.NotFound;
        
        var visit = await store.GetAsync(tenantId, new VisitId(visitId), cancellationToken);
        if (visit is null) return MarkVisitResult.NotFound;

        if (visit.UserId != actorId) return MarkVisitResult.Forbidden;

        var expectedStatus = visit.Status;
        try
        {
            visit.MarkArrived();
        }
        catch (InvalidOperationException)
        {
            return MarkVisitResult.InvalidState;
        }

        var success = await store.TryUpdateStatusAsync(tenantId, visit.Id, actorId, expectedStatus, visit.Status, visit.ExceptionReason, cancellationToken);
        if (!success) return MarkVisitResult.Conflict;

        return MarkVisitResult.Success;
    }
}

public sealed class MarkVisitInVisit(IVisitStore store)
{
    public async Task<MarkVisitResult> ExecuteAsync(TenantId tenantId, UserId actorId, Guid visitId, CancellationToken cancellationToken)
    {
        if (tenantId.IsEmpty || actorId.IsEmpty || visitId == Guid.Empty) return MarkVisitResult.NotFound;
        
        var visit = await store.GetAsync(tenantId, new VisitId(visitId), cancellationToken);
        if (visit is null) return MarkVisitResult.NotFound;

        if (visit.UserId != actorId) return MarkVisitResult.Forbidden;

        var expectedStatus = visit.Status;
        try
        {
            visit.StartVisit();
        }
        catch (InvalidOperationException)
        {
            return MarkVisitResult.InvalidState;
        }

        var success = await store.TryUpdateStatusAsync(tenantId, visit.Id, actorId, expectedStatus, visit.Status, visit.ExceptionReason, cancellationToken);
        if (!success) return MarkVisitResult.Conflict;

        return MarkVisitResult.Success;
    }
}

public sealed class MarkVisitCompleted(IVisitStore store)
{
    public async Task<MarkVisitResult> ExecuteAsync(TenantId tenantId, UserId actorId, Guid visitId, CancellationToken cancellationToken)
    {
        if (tenantId.IsEmpty || actorId.IsEmpty || visitId == Guid.Empty) return MarkVisitResult.NotFound;
        
        var visit = await store.GetAsync(tenantId, new VisitId(visitId), cancellationToken);
        if (visit is null) return MarkVisitResult.NotFound;

        if (visit.UserId != actorId) return MarkVisitResult.Forbidden;

        var expectedStatus = visit.Status;
        try
        {
            visit.Complete();
        }
        catch (InvalidOperationException)
        {
            return MarkVisitResult.InvalidState;
        }

        var success = await store.TryUpdateStatusAsync(tenantId, visit.Id, actorId, expectedStatus, visit.Status, visit.ExceptionReason, cancellationToken);
        if (!success) return MarkVisitResult.Conflict;

        return MarkVisitResult.Success;
    }
}
public sealed class MarkVisitFailed(IVisitStore store, LifeDrop.Application.Roles.IPermissionStore permissions)
{
    public async Task<MarkVisitResult> ExecuteAsync(TenantId tenantId, UserId actorId, Guid visitId, string reason, CancellationToken cancellationToken)
    {
        if (tenantId.IsEmpty || actorId.IsEmpty || visitId == Guid.Empty) return MarkVisitResult.NotFound;
        
        if (!await permissions.HasPermissionAsync(tenantId, actorId, "visits.fail", cancellationToken))
            return MarkVisitResult.Forbidden;

        var visit = await store.GetAsync(tenantId, new VisitId(visitId), cancellationToken);
        if (visit is null) return MarkVisitResult.NotFound;

        if (visit.UserId != actorId) return MarkVisitResult.Forbidden;

        var expectedStatus = visit.Status;
        try
        {
            visit.Fail(reason);
        }
        catch (ArgumentException)
        {
            return MarkVisitResult.InvalidData;
        }
        catch (InvalidOperationException)
        {
            return MarkVisitResult.InvalidState;
        }

        var success = await store.TryUpdateStatusAsync(tenantId, visit.Id, actorId, expectedStatus, visit.Status, visit.ExceptionReason, cancellationToken);
        if (!success) return MarkVisitResult.Conflict;

        return MarkVisitResult.Success;
    }
}
