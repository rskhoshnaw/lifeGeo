using LifeDrop.Domain.Common;

namespace LifeDrop.Domain.Visits;

public class Visit : AggregateRoot<VisitId>
{
    public TenantId TenantId { get; private set; }
    public UserId UserId { get; private set; }
    public CustomerId CustomerId { get; private set; }
    public VisitStatus Status { get; private set; }
    public string? ExceptionReason { get; private set; }
    public DateOnly ScheduleDate { get; private set; }
    public VisitType Type { get; private set; } = VisitType.Sales;
    public VisitPriority Priority { get; private set; } = VisitPriority.Normal;
    public TimeOnly? PlannedTime { get; private set; }
    public bool IsUnplanned { get; private set; }
    public string? UnplannedReason { get; private set; }

    public const int MaxUnplannedReasonLength = 500;

    // Product decision: either exception outcome may occur at any active stage
    // (Planned, EnRoute, Arrived, InVisit). A plan can fail before departure,
    // and a visit can be cancelled even after work has started. Requiring a
    // reason preserves the context; role permission is enforced by the server.
    // Completed, Failed and Cancelled are terminal and cannot transition again.

    private Visit(VisitId id, TenantId tenantId, UserId userId, CustomerId customerId, DateOnly scheduleDate) : base(id)
    {
        TenantId = tenantId;
        UserId = userId;
        CustomerId = customerId;
        Status = VisitStatus.Planned;
        ScheduleDate = scheduleDate;
    }

    public static Visit Create(TenantId tenantId, UserId userId, CustomerId customerId, DateOnly scheduleDate)
    {
        if (tenantId.IsEmpty) throw new ArgumentException("TenantId is required.", nameof(tenantId));
        if (userId.IsEmpty) throw new ArgumentException("UserId is required.", nameof(userId));
        if (customerId.IsEmpty) throw new ArgumentException("CustomerId is required.", nameof(customerId));
        if (scheduleDate == default) throw new ArgumentException("ScheduleDate is required.", nameof(scheduleDate));

        return new Visit(VisitId.New(), tenantId, userId, customerId, scheduleDate);
    }

    // Planned visit with planning details (plan step 1.2).
    public static Visit Create(TenantId tenantId, UserId userId, CustomerId customerId, DateOnly scheduleDate,
        VisitType type, VisitPriority priority, TimeOnly? plannedTime)
    {
        var visit = Create(tenantId, userId, customerId, scheduleDate);
        visit.Type = type;
        visit.Priority = priority;
        visit.PlannedTime = plannedTime;
        return visit;
    }

    // Visit without a prior plan (PRD §5.1): allowed only with permission and a recorded reason.
    public static Visit CreateUnplanned(TenantId tenantId, UserId userId, CustomerId customerId, DateOnly scheduleDate,
        VisitType type, string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length > MaxUnplannedReasonLength)
            throw new ArgumentException("A reason of 1-500 characters is required for an unplanned visit.", nameof(reason));

        var visit = Create(tenantId, userId, customerId, scheduleDate, type, VisitPriority.Normal, null);
        visit.IsUnplanned = true;
        visit.UnplannedReason = reason.Trim();
        return visit;
    }

    public static Visit Load(VisitId id, TenantId tenantId, UserId userId, CustomerId customerId, DateOnly scheduleDate, VisitStatus status, string? exceptionReason)
    {
        var visit = new Visit(id, tenantId, userId, customerId, scheduleDate);
        visit.Status = status;
        visit.ExceptionReason = exceptionReason;
        return visit;
    }

    public void MarkEnRoute()
    {
        if (Status != VisitStatus.Planned)
            throw new InvalidOperationException($"Cannot transition to {VisitStatus.EnRoute} from {Status}.");

        Status = VisitStatus.EnRoute;
    }

    public void MarkArrived()
    {
        if (Status != VisitStatus.EnRoute)
            throw new InvalidOperationException($"Cannot transition to {VisitStatus.Arrived} from {Status}.");

        Status = VisitStatus.Arrived;
    }

    public void StartVisit()
    {
        if (Status != VisitStatus.Arrived)
            throw new InvalidOperationException($"Cannot transition to {VisitStatus.InVisit} from {Status}.");

        Status = VisitStatus.InVisit;
    }

    public void Complete()
    {
        if (Status != VisitStatus.InVisit)
            throw new InvalidOperationException($"Cannot transition to {VisitStatus.Completed} from {Status}.");

        Status = VisitStatus.Completed;
    }

    public void Cancel(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("A reason is required to cancel a visit.", nameof(reason));

        if (Status == VisitStatus.Completed || Status == VisitStatus.Failed || Status == VisitStatus.Cancelled)
            throw new InvalidOperationException($"Cannot cancel a visit that is already {Status}.");

        Status = VisitStatus.Cancelled;
        ExceptionReason = reason;
    }

    public void Fail(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("A reason is required to fail a visit.", nameof(reason));

        if (Status == VisitStatus.Completed || Status == VisitStatus.Failed || Status == VisitStatus.Cancelled)
            throw new InvalidOperationException($"Cannot fail a visit that is already {Status}.");

        Status = VisitStatus.Failed;
        ExceptionReason = reason;
    }
}
