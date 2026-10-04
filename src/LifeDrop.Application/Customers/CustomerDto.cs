using LifeDrop.Domain.Common;

namespace LifeDrop.Application.Customers;

public sealed record CustomerDto(
    Guid Id,
    Guid TenantId,
    string Type,
    string Code,
    string Name,
    string Status,
    DateTimeOffset CreatedAt,
    double? Latitude = null,
    double? Longitude = null,
    string? LocationSource = null,
    DateTimeOffset? LocationUpdatedAt = null,
    int GeofenceRadiusMeters = CustomerDto.DefaultGeofenceRadiusMeters
)
{
    // PRD §6.1: default check-in radius when the customer has no own radius.
    public const int DefaultGeofenceRadiusMeters = 20;
}
