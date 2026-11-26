using LifeDrop.Mobile.Auth;

namespace LifeDrop.Mobile.Visits;

// Goods moving between warehouses from the phone (plan step 5.61). Online only: the server takes the goods out of the sender when it accepts the transfer.
// Bucket: sellable | damaged (damaged and expired goods stay damaged at the receiver).
public sealed record TransferLineItem(int LineNo, Guid ProductId, string ProductCode, string ProductName, string Unit, string Bucket, decimal Quantity, decimal? ReceivedQuantity);
// Status: sent (on the road) | received | cancelled.
public sealed record TransferItem(Guid Id, string Number, Guid FromWarehouseId, string FromName, Guid ToWarehouseId, string ToName, string Status, string? Note, DateTimeOffset CreatedAt,
    string? CloseNote, IReadOnlyList<TransferLineItem> Lines);
public sealed record TransferWarehouse(Guid Id, string Code, string Name, string? CityName);
public sealed record TransferLineInput(Guid ProductId, decimal Quantity, string Bucket);

public interface ITransfersApiClient
{
    // The transfers of the user's warehouses, newest first (50); status "" = all.
    Task<(VisitActionResult Result, IReadOnlyList<TransferItem> Items)> ListAsync(string status, CancellationToken cancellationToken = default);
    // The warehouses goods can be sent to.
    Task<(VisitActionResult Result, IReadOnlyList<TransferWarehouse> Items)> ListWarehousesAsync(CancellationToken cancellationToken = default);
    // ClientRef stays the same for every retry of one transfer, so a lost answer never sends the goods twice.
    Task<(VisitActionResult Result, string? Number)> CreateAsync(Guid fromWarehouseId, Guid toWarehouseId, Guid clientRef, string? note, IReadOnlyList<TransferLineInput> lines, CancellationToken cancellationToken = default);
    // What arrived per line (a line that is left out arrives in full).
    Task<VisitActionResult> ReceiveAsync(Guid transferId, IReadOnlyDictionary<int, decimal> arrived, string? note, CancellationToken cancellationToken = default);
    Task<VisitActionResult> CancelAsync(Guid transferId, string? note, CancellationToken cancellationToken = default);
}

public sealed class TransfersApiClient : ITransfersApiClient
{
    private readonly OrdersHttp _http;

    public TransfersApiClient(HttpClient httpClient, IAuthSession session)
    {
        _http = new OrdersHttp(httpClient ?? throw new ArgumentNullException(nameof(httpClient)), session ?? throw new ArgumentNullException(nameof(session)));
    }

    public async Task<(VisitActionResult Result, IReadOnlyList<TransferItem> Items)> ListAsync(string status, CancellationToken cancellationToken = default)
    {
        var (result, items) = await _http.CallAsync<List<TransferDto>>(HttpMethod.Get, "/transfers?limit=50" + (string.IsNullOrEmpty(status) ? "" : "&status=" + Uri.EscapeDataString(status)), null, cancellationToken);
        if (result != VisitActionResult.Success) return (result, Array.Empty<TransferItem>());
        if (items is null || items.Any(t => t is null || t.Id == Guid.Empty || string.IsNullOrWhiteSpace(t.Number) || string.IsNullOrWhiteSpace(t.Status))) return (VisitActionResult.ServerError, Array.Empty<TransferItem>());
        return (result, items.Select(t => new TransferItem(t.Id, t.Number!, t.FromWarehouseId, $"{t.FromWarehouseCode} · {t.FromWarehouseName}", t.ToWarehouseId, $"{t.ToWarehouseCode} · {t.ToWarehouseName}", t.Status!, t.Note, t.CreatedAt, t.CloseNote,
            (t.Items ?? []).Select(l => new TransferLineItem(l.LineNo, l.ProductId, l.ProductCode ?? "", l.ProductName ?? "", l.Unit ?? "", l.Bucket ?? "sellable", l.Quantity, l.ReceivedQuantity)).ToList())).ToList());
    }

    public async Task<(VisitActionResult Result, IReadOnlyList<TransferWarehouse> Items)> ListWarehousesAsync(CancellationToken cancellationToken = default)
    {
        var (result, items) = await _http.CallAsync<List<WarehouseDto>>(HttpMethod.Get, "/transfers/warehouses", null, cancellationToken);
        if (result != VisitActionResult.Success) return (result, Array.Empty<TransferWarehouse>());
        if (items is null || items.Any(w => w is null || w.Id == Guid.Empty || string.IsNullOrWhiteSpace(w.Name))) return (VisitActionResult.ServerError, Array.Empty<TransferWarehouse>());
        return (result, items.Select(w => new TransferWarehouse(w.Id, w.Code ?? "", w.Name!, w.CityName)).ToList());
    }

    public async Task<(VisitActionResult Result, string? Number)> CreateAsync(Guid fromWarehouseId, Guid toWarehouseId, Guid clientRef, string? note, IReadOnlyList<TransferLineInput> lines, CancellationToken cancellationToken = default)
    {
        var body = new { fromWarehouseId, toWarehouseId, clientRef, note, items = lines.Select(l => new { productId = l.ProductId, quantity = l.Quantity, bucket = l.Bucket }).ToList() };
        var (result, dto) = await _http.CallAsync<CreatedDto>(HttpMethod.Post, "/transfers", body, cancellationToken);
        if (result != VisitActionResult.Success) return (result, null);
        return dto is null || dto.Id == Guid.Empty || string.IsNullOrWhiteSpace(dto.Number) ? (VisitActionResult.ServerError, null) : (result, dto.Number);
    }

    public async Task<VisitActionResult> ReceiveAsync(Guid transferId, IReadOnlyDictionary<int, decimal> arrived, string? note, CancellationToken cancellationToken = default) =>
        (await _http.CallAsync<object>(HttpMethod.Post, $"/transfers/{transferId}/receive", new { note, lines = arrived.Select(a => new { lineNo = a.Key, receivedQuantity = a.Value }).ToList() }, cancellationToken)).Result;

    public async Task<VisitActionResult> CancelAsync(Guid transferId, string? note, CancellationToken cancellationToken = default) =>
        (await _http.CallAsync<object>(HttpMethod.Post, $"/transfers/{transferId}/cancel", new { note }, cancellationToken)).Result;

    private sealed record LineDto(int LineNo, Guid ProductId, string? ProductCode, string? ProductName, string? Unit, string? Bucket, decimal Quantity, decimal? ReceivedQuantity);
    private sealed record TransferDto(Guid Id, string? Number, Guid FromWarehouseId, string? FromWarehouseCode, string? FromWarehouseName, Guid ToWarehouseId, string? ToWarehouseCode, string? ToWarehouseName,
        string? Status, string? Note, DateTimeOffset CreatedAt, string? CloseNote, List<LineDto>? Items);
    private sealed record WarehouseDto(Guid Id, string? Code, string? Name, string? CityName);
    private sealed record CreatedDto(Guid Id, string? Number, bool Replayed);
}
