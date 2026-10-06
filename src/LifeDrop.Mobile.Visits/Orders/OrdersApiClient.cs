using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using LifeDrop.Mobile.Auth;
using LifeDrop.Mobile.Auth.Models;

namespace LifeDrop.Mobile.Visits;

public sealed record OrderWarehouse(Guid Id, string Code, string Name);

// A product with the price this customer pays today, as the server works it out (general or city price, then the customer's discounts).
// The app only shows it; the order carries no price, the server sets it again when the order is placed.
public sealed record PricedProduct(Guid ProductId, string Code, string Name, string? NameCkb, string? NameAr, string? NameEn, string Unit, string Currency,
    decimal ListPrice, decimal Price)
{
    public string DisplayName(string language)
    {
        var name = language switch { "ckb" => NameCkb, "ar" => NameAr, "en" => NameEn, _ => null };
        return string.IsNullOrWhiteSpace(name) ? Name : name;
    }
}

// Replayed: the server had this order already (a retry of a lost answer).
public sealed record PlacedOrder(Guid Id, string Number, string Currency, decimal Total, bool Replayed);

public sealed record OrderLineInput(Guid ProductId, decimal Quantity);

// Placing an order from the phone (plan step 2.2). Online only: an order counts as placed only after the server accepts it.
public interface IOrdersApiClient
{
    Task<(VisitActionResult Result, IReadOnlyList<OrderWarehouse> Warehouses)> GetWarehousesAsync(CancellationToken cancellationToken = default);
    Task<(VisitActionResult Result, IReadOnlyList<PricedProduct> Products)> GetPricesAsync(Guid customerId, DateOnly day, CancellationToken cancellationToken = default);
    // ClientRef stays the same for every retry of one order, so a lost answer never makes two orders.
    // offline: the order was kept on the phone without internet; the server then places it even when its visit was cancelled meanwhile (without the visit), plan step 2.13.
    Task<(VisitActionResult Result, PlacedOrder? Order)> PlaceAsync(Guid customerId, Guid warehouseId, Guid? visitId, Guid clientRef, string? note,
        IReadOnlyList<OrderLineInput> lines, CancellationToken cancellationToken = default, bool offline = false);
}

public sealed class OrdersApiClient : IOrdersApiClient
{
    private readonly OrdersHttp _http;

    public OrdersApiClient(HttpClient httpClient, IAuthSession session)
    {
        _http = new OrdersHttp(httpClient ?? throw new ArgumentNullException(nameof(httpClient)), session ?? throw new ArgumentNullException(nameof(session)));
    }

    public async Task<(VisitActionResult Result, IReadOnlyList<OrderWarehouse> Warehouses)> GetWarehousesAsync(CancellationToken cancellationToken = default)
    {
        var (result, items) = await CallAsync<List<WarehouseDto>>(HttpMethod.Get, "/warehouses", null, cancellationToken);
        if (result != VisitActionResult.Success) return (result, Array.Empty<OrderWarehouse>());
        if (items is null || items.Any(w => w is null || w.Id == Guid.Empty || string.IsNullOrWhiteSpace(w.Name))) return (VisitActionResult.ServerError, Array.Empty<OrderWarehouse>());
        return (result, items.Select(w => new OrderWarehouse(w.Id, w.Code ?? "", w.Name!)).ToList());
    }

    public async Task<(VisitActionResult Result, IReadOnlyList<PricedProduct> Products)> GetPricesAsync(Guid customerId, DateOnly day, CancellationToken cancellationToken = default)
    {
        var (result, items) = await CallAsync<List<PriceDto>>(HttpMethod.Get, $"/customers/{customerId}/prices?date={day:yyyy-MM-dd}", null, cancellationToken);
        if (result != VisitActionResult.Success) return (result, Array.Empty<PricedProduct>());
        if (items is null || items.Any(p => p is null || p.ProductId == Guid.Empty || string.IsNullOrWhiteSpace(p.Code) || string.IsNullOrWhiteSpace(p.Currency)))
            return (VisitActionResult.ServerError, Array.Empty<PricedProduct>());
        return (result, items.Select(p => new PricedProduct(p.ProductId, p.Code!, p.Name ?? "", p.NameCkb, p.NameAr, p.NameEn, p.Unit ?? "", p.Currency!, p.ListPrice, p.Price)).ToList());
    }

    public async Task<(VisitActionResult Result, PlacedOrder? Order)> PlaceAsync(Guid customerId, Guid warehouseId, Guid? visitId, Guid clientRef, string? note,
        IReadOnlyList<OrderLineInput> lines, CancellationToken cancellationToken = default, bool offline = false)
    {
        // Only products and quantities go to the server: it knows the prices.
        var body = new { customerId, warehouseId, visitId, clientRef, note, items = lines.Select(l => new { productId = l.ProductId, quantity = l.Quantity }).ToList(), detachVisitIfInvalid = offline ? true : (bool?)null };
        var (result, dto) = await CallAsync<PlacedDto>(HttpMethod.Post, "/orders", body, cancellationToken);
        if (result != VisitActionResult.Success) return (result, null);
        return dto is null || dto.Id == Guid.Empty || string.IsNullOrWhiteSpace(dto.Number)
            ? (VisitActionResult.ServerError, null)
            : (result, new PlacedOrder(dto.Id, dto.Number, dto.Currency ?? "", dto.Total, dto.Replayed));
    }

    private Task<(VisitActionResult Result, T? Data)> CallAsync<T>(HttpMethod method, string path, object? body, CancellationToken cancellationToken) =>
        _http.CallAsync<T>(method, path, body, cancellationToken);

    private sealed record WarehouseDto(Guid Id, string? Code, string? Name);
    private sealed record PriceDto(Guid ProductId, string? Code, string? Name, string? NameCkb, string? NameAr, string? NameEn, string? Unit, string? Currency, decimal ListPrice, decimal Price);
    private sealed record PlacedDto(Guid Id, string? Number, string? Currency, decimal Total, bool Replayed);
}
