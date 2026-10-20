using LifeDrop.Domain.Common;

namespace LifeDrop.Application.Customers;

// Id, kind (city|neighbourhood|street|alley) and name of one place on the way from the city to the customer.
public sealed record PlacePathItem(Guid Id, string Kind, string Name);

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
    // Customer profile (plan step 1.23-b): contact details and the most precise place the customer is known to be in;
    // PlacePath lists that place and the ones above it, from the city down.
    string? Phone = null,
    string? Address = null,
    Guid? PlaceId = null,
    IReadOnlyList<PlacePathItem>? PlacePath = null,
    // Priority set by a manager (plan step 1.26): high | normal | low.
    string Priority = "normal",
    // Plan step 4.15: the person to talk to, a second phone and a note.
    string? ContactName = null,
    string? Phone2 = null,
    string? Notes = null,
    // Plan step 4.27: doctor details (clinic, specialty key, cooperates, dealer, class A+|A|B|C).
    string? ClinicName = null,
    string? Specialty = null,
    bool? Cooperates = null,
    bool? Dealer = null,
    string? CustomerClass = null
)
{
    // PRD §6.1: default check-in radius when the customer has no own radius.
    public const int DefaultGeofenceRadiusMeters = 20;
}
