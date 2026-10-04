using System.Collections.Generic;
using LifeDrop.Application.Roles;
using LifeDrop.Domain.Common;

namespace LifeDrop.Application.Customers;

public enum CreateCustomerResult { Created, DuplicateCode, Forbidden, InvalidData }

public sealed record CreateCustomerRequest(string Type, string Code, string Name);

public interface ICustomerStore
{
    // scope limits rows to the caller's data scope (plan step 0.6b); out-of-scope rows behave as not found.
    Task<CustomerDto?> GetByIdAsync(TenantId tenantId, CustomerId id, AccessScope scope, CancellationToken cancellationToken);
    Task<CreateCustomerResult> CreateAsync(TenantId tenantId, UserId actorId, CustomerId id, CreateCustomerRequest request, CancellationToken cancellationToken);
    Task<IReadOnlyList<CustomerDto>> ListAsync(TenantId tenantId, AccessScope scope, int limit, DateTimeOffset? cursorCreatedAt, Guid? cursorId, CancellationToken cancellationToken);

    // Location and geofence updates (plan step 1.5); false when the customer is not in the actor's scope.
    // Each writes its audit row in the same transaction.
    Task<bool> SetLocationAsync(TenantId tenantId, UserId actorId, CustomerId id, AccessScope scope,
        double latitude, double longitude, string source, double? accuracyMeters, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
    Task<bool> SetGeofenceRadiusAsync(TenantId tenantId, UserId actorId, CustomerId id, AccessScope scope,
        int? radiusMeters, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
}

public sealed class CreateCustomer(ICustomerStore store)
{
    public async Task<(CreateCustomerResult Result, CustomerId Id)> ExecuteAsync(TenantId tenantId, UserId actorId, CreateCustomerRequest request, CancellationToken cancellationToken)
    {
        if (tenantId.IsEmpty || actorId.IsEmpty) return (CreateCustomerResult.Forbidden, CustomerId.Empty);
        if (string.IsNullOrWhiteSpace(request.Type) || string.IsNullOrWhiteSpace(request.Code) || string.IsNullOrWhiteSpace(request.Name))
            return (CreateCustomerResult.InvalidData, CustomerId.Empty);

        var id = CustomerId.New();
        var result = await store.CreateAsync(tenantId, actorId, id, request, cancellationToken);
        return (result, id);
    }
}

public sealed class GetCustomer(ICustomerStore store)
{
    public Task<CustomerDto?> ExecuteAsync(TenantId tenantId, CustomerId id, AccessScope scope, CancellationToken cancellationToken)
    {
        if (tenantId.IsEmpty || id.IsEmpty) return Task.FromResult<CustomerDto?>(null);
        return store.GetByIdAsync(tenantId, id, scope, cancellationToken);
    }
}

public sealed class ListCustomers(ICustomerStore store)
{
    public async Task<(bool IsValid, IReadOnlyList<CustomerDto> Items)> ExecuteAsync(TenantId tenantId, AccessScope scope, int? limit, DateTimeOffset? cursorCreatedAt, Guid? cursorId, CancellationToken cancellationToken)
    {
        if (tenantId.IsEmpty) return (false, Array.Empty<CustomerDto>());
        
        var actualLimit = limit ?? 20;
        if (actualLimit <= 0 || actualLimit > 100) return (false, Array.Empty<CustomerDto>());
        if (cursorCreatedAt.HasValue != cursorId.HasValue) return (false, Array.Empty<CustomerDto>());

        var items = await store.ListAsync(tenantId, scope, actualLimit, cursorCreatedAt, cursorId, cancellationToken);
        return (true, items);
    }
}

// Source "gps": captured on site, AccuracyMeters (0 < a <= 100) required. Source "manual": placed by a person on a map.
public sealed record SetCustomerLocationRequest(double Latitude, double Longitude, string Source, double? AccuracyMeters = null);
// RadiusMeters 5-1000; null restores the default of 20 m.
public sealed record SetGeofenceRadiusRequest(int? RadiusMeters);

public enum CustomerUpdateResult { Success, Forbidden, NotFound, InvalidData }

// Customer location and geofence radius (plan step 1.5, PRD §6.1, §8). The customer must be within the actor's
// data scope for the permission; changes are audited.
public sealed class CustomerLocationService(ICustomerStore store, IPermissionStore permissions)
{
    public const double MaxGpsAccuracyMeters = 100;
    public const int MinRadiusMeters = 5;
    public const int MaxRadiusMeters = 1000;

    public async Task<CustomerUpdateResult> SetLocationAsync(TenantId tenantId, UserId actorId, Guid customerId, SetCustomerLocationRequest request, CancellationToken cancellationToken)
    {
        var scope = await ScopeAsync(tenantId, actorId, PermissionKeys.CustomersLocationWrite, cancellationToken);
        if (scope is null) return CustomerUpdateResult.Forbidden;
        if (!IsValidLocation(request)) return CustomerUpdateResult.InvalidData;

        return await store.SetLocationAsync(tenantId, actorId, new CustomerId(customerId), scope, request.Latitude, request.Longitude,
            request.Source, request.Source == "gps" ? request.AccuracyMeters : null, cancellationToken)
            ? CustomerUpdateResult.Success : CustomerUpdateResult.NotFound;
    }

    public async Task<CustomerUpdateResult> SetGeofenceRadiusAsync(TenantId tenantId, UserId actorId, Guid customerId, SetGeofenceRadiusRequest request, CancellationToken cancellationToken)
    {
        var scope = await ScopeAsync(tenantId, actorId, PermissionKeys.CustomersGeofenceWrite, cancellationToken);
        if (scope is null) return CustomerUpdateResult.Forbidden;
        if (request.RadiusMeters is < MinRadiusMeters or > MaxRadiusMeters) return CustomerUpdateResult.InvalidData;

        return await store.SetGeofenceRadiusAsync(tenantId, actorId, new CustomerId(customerId), scope, request.RadiusMeters, cancellationToken)
            ? CustomerUpdateResult.Success : CustomerUpdateResult.NotFound;
    }

    // Rejects out-of-range values, the 0,0 "null island" default of broken GPS, and imprecise GPS fixes.
    public static bool IsValidLocation(SetCustomerLocationRequest request) =>
        double.IsFinite(request.Latitude) && double.IsFinite(request.Longitude) &&
        request.Latitude is >= -90 and <= 90 && request.Longitude is >= -180 and <= 180 &&
        !(request.Latitude == 0 && request.Longitude == 0) &&
        request.Source switch
        {
            "gps" => request.AccuracyMeters is > 0 and <= MaxGpsAccuracyMeters,
            "manual" => true,
            _ => false
        };

    private async Task<AccessScope?> ScopeAsync(TenantId tenantId, UserId actorId, string permission, CancellationToken cancellationToken) =>
        tenantId.IsEmpty || actorId.IsEmpty ? null : await permissions.GetAccessScopeAsync(tenantId, actorId, permission, cancellationToken);
}
