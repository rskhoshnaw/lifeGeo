using LifeDrop.Application.Roles;
using LifeDrop.Domain.Common;

namespace LifeDrop.Application.Orders;

// Orders sent from a wholesaler ("mudhakhar", plan step 5.6, owner request 2026-10-26). We give the medicine to the wholesaler and it takes it to the pharmacy with its own car, so the
// order has no preparation and no driver. The wholesaler officer (a member of our staff, permission wholesaler.work) sees the orders of the wholesalers they work with
// (warehouse_staff, as for the warehouse), tells the wholesaler to send one and marks it "dispatched", and prints the list as a PDF.
public sealed record WholesalerDto(Guid Id, string Code, string Name, int OpenOrders);

public sealed record WholesalerLineDto(string ProductCode, string ProductName, string Unit, decimal Quantity, decimal UnitPrice, decimal LineTotal);

// Place: the city, neighbourhood and street of the customer, from the top down, as one text.
public sealed record WholesalerOrderDto(Guid Id, string Number, DateTimeOffset CreatedAt, string Status, Guid WarehouseId, string WholesalerName, Guid CustomerId, string CustomerCode,
    string CustomerName, string? ContactName, string? Phone, string? Phone2, string? Address, string? Place, string Currency, decimal Total, string? Note, string? VisitorEmail,
    DateTimeOffset? DispatchedAt, string? DispatchedByEmail, IReadOnlyList<WholesalerLineDto> Lines);

public sealed record DispatchRequest(string? Note);

public interface IWholesalerStore
{
    // The active wholesaler warehouses the actor works in, with the number of their orders that wait ("placed").
    Task<IReadOnlyList<WholesalerDto>> ListWholesalersAsync(TenantId tenantId, UserId actorId, CancellationToken cancellationToken);
    // The orders (newest first) of the wholesalers the actor works in, in one status, of one wholesaler or of all of them; lines included.
    Task<IReadOnlyList<WholesalerOrderDto>> ListOrdersAsync(TenantId tenantId, UserId actorId, Guid? warehouseId, string status, int limit, CancellationToken cancellationToken);
    // placed -> dispatched. NotFound: not an order of a wholesaler the actor works in. Conflict: not "placed" any more. One history row and one audit row (order.dispatched).
    Task<OrderResult> DispatchAsync(TenantId tenantId, UserId actorId, Guid orderId, string? note, CancellationToken cancellationToken);
}

public sealed class WholesalerOrders(IWholesalerStore store, IPermissionStore permissions)
{
    public const int MaxLimit = 200;
    public const int MaxNoteLength = 500;

    private async Task<bool> AllowedAsync(TenantId tenantId, UserId actorId, CancellationToken cancellationToken) =>
        !tenantId.IsEmpty && !actorId.IsEmpty && await permissions.HasPermissionAsync(tenantId, actorId, PermissionKeys.WholesalerWork, cancellationToken);

    public async Task<(OrderResult Result, IReadOnlyList<WholesalerDto> Items)> ListWholesalersAsync(TenantId tenantId, UserId actorId, CancellationToken cancellationToken) =>
        await AllowedAsync(tenantId, actorId, cancellationToken)
            ? (OrderResult.Success, await store.ListWholesalersAsync(tenantId, actorId, cancellationToken))
            : (OrderResult.Forbidden, Array.Empty<WholesalerDto>());

    // status: placed (the default, what waits), dispatched or cancelled.
    public async Task<(OrderResult Result, IReadOnlyList<WholesalerOrderDto> Items)> ListOrdersAsync(TenantId tenantId, UserId actorId, Guid? warehouseId, string? status, int? limit,
        CancellationToken cancellationToken)
    {
        if (!await AllowedAsync(tenantId, actorId, cancellationToken)) return (OrderResult.Forbidden, Array.Empty<WholesalerOrderDto>());
        var take = limit ?? 100;
        var wanted = status ?? "placed";
        if (take is < 1 or > MaxLimit || warehouseId == Guid.Empty || wanted is not ("placed" or "dispatched" or "cancelled")) return (OrderResult.InvalidData, Array.Empty<WholesalerOrderDto>());
        return (OrderResult.Success, await store.ListOrdersAsync(tenantId, actorId, warehouseId, wanted, take, cancellationToken));
    }

    public async Task<OrderResult> DispatchAsync(TenantId tenantId, UserId actorId, Guid orderId, DispatchRequest? request, CancellationToken cancellationToken)
    {
        if (!await AllowedAsync(tenantId, actorId, cancellationToken)) return OrderResult.Forbidden;
        var note = string.IsNullOrWhiteSpace(request?.Note) ? null : request!.Note!.Trim();
        if (orderId == Guid.Empty || note?.Length > MaxNoteLength) return OrderResult.InvalidData;
        return await store.DispatchAsync(tenantId, actorId, orderId, note, cancellationToken);
    }
}
