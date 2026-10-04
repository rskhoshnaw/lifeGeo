using System;
using System.Threading;
using System.Threading.Tasks;
using LifeDrop.Domain.Common;
using LifeDrop.Domain.Visits;

namespace LifeDrop.Application.Visits;

public enum CreateVisitResult { Created, Forbidden, InvalidData, UserNotFound, CustomerNotFound, CustomerNotApproved }

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

    // Conditional updates with their audit row in the same transaction (plan step 1.4). False when the visit
    // changed since it was read (other assignee or status).
    Task<bool> TryReassignAsync(TenantId tenantId, UserId actorId, VisitId visitId, UserId expectedUserId, UserId newUserId, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
    Task<bool> TryCancelAsync(TenantId tenantId, UserId actorId, VisitId visitId, UserId expectedUserId, VisitStatus expectedStatus, string reason, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    // Visit report (plan step 1.6): saved only while the visit is InVisit and assigned to the actor, with audit.
    Task<bool> TrySaveReportAsync(TenantId tenantId, UserId actorId, VisitId visitId, VisitReport report, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
    Task<VisitReportDto?> GetReportAsync(TenantId tenantId, VisitId visitId, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    // Check-in/out evidence (plan steps 1.7, 1.8). Distance in metres from the customer's location to the fix and
    // the effective radius; null when the customer has no location.
    Task<(double DistanceMeters, int RadiusMeters)?> MeasureFromCustomerAsync(TenantId tenantId, CustomerId customerId, GpsFix fix, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
    // True when an accepted event of this kind with this idempotency key exists for the visit (a network retry).
    Task<bool> HasAcceptedEventAsync(TenantId tenantId, VisitId visitId, string kind, string idempotencyKey, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
    // Moves the visit from expectedStatus to newStatus for its assignee and stores the accepted event and audit row
    // in one transaction. False when the visit changed meanwhile.
    Task<bool> TryAcceptLocationEventAsync(TenantId tenantId, UserId actorId, VisitId visitId, VisitStatus expectedStatus, VisitStatus newStatus,
        LocationEvent locationEvent, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
    // Stores a rejected attempt and its audit row.
    Task RecordRejectedLocationEventAsync(TenantId tenantId, UserId actorId, VisitId visitId, LocationEvent locationEvent, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
    // Offline check (decision D5): does the user's tracking path have a point within maxDistance of the customer
    // between from and to?
    Task<bool> HasTrackingPointNearCustomerAsync(TenantId tenantId, UserId userId, CustomerId customerId, DateTimeOffset from, DateTimeOffset to,
        double maxDistanceMeters, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    // Status history of a visit, oldest first (plan step 1.9).
    Task<System.Collections.Generic.List<VisitStatusChangeDto>> ListStatusHistoryAsync(TenantId tenantId, VisitId visitId, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
}

// FromStatus is null for the creation row; ActorId is null only for rows imported without an actor.
public sealed record VisitStatusChangeDto(Guid? ActorId, string? FromStatus, string ToStatus, string? Reason, DateTimeOffset OccurredAt);

public sealed record LocationEvent(string Kind, GpsFix Fix, Guid DeviceId, double? DistanceMeters, int? RadiusMeters,
    LocationRejection? Rejection, string? IdempotencyKey, bool Offline = false, long? ElapsedMs = null,
    IReadOnlyList<string>? ReviewReasons = null);

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

        var customer = await customers.GetByIdAsync(tenantId, new CustomerId(request.CustomerId), scope, cancellationToken);
        if (customer is null) return (CreateVisitResult.CustomerNotFound, null);
        // Plan step 1.15 (D11): no visits for a customer still waiting for approval, or rejected.
        if (customer.ApprovalStatus != "approved") return (CreateVisitResult.CustomerNotApproved, null);

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

        var customer = await customers.GetByIdAsync(tenantId, new CustomerId(request.CustomerId), scope, cancellationToken);
        if (customer is null) return (CreateVisitResult.CustomerNotFound, null);
        // Plan step 1.15 (D11): no visits for a customer still waiting for approval, or rejected.
        if (customer.ApprovalStatus != "approved") return (CreateVisitResult.CustomerNotApproved, null);

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

public sealed record ReassignVisitRequest(Guid AssignedUserId);
public sealed record CancelVisitRequest(string Reason);

public enum ManageVisitResult { Success, NotFound, Forbidden, InvalidState, InvalidData, Conflict, UserNotFound }

// Manager actions on a visit (plan step 1.4, PRD §5.1, §5.2). The visit must be within the actor's data scope
// for the permission (otherwise it is reported as not found); a new assignee must also be within that scope.
public sealed class ManageVisit(IVisitStore store, LifeDrop.Application.Roles.IPermissionStore permissions)
{
    public async Task<ManageVisitResult> ReassignAsync(TenantId tenantId, UserId actorId, Guid visitId, ReassignVisitRequest request, CancellationToken cancellationToken)
    {
        var (scope, visit, error) = await LoadInScopeAsync(tenantId, actorId, visitId, LifeDrop.Application.Roles.PermissionKeys.VisitsWrite, cancellationToken);
        if (error is not null) return error.Value;

        var newUserId = new UserId(request.AssignedUserId);
        if (newUserId.IsEmpty) return ManageVisitResult.InvalidData;
        var (exists, newUserRegion) = await permissions.GetUserRegionAsync(tenantId, newUserId, cancellationToken);
        if (!exists) return ManageVisitResult.UserNotFound;
        if (!IsUserInScope(scope!, newUserId, newUserRegion)) return ManageVisitResult.Forbidden;

        var previousUserId = visit!.UserId;
        try { visit.Reassign(newUserId); }
        catch (ArgumentException) { return ManageVisitResult.InvalidData; }
        catch (InvalidOperationException) { return ManageVisitResult.InvalidState; }

        return await store.TryReassignAsync(tenantId, actorId, visit.Id, previousUserId, newUserId, cancellationToken)
            ? ManageVisitResult.Success : ManageVisitResult.Conflict;
    }

    public async Task<ManageVisitResult> CancelAsync(TenantId tenantId, UserId actorId, Guid visitId, CancelVisitRequest request, CancellationToken cancellationToken)
    {
        var (_, visit, error) = await LoadInScopeAsync(tenantId, actorId, visitId, LifeDrop.Application.Roles.PermissionKeys.VisitsCancel, cancellationToken);
        if (error is not null) return error.Value;

        var expectedStatus = visit!.Status;
        try { visit.Cancel(request.Reason?.Trim()); }
        catch (ArgumentException) { return ManageVisitResult.InvalidData; }
        catch (InvalidOperationException) { return ManageVisitResult.InvalidState; }
        if (visit.ExceptionReason!.Length > Visit.MaxUnplannedReasonLength) return ManageVisitResult.InvalidData;

        return await store.TryCancelAsync(tenantId, actorId, visit.Id, visit.UserId, expectedStatus, visit.ExceptionReason, cancellationToken)
            ? ManageVisitResult.Success : ManageVisitResult.Conflict;
    }

    private async Task<(LifeDrop.Application.Roles.AccessScope? Scope, Visit? Visit, ManageVisitResult? Error)> LoadInScopeAsync(
        TenantId tenantId, UserId actorId, Guid visitId, string permission, CancellationToken cancellationToken)
    {
        if (tenantId.IsEmpty || actorId.IsEmpty) return (null, null, ManageVisitResult.Forbidden);
        var scope = await permissions.GetAccessScopeAsync(tenantId, actorId, permission, cancellationToken);
        if (scope is null) return (null, null, ManageVisitResult.Forbidden);
        if (visitId == Guid.Empty) return (scope, null, ManageVisitResult.NotFound);

        var visit = await store.GetAsync(tenantId, new VisitId(visitId), cancellationToken);
        if (visit is null) return (scope, null, ManageVisitResult.NotFound);
        var (_, assigneeRegion) = await permissions.GetUserRegionAsync(tenantId, visit.UserId, cancellationToken);
        return IsUserInScope(scope, visit.UserId, assigneeRegion)
            ? (scope, visit, null)
            : (scope, null, ManageVisitResult.NotFound);
    }

    private static bool IsUserInScope(LifeDrop.Application.Roles.AccessScope scope, UserId userId, Guid? userRegion) => scope.Scope switch
    {
        LifeDrop.Application.Roles.DataScope.Tenant => true,
        LifeDrop.Application.Roles.DataScope.Region => userId == scope.ActorId || (scope.RegionId is not null && userRegion == scope.RegionId),
        _ => userId == scope.ActorId
    };
}

// Outcome: products_presented | order_intent | follow_up_needed | no_interest | other. ContractVersion: 1 (default).
public sealed record SaveVisitReportRequest(string? Outcome, string? Note, int? ContractVersion = null);
public sealed record VisitReportDto(Guid VisitId, string Outcome, string Note, int ContractVersion, Guid SubmittedBy,
    DateTimeOffset SubmittedAt, DateTimeOffset UpdatedAt);

// Visit report (plan step 1.6, PRD §5.3): written by the assigned user while InVisit; editable until check-out.
// Readable by the assignee and by users whose visits.write scope covers the assignee.
public sealed class VisitReports(IVisitStore store, LifeDrop.Application.Roles.IPermissionStore permissions)
{
    public async Task<ManageVisitResult> SaveAsync(TenantId tenantId, UserId actorId, Guid visitId, SaveVisitReportRequest request, CancellationToken cancellationToken)
    {
        if (tenantId.IsEmpty || actorId.IsEmpty) return ManageVisitResult.Forbidden;
        var visit = visitId == Guid.Empty ? null : await store.GetAsync(tenantId, new VisitId(visitId), cancellationToken);
        if (visit is null) return ManageVisitResult.NotFound;
        if (visit.UserId != actorId) return ManageVisitResult.Forbidden;
        if (visit.Status != VisitStatus.InVisit) return ManageVisitResult.InvalidState;
        if (!VisitReport.TryCreate(request.Outcome, request.Note, request.ContractVersion, out var report))
            return ManageVisitResult.InvalidData;

        return await store.TrySaveReportAsync(tenantId, actorId, visit.Id, report!, cancellationToken)
            ? ManageVisitResult.Success : ManageVisitResult.Conflict;
    }

    public async Task<(ManageVisitResult Result, VisitReportDto? Report)> GetAsync(TenantId tenantId, UserId actorId, Guid visitId, CancellationToken cancellationToken)
    {
        if (tenantId.IsEmpty || actorId.IsEmpty) return (ManageVisitResult.Forbidden, null);
        var visit = visitId == Guid.Empty ? null : await store.GetAsync(tenantId, new VisitId(visitId), cancellationToken);
        if (visit is null) return (ManageVisitResult.NotFound, null);

        if (!await CanReadAsync(permissions, tenantId, actorId, visit, cancellationToken)) return (ManageVisitResult.NotFound, null);

        var report = await store.GetReportAsync(tenantId, visit.Id, cancellationToken);
        return report is null ? (ManageVisitResult.NotFound, null) : (ManageVisitResult.Success, report);
    }

    // Status history (plan step 1.9): same readers as the report.
    public async Task<(ManageVisitResult Result, System.Collections.Generic.List<VisitStatusChangeDto>? History)> GetHistoryAsync(
        TenantId tenantId, UserId actorId, Guid visitId, CancellationToken cancellationToken)
    {
        if (tenantId.IsEmpty || actorId.IsEmpty) return (ManageVisitResult.Forbidden, null);
        var visit = visitId == Guid.Empty ? null : await store.GetAsync(tenantId, new VisitId(visitId), cancellationToken);
        if (visit is null || !await CanReadAsync(permissions, tenantId, actorId, visit, cancellationToken)) return (ManageVisitResult.NotFound, null);
        return (ManageVisitResult.Success, await store.ListStatusHistoryAsync(tenantId, visit.Id, cancellationToken));
    }

    // The assignee, or a user whose visits.write scope covers the assignee.
    private static async Task<bool> CanReadAsync(LifeDrop.Application.Roles.IPermissionStore permissions, TenantId tenantId, UserId actorId,
        Visit visit, CancellationToken cancellationToken)
    {
        if (visit.UserId == actorId) return true;
        var scope = await permissions.GetAccessScopeAsync(tenantId, actorId, LifeDrop.Application.Roles.PermissionKeys.VisitsWrite, cancellationToken);
        if (scope is null) return false;
        var (_, assigneeRegion) = await permissions.GetUserRegionAsync(tenantId, visit.UserId, cancellationToken);
        return scope.Scope == LifeDrop.Application.Roles.DataScope.Tenant ||
               (scope.Scope == LifeDrop.Application.Roles.DataScope.Region && scope.RegionId is not null && assigneeRegion == scope.RegionId);
    }
}

// IsOffline: saved on the phone without internet and sent later (decision D5); ElapsedSinceCaptureMs is the time
// since capture measured by the phone's monotonic clock, sent with offline attempts.
public sealed record CheckInRequest(double Latitude, double Longitude, double AccuracyMeters, DateTimeOffset CapturedAt, bool IsMocked = false,
    bool IsOffline = false, long? ElapsedSinceCaptureMs = null);

// Body of a successful offline check-in/out: whether a manager must review it, and why.
public sealed record OfflineCheckpointResponse(bool ReviewRequired, IReadOnlyList<string> ReviewReasons);

public enum LocationCheckResult { Success, NotFound, Forbidden, DeviceRequired, InvalidState, InvalidData, CustomerLocationMissing, ReportRequired, Rejected, Conflict }

// Check-in and check-out (plan steps 1.7, 1.8; PRD §5.2, §6, decision D2): only the assignee, from an approved
// bound device, with a fresh, accurate, non-mocked fix inside the customer's geofence. Check-out also requires a
// saved report. Every attempt is recorded and audited.
public sealed class VisitCheckpoint(IVisitStore store, TimeProvider clock)
{
    public const string CheckInKind = "check_in";
    public const string CheckOutKind = "check_out";
    public const int MaxIdempotencyKeyLength = 100;

    // Offline attempts that were accepted: whether review is needed (null for online attempts and replays).
    public OfflineCheckpointResponse? LastOfflineOutcome { get; private set; }

    public Task<(LocationCheckResult Result, LocationRejection? Rejection)> CheckInAsync(TenantId tenantId, UserId actorId, Guid? deviceId,
        Guid visitId, CheckInRequest request, string? idempotencyKey, CancellationToken cancellationToken) =>
        RunAsync(CheckInKind, VisitStatus.Arrived, VisitStatus.InVisit, requireReport: false,
            tenantId, actorId, deviceId, visitId, request, idempotencyKey, cancellationToken);

    public Task<(LocationCheckResult Result, LocationRejection? Rejection)> CheckOutAsync(TenantId tenantId, UserId actorId, Guid? deviceId,
        Guid visitId, CheckInRequest request, string? idempotencyKey, CancellationToken cancellationToken) =>
        RunAsync(CheckOutKind, VisitStatus.InVisit, VisitStatus.Completed, requireReport: true,
            tenantId, actorId, deviceId, visitId, request, idempotencyKey, cancellationToken);

    private async Task<(LocationCheckResult Result, LocationRejection? Rejection)> RunAsync(string kind, VisitStatus from, VisitStatus to, bool requireReport,
        TenantId tenantId, UserId actorId, Guid? deviceId, Guid visitId, CheckInRequest request, string? idempotencyKey, CancellationToken cancellationToken)
    {
        if (tenantId.IsEmpty || actorId.IsEmpty) return (LocationCheckResult.Forbidden, null);
        var visit = visitId == Guid.Empty ? null : await store.GetAsync(tenantId, new VisitId(visitId), cancellationToken);
        if (visit is null) return (LocationCheckResult.NotFound, null);
        if (visit.UserId != actorId) return (LocationCheckResult.Forbidden, null);
        if (deviceId is null || deviceId == Guid.Empty) return (LocationCheckResult.DeviceRequired, null);
        if (idempotencyKey is { Length: 0 or > MaxIdempotencyKeyLength }) return (LocationCheckResult.InvalidData, null);

        if (idempotencyKey is not null && visit.Status == to &&
            await store.HasAcceptedEventAsync(tenantId, visit.Id, kind, idempotencyKey, cancellationToken))
            return (LocationCheckResult.Success, null);
        if (visit.Status != from) return (LocationCheckResult.InvalidState, null);
        if (requireReport && await store.GetReportAsync(tenantId, visit.Id, cancellationToken) is null)
            return (LocationCheckResult.ReportRequired, null);

        var fix = new GpsFix(request.Latitude, request.Longitude, request.AccuracyMeters, request.CapturedAt, request.IsMocked);
        if (!fix.IsWellFormed) return (LocationCheckResult.InvalidData, null);

        var measured = await store.MeasureFromCustomerAsync(tenantId, visit.CustomerId, fix, cancellationToken);
        if (measured is null) return (LocationCheckResult.CustomerLocationMissing, null);

        var (distance, radius) = measured.Value;
        var now = clock.GetUtcNow();
        if (request.ElapsedSinceCaptureMs is < 0) return (LocationCheckResult.InvalidData, null);
        var rejection = request.IsOffline
            ? OfflineCheckPolicy.Evaluate(fix, now, distance, radius)
            : LocationCheckPolicy.Evaluate(fix, now, distance, radius);

        List<string>? reviewReasons = null;
        if (request.IsOffline && rejection is null)
        {
            reviewReasons = new List<string>();
            var elapsed = request.ElapsedSinceCaptureMs is { } ms ? TimeSpan.FromMilliseconds(ms) : (TimeSpan?)null;
            if (!OfflineCheckPolicy.ClockAgrees(fix.CapturedAt, now, elapsed))
                reviewReasons.Add(OfflineReviewReason.ClockMismatch.ToContract());
            if (!await store.HasTrackingPointNearCustomerAsync(tenantId, actorId, visit.CustomerId,
                    fix.CapturedAt - OfflineCheckPolicy.TrackingWindow, fix.CapturedAt + OfflineCheckPolicy.TrackingWindow,
                    radius + fix.AccuracyMeters + OfflineCheckPolicy.TrackingMarginMeters, cancellationToken))
                reviewReasons.Add(OfflineReviewReason.NoTrackingNearby.ToContract());
        }

        var locationEvent = new LocationEvent(kind, fix, deviceId.Value, distance, radius, rejection, idempotencyKey,
            request.IsOffline, request.ElapsedSinceCaptureMs, reviewReasons);
        if (rejection is not null)
        {
            await store.RecordRejectedLocationEventAsync(tenantId, actorId, visit.Id, locationEvent, cancellationToken);
            return (LocationCheckResult.Rejected, rejection);
        }

        if (!await store.TryAcceptLocationEventAsync(tenantId, actorId, visit.Id, from, to, locationEvent, cancellationToken))
            return (LocationCheckResult.Conflict, null);
        if (reviewReasons is not null) LastOfflineOutcome = new OfflineCheckpointResponse(reviewReasons.Count > 0, reviewReasons);
        return (LocationCheckResult.Success, null);
    }
}
