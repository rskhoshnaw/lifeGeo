using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LifeDrop.Application.Roles;
using LifeDrop.Domain.Common;

namespace LifeDrop.Application.Customers;

// A manager gives customers to another user (plan step 5.28, owner request 2026-11-08): the ticked customers (CustomerIds, 1-200) or every customer a user
// owns (FromUserId, for example when somebody leaves). Exactly one of the two names the customers. The new owner must be an active user who may see customers;
// the customer takes the region of the new owner's team (it keeps its region when that user has no team). Visits already planned and past reports stay as they are.
public sealed record SetCustomerOwnerRequest(Guid? OwnerUserId, IReadOnlyList<Guid>? CustomerIds = null, Guid? FromUserId = null);

// Updated: customers that changed owner. Skipped: customers in the request that did not change (not in the caller's area, archived, already owned by the new owner, or a
// type the new owner may not hold: doctors for a sales rep, other types for a scientific rep).
public sealed record CustomerOwnerDto(int Updated, int Skipped);

public enum CustomerOwnerResult { Success, Forbidden, InvalidData, NotFound }

// The new owner as the store sees it: active user of the company and the region of the team.
public sealed record NewOwnerInfo(Guid UserId, Guid? RegionId);

public interface ICustomerOwnerStore
{
    // Null when the user does not exist in the company or is not active.
    Task<NewOwnerInfo?> FindOwnerAsync(TenantId tenantId, Guid userId, CancellationToken cancellationToken);

    // Changes the owner of the customers in scope (and of a type the new owner may hold); one audit row per changed customer, one transaction.
    // Returns how many changed.
    Task<int> SetAsync(TenantId tenantId, UserId actorId, AccessScope scope, AccessScope ownerScope, NewOwnerInfo owner, IReadOnlyList<Guid>? customerIds, Guid? fromUserId,
        CancellationToken cancellationToken);
}

// Needs customers.approve (sales manager, supervisor, company manager); their data scope limits the customers, and a supervisor can only give to a user of the own region.
public sealed class CustomerOwnerService(ICustomerOwnerStore store, IPermissionStore permissions)
{
    public const int MaxCustomers = 200;

    public async Task<(CustomerOwnerResult Result, CustomerOwnerDto? Data)> SetAsync(TenantId tenantId, UserId actorId, SetCustomerOwnerRequest request, CancellationToken cancellationToken)
    {
        if (tenantId.IsEmpty || actorId.IsEmpty) return (CustomerOwnerResult.Forbidden, null);
        var scope = await permissions.GetAccessScopeAsync(tenantId, actorId, PermissionKeys.CustomersApprove, cancellationToken);
        if (scope is null) return (CustomerOwnerResult.Forbidden, null);

        var hasIds = request.CustomerIds is { Count: > 0 };
        if (request.OwnerUserId is not { } newOwnerId || newOwnerId == Guid.Empty || hasIds == (request.FromUserId is not null) ||
            (hasIds && (request.CustomerIds!.Count > MaxCustomers || request.CustomerIds.Any(i => i == Guid.Empty))) ||
            request.FromUserId == Guid.Empty || request.FromUserId == newOwnerId)
            return (CustomerOwnerResult.InvalidData, null);

        var owner = await store.FindOwnerAsync(tenantId, newOwnerId, cancellationToken);
        if (owner is null) return (CustomerOwnerResult.NotFound, null);
        // A supervisor works inside the own region: the new owner must belong to it.
        if (scope.Scope == DataScope.Region && (scope.RegionId is null || owner.RegionId != scope.RegionId)) return (CustomerOwnerResult.Forbidden, null);
        // The new owner must be able to see customers at all, and which types shows in the scope (HideDoctors, DoctorsOnly).
        var ownerScope = await permissions.GetAccessScopeAsync(tenantId, new UserId(newOwnerId), PermissionKeys.CustomersRead, cancellationToken);
        if (ownerScope is null) return (CustomerOwnerResult.InvalidData, null);

        var ids = hasIds ? request.CustomerIds!.Distinct().ToList() : null;
        var updated = await store.SetAsync(tenantId, actorId, scope, ownerScope, owner, ids, request.FromUserId, cancellationToken);
        return (CustomerOwnerResult.Success, new CustomerOwnerDto(updated, ids is null ? 0 : ids.Count - updated));
    }
}
