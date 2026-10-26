using LifeDrop.Application.Orders;
using LifeDrop.Application.Roles;
using LifeDrop.Domain.Common;

namespace LifeDrop.Application.Customers;

// What happened with one customer (plan step 5.4): the visits with their result and what was bought. Status is the visit's own status (Completed, Failed, Cancelled, ...);
// Outcome and Note come from the visit report, Reason from a failed or cancelled visit. Orders is null when the reader may not read orders.
public sealed record CustomerVisitHistoryDto(Guid Id, DateOnly Date, string Status, string VisitType, string? UserEmail, DateTimeOffset? CheckInAt, DateTimeOffset? CheckOutAt,
    string? Outcome, string? Note, string? Reason);
public sealed record CustomerOrderLineDto(string ProductName, decimal Quantity, decimal? DeliveredQuantity);
public sealed record CustomerOrderHistoryDto(Guid Id, string Number, DateTimeOffset CreatedAt, string Status, string Currency, decimal Total, bool PricesHidden, string? CreatedByEmail,
    IReadOnlyList<CustomerOrderLineDto> Lines);
public sealed record CustomerHistoryDto(IReadOnlyList<CustomerVisitHistoryDto> Visits, IReadOnlyList<CustomerOrderHistoryDto>? Orders);

public interface ICustomerHistoryStore
{
    // The newest visits of the customer, newest first.
    Task<IReadOnlyList<CustomerVisitHistoryDto>> ListVisitsAsync(TenantId tenantId, Guid customerId, int limit, CancellationToken cancellationToken);
    // The product lines (name, ordered and delivered quantity; never a price) of the orders, by order id.
    Task<IReadOnlyDictionary<Guid, IReadOnlyList<CustomerOrderLineDto>>> ListOrderLinesAsync(TenantId tenantId, IReadOnlyCollection<Guid> orderIds, CancellationToken cancellationToken);
}

// Needs customers.read with the customer inside the reader's data scope (otherwise "not found", doctors included); the orders need orders.read and follow the order rules
// (area, hidden prices).
public sealed class CustomerHistory(ICustomerStore customers, ICustomerHistoryStore store, IPermissionStore permissions, OrderService orders)
{
    public const int DefaultLimit = 30;
    public const int MaxLimit = 100;

    public async Task<(CustomerUpdateResult Result, CustomerHistoryDto? History)> GetAsync(TenantId tenantId, UserId actorId, Guid customerId, int? limit, CancellationToken cancellationToken)
    {
        var scope = tenantId.IsEmpty || actorId.IsEmpty ? null : await permissions.GetAccessScopeAsync(tenantId, actorId, PermissionKeys.CustomersRead, cancellationToken);
        if (scope is null) return (CustomerUpdateResult.Forbidden, null);
        var take = limit ?? DefaultLimit;
        if (customerId == Guid.Empty || take is < 1 or > MaxLimit) return (CustomerUpdateResult.InvalidData, null);
        if (await customers.GetByIdAsync(tenantId, new CustomerId(customerId), scope, cancellationToken) is null) return (CustomerUpdateResult.NotFound, null);

        var visits = await store.ListVisitsAsync(tenantId, customerId, take, cancellationToken);
        IReadOnlyList<CustomerOrderHistoryDto>? history = null;
        var (result, items) = await orders.ListAsync(tenantId, actorId, customerId, null, take, null, null, cancellationToken);
        if (result == OrderResult.Success)
        {
            var lines = await store.ListOrderLinesAsync(tenantId, items.Select(o => o.Id).ToList(), cancellationToken);
            history = items.Select(o => new CustomerOrderHistoryDto(o.Id, o.Number, o.CreatedAt, o.Status, o.Currency, o.Total, o.PricesHidden, o.CreatedByEmail,
                lines.TryGetValue(o.Id, out var orderLines) ? orderLines : Array.Empty<CustomerOrderLineDto>())).ToList();
        }
        return (CustomerUpdateResult.Success, new CustomerHistoryDto(visits, history));
    }
}
