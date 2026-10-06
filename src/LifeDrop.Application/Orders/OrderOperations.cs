using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LifeDrop.Application.Catalog;
using LifeDrop.Application.Customers;
using LifeDrop.Application.Roles;
using LifeDrop.Application.Visits;
using LifeDrop.Domain.Common;
using LifeDrop.Domain.Visits;

namespace LifeDrop.Application.Orders;

// Orders and warehouses (plan step 2.2). An order is placed from the rep's app or the panel for one approved customer, optionally for
// a visit, and sent to ONE warehouse chosen by the person who places it. All lines share one currency. The server works out every
// price (general or city price, then the customer's discounts; see PriceCalculator) and stores it on the line with the discounts
// that were applied; the phone never sends a price. "Placed" is true only after the server accepted the order. A retry with the
// same ClientRef returns the order that exists.
public sealed record WarehouseDto(Guid Id, string Code, string Name, string Status);
public sealed record SaveWarehouseRequest(string? Code, string? Name, string? Status = null);

public sealed record OrderLineRequest(Guid? ProductId, decimal? Quantity);
public sealed record CreateOrderRequest(Guid? CustomerId, Guid? WarehouseId, Guid? VisitId, Guid? ClientRef, IReadOnlyList<OrderLineRequest>? Items, string? Note = null);
public sealed record CancelOrderRequest(string? Reason);

public sealed record OrderLineDto(int LineNo, Guid ProductId, string ProductCode, string ProductName, string Unit, decimal Quantity,
    decimal ListPrice, decimal UnitPrice, decimal LineTotal, IReadOnlyList<AppliedDiscountDto> Discounts);

// Number: "O-000123". Items is empty in a list and filled when one order is read.
public sealed record OrderDto(Guid Id, string Number, Guid CustomerId, string CustomerCode, string CustomerName, Guid? VisitId, Guid WarehouseId,
    string WarehouseName, Guid CreatedBy, string? CreatedByEmail, string Status, string Currency, decimal Total, string? Note, string? CancelReason,
    DateTimeOffset CreatedAt, IReadOnlyList<OrderLineDto> Items);

// Replayed: the same ClientRef was sent before and the order that exists is returned.
public sealed record CreatedOrderDto(Guid Id, string Number, string Currency, decimal Total, bool Replayed);

public enum OrderResult { Success, Forbidden, NotFound, InvalidData, CurrencyMismatch, CustomerNotApproved, Conflict }

// The order with prices already worked out, ready to be written.
public sealed record PricedLine(Guid ProductId, string ProductCode, string ProductName, string Unit, decimal Quantity, decimal ListPrice, decimal UnitPrice,
    decimal LineTotal, IReadOnlyList<AppliedDiscountDto> Discounts);
public sealed record NewOrder(Guid CustomerId, Guid WarehouseId, Guid? VisitId, Guid ClientRef, string Currency, decimal Total, string? Note, IReadOnlyList<PricedLine> Lines);

public interface IOrderStore
{
    Task<IReadOnlyList<WarehouseDto>> ListWarehousesAsync(TenantId tenantId, bool includeInactive, CancellationToken cancellationToken);
    // Null when the code exists already.
    Task<Guid?> CreateWarehouseAsync(TenantId tenantId, UserId actorId, SaveWarehouseRequest warehouse, CancellationToken cancellationToken);
    Task<bool> UpdateWarehouseAsync(TenantId tenantId, UserId actorId, Guid warehouseId, SaveWarehouseRequest warehouse, CancellationToken cancellationToken);

    // NotFound: warehouse or visit missing. InvalidData: warehouse inactive or the visit is not usable. Replays the order of an earlier send with
    // the same ClientRef (same creator and customer; else Conflict). Writes the order, its lines, the number and the audit row in one transaction.
    Task<(OrderResult Result, CreatedOrderDto? Order)> CreateAsync(TenantId tenantId, UserId actorId, NewOrder order, CancellationToken cancellationToken);
    Task<OrderDto?> GetAsync(TenantId tenantId, Guid orderId, AccessScope scope, CancellationToken cancellationToken);
    Task<IReadOnlyList<OrderDto>> ListAsync(TenantId tenantId, AccessScope scope, Guid? customerId, string? status, int limit, DateTimeOffset? before, Guid? beforeId,
        CancellationToken cancellationToken);
    // NotFound: not in scope. Forbidden: not a manager and not the creator. Conflict: the order is not "placed" any more.
    Task<OrderResult> CancelAsync(TenantId tenantId, UserId actorId, Guid orderId, AccessScope scope, bool isManager, string reason, CancellationToken cancellationToken);
    // Used by the visit report: "order placed" needs a real order of that visit.
    Task<bool> HasPlacedOrderForVisitAsync(TenantId tenantId, Guid visitId, CancellationToken cancellationToken);
}

public sealed class OrderService(IOrderStore store, IProductStore products, IPermissionStore permissions, ICustomerStore customers, IVisitStore visits, TimeProvider clock)
{
    public const int MaxLines = 100;
    public const decimal MaxQuantity = 100_000m;
    public const int MaxNoteLength = 500;

    public async Task<(OrderResult Result, IReadOnlyList<WarehouseDto> Items)> ListWarehousesAsync(TenantId tenantId, UserId actorId, bool includeInactive, CancellationToken cancellationToken)
    {
        var reads = await ScopeAsync(tenantId, actorId, PermissionKeys.OrdersRead, cancellationToken) is not null ||
                    await ScopeAsync(tenantId, actorId, PermissionKeys.OrdersCreate, cancellationToken) is not null;
        if (!reads) return (OrderResult.Forbidden, Array.Empty<WarehouseDto>());
        var inactive = includeInactive && await ScopeAsync(tenantId, actorId, PermissionKeys.WarehousesManage, cancellationToken) is not null;
        return (OrderResult.Success, await store.ListWarehousesAsync(tenantId, inactive, cancellationToken));
    }

    public async Task<(OrderResult Result, Guid? Id)> CreateWarehouseAsync(TenantId tenantId, UserId actorId, SaveWarehouseRequest request, CancellationToken cancellationToken)
    {
        if (await ScopeAsync(tenantId, actorId, PermissionKeys.WarehousesManage, cancellationToken) is null) return (OrderResult.Forbidden, null);
        var code = request.Code?.Trim();
        if (string.IsNullOrEmpty(code) || code.Length > 40 || !CleanWarehouse(request, out var clean)) return (OrderResult.InvalidData, null);
        var id = await store.CreateWarehouseAsync(tenantId, actorId, clean! with { Code = code }, cancellationToken);
        return id is null ? (OrderResult.Conflict, null) : (OrderResult.Success, id);
    }

    public async Task<OrderResult> UpdateWarehouseAsync(TenantId tenantId, UserId actorId, Guid warehouseId, SaveWarehouseRequest request, CancellationToken cancellationToken)
    {
        if (await ScopeAsync(tenantId, actorId, PermissionKeys.WarehousesManage, cancellationToken) is null) return OrderResult.Forbidden;
        if (warehouseId == Guid.Empty || !CleanWarehouse(request, out var clean)) return OrderResult.InvalidData;
        return await store.UpdateWarehouseAsync(tenantId, actorId, warehouseId, clean!, cancellationToken) ? OrderResult.Success : OrderResult.NotFound;
    }

    public async Task<(OrderResult Result, CreatedOrderDto? Order)> CreateAsync(TenantId tenantId, UserId actorId, CreateOrderRequest request, CancellationToken cancellationToken)
    {
        var scope = await ScopeAsync(tenantId, actorId, PermissionKeys.OrdersCreate, cancellationToken);
        if (scope is null) return (OrderResult.Forbidden, null);
        var note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim();
        var items = request.Items;
        if (request.CustomerId is null || request.CustomerId == Guid.Empty || request.WarehouseId is null || request.WarehouseId == Guid.Empty ||
            request.VisitId == Guid.Empty || request.ClientRef is null || request.ClientRef == Guid.Empty || note is { Length: > MaxNoteLength } ||
            items is null || items.Count is 0 or > MaxLines || items.Any(i => i.ProductId is null || i.ProductId == Guid.Empty || !ValidQuantity(i.Quantity)) ||
            items.Select(i => i.ProductId).Distinct().Count() != items.Count)
            return (OrderResult.InvalidData, null);

        // The customer must be inside the caller's area and approved.
        var customer = await customers.GetByIdAsync(tenantId, new CustomerId(request.CustomerId.Value), scope, cancellationToken);
        if (customer is null) return (OrderResult.NotFound, null);
        if (customer.ApprovalStatus != "approved") return (OrderResult.CustomerNotApproved, null);

        // A visit is linked only when it is of this customer and of the caller (or a manager's scope), and it is not cancelled or failed.
        if (request.VisitId is { } visitId)
        {
            var visit = await visits.GetAsync(tenantId, new VisitId(visitId), cancellationToken);
            if (visit is null || visit.CustomerId.Value != customer.Id) return (OrderResult.NotFound, null);
            if (visit.UserId != actorId && await ScopeAsync(tenantId, actorId, PermissionKeys.VisitsWrite, cancellationToken) is null) return (OrderResult.NotFound, null);
            if (visit.Status is VisitStatus.Cancelled or VisitStatus.Failed) return (OrderResult.InvalidData, null);
        }

        // Prices of today (Iraq) from the server, never from the phone.
        var pricing = await products.GetPricingAsync(tenantId, customer.Id, IraqCalendar.Today(clock), cancellationToken);
        var lines = new List<PricedLine>(items.Count);
        foreach (var item in items)
        {
            var row = pricing.Products.FirstOrDefault(p => p.Product.Id == item.ProductId);
            if (row is null) return (OrderResult.InvalidData, null);   // unknown or inactive product
            var product = row.Product;
            var list = row.CityPrice ?? product.BasePrice;
            var (unitPrice, applied) = PriceCalculator.Apply(list, product.Id, pricing.Discounts);
            lines.Add(new PricedLine(product.Id, product.Code, product.Name, product.Unit, item.Quantity!.Value, list, unitPrice,
                decimal.Round(unitPrice * item.Quantity.Value, 3, MidpointRounding.AwayFromZero), applied));
        }
        var currency = pricing.Products.First(p => p.Product.Id == items[0].ProductId).Product.Currency;
        if (items.Any(i => pricing.Products.First(p => p.Product.Id == i.ProductId).Product.Currency != currency)) return (OrderResult.CurrencyMismatch, null);

        return await store.CreateAsync(tenantId, actorId, new NewOrder(customer.Id, request.WarehouseId.Value, request.VisitId, request.ClientRef.Value, currency,
            lines.Sum(l => l.LineTotal), note, lines), cancellationToken);
    }

    public async Task<(OrderResult Result, OrderDto? Order)> GetAsync(TenantId tenantId, UserId actorId, Guid orderId, CancellationToken cancellationToken)
    {
        var scope = await ScopeAsync(tenantId, actorId, PermissionKeys.OrdersRead, cancellationToken);
        if (scope is null) return (OrderResult.Forbidden, null);
        var order = orderId == Guid.Empty ? null : await store.GetAsync(tenantId, orderId, scope, cancellationToken);
        return order is null ? (OrderResult.NotFound, null) : (OrderResult.Success, order);
    }

    // Newest first. Before / BeforeId continue after the last row of the previous page.
    public async Task<(OrderResult Result, IReadOnlyList<OrderDto> Items)> ListAsync(TenantId tenantId, UserId actorId, Guid? customerId, string? status, int? limit,
        DateTimeOffset? before, Guid? beforeId, CancellationToken cancellationToken)
    {
        var scope = await ScopeAsync(tenantId, actorId, PermissionKeys.OrdersRead, cancellationToken);
        if (scope is null) return (OrderResult.Forbidden, Array.Empty<OrderDto>());
        var take = limit ?? 50;
        if (take is < 1 or > 100 || (before is null) != (beforeId is null) || customerId == Guid.Empty || status is not (null or "placed" or "cancelled"))
            return (OrderResult.InvalidData, Array.Empty<OrderDto>());
        return (OrderResult.Success, await store.ListAsync(tenantId, scope, customerId, status, take, before, beforeId, cancellationToken));
    }

    // The sales manager and supervisors (orders.cancel) cancel orders in their area; the creator cancels their own while it is still "placed".
    public async Task<OrderResult> CancelAsync(TenantId tenantId, UserId actorId, Guid orderId, CancelOrderRequest request, CancellationToken cancellationToken)
    {
        var scope = await ScopeAsync(tenantId, actorId, PermissionKeys.OrdersRead, cancellationToken);
        if (scope is null) return OrderResult.Forbidden;
        var reason = request.Reason?.Trim();
        if (orderId == Guid.Empty || string.IsNullOrEmpty(reason) || reason.Length > MaxNoteLength) return OrderResult.InvalidData;
        var manager = await ScopeAsync(tenantId, actorId, PermissionKeys.OrdersCancel, cancellationToken) is not null;
        return await store.CancelAsync(tenantId, actorId, orderId, scope, manager, reason, cancellationToken);
    }

    private Task<AccessScope?> ScopeAsync(TenantId tenantId, UserId actorId, string permission, CancellationToken cancellationToken) =>
        tenantId.IsEmpty || actorId.IsEmpty ? Task.FromResult<AccessScope?>(null) : permissions.GetAccessScopeAsync(tenantId, actorId, permission, cancellationToken);

    private static bool ValidQuantity(decimal? quantity) => quantity is > 0 and <= MaxQuantity && decimal.Round(quantity.Value, 3) == quantity.Value;

    private static bool CleanWarehouse(SaveWarehouseRequest request, out SaveWarehouseRequest? clean)
    {
        var name = request.Name?.Trim();
        var status = request.Status ?? "active";
        clean = null;
        if (string.IsNullOrEmpty(name) || name.Length > 160 || status is not ("active" or "inactive")) return false;
        clean = request with { Name = name, Status = status };
        return true;
    }
}
