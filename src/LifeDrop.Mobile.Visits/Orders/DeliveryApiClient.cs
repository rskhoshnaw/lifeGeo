namespace LifeDrop.Mobile.Visits;

public sealed record DeliverySummary(Guid Id, string Number, string CustomerName, string Status, DateTimeOffset CreatedAt);
public sealed record DeliveryLine(int LineNo, string Code, string Name, string Unit, decimal Prepared, decimal? Delivered, string? Reason);
public sealed record DeliveryDetail(Guid Id, string Number, string Status, string CustomerName, string? Phone, string? Address, double? Latitude, double? Longitude,
    string? Note, string? ReceiverName, string? FailureReason, IReadOnlyList<DeliveryLine> Lines);
public sealed record DeliveredLineInput(int LineNo, decimal Quantity, string? Reason);

// Where the phone is when a step is recorded. Evidence only; the server refuses a position the phone says is fake.
public sealed record PhoneLocation(double Latitude, double Longitude, double AccuracyMeters, bool IsMocked)
{
    public static PhoneLocation? From(DeviceFix? fix) => fix is null ? null : new PhoneLocation(fix.Latitude, fix.Longitude, fix.AccuracyMeters, fix.IsMocked);
}

// The driver's work from the phone (plan step 2.4): the orders assigned to the driver, pick up, leave, deliver or fail. Online only.
public interface IDeliveryApiClient
{
    // Status empty = the open ones (handed over, picked up, on the way).
    Task<(VisitActionResult Result, IReadOnlyList<DeliverySummary> Orders)> GetOrdersAsync(string? status, CancellationToken cancellationToken = default);
    Task<(VisitActionResult Result, DeliveryDetail? Order)> GetOrderAsync(Guid orderId, CancellationToken cancellationToken = default);
    Task<VisitActionResult> PickUpAsync(Guid orderId, PhoneLocation? location, CancellationToken cancellationToken = default);
    Task<VisitActionResult> DepartAsync(Guid orderId, PhoneLocation? location, CancellationToken cancellationToken = default);
    Task<VisitActionResult> DeliverAsync(Guid orderId, string receiverName, string? note, IReadOnlyList<DeliveredLineInput> lines, PhoneLocation? location,
        CancellationToken cancellationToken = default);
    Task<VisitActionResult> FailAsync(Guid orderId, string reason, PhoneLocation? location, CancellationToken cancellationToken = default);
}

public sealed class DeliveryApiClient : IDeliveryApiClient
{
    private readonly OrdersHttp _http;

    public DeliveryApiClient(HttpClient httpClient, LifeDrop.Mobile.Auth.IAuthSession session) =>
        _http = new OrdersHttp(httpClient ?? throw new ArgumentNullException(nameof(httpClient)), session ?? throw new ArgumentNullException(nameof(session)));

    public async Task<(VisitActionResult Result, IReadOnlyList<DeliverySummary> Orders)> GetOrdersAsync(string? status, CancellationToken cancellationToken = default)
    {
        var (result, items) = await _http.CallAsync<List<OrderDto>>(HttpMethod.Get, "/deliveries?limit=100" + (string.IsNullOrEmpty(status) ? "" : "&status=" + status), null, cancellationToken);
        if (result != VisitActionResult.Success) return (result, Array.Empty<DeliverySummary>());
        if (items is null || items.Any(o => o is null || o.Id == Guid.Empty || string.IsNullOrWhiteSpace(o.Number) || string.IsNullOrWhiteSpace(o.Status)))
            return (VisitActionResult.ServerError, Array.Empty<DeliverySummary>());
        return (result, items.Select(o => new DeliverySummary(o.Id, o.Number!, o.CustomerName ?? "", o.Status!, o.CreatedAt)).ToList());
    }

    public async Task<(VisitActionResult Result, DeliveryDetail? Order)> GetOrderAsync(Guid orderId, CancellationToken cancellationToken = default)
    {
        var (result, o) = await _http.CallAsync<OrderDto>(HttpMethod.Get, $"/deliveries/{orderId}", null, cancellationToken);
        if (result != VisitActionResult.Success) return (result, null);
        if (o is null || o.Id == Guid.Empty || string.IsNullOrWhiteSpace(o.Number) || string.IsNullOrWhiteSpace(o.Status) || o.Items is null || o.Items.Any(l => l is null || l.LineNo < 1))
            return (VisitActionResult.ServerError, null);
        var d = o.Delivery;
        return (result, new DeliveryDetail(o.Id, o.Number!, o.Status!, o.CustomerName ?? "", d?.Phone, d?.Address, d?.Latitude, d?.Longitude, o.Note, d?.ReceiverName, d?.FailureReason,
            o.Items.Select(l => new DeliveryLine(l.LineNo, l.ProductCode ?? "", l.ProductName ?? "", l.Unit ?? "", l.PreparedQuantity ?? l.Quantity, l.DeliveredQuantity, l.DeliveryReason)).ToList()));
    }

    private static object? Body(PhoneLocation? location) => location is null ? null : new { location = new { latitude = location.Latitude, longitude = location.Longitude, accuracyMeters = location.AccuracyMeters, isMocked = location.IsMocked } };

    public async Task<VisitActionResult> PickUpAsync(Guid orderId, PhoneLocation? location, CancellationToken cancellationToken = default) =>
        (await _http.CallAsync<object>(HttpMethod.Post, $"/deliveries/{orderId}/pickup", Body(location) ?? new { }, cancellationToken)).Result;

    public async Task<VisitActionResult> DepartAsync(Guid orderId, PhoneLocation? location, CancellationToken cancellationToken = default) =>
        (await _http.CallAsync<object>(HttpMethod.Post, $"/deliveries/{orderId}/depart", Body(location) ?? new { }, cancellationToken)).Result;

    public async Task<VisitActionResult> DeliverAsync(Guid orderId, string receiverName, string? note, IReadOnlyList<DeliveredLineInput> lines, PhoneLocation? location,
        CancellationToken cancellationToken = default) =>
        (await _http.CallAsync<object>(HttpMethod.Post, $"/deliveries/{orderId}/deliver", new
        {
            receiverName, note,
            lines = lines.Select(l => new { lineNo = l.LineNo, quantity = l.Quantity, reason = l.Reason }).ToList(),
            location = location is null ? null : new { latitude = location.Latitude, longitude = location.Longitude, accuracyMeters = location.AccuracyMeters, isMocked = location.IsMocked }
        }, cancellationToken)).Result;

    public async Task<VisitActionResult> FailAsync(Guid orderId, string reason, PhoneLocation? location, CancellationToken cancellationToken = default) =>
        (await _http.CallAsync<object>(HttpMethod.Post, $"/deliveries/{orderId}/fail", new
        {
            reason,
            location = location is null ? null : new { latitude = location.Latitude, longitude = location.Longitude, accuracyMeters = location.AccuracyMeters, isMocked = location.IsMocked }
        }, cancellationToken)).Result;

    private sealed record LineDto(int LineNo, string? ProductCode, string? ProductName, string? Unit, decimal Quantity, decimal? PreparedQuantity, decimal? DeliveredQuantity, string? DeliveryReason);
    private sealed record InfoDto(string? Phone, string? Address, double? Latitude, double? Longitude, string? ReceiverName, string? DeliveryNote, string? FailureReason);
    private sealed record OrderDto(Guid Id, string? Number, string? CustomerName, string? Status, string? Note, DateTimeOffset CreatedAt, List<LineDto>? Items, InfoDto? Delivery);
}

// What the driver types when handing over (plan step 2.4): the receiver, the delivered quantity of every line (empty = as prepared) and a reason for
// every line that is less. Delivered is possible only when everything fits; at least one line must be delivered (otherwise it is a failure).
public sealed class DeliveryDraft(DeliveryDetail order)
{
    private readonly Dictionary<int, decimal> _quantities = [];
    private readonly Dictionary<int, string> _reasons = [];

    public DeliveryDetail Order { get; } = order;
    public string? ReceiverName { get; set; }
    public string? Note { get; set; }

    public bool SetQuantity(int lineNo, string? text)
    {
        var line = Order.Lines.FirstOrDefault(l => l.LineNo == lineNo);
        var value = OrderDraft.ParseQuantity(text);
        if (line is null || value is null || value > line.Prepared) return false;
        if (string.IsNullOrWhiteSpace(text)) _quantities.Remove(lineNo);
        else _quantities[lineNo] = value.Value;
        if (QuantityOf(lineNo) == line.Prepared) _reasons.Remove(lineNo);
        return true;
    }

    public void SetReason(int lineNo, string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) _reasons.Remove(lineNo);
        else _reasons[lineNo] = reason.Trim();
    }

    public decimal QuantityOf(int lineNo) => _quantities.TryGetValue(lineNo, out var q) ? q : Order.Lines.First(l => l.LineNo == lineNo).Prepared;
    public bool IsShort(int lineNo) => QuantityOf(lineNo) < Order.Lines.First(l => l.LineNo == lineNo).Prepared;
    public string? ReasonOf(int lineNo) => _reasons.GetValueOrDefault(lineNo);

    public bool CanDeliver => Order.Status == "on_the_way" && !string.IsNullOrWhiteSpace(ReceiverName) && ReceiverName!.Trim().Length <= 100 && Order.Lines.Count > 0 &&
        Order.Lines.Any(l => QuantityOf(l.LineNo) > 0) && Order.Lines.All(l => !IsShort(l.LineNo) || ReasonOf(l.LineNo) is { Length: > 0 });

    public IReadOnlyList<DeliveredLineInput> Lines => Order.Lines.Select(l => new DeliveredLineInput(l.LineNo, QuantityOf(l.LineNo), IsShort(l.LineNo) ? ReasonOf(l.LineNo) : null)).ToList();
}
