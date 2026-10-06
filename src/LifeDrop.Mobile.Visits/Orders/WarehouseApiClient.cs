using System.Text.Json;

namespace LifeDrop.Mobile.Visits;

public sealed record WarehouseOrderSummary(Guid Id, string Number, string CustomerName, string WarehouseName, string Status, DateTimeOffset CreatedAt);
public sealed record WarehouseOrderLine(int LineNo, string Code, string Name, string Unit, decimal Quantity, decimal? PreparedQuantity, string? ShortageReason);
public sealed record WarehouseOrderDetail(Guid Id, string Number, string Status, string CustomerName, string WarehouseName, string? Note, string? DriverEmail,
    IReadOnlyList<WarehouseOrderLine> Lines);
public sealed record PreparedLineInput(int LineNo, decimal Quantity, string? Reason);
public sealed record DriverItem(Guid Id, string Name);

// Warehouse work from the phone (plan step 2.3): the orders of the warehouses the user works in, start, report what was prepared, hand over to a
// driver. Online only; the server checks the user's warehouses and every step again.
public interface IWarehouseApiClient
{
    // Status empty = the open orders (placed, preparing, ready).
    Task<(VisitActionResult Result, IReadOnlyList<WarehouseOrderSummary> Orders)> GetOrdersAsync(string? status, CancellationToken cancellationToken = default);
    Task<(VisitActionResult Result, WarehouseOrderDetail? Order)> GetOrderAsync(Guid orderId, CancellationToken cancellationToken = default);
    Task<VisitActionResult> StartAsync(Guid orderId, CancellationToken cancellationToken = default);
    Task<VisitActionResult> ReadyAsync(Guid orderId, IReadOnlyList<PreparedLineInput> lines, CancellationToken cancellationToken = default);
    Task<(VisitActionResult Result, IReadOnlyList<DriverItem> Drivers)> GetDriversAsync(CancellationToken cancellationToken = default);
    Task<VisitActionResult> HandOverAsync(Guid orderId, Guid driverId, CancellationToken cancellationToken = default);
}

public sealed class WarehouseApiClient : IWarehouseApiClient
{
    private readonly OrdersHttp _http;

    public WarehouseApiClient(HttpClient httpClient, LifeDrop.Mobile.Auth.IAuthSession session) =>
        _http = new OrdersHttp(httpClient ?? throw new ArgumentNullException(nameof(httpClient)), session ?? throw new ArgumentNullException(nameof(session)));

    public async Task<(VisitActionResult Result, IReadOnlyList<WarehouseOrderSummary> Orders)> GetOrdersAsync(string? status, CancellationToken cancellationToken = default)
    {
        var (result, items) = await _http.CallAsync<List<OrderDto>>(HttpMethod.Get, "/warehouse/orders?limit=100" + (string.IsNullOrEmpty(status) ? "" : "&status=" + status), null, cancellationToken);
        if (result != VisitActionResult.Success) return (result, Array.Empty<WarehouseOrderSummary>());
        if (items is null || items.Any(o => o is null || o.Id == Guid.Empty || string.IsNullOrWhiteSpace(o.Number) || string.IsNullOrWhiteSpace(o.Status)))
            return (VisitActionResult.ServerError, Array.Empty<WarehouseOrderSummary>());
        return (result, items.Select(o => new WarehouseOrderSummary(o.Id, o.Number!, o.CustomerName ?? "", o.WarehouseName ?? "", o.Status!, o.CreatedAt)).ToList());
    }

    public async Task<(VisitActionResult Result, WarehouseOrderDetail? Order)> GetOrderAsync(Guid orderId, CancellationToken cancellationToken = default)
    {
        var (result, o) = await _http.CallAsync<OrderDto>(HttpMethod.Get, $"/warehouse/orders/{orderId}", null, cancellationToken);
        if (result != VisitActionResult.Success) return (result, null);
        if (o is null || o.Id == Guid.Empty || string.IsNullOrWhiteSpace(o.Number) || string.IsNullOrWhiteSpace(o.Status) || o.Items is null || o.Items.Any(l => l is null || l.LineNo < 1))
            return (VisitActionResult.ServerError, null);
        return (result, new WarehouseOrderDetail(o.Id, o.Number!, o.Status!, o.CustomerName ?? "", o.WarehouseName ?? "", o.Note, o.DriverEmail,
            o.Items.Select(l => new WarehouseOrderLine(l.LineNo, l.ProductCode ?? "", l.ProductName ?? "", l.Unit ?? "", l.Quantity, l.PreparedQuantity, l.ShortageReason)).ToList()));
    }

    public async Task<VisitActionResult> StartAsync(Guid orderId, CancellationToken cancellationToken = default) =>
        (await _http.CallAsync<object>(HttpMethod.Post, $"/warehouse/orders/{orderId}/start", null, cancellationToken)).Result;

    public async Task<VisitActionResult> ReadyAsync(Guid orderId, IReadOnlyList<PreparedLineInput> lines, CancellationToken cancellationToken = default) =>
        (await _http.CallAsync<object>(HttpMethod.Post, $"/warehouse/orders/{orderId}/ready",
            new { lines = lines.Select(l => new { lineNo = l.LineNo, quantity = l.Quantity, reason = l.Reason }).ToList() }, cancellationToken)).Result;

    public async Task<(VisitActionResult Result, IReadOnlyList<DriverItem> Drivers)> GetDriversAsync(CancellationToken cancellationToken = default)
    {
        var (result, items) = await _http.CallAsync<List<DriverDto>>(HttpMethod.Get, "/warehouse/drivers", null, cancellationToken);
        if (result != VisitActionResult.Success) return (result, Array.Empty<DriverItem>());
        if (items is null || items.Any(d => d is null || d.Id == Guid.Empty)) return (VisitActionResult.ServerError, Array.Empty<DriverItem>());
        return (result, items.Select(d => new DriverItem(d.Id, string.IsNullOrWhiteSpace(d.Email) ? d.Id.ToString() : d.Email!)).ToList());
    }

    public async Task<VisitActionResult> HandOverAsync(Guid orderId, Guid driverId, CancellationToken cancellationToken = default) =>
        (await _http.CallAsync<object>(HttpMethod.Post, $"/warehouse/orders/{orderId}/handover", new { driverId }, cancellationToken)).Result;

    private sealed record LineDto(int LineNo, string? ProductCode, string? ProductName, string? Unit, decimal Quantity, decimal? PreparedQuantity, string? ShortageReason);
    private sealed record OrderDto(Guid Id, string? Number, string? CustomerName, string? WarehouseName, string? Status, string? Note, string? DriverEmail, DateTimeOffset CreatedAt, List<LineDto>? Items);
    private sealed record DriverDto(Guid Id, string? Email);
}

// What the warehouse worker types while preparing an order (plan step 2.3): the prepared quantity of every line (empty = as ordered) and a
// reason for every line that is short. Ready is possible only when every line is valid.
public sealed class PreparationDraft(WarehouseOrderDetail order)
{
    private readonly Dictionary<int, decimal> _quantities = [];
    private readonly Dictionary<int, string> _reasons = [];

    public WarehouseOrderDetail Order { get; } = order;

    // False for text that is not a quantity or is more than ordered (the old value stays). Empty means "as ordered".
    public bool SetQuantity(int lineNo, string? text)
    {
        var line = Order.Lines.FirstOrDefault(l => l.LineNo == lineNo);
        var value = OrderDraft.ParseQuantity(text);
        if (line is null || value is null || (value > line.Quantity)) return false;
        if (string.IsNullOrWhiteSpace(text)) _quantities.Remove(lineNo);
        else _quantities[lineNo] = value.Value;
        if (QuantityOf(lineNo) == line.Quantity) _reasons.Remove(lineNo);
        return true;
    }

    public void SetReason(int lineNo, string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) _reasons.Remove(lineNo);
        else _reasons[lineNo] = reason.Trim();
    }

    public decimal QuantityOf(int lineNo) => _quantities.TryGetValue(lineNo, out var q) ? q : Order.Lines.First(l => l.LineNo == lineNo).Quantity;
    public bool IsShort(int lineNo) => QuantityOf(lineNo) < Order.Lines.First(l => l.LineNo == lineNo).Quantity;
    public string? ReasonOf(int lineNo) => _reasons.GetValueOrDefault(lineNo);

    public bool CanMarkReady => Order.Status == "preparing" && Order.Lines.Count > 0 && Order.Lines.All(l => !IsShort(l.LineNo) || ReasonOf(l.LineNo) is { Length: > 0 });

    public IReadOnlyList<PreparedLineInput> Lines => Order.Lines.Select(l => new PreparedLineInput(l.LineNo, QuantityOf(l.LineNo), IsShort(l.LineNo) ? ReasonOf(l.LineNo) : null)).ToList();
}
