using LifeDrop.Domain.Common;

namespace LifeDrop.Application.Customers;

public sealed record CustomerDto(
    Guid Id,
    Guid TenantId,
    string Type,
    string Code,
    string Name,
    string Status,
    DateTimeOffset CreatedAt
);
