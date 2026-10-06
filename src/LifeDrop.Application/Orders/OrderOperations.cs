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
public sealed record CreateOrderRequest(Guid? CustomerId, Guid? WarehouseId, Guid? VisitId, Guid? ClientRef, IReadOnlyList<OrderLineRequest>? Items, string? Note = null, bool? DetachVisitIfInvalid = null);
public sealed record CancelOrderRequest(string? Reason);

public sealed record OrderLineDto(int LineNo, Guid ProductId, string ProductCode, string ProductName, string Unit, decimal Quantity,
    decimal ListPrice, decimal UnitPrice, decimal LineTotal, IReadOnlyList<AppliedDiscountDto> Discounts,
    // Warehouse work (plan step 2.3): what the warehouse really prepared (null until it says so) and why it was less.
    decimal? PreparedQuantity = null, string? ShortageReason = null,
    // Delivery (plan step 2.4): what the driver really handed over and why it was less than prepared.
    decimal? DeliveredQuantity = null, string? DeliveryReason = null);

// One step of an order: the status it moved to, who did it (email) and when. Note: the cancel reason.
public sealed record OrderHistoryDto(string Status, string? ActorEmail, string? Note, DateTimeOffset At,
    // The evidence a driver's step carries (plan step 2.4): the phone's position and the distance to the customer when both are known.
    double? Latitude = null, double? Longitude = null, int? DistanceMeters = null);

// For the driver (plan step 2.4): where to go and who received the goods.
public sealed record DeliveryInfo(string? Phone, string? Address, double? Latitude, double? Longitude, string? ReceiverName, string? DeliveryNote, string? FailureReason);

// Number: "O-000123". Items is empty in a list and filled when one order is read.
public sealed record OrderDto(Guid Id, string Number, Guid CustomerId, string CustomerCode, string CustomerName, Guid? VisitId, Guid WarehouseId,
    string WarehouseName, Guid CreatedBy, string? CreatedByEmail, string Status, string Currency, decimal Total, string? Note, string? CancelReason,
    DateTimeOffset CreatedAt, IReadOnlyList<OrderLineDto> Items,
    // Plan step 2.3: the driver who took the order, and its steps (when one order is read).
    Guid? DriverId = null, string? DriverEmail = null, IReadOnlyList<OrderHistoryDto>? History = null, DeliveryInfo? Delivery = null);

// Replayed: the same ClientRef was sent before and the order that exists is returned.
// VisitDetached: an order saved without internet whose visit was cancelled or failed by the time it arrived is placed without the visit (plan step 2.13).
public sealed record CreatedOrderDto(Guid Id, string Number, string Currency, decimal Total, bool Replayed, bool VisitDetached = false);

public enum OrderResult { Success, Forbidden, NotFound, InvalidData, CurrencyMismatch, CustomerNotApproved, Conflict, GpsMocked }

// Delivery (plan step 2.4). Location: what the phone knows about where it is (all optional; a fake position is refused).
public sealed record DeliveryLocation(double? Latitude, double? Longitude, double? AccuracyMeters = null, bool IsMocked = false);
public sealed record DeliveryStepRequest(DeliveryLocation? Location = null);
public sealed record DeliveredLineRequest(int? LineNo, decimal? Quantity, string? Reason = null);
public sealed record DeliverRequest(string? ReceiverName, string? Note, IReadOnlyList<DeliveredLineRequest>? Lines, DeliveryLocation? Location = null);
public sealed record FailDeliveryRequest(string? Reason, DeliveryLocation? Location = null);

// Warehouse work (plan step 2.3). Quantity: what was really prepared (0 up to the ordered quantity); Reason: needed when it is less.
public sealed record PreparedLineRequest(int? LineNo, decimal? Quantity, string? Reason = null);
public sealed record MarkReadyRequest(IReadOnlyList<PreparedLineRequest>? Lines);
public sealed record HandOverRequest(Guid? DriverId);
public sealed record StaffMemberDto(Guid UserId, string? Email);
public sealed record AddStaffRequest(Guid? UserId);
public sealed record DriverDto(Guid Id, string? Email);

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
    // Staff of a warehouse (plan step 2.3). NotFound: warehouse or user missing. InvalidData: the user's roles do not give warehouse.work.
    Task<IReadOnlyList<StaffMemberDto>?> ListStaffAsync(TenantId tenantId, Guid warehouseId, CancellationToken cancellationToken);
    Task<OrderResult> AddStaffAsync(TenantId tenantId, UserId actorId, Guid warehouseId, Guid userId, CancellationToken cancellationToken);
    Task<bool> RemoveStaffAsync(TenantId tenantId, UserId actorId, Guid warehouseId, Guid userId, CancellationToken cancellationToken);
    Task<IReadOnlyList<DriverDto>> ListDriversAsync(TenantId tenantId, CancellationToken cancellationToken);

    // Warehouse work: only the orders of the warehouses the actor works in (else NotFound). Status null = the open ones (placed, preparing, ready).
    Task<IReadOnlyList<OrderDto>> ListForWarehouseAsync(TenantId tenantId, UserId actorId, string? status, int limit, DateTimeOffset? before, Guid? beforeId,
        CancellationToken cancellationToken);
    Task<OrderDto?> GetForWarehouseAsync(TenantId tenantId, UserId actorId, Guid orderId, CancellationToken cancellationToken);
    // placed -> preparing; preparing -> ready with the prepared quantity of every line; ready -> handed_over to a driver (a user with the Driver role).
    // Conflict: the order is in another status. InvalidData: the lines do not match, a quantity is out of range or a reason is missing, or the driver is not a driver.
    Task<OrderResult> StartPreparingAsync(TenantId tenantId, UserId actorId, Guid orderId, CancellationToken cancellationToken);
    Task<OrderResult> MarkReadyAsync(TenantId tenantId, UserId actorId, Guid orderId, IReadOnlyList<PreparedLineRequest> lines, CancellationToken cancellationToken);
    Task<OrderResult> HandOverAsync(TenantId tenantId, UserId actorId, Guid orderId, Guid driverId, CancellationToken cancellationToken);

    // Delivery by the driver (plan step 2.4): only the orders assigned to the actor (else NotFound). Status null = the open ones (handed_over, picked_up,
    // on_the_way). pickup: handed_over -> picked_up; depart: picked_up -> on_the_way; deliver: on_the_way -> delivered with the delivered quantity of every
    // line; fail: handed_over, picked_up or on_the_way -> delivery_failed. Conflict: another status. InvalidData: the lines or the reason do not fit.
    Task<IReadOnlyList<OrderDto>> ListForDriverAsync(TenantId tenantId, UserId actorId, string? status, int limit, DateTimeOffset? before, Guid? beforeId,
        CancellationToken cancellationToken);
    Task<OrderDto?> GetForDriverAsync(TenantId tenantId, UserId actorId, Guid orderId, CancellationToken cancellationToken);
    Task<OrderResult> DriverStepAsync(TenantId tenantId, UserId actorId, Guid orderId, string step, DeliveryLocation? location, CancellationToken cancellationToken);
    Task<OrderResult> DeliverAsync(TenantId tenantId, UserId actorId, Guid orderId, string receiverName, string? note, IReadOnlyList<DeliveredLineRequest> lines,
        DeliveryLocation? location, CancellationToken cancellationToken);
    Task<OrderResult> FailDeliveryAsync(TenantId tenantId, UserId actorId, Guid orderId, string reason, DeliveryLocation? location, CancellationToken cancellationToken);

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
        var visitIdToUse = request.VisitId;
        var visitDetached = false;
        if (request.VisitId is { } visitId)
        {
            var visit = await visits.GetAsync(tenantId, new VisitId(visitId), cancellationToken);
            var problem = visit is null || visit.CustomerId.Value != customer.Id ? OrderResult.NotFound
                : visit.UserId != actorId && await ScopeAsync(tenantId, actorId, PermissionKeys.VisitsWrite, cancellationToken) is null ? OrderResult.NotFound
                : visit.Status is VisitStatus.Cancelled or VisitStatus.Failed ? OrderResult.InvalidData : OrderResult.Success;
            if (problem != OrderResult.Success)
            {
                // An order the phone kept without internet is still wanted when its visit was cancelled meanwhile: it is placed without the visit (plan step 2.13).
                if (request.DetachVisitIfInvalid != true) return (problem, null);
                visitIdToUse = null;
                visitDetached = true;
            }
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

        var (created, order) = await store.CreateAsync(tenantId, actorId, new NewOrder(customer.Id, request.WarehouseId.Value, visitIdToUse, request.ClientRef.Value, currency,
            lines.Sum(l => l.LineTotal), note, lines), cancellationToken);
        return (created, order is not null && visitDetached && !order.Replayed ? order with { VisitDetached = true } : order);
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
        if (take is < 1 or > 100 || (before is null) != (beforeId is null) || customerId == Guid.Empty || status is not (null or "placed" or "preparing" or "ready" or "handed_over" or "cancelled"))
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

    // ---- Warehouse work (plan step 2.3): needs warehouse.work and works only on the orders of the user's own warehouses ----

    public async Task<(OrderResult Result, IReadOnlyList<StaffMemberDto>? Items)> ListStaffAsync(TenantId tenantId, UserId actorId, Guid warehouseId, CancellationToken cancellationToken)
    {
        if (await ScopeAsync(tenantId, actorId, PermissionKeys.WarehousesManage, cancellationToken) is null) return (OrderResult.Forbidden, null);
        var items = warehouseId == Guid.Empty ? null : await store.ListStaffAsync(tenantId, warehouseId, cancellationToken);
        return items is null ? (OrderResult.NotFound, null) : (OrderResult.Success, items);
    }

    public async Task<OrderResult> AddStaffAsync(TenantId tenantId, UserId actorId, Guid warehouseId, AddStaffRequest request, CancellationToken cancellationToken)
    {
        if (await ScopeAsync(tenantId, actorId, PermissionKeys.WarehousesManage, cancellationToken) is null) return OrderResult.Forbidden;
        if (warehouseId == Guid.Empty || request.UserId is null || request.UserId == Guid.Empty) return OrderResult.InvalidData;
        return await store.AddStaffAsync(tenantId, actorId, warehouseId, request.UserId.Value, cancellationToken);
    }

    public async Task<OrderResult> RemoveStaffAsync(TenantId tenantId, UserId actorId, Guid warehouseId, Guid userId, CancellationToken cancellationToken)
    {
        if (await ScopeAsync(tenantId, actorId, PermissionKeys.WarehousesManage, cancellationToken) is null) return OrderResult.Forbidden;
        return await store.RemoveStaffAsync(tenantId, actorId, warehouseId, userId, cancellationToken) ? OrderResult.Success : OrderResult.NotFound;
    }

    public async Task<(OrderResult Result, IReadOnlyList<DriverDto> Items)> ListDriversAsync(TenantId tenantId, UserId actorId, CancellationToken cancellationToken)
    {
        if (await ScopeAsync(tenantId, actorId, PermissionKeys.WarehouseWork, cancellationToken) is null) return (OrderResult.Forbidden, Array.Empty<DriverDto>());
        return (OrderResult.Success, await store.ListDriversAsync(tenantId, cancellationToken));
    }

    public async Task<(OrderResult Result, IReadOnlyList<OrderDto> Items)> ListForWarehouseAsync(TenantId tenantId, UserId actorId, string? status, int? limit,
        DateTimeOffset? before, Guid? beforeId, CancellationToken cancellationToken)
    {
        if (await ScopeAsync(tenantId, actorId, PermissionKeys.WarehouseWork, cancellationToken) is null) return (OrderResult.Forbidden, Array.Empty<OrderDto>());
        var take = limit ?? 50;
        if (take is < 1 or > 100 || (before is null) != (beforeId is null) || status is not (null or "placed" or "preparing" or "ready" or "handed_over" or "cancelled"))
            return (OrderResult.InvalidData, Array.Empty<OrderDto>());
        return (OrderResult.Success, await store.ListForWarehouseAsync(tenantId, actorId, status, take, before, beforeId, cancellationToken));
    }

    public async Task<(OrderResult Result, OrderDto? Order)> GetForWarehouseAsync(TenantId tenantId, UserId actorId, Guid orderId, CancellationToken cancellationToken)
    {
        if (await ScopeAsync(tenantId, actorId, PermissionKeys.WarehouseWork, cancellationToken) is null) return (OrderResult.Forbidden, null);
        var order = orderId == Guid.Empty ? null : await store.GetForWarehouseAsync(tenantId, actorId, orderId, cancellationToken);
        return order is null ? (OrderResult.NotFound, null) : (OrderResult.Success, order);
    }

    public async Task<OrderResult> StartPreparingAsync(TenantId tenantId, UserId actorId, Guid orderId, CancellationToken cancellationToken)
    {
        if (await ScopeAsync(tenantId, actorId, PermissionKeys.WarehouseWork, cancellationToken) is null) return OrderResult.Forbidden;
        return orderId == Guid.Empty ? OrderResult.InvalidData : await store.StartPreparingAsync(tenantId, actorId, orderId, cancellationToken);
    }

    public async Task<OrderResult> MarkReadyAsync(TenantId tenantId, UserId actorId, Guid orderId, MarkReadyRequest request, CancellationToken cancellationToken)
    {
        if (await ScopeAsync(tenantId, actorId, PermissionKeys.WarehouseWork, cancellationToken) is null) return OrderResult.Forbidden;
        var lines = request.Lines;
        if (orderId == Guid.Empty || lines is null || lines.Count is 0 or > MaxLines ||
            lines.Any(l => l.LineNo is null or < 1 || l.Quantity is null or < 0 or > MaxQuantity || decimal.Round(l.Quantity.Value, 3) != l.Quantity || l.Reason?.Trim().Length > 300) ||
            lines.Select(l => l.LineNo).Distinct().Count() != lines.Count)
            return OrderResult.InvalidData;
        return await store.MarkReadyAsync(tenantId, actorId, orderId,
            lines.Select(l => l with { Reason = string.IsNullOrWhiteSpace(l.Reason) ? null : l.Reason.Trim() }).ToList(), cancellationToken);
    }

    public async Task<OrderResult> HandOverAsync(TenantId tenantId, UserId actorId, Guid orderId, HandOverRequest request, CancellationToken cancellationToken)
    {
        if (await ScopeAsync(tenantId, actorId, PermissionKeys.WarehouseWork, cancellationToken) is null) return OrderResult.Forbidden;
        if (orderId == Guid.Empty || request.DriverId is null || request.DriverId == Guid.Empty) return OrderResult.InvalidData;
        return await store.HandOverAsync(tenantId, actorId, orderId, request.DriverId.Value, cancellationToken);
    }

    // ---- Delivery by the driver (plan step 2.4): needs delivery.work and only the orders assigned to the user ----

    public async Task<(OrderResult Result, IReadOnlyList<OrderDto> Items)> ListForDriverAsync(TenantId tenantId, UserId actorId, string? status, int? limit,
        DateTimeOffset? before, Guid? beforeId, CancellationToken cancellationToken)
    {
        if (await ScopeAsync(tenantId, actorId, PermissionKeys.DeliveryWork, cancellationToken) is null) return (OrderResult.Forbidden, Array.Empty<OrderDto>());
        var take = limit ?? 50;
        if (take is < 1 or > 100 || (before is null) != (beforeId is null) || status is not (null or "handed_over" or "picked_up" or "on_the_way" or "delivered" or "delivery_failed"))
            return (OrderResult.InvalidData, Array.Empty<OrderDto>());
        return (OrderResult.Success, await store.ListForDriverAsync(tenantId, actorId, status, take, before, beforeId, cancellationToken));
    }

    public async Task<(OrderResult Result, OrderDto? Order)> GetForDriverAsync(TenantId tenantId, UserId actorId, Guid orderId, CancellationToken cancellationToken)
    {
        if (await ScopeAsync(tenantId, actorId, PermissionKeys.DeliveryWork, cancellationToken) is null) return (OrderResult.Forbidden, null);
        var order = orderId == Guid.Empty ? null : await store.GetForDriverAsync(tenantId, actorId, orderId, cancellationToken);
        return order is null ? (OrderResult.NotFound, null) : (OrderResult.Success, order);
    }

    // step: "pickup" or "depart".
    public async Task<OrderResult> DriverStepAsync(TenantId tenantId, UserId actorId, Guid orderId, string step, DeliveryStepRequest request, CancellationToken cancellationToken)
    {
        if (await ScopeAsync(tenantId, actorId, PermissionKeys.DeliveryWork, cancellationToken) is null) return OrderResult.Forbidden;
        if (orderId == Guid.Empty) return OrderResult.InvalidData;
        var location = CheckLocation(request.Location, out var problem);
        return problem is not null ? problem.Value : await store.DriverStepAsync(tenantId, actorId, orderId, step, location, cancellationToken);
    }

    public async Task<OrderResult> DeliverAsync(TenantId tenantId, UserId actorId, Guid orderId, DeliverRequest request, CancellationToken cancellationToken)
    {
        if (await ScopeAsync(tenantId, actorId, PermissionKeys.DeliveryWork, cancellationToken) is null) return OrderResult.Forbidden;
        var receiver = request.ReceiverName?.Trim();
        var note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim();
        var lines = request.Lines;
        if (orderId == Guid.Empty || string.IsNullOrEmpty(receiver) || receiver.Length > 100 || note is { Length: > MaxNoteLength } ||
            lines is null || lines.Count is 0 or > MaxLines ||
            lines.Any(l => l.LineNo is null or < 1 || l.Quantity is null or < 0 or > MaxQuantity || decimal.Round(l.Quantity.Value, 3) != l.Quantity || l.Reason?.Trim().Length > 300) ||
            lines.Select(l => l.LineNo).Distinct().Count() != lines.Count || lines.All(l => l.Quantity == 0))
            return OrderResult.InvalidData;
        var location = CheckLocation(request.Location, out var problem);
        if (problem is not null) return problem.Value;
        return await store.DeliverAsync(tenantId, actorId, orderId, receiver, note,
            lines.Select(l => l with { Reason = string.IsNullOrWhiteSpace(l.Reason) ? null : l.Reason.Trim() }).ToList(), location, cancellationToken);
    }

    public async Task<OrderResult> FailDeliveryAsync(TenantId tenantId, UserId actorId, Guid orderId, FailDeliveryRequest request, CancellationToken cancellationToken)
    {
        if (await ScopeAsync(tenantId, actorId, PermissionKeys.DeliveryWork, cancellationToken) is null) return OrderResult.Forbidden;
        var reason = request.Reason?.Trim();
        if (orderId == Guid.Empty || string.IsNullOrEmpty(reason) || reason.Length > 300) return OrderResult.InvalidData;
        var location = CheckLocation(request.Location, out var problem);
        return problem is not null ? problem.Value : await store.FailDeliveryAsync(tenantId, actorId, orderId, reason, location, cancellationToken);
    }

    // The position is evidence, so it is optional; but it must be a real coordinate and not a position the phone says is fake.
    private static DeliveryLocation? CheckLocation(DeliveryLocation? location, out OrderResult? problem)
    {
        problem = null;
        if (location is null || (location.Latitude is null && location.Longitude is null)) return null;
        if (location.IsMocked) { problem = OrderResult.GpsMocked; return null; }
        if (location.Latitude is not (>= -90 and <= 90) || location.Longitude is not (>= -180 and <= 180) || location.AccuracyMeters is < 0 or > 100_000 ||
            double.IsNaN(location.Latitude.Value) || double.IsNaN(location.Longitude.Value))
        {
            problem = OrderResult.InvalidData;
            return null;
        }
        return location;
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
