using System.Globalization;
using LifeDrop.Mobile.Auth;

namespace LifeDrop.Mobile.Visits;

// Replayed: the server had this payment already (a retry of a lost answer). Status: collected (waits for the finance manager) or confirmed.
public sealed record RecordedPayment(Guid Id, string Number, string Status, bool Replayed);
public sealed record PaymentSummary(Guid Id, string Number, string CustomerName, string Method, string Currency, decimal Amount, string Status, DateTimeOffset CreatedAt);
public sealed record BalanceLine(string Currency, decimal Delivered, decimal Confirmed, decimal Collected, decimal Balance);

// Recording a payment from the phone (plan step 2.5): the visitor takes the money and records it; it stays "collected" until the finance manager
// confirms that it arrived. Online only: a payment counts as recorded only after the server accepts it.
public interface IPaymentsApiClient
{
    // ClientRef stays the same for every retry of one payment, so a lost answer never makes two payments.
    // offline: the payment was kept on the phone without internet; the server records it even when its order was cancelled meanwhile (without the order), plan step 2.13.
    Task<(VisitActionResult Result, RecordedPayment? Payment)> RecordAsync(PaymentDraft draft, CancellationToken cancellationToken = default, bool offline = false);
    Task<(VisitActionResult Result, IReadOnlyList<PaymentSummary> Payments)> GetMineAsync(CancellationToken cancellationToken = default);
    Task<(VisitActionResult Result, IReadOnlyList<BalanceLine> Lines)> GetBalanceAsync(Guid customerId, CancellationToken cancellationToken = default);
}

// The payment a field user is typing: method, currency, amount, the reference that FastPay and FIB need, a note. Optional order of the customer.
public sealed class PaymentDraft(Guid customerId, Guid? orderId = null, Guid? clientRef = null)
{
    public const decimal MaxAmount = 999_999_999_999m;
    public static readonly string[] Methods = ["cash", "fastpay", "fib", "other"];
    public static readonly string[] Currencies = ["IQD", "USD"];

    public Guid CustomerId { get; } = customerId;
    public Guid? OrderId { get; } = orderId;
    public Guid ClientRef { get; } = clientRef ?? Guid.NewGuid();
    // Kept on the phone to be sent when the internet returns (plan step 2.13): the form then offers no second save.
    public bool Queued { get; set; }
    public string Method { get; set; } = "cash";
    public string Currency { get; set; } = "IQD";
    public string? AmountText { get; set; }
    public string? Reference { get; set; }
    public string? Note { get; set; }
    public RecordedPayment? Recorded { get; set; }

    public bool NeedsReference => Method is "fastpay" or "fib";

    // Digits of any script (Arabic-Indic and Persian digits are common on these phones), a dot or a comma as decimal mark; null when it is not an amount.
    public static decimal? ParseAmount(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var ascii = new string(text.Trim().Select(c => c switch
        {
            >= '٠' and <= '٩' => (char)('0' + (c - '٠')),
            >= '۰' and <= '۹' => (char)('0' + (c - '۰')),
            '٫' or ',' => '.',
            _ => c
        }).ToArray());
        if (!decimal.TryParse(ascii, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)) return null;
        return value <= 0 || value > MaxAmount || decimal.Round(value, 3) != value ? null : value;
    }

    public decimal? Amount => ParseAmount(AmountText);

    public bool CanSave => Recorded is null && !Queued && Amount is not null && Methods.Contains(Method) && Currencies.Contains(Currency) &&
        (!NeedsReference || !string.IsNullOrWhiteSpace(Reference)) && (Reference?.Trim().Length ?? 0) <= 100 && (Note?.Trim().Length ?? 0) <= 500;
}

public sealed class PaymentsApiClient : IPaymentsApiClient
{
    private readonly OrdersHttp _http;

    public PaymentsApiClient(HttpClient httpClient, IAuthSession session) =>
        _http = new OrdersHttp(httpClient ?? throw new ArgumentNullException(nameof(httpClient)), session ?? throw new ArgumentNullException(nameof(session)));

    public async Task<(VisitActionResult Result, RecordedPayment? Payment)> RecordAsync(PaymentDraft draft, CancellationToken cancellationToken = default, bool offline = false)
    {
        if (!draft.CanSave) return (VisitActionResult.InvalidData, null);
        var (result, p) = await _http.CallAsync<CreatedDto>(HttpMethod.Post, "/payments", new
        {
            customerId = draft.CustomerId,
            orderId = draft.OrderId,
            method = draft.Method,
            currency = draft.Currency,
            amount = draft.Amount,
            reference = string.IsNullOrWhiteSpace(draft.Reference) ? null : draft.Reference.Trim(),
            note = string.IsNullOrWhiteSpace(draft.Note) ? null : draft.Note.Trim(),
            clientRef = draft.ClientRef,
            detachOrderIfInvalid = offline ? true : (bool?)null
        }, cancellationToken);
        if (result != VisitActionResult.Success) return (result, null);
        if (p is null || p.Id == Guid.Empty || string.IsNullOrWhiteSpace(p.Number) || string.IsNullOrWhiteSpace(p.Status)) return (VisitActionResult.ServerError, null);
        return (result, new RecordedPayment(p.Id, p.Number!, p.Status!, p.Replayed));
    }

    public async Task<(VisitActionResult Result, IReadOnlyList<PaymentSummary> Payments)> GetMineAsync(CancellationToken cancellationToken = default)
    {
        var (result, items) = await _http.CallAsync<List<PaymentDto>>(HttpMethod.Get, "/payments/mine?limit=100", null, cancellationToken);
        if (result != VisitActionResult.Success) return (result, Array.Empty<PaymentSummary>());
        if (items is null || items.Any(p => p is null || p.Id == Guid.Empty || string.IsNullOrWhiteSpace(p.Number) || string.IsNullOrWhiteSpace(p.Status)))
            return (VisitActionResult.ServerError, Array.Empty<PaymentSummary>());
        return (result, items.Select(p => new PaymentSummary(p.Id, p.Number!, p.CustomerName ?? "", p.Method ?? "", p.Currency ?? "", p.Amount, p.Status!, p.CreatedAt)).ToList());
    }

    public async Task<(VisitActionResult Result, IReadOnlyList<BalanceLine> Lines)> GetBalanceAsync(Guid customerId, CancellationToken cancellationToken = default)
    {
        var (result, balance) = await _http.CallAsync<BalanceDto>(HttpMethod.Get, $"/customers/{customerId}/balance", null, cancellationToken);
        if (result != VisitActionResult.Success) return (result, Array.Empty<BalanceLine>());
        if (balance?.Items is null) return (VisitActionResult.ServerError, Array.Empty<BalanceLine>());
        return (result, balance.Items.Select(i => new BalanceLine(i.Currency ?? "", i.Delivered, i.Confirmed, i.Collected, i.Balance)).ToList());
    }

    private sealed record CreatedDto(Guid Id, string? Number, string? Status, bool Replayed);
    private sealed record PaymentDto(Guid Id, string? Number, string? CustomerName, string? Method, string? Currency, decimal Amount, string? Status, DateTimeOffset CreatedAt);
    private sealed record BalanceItemDto(string? Currency, decimal Delivered, decimal Confirmed, decimal Collected, decimal Balance);
    private sealed record BalanceDto(List<BalanceItemDto>? Items);
}
