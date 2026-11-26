using LifeDrop.Mobile.Auth;

namespace LifeDrop.Mobile.Visits;

// Customer returns from the phone (plan step 5.54). Online only: a return exists only after the server accepts it; the sales manager then approves or rejects it.
public sealed record ReturnLineInput(Guid ProductId, decimal Quantity, string Reason, string Destination);
public sealed record CreatedReturn(Guid Id, string Number, string Currency, decimal Total, bool Replayed);
public sealed record ReturnLineItem(Guid ProductId, string ProductName, string Unit, decimal Quantity, decimal LineTotal, string Reason, string Destination, bool? StockApplied);
// Status: requested | approved | rejected | cancelled. DecisionNote: what the manager (or the author, on cancel) wrote.
public sealed record MyReturn(Guid Id, string Number, string CustomerName, string Status, string Currency, decimal Total, bool PricesHidden, string? DecisionNote, DateTimeOffset CreatedAt,
    IReadOnlyList<ReturnLineItem> Lines);

public interface IReturnsApiClient
{
    // ClientRef stays the same for every retry of one return, so a lost answer never makes two returns. The warehouse is needed when a line goes back to a warehouse.
    Task<(VisitActionResult Result, CreatedReturn? Return)> CreateAsync(Guid customerId, Guid? orderId, Guid? warehouseId, Guid clientRef, string? note, IReadOnlyList<ReturnLineInput> lines,
        CancellationToken cancellationToken = default);
    // The returns the user wrote, newest first, 50 at a time (`before` = CreatedAt and id of the last one of the page before).
    Task<(VisitActionResult Result, IReadOnlyList<MyReturn> Items)> ListMineAsync(DateTimeOffset? before, Guid? beforeId, CancellationToken cancellationToken = default);
    Task<VisitActionResult> CancelAsync(Guid returnId, string reason, CancellationToken cancellationToken = default);
}

public sealed class ReturnsApiClient : IReturnsApiClient
{
    private readonly OrdersHttp _http;

    public ReturnsApiClient(HttpClient httpClient, IAuthSession session)
    {
        _http = new OrdersHttp(httpClient ?? throw new ArgumentNullException(nameof(httpClient)), session ?? throw new ArgumentNullException(nameof(session)));
    }

    public async Task<(VisitActionResult Result, CreatedReturn? Return)> CreateAsync(Guid customerId, Guid? orderId, Guid? warehouseId, Guid clientRef, string? note, IReadOnlyList<ReturnLineInput> lines,
        CancellationToken cancellationToken = default)
    {
        // Only products, quantities, reasons and where they go: the server knows the prices.
        var body = new { customerId, orderId, warehouseId, clientRef, note, items = lines.Select(l => new { productId = l.ProductId, quantity = l.Quantity, reason = l.Reason, destination = l.Destination }).ToList() };
        var (result, dto) = await _http.CallAsync<CreatedDto>(HttpMethod.Post, "/returns", body, cancellationToken);
        if (result != VisitActionResult.Success) return (result, null);
        return dto is null || dto.Id == Guid.Empty || string.IsNullOrWhiteSpace(dto.Number)
            ? (VisitActionResult.ServerError, null)
            : (result, new CreatedReturn(dto.Id, dto.Number, dto.Currency ?? "", dto.Total, dto.Replayed));
    }

    public async Task<(VisitActionResult Result, IReadOnlyList<MyReturn> Items)> ListMineAsync(DateTimeOffset? before, Guid? beforeId, CancellationToken cancellationToken = default)
    {
        var path = "/returns?mine=true&limit=50" + (before is { } at && beforeId is { } id ? $"&before={Uri.EscapeDataString(at.ToString("O"))}&beforeId={id}" : "");
        var (result, items) = await _http.CallAsync<List<ReturnDto>>(HttpMethod.Get, path, null, cancellationToken);
        if (result != VisitActionResult.Success) return (result, Array.Empty<MyReturn>());
        if (items is null || items.Any(r => r is null || r.Id == Guid.Empty || string.IsNullOrWhiteSpace(r.Number) || string.IsNullOrWhiteSpace(r.Status))) return (VisitActionResult.ServerError, Array.Empty<MyReturn>());
        return (result, items.Select(r => new MyReturn(r.Id, r.Number!, r.CustomerName ?? "", r.Status!, r.Currency ?? "", r.Total, r.PricesHidden, r.DecisionNote, r.CreatedAt,
            (r.Items ?? []).Select(l => new ReturnLineItem(l.ProductId, l.ProductName ?? "", l.Unit ?? "", l.Quantity, l.LineTotal, l.Reason ?? "", l.Destination ?? "", l.StockApplied)).ToList())).ToList());
    }

    public async Task<VisitActionResult> CancelAsync(Guid returnId, string reason, CancellationToken cancellationToken = default) =>
        (await _http.CallAsync<object>(HttpMethod.Post, $"/returns/{returnId}/cancel", new { note = reason }, cancellationToken)).Result;

    private sealed record CreatedDto(Guid Id, string? Number, string? Currency, decimal Total, bool Replayed);
    private sealed record LineDto(Guid ProductId, string? ProductName, string? Unit, decimal Quantity, decimal LineTotal, string? Reason, string? Destination, bool? StockApplied);
    private sealed record ReturnDto(Guid Id, string? Number, string? CustomerName, string? Status, string? Currency, decimal Total, bool PricesHidden, string? DecisionNote, DateTimeOffset CreatedAt, List<LineDto>? Items);
}
