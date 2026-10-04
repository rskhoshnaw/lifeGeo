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
    DateTimeOffset CreatedAt
);
