using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LifeDrop.Application.Roles;
using LifeDrop.Domain.Common;
using LifeDrop.Domain.Visits;

namespace LifeDrop.Application.Customers;

// Priority of customers set by a manager (plan step 1.26). Priority: high | normal | low. Exactly one of CustomerIds (1-200) or
// PlaceId (every customer in that place and below it) names the customers; only customers inside the caller's scope change.
public sealed record SetCustomerPriorityRequest(string? Priority, IReadOnlyList<Guid>? CustomerIds = null, Guid? PlaceId = null);

public sealed record CustomerPriorityDto(int Updated);

public enum CustomerPriorityResult { Success, Forbidden, InvalidData }

public interface ICustomerPriorityStore
{
    // Sets the priority of the customers in scope that do not have it yet; one audit row per changed customer, one transaction.
    // Returns how many changed.
    Task<int> SetAsync(TenantId tenantId, UserId actorId, AccessScope scope, string priority, IReadOnlyList<Guid>? customerIds, Guid? placeId,
        CancellationToken cancellationToken);
}

// Needs visits.write (sales manager, supervisor): the people who plan for others; their data scope limits the customers.
public sealed class CustomerPriorityService(ICustomerPriorityStore store, IPermissionStore permissions)
{
    public const int MaxCustomers = 200;

    public async Task<(CustomerPriorityResult Result, CustomerPriorityDto? Data)> SetAsync(TenantId tenantId, UserId actorId,
        SetCustomerPriorityRequest request, CancellationToken cancellationToken)
    {
        if (tenantId.IsEmpty || actorId.IsEmpty) return (CustomerPriorityResult.Forbidden, null);
        var scope = await permissions.GetAccessScopeAsync(tenantId, actorId, PermissionKeys.VisitsWrite, cancellationToken);
        if (scope is null) return (CustomerPriorityResult.Forbidden, null);

        var hasIds = request.CustomerIds is { Count: > 0 };
        if (!VisitPlanningContract.TryParsePriority(request.Priority, out _) || request.Priority is null ||
            hasIds == (request.PlaceId is not null) ||
            (hasIds && (request.CustomerIds!.Count > MaxCustomers || request.CustomerIds.Any(i => i == Guid.Empty))) ||
            request.PlaceId == Guid.Empty)
            return (CustomerPriorityResult.InvalidData, null);

        var updated = await store.SetAsync(tenantId, actorId, scope, request.Priority, hasIds ? request.CustomerIds!.Distinct().ToList() : null,
            request.PlaceId, cancellationToken);
        return (CustomerPriorityResult.Success, new CustomerPriorityDto(updated));
    }
}
