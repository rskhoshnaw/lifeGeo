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
    int GeofenceRadiusMeters = CustomerDto.DefaultGeofenceRadiusMeters,
    // Plan step 1.15 (D11): region and approval of customers registered outside the rep's region.
    Guid? RegionId = null,
    string ApprovalStatus = "approved",
    // Customer profile (plan step 1.23-b): contact details and the place in the reference list.
    string? Phone = null,
    string? Address = null,
    Guid? CityId = null,
    Guid? NeighbourhoodId = null,
    Guid? StreetId = null
)
{
    // PRD §6.1: default check-in radius when the customer has no own radius.
    public const int DefaultGeofenceRadiusMeters = 20;
}
