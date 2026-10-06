using System;

namespace LifeDrop.Application.Visits;

public sealed record VisitDto(
    Guid Id,
    Guid TenantId,
    Guid UserId,
    Guid CustomerId,
    string Status,
    string? ExceptionReason,
    DateOnly ScheduleDate,
    DateTimeOffset CreatedAt,
    string VisitType,
    string Priority,
    TimeOnly? PlannedTime,
    bool IsUnplanned,
    string? UnplannedReason,
    // Customer display fields for the daily plan (plan step 1.10).
    string? CustomerName = null,
    string? CustomerCode = null,
    // Planning board (plan step 1.18): the assignee's email, so the panel can show who the visit belongs to.
    string? AssigneeEmail = null,
    // Flexible planning (plan step 1.23): manual position in the day's plan (null = not ordered), how often the visit
    // was moved to another day (with the last reason) and whether the day/time fits the customer's accepted visit
    // windows: "none" (the customer has no windows), "within" or "outside".
    int? SortOrder = null,
    int PostponeCount = 0,
    string? PostponeReason = null,
    string WindowStatus = "none",
    // Priority the manager gave the customer (plan step 1.26): high | normal | low (not the priority of this visit).
    string CustomerPriority = "normal"
);
