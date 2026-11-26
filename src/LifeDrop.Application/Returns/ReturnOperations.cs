using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LifeDrop.Application.Catalog;
using LifeDrop.Application.Customers;
using LifeDrop.Application.Orders;
using LifeDrop.Application.Roles;
using LifeDrop.Application.Visits;
using LifeDrop.Domain.Common;

namespace LifeDrop.Application.Returns;

// Customer returns (plan step 5.54). A return belongs to a customer (optionally to one of its delivered orders) and has one line per product: quantity, reason and where the goods go:
// 'warehouse' (back to the stock of the chosen warehouse) or 'damaged' (damaged or expired goods, counted apart and never added to the stock that can be sold). The credit of a line is the unit
// price of the order line (without an order: today's price of the customer) x quantity. One currency per return. The sales manager approves or rejects; only an approved return reduces the
// customer's balance and puts the 'warehouse' lines back on the shelf. Nothing is deleted: every change is in the audit log.
public sealed record ReturnItemRequest(Guid? ProductId, decimal? Quantity, string? Reason, string? Destination);

// WarehouseId is needed when a line goes back to a warehouse. ClientRef makes a retry of a lost answer return the same return.
public sealed record CreateReturnRequest(Guid? CustomerId, Guid? OrderId, Guid? WarehouseId, string? Note, Guid? ClientRef, IReadOnlyList<ReturnItemRequest>? Items);
// Lines (plan step 5.56): the line numbers to approve; the others are rejected. Null approves every line. Only used by approve.
public sealed record DecideReturnRequest(string? Note, IReadOnlyList<int>? Lines = null);

// StockApplied: for an approved 'warehouse' line, whether the quantity was added to the warehouse (false when that warehouse does not count the product); null otherwise.
public sealed record ReturnLineDto(int LineNo, Guid ProductId, string ProductCode, string ProductName, string Unit, decimal Quantity, decimal UnitPrice, decimal LineTotal,
    string Reason, string Destination, bool? StockApplied,
    // Plan step 5.56: approved | rejected when the sales manager decided line by line (null while undecided).
    string? Decision = null);

// Number: "R-000001". Status: requested | approved | rejected | cancelled. PricesHidden: the reader must not see prices (every amount is 0).
public sealed record ReturnDto(Guid Id, string Number, Guid CustomerId, string CustomerCode, string CustomerName, Guid? OrderId, string? OrderNumber, Guid? WarehouseId, string? WarehouseName,
    string Status, string Currency, decimal Total, string? Note, Guid CreatedBy, string? CreatedByEmail, string? DecisionNote, string? DecidedByEmail, DateTimeOffset? DecidedAt,
    DateTimeOffset CreatedAt, IReadOnlyList<ReturnLineDto> Items, bool PricesHidden = false,
    // Pictures of the goods (plan step 5.55): ids of GET /returns/{id}/photos/{photoId}, oldest first.
    IReadOnlyList<Guid>? PhotoIds = null);

public sealed record CreatedReturnDto(Guid Id, string Number, string Currency, decimal Total, bool Replayed);

// Approved damaged and expired goods of a period, by product and reason (GET /returns/damaged).
public sealed record DamagedGoodsDto(Guid ProductId, string ProductCode, string ProductName, string Unit, string Reason, decimal Quantity, decimal Value, string Currency);

public enum ReturnResult { Success, Forbidden, NotFound, InvalidData, Conflict, TooMuch, CurrencyMismatch, TooManyPhotos }

public sealed record ReturnPhoto(string ContentType, byte[] Data);

// How much of the returns a user may see: everything, a region (and the user's own), or only what the user wrote.
public enum ReturnView { Everything, Region, Mine }

// A line as the service worked it out: the price is the server's, never the phone's.
public sealed record NewReturnLine(Guid ProductId, string ProductCode, string ProductName, string Unit, decimal Quantity, decimal UnitPrice, decimal LineTotal, string Reason, string Destination);
public sealed record NewReturn(Guid CustomerId, Guid? OrderId, Guid? WarehouseId, string Currency, decimal Total, string? Note, Guid ClientRef, IReadOnlyList<NewReturnLine> Lines);

public interface IReturnStore
{
    // NotFound: the warehouse is missing. InvalidData: the warehouse is not an active company warehouse. A retry with the same ClientRef (same author and customer) gives back the return; else Conflict.
    Task<(ReturnResult Result, CreatedReturnDto? Return)> CreateAsync(TenantId tenantId, UserId actorId, NewReturn value, CancellationToken cancellationToken);
    // How much of each product of the order is in a return that is still requested or already approved.
    Task<IReadOnlyDictionary<Guid, decimal>> ReturnedQuantitiesAsync(TenantId tenantId, Guid orderId, CancellationToken cancellationToken);
    Task<IReadOnlyList<ReturnDto>> ListAsync(TenantId tenantId, UserId actorId, ReturnView view, Guid? regionId, Guid? customerId, string? status, int limit, DateTimeOffset? before, Guid? beforeId,
        CancellationToken cancellationToken);
    Task<ReturnDto?> GetAsync(TenantId tenantId, UserId actorId, ReturnView view, Guid? regionId, Guid returnId, CancellationToken cancellationToken);
    // requested -> approved: the 'warehouse' lines go back on the stock where the warehouse counts the product; the author is told. Conflict: not requested any more.
    // `lines`: the line numbers to approve (the others are rejected), null = all. InvalidData: a number that is not a line of the return.
    Task<ReturnResult> ApproveAsync(TenantId tenantId, UserId actorId, Guid returnId, string? note, IReadOnlyList<int>? lines, CancellationToken cancellationToken);
    // requested -> rejected with a reason; the author is told.
    Task<ReturnResult> RejectAsync(TenantId tenantId, UserId actorId, Guid returnId, string reason, CancellationToken cancellationToken);
    // requested -> cancelled, only by its author (Forbidden for anyone else).
    Task<ReturnResult> CancelAsync(TenantId tenantId, UserId actorId, Guid returnId, string reason, CancellationToken cancellationToken);
    Task<IReadOnlyList<DamagedGoodsDto>> DamagedAsync(TenantId tenantId, UserId actorId, ReturnView view, Guid? regionId, DateOnly from, DateOnly to, CancellationToken cancellationToken);
    // Plan step 5.55: a picture of the goods, only by the author while the return is requested, at most maxPhotos. NotFound: no such return; Forbidden: not the author; Conflict: not requested.
    Task<ReturnResult> AddPhotoAsync(TenantId tenantId, UserId author, Guid returnId, string contentType, byte[] data, int maxPhotos, CancellationToken cancellationToken);
    Task<ReturnPhoto?> GetPhotoAsync(TenantId tenantId, UserId actorId, ReturnView view, Guid? regionId, Guid returnId, Guid photoId, CancellationToken cancellationToken);
}

public sealed class ReturnService(IReturnStore store, IProductStore products, IOrderStore orders, IPermissionStore permissions, ICustomerStore customers, PriceVisibility visibility, TimeProvider clock)
{
    public const int MaxLines = 100;
    public const int MaxPhotos = 3;
    public const int MaxPhotoBytes = 3 * 1024 * 1024;
    public static readonly string[] PhotoTypes = ["image/jpeg", "image/png", "image/webp"];
    public const decimal MaxQuantity = 9_999_999m;
    public static readonly string[] Reasons = ["damaged", "expired", "wrong_item", "customer_request", "other"];
    public static readonly string[] Destinations = ["warehouse", "damaged"];

    // Damaged and expired goods never go back on the shelf by default; the other reasons do. The author may change it per line.
    public static string DefaultDestination(string reason) => reason is "damaged" or "expired" ? "damaged" : "warehouse";

    public async Task<(ReturnResult Result, CreatedReturnDto? Return)> CreateAsync(TenantId tenantId, UserId actorId, CreateReturnRequest request, CancellationToken cancellationToken)
    {
        var scope = await ScopeAsync(tenantId, actorId, PermissionKeys.ReturnsCreate, cancellationToken);
        if (scope is null) return (ReturnResult.Forbidden, null);
        var note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim();
        var items = request.Items;
        if (request.CustomerId is null || request.CustomerId == Guid.Empty || request.OrderId == Guid.Empty || request.WarehouseId == Guid.Empty || request.ClientRef is null || request.ClientRef == Guid.Empty ||
            note?.Length > 500 || items is null || items.Count is < 1 or > MaxLines ||
            items.Any(i => i.ProductId is null || i.ProductId == Guid.Empty || i.Quantity is not > 0 or null || i.Quantity > MaxQuantity || decimal.Round(i.Quantity!.Value, 3) != i.Quantity ||
                           i.Reason is null || !Reasons.Contains(i.Reason) || (i.Destination is not null && !Destinations.Contains(i.Destination))) ||
            items.Select(i => i.ProductId).Distinct().Count() != items.Count)
            return (ReturnResult.InvalidData, null);
        var wanted = items.Select(i => (Item: i, Destination: i.Destination ?? DefaultDestination(i.Reason!))).ToList();
        // The warehouse is always needed: goods that go back are added to its stock, damaged and expired goods are held apart in the same warehouse (plan step 5.56).
        if (request.WarehouseId is null) return (ReturnResult.InvalidData, null);

        var customer = await customers.GetByIdAsync(tenantId, new CustomerId(request.CustomerId.Value), scope, cancellationToken);
        if (customer is null) return (ReturnResult.NotFound, null);

        var lines = new List<NewReturnLine>(wanted.Count);
        string currency;
        if (request.OrderId is { } orderId)
        {
            // Against a delivered order of this customer: the price is the price of the order line and no more than was delivered (minus what other returns already took) can come back.
            var orderScope = await ScopeAsync(tenantId, actorId, PermissionKeys.OrdersRead, cancellationToken) ?? scope;
            var order = await orders.GetAsync(tenantId, orderId, orderScope, cancellationToken);
            if (order is null || order.CustomerId != customer.Id) return (ReturnResult.NotFound, null);
            if (order.Status != "delivered") return (ReturnResult.InvalidData, null);
            var already = await store.ReturnedQuantitiesAsync(tenantId, orderId, cancellationToken);
            foreach (var (item, destination) in wanted)
            {
                var line = order.Items.FirstOrDefault(l => l.ProductId == item.ProductId);
                if (line is null) return (ReturnResult.InvalidData, null);
                var free = (line.DeliveredQuantity ?? line.Quantity) - already.GetValueOrDefault(line.ProductId);
                if (item.Quantity > free) return (ReturnResult.TooMuch, null);
                lines.Add(new NewReturnLine(line.ProductId, line.ProductCode, line.ProductName, line.Unit, item.Quantity!.Value, line.UnitPrice,
                    decimal.Round(line.UnitPrice * item.Quantity.Value, 3, MidpointRounding.AwayFromZero), item.Reason!, destination));
            }
            currency = order.Currency;
        }
        else
        {
            // Without an order: today's price of this customer (server side), as an order would be priced.
            var pricing = await products.GetPricingAsync(tenantId, customer.Id, IraqCalendar.Today(clock), cancellationToken);
            var currencies = new HashSet<string>();
            foreach (var (item, destination) in wanted)
            {
                var row = pricing.Products.FirstOrDefault(p => p.Product.Id == item.ProductId);
                if (row is null) return (ReturnResult.InvalidData, null);
                var list = row.CityPrice ?? row.Product.BasePrice;
                var (unitPrice, _) = PriceCalculator.Apply(list, row.Product.Id, pricing.Discounts);
                currencies.Add(row.Product.Currency);
                lines.Add(new NewReturnLine(row.Product.Id, row.Product.Code, row.Product.Name, row.Product.Unit, item.Quantity!.Value, unitPrice,
                    decimal.Round(unitPrice * item.Quantity.Value, 3, MidpointRounding.AwayFromZero), item.Reason!, destination));
            }
            if (currencies.Count > 1) return (ReturnResult.CurrencyMismatch, null);
            currency = currencies.Single();
        }

        var total = lines.Sum(l => l.LineTotal);
        var (result, created) = await store.CreateAsync(tenantId, actorId, new NewReturn(customer.Id, request.OrderId, request.WarehouseId, currency, total, note, request.ClientRef.Value, lines), cancellationToken);
        if (created is not null && (await visibility.HiddenForAsync(tenantId, actorId, [customer.Id], cancellationToken)).Contains(customer.Id)) created = created with { Total = 0 };
        return (result, created);
    }

    public async Task<(ReturnResult Result, IReadOnlyList<ReturnDto> Items)> ListAsync(TenantId tenantId, UserId actorId, Guid? customerId, string? status, bool mine, int? limit,
        DateTimeOffset? before, Guid? beforeId, CancellationToken cancellationToken)
    {
        var (view, region) = await ViewAsync(tenantId, actorId, mine, cancellationToken);
        if (view is null) return (ReturnResult.Forbidden, Array.Empty<ReturnDto>());
        var take = limit ?? 50;
        if (take is < 1 or > 100 || (before is null) != (beforeId is null) || customerId == Guid.Empty || status is not (null or "requested" or "approved" or "rejected" or "cancelled"))
            return (ReturnResult.InvalidData, Array.Empty<ReturnDto>());
        var items = await store.ListAsync(tenantId, actorId, view.Value, region, customerId, status, take, before, beforeId, cancellationToken);
        return (ReturnResult.Success, await MaskAsync(tenantId, actorId, items, cancellationToken));
    }

    public async Task<(ReturnResult Result, ReturnDto? Return)> GetAsync(TenantId tenantId, UserId actorId, Guid returnId, CancellationToken cancellationToken)
    {
        var (view, region) = await ViewAsync(tenantId, actorId, false, cancellationToken);
        if (view is null) return (ReturnResult.Forbidden, null);
        var found = returnId == Guid.Empty ? null : await store.GetAsync(tenantId, actorId, view.Value, region, returnId, cancellationToken);
        return found is null ? (ReturnResult.NotFound, null) : (ReturnResult.Success, (await MaskAsync(tenantId, actorId, [found], cancellationToken))[0]);
    }

    public async Task<ReturnResult> ApproveAsync(TenantId tenantId, UserId actorId, Guid returnId, DecideReturnRequest request, CancellationToken cancellationToken)
    {
        var note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim();
        var chosen = request.Lines?.Distinct().ToList();
        if (note?.Length > 500 || chosen is not null && (chosen.Count == 0 || chosen.Any(n => n < 1 || n > MaxLines))) return ReturnResult.InvalidData;
        var denied = await CheckDeciderAsync(tenantId, actorId, returnId, cancellationToken);
        return denied ?? await store.ApproveAsync(tenantId, actorId, returnId, note, chosen, cancellationToken);
    }

    public async Task<ReturnResult> RejectAsync(TenantId tenantId, UserId actorId, Guid returnId, DecideReturnRequest request, CancellationToken cancellationToken)
    {
        var reason = request.Note?.Trim();
        if (string.IsNullOrEmpty(reason) || reason.Length > 500) return ReturnResult.InvalidData;
        var denied = await CheckDeciderAsync(tenantId, actorId, returnId, cancellationToken);
        return denied ?? await store.RejectAsync(tenantId, actorId, returnId, reason, cancellationToken);
    }

    public async Task<ReturnResult> CancelAsync(TenantId tenantId, UserId actorId, Guid returnId, DecideReturnRequest request, CancellationToken cancellationToken)
    {
        if (await ScopeAsync(tenantId, actorId, PermissionKeys.ReturnsCreate, cancellationToken) is null) return ReturnResult.Forbidden;
        var reason = request.Note?.Trim();
        if (returnId == Guid.Empty || string.IsNullOrEmpty(reason) || reason.Length > 500) return ReturnResult.InvalidData;
        return await store.CancelAsync(tenantId, actorId, returnId, reason, cancellationToken);
    }

    // A picture of the goods (raw bytes; JPEG, PNG or WEBP, at most 3 MB, three per return), added by the author while the return waits for approval.
    public async Task<ReturnResult> AddPhotoAsync(TenantId tenantId, UserId actorId, Guid returnId, string? contentType, byte[] data, CancellationToken cancellationToken)
    {
        if (await ScopeAsync(tenantId, actorId, PermissionKeys.ReturnsCreate, cancellationToken) is null) return ReturnResult.Forbidden;
        var type = contentType?.Split(';')[0].Trim().ToLowerInvariant();
        if (returnId == Guid.Empty || type is null || !PhotoTypes.Contains(type) || data.Length is < 1 or > MaxPhotoBytes) return ReturnResult.InvalidData;
        return await store.AddPhotoAsync(tenantId, actorId, returnId, type, data, MaxPhotos, cancellationToken);
    }

    public async Task<(ReturnResult Result, ReturnPhoto? Photo)> GetPhotoAsync(TenantId tenantId, UserId actorId, Guid returnId, Guid photoId, CancellationToken cancellationToken)
    {
        var (view, region) = await ViewAsync(tenantId, actorId, false, cancellationToken);
        if (view is null) return (ReturnResult.Forbidden, null);
        var photo = returnId == Guid.Empty || photoId == Guid.Empty ? null : await store.GetPhotoAsync(tenantId, actorId, view.Value, region, returnId, photoId, cancellationToken);
        return photo is null ? (ReturnResult.NotFound, null) : (ReturnResult.Success, photo);
    }

    // Approved damaged and expired goods between two days (Iraq days), by product and reason.
    public async Task<(ReturnResult Result, IReadOnlyList<DamagedGoodsDto> Items)> DamagedAsync(TenantId tenantId, UserId actorId, DateOnly? from, DateOnly? to, CancellationToken cancellationToken)
    {
        var (view, region) = await ViewAsync(tenantId, actorId, false, cancellationToken);
        if (view is null || view == ReturnView.Mine && await ScopeAsync(tenantId, actorId, PermissionKeys.ReturnsRead, cancellationToken) is null) return (ReturnResult.Forbidden, Array.Empty<DamagedGoodsDto>());
        var end = to ?? IraqCalendar.Today(clock);
        var start = from ?? end.AddDays(-30);
        if (start > end || end.DayNumber - start.DayNumber > 366) return (ReturnResult.InvalidData, Array.Empty<DamagedGoodsDto>());
        return (ReturnResult.Success, await store.DamagedAsync(tenantId, actorId, view.Value, region, start, end, cancellationToken));
    }

    // The sales manager (returns.approve) decides, for returns inside the data scope of that permission.
    private async Task<ReturnResult?> CheckDeciderAsync(TenantId tenantId, UserId actorId, Guid returnId, CancellationToken cancellationToken)
    {
        var scope = await ScopeAsync(tenantId, actorId, PermissionKeys.ReturnsApprove, cancellationToken);
        if (scope is null) return ReturnResult.Forbidden;
        if (returnId == Guid.Empty) return ReturnResult.InvalidData;
        var view = scope.Scope == DataScope.Tenant ? ReturnView.Everything : scope.RegionId is not null ? ReturnView.Region : ReturnView.Mine;
        return await store.GetAsync(tenantId, actorId, view, scope.RegionId, returnId, cancellationToken) is null ? ReturnResult.NotFound : null;
    }

    // Everything for tenant-wide readers, the region for supervisors; a user who only creates sees what they wrote. `mine` narrows any reader to their own.
    private async Task<(ReturnView? View, Guid? Region)> ViewAsync(TenantId tenantId, UserId actorId, bool mine, CancellationToken cancellationToken)
    {
        var read = mine ? null : await ScopeAsync(tenantId, actorId, PermissionKeys.ReturnsRead, cancellationToken);
        if (read is not null) return read.Scope == DataScope.Tenant ? (ReturnView.Everything, null) : read.RegionId is { } r ? (ReturnView.Region, r) : (ReturnView.Mine, null);
        return await ScopeAsync(tenantId, actorId, PermissionKeys.ReturnsCreate, cancellationToken) is not null || await ScopeAsync(tenantId, actorId, PermissionKeys.ReturnsRead, cancellationToken) is not null
            ? (ReturnView.Mine, null) : (null, null);
    }

    private async Task<IReadOnlyList<ReturnDto>> MaskAsync(TenantId tenantId, UserId actorId, IReadOnlyList<ReturnDto> items, CancellationToken cancellationToken)
    {
        if (items.Count == 0) return items;
        var hidden = await visibility.HiddenForAsync(tenantId, actorId, items.Select(i => i.CustomerId).Distinct().ToList(), cancellationToken);
        return items.Select(i => hidden.Contains(i.CustomerId) ? i with { Total = 0, PricesHidden = true, Items = i.Items.Select(l => l with { UnitPrice = 0, LineTotal = 0 }).ToList() } : i).ToList();
    }

    private Task<AccessScope?> ScopeAsync(TenantId tenantId, UserId actorId, string permission, CancellationToken cancellationToken) =>
        tenantId.IsEmpty || actorId.IsEmpty ? Task.FromResult<AccessScope?>(null) : permissions.GetAccessScopeAsync(tenantId, actorId, permission, cancellationToken);
}
