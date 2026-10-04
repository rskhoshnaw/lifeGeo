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
    string? CustomerCode = null
);
