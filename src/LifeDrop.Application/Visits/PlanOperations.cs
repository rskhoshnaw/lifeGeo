using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LifeDrop.Application.Roles;
using LifeDrop.Domain.Common;
using LifeDrop.Domain.Visits;

namespace LifeDrop.Application.Visits;

// VisitIds: the whole day's visits of one assignee in the wanted order. AssignedUserId defaults to the caller.
public sealed record ReorderPlanRequest(DateOnly ScheduleDate, IReadOnlyList<Guid> VisitIds, Guid? AssignedUserId = null);

// ScheduleDate: the new day (today or later, Asia/Baghdad). Reason: optional, up to 500 characters
// (for example "customer not available").
public sealed record PostponeVisitRequest(DateOnly ScheduleDate, string? Reason = null);

// Flexible planning (plan step 1.23, owner request 2026-10-05): the person who owns the plan, and managers and
// supervisors within their data scope, may reorder a day and move a visit to another day. A field user needs
// visits.plan for their own plan; everyone else needs visits.write and the assignee inside their data scope.
public sealed class PlanVisits(IVisitStore store, IPermissionStore permissions, TimeProvider clock)
{
    public const int MaxOrderedVisits = 500;
    public const int MaxReasonLength = 500;

    public async Task<ManageVisitResult> ReorderAsync(TenantId tenantId, UserId actorId, ReorderPlanRequest request, CancellationToken cancellationToken)
    {
        if (tenantId.IsEmpty || actorId.IsEmpty) return ManageVisitResult.Forbidden;
        var ids = request.VisitIds;
        if (ids is null || ids.Count == 0 || ids.Count > MaxOrderedVisits || request.ScheduleDate == default ||
            ids.Any(id => id == Guid.Empty) || ids.Distinct().Count() != ids.Count)
            return ManageVisitResult.InvalidData;

        var assignee = request.AssignedUserId is { } id && id != Guid.Empty ? new UserId(id) : actorId;
        var denied = await AuthorizeAsync(tenantId, actorId, assignee, cancellationToken);
        if (denied is not null) return denied.Value;

        return await store.TryReorderAsync(tenantId, actorId, assignee, request.ScheduleDate, ids, cancellationToken)
            ? ManageVisitResult.Success : ManageVisitResult.Conflict;
    }

    public async Task<ManageVisitResult> PostponeAsync(TenantId tenantId, UserId actorId, Guid visitId, PostponeVisitRequest request, CancellationToken cancellationToken)
    {
        if (tenantId.IsEmpty || actorId.IsEmpty) return ManageVisitResult.Forbidden;
        if (visitId == Guid.Empty) return ManageVisitResult.NotFound;
        var reason = string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason.Trim();
        if (reason is { Length: > MaxReasonLength }) return ManageVisitResult.InvalidData;

        var visit = await store.GetAsync(tenantId, new VisitId(visitId), cancellationToken);
        if (visit is null) return ManageVisitResult.NotFound;
        var denied = await AuthorizeAsync(tenantId, actorId, visit.UserId, cancellationToken);
        if (denied is not null) return denied == ManageVisitResult.Forbidden ? ManageVisitResult.Forbidden : ManageVisitResult.NotFound;

        var expectedStatus = visit.Status;
        try { visit.Postpone(request.ScheduleDate, IraqCalendar.Today(clock)); }
        catch (ArgumentException) { return ManageVisitResult.InvalidData; }
        catch (InvalidOperationException) { return ManageVisitResult.InvalidState; }

        return await store.TryPostponeAsync(tenantId, actorId, visit.Id, visit.UserId, expectedStatus, request.ScheduleDate, reason, cancellationToken)
            ? ManageVisitResult.Success : ManageVisitResult.Conflict;
    }

    // Null when allowed. Forbidden: the caller has neither permission. NotFound: the assignee is outside the caller's scope.
    private async Task<ManageVisitResult?> AuthorizeAsync(TenantId tenantId, UserId actorId, UserId assignee, CancellationToken cancellationToken)
    {
        var write = await permissions.GetAccessScopeAsync(tenantId, actorId, PermissionKeys.VisitsWrite, cancellationToken);
        if (write is not null)
        {
            var (exists, region) = await permissions.GetUserRegionAsync(tenantId, assignee, cancellationToken);
            if (exists && ManageVisit.IsUserInScope(write, assignee, region)) return null;
        }

        var plan = await permissions.GetAccessScopeAsync(tenantId, actorId, PermissionKeys.VisitsPlan, cancellationToken);
        if (plan is not null && assignee == actorId) return null;

        return write is null && plan is null ? ManageVisitResult.Forbidden : ManageVisitResult.NotFound;
    }
}
