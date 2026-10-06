using System.Globalization;
using LifeDrop.Mobile.Auth;

namespace LifeDrop.Mobile.Visits;

public sealed record SampleProduct(Guid Id, string Code, string Name, string Unit);
public sealed record RecordedSample(Guid Id, string Number, bool Replayed);

// Giving pharma samples from the phone (plan step 2.6): a catalogue product, a quantity, the recipient, batch and expiry. Online only.
public interface ISamplesApiClient
{
    Task<(VisitActionResult Result, IReadOnlyList<SampleProduct> Products)> GetProductsAsync(CancellationToken cancellationToken = default);
    // The draft's ClientRef stays the same for every retry, so a lost answer never records two distributions.
    Task<(VisitActionResult Result, RecordedSample? Sample)> RecordAsync(SampleDraft draft, PhoneLocation? location, CancellationToken cancellationToken = default);
}

// The distribution a field user is typing. Only the product and the quantity are required.
public sealed class SampleDraft(Guid customerId, Guid? visitId = null)
{
    public const decimal MaxQuantity = 100_000m;

    public Guid CustomerId { get; } = customerId;
    public Guid? VisitId { get; } = visitId;
    public Guid ClientRef { get; } = Guid.NewGuid();
    public Guid? ProductId { get; set; }
    public string? QuantityText { get; set; }
    public string? Recipient { get; set; }
    public string? Batch { get; set; }
    public string? ExpiryText { get; set; }
    public string? Note { get; set; }
    public RecordedSample? Recorded { get; set; }

    private static string Ascii(string text) => new(text.Trim().Select(c => c switch
    {
        >= '٠' and <= '٩' => (char)('0' + (c - '٠')),   // Arabic-Indic
        >= '۰' and <= '۹' => (char)('0' + (c - '۰')),   // Persian
        '٫' or ',' => '.',
        '/' => '-',
        _ => c
    }).ToArray());

    public static decimal? ParseQuantity(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (!decimal.TryParse(Ascii(text), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)) return null;
        return value <= 0 || value > MaxQuantity || decimal.Round(value, 3) != value ? null : value;
    }

    // Empty is allowed (no expiry); otherwise a date as yyyy-MM-dd that is not before today. Returns false for text that is not such a date.
    public static bool TryParseExpiry(string? text, DateOnly today, out DateOnly? expiry)
    {
        expiry = null;
        if (string.IsNullOrWhiteSpace(text)) return true;
        if (!DateOnly.TryParseExact(Ascii(text), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) || date < today) return false;
        expiry = date;
        return true;
    }

    public decimal? Quantity => ParseQuantity(QuantityText);

    public bool CanSave(DateOnly today) => Recorded is null && ProductId is not null && Quantity is not null && TryParseExpiry(ExpiryText, today, out _) &&
        (Recipient?.Trim().Length ?? 0) <= 100 && (Batch?.Trim().Length ?? 0) <= 50 && (Note?.Trim().Length ?? 0) <= 500;
}

public sealed class SamplesApiClient : ISamplesApiClient
{
    private readonly OrdersHttp _http;

    public SamplesApiClient(HttpClient httpClient, IAuthSession session) =>
        _http = new OrdersHttp(httpClient ?? throw new ArgumentNullException(nameof(httpClient)), session ?? throw new ArgumentNullException(nameof(session)));

    public async Task<(VisitActionResult Result, IReadOnlyList<SampleProduct> Products)> GetProductsAsync(CancellationToken cancellationToken = default)
    {
        var (result, items) = await _http.CallAsync<List<ProductDto>>(HttpMethod.Get, "/products", null, cancellationToken);
        if (result != VisitActionResult.Success) return (result, Array.Empty<SampleProduct>());
        if (items is null || items.Any(p => p is null || p.Id == Guid.Empty || string.IsNullOrWhiteSpace(p.Code))) return (VisitActionResult.ServerError, Array.Empty<SampleProduct>());
        return (result, items.Select(p => new SampleProduct(p.Id, p.Code!, p.Name ?? "", p.Unit ?? "")).ToList());
    }

    public async Task<(VisitActionResult Result, RecordedSample? Sample)> RecordAsync(SampleDraft draft, PhoneLocation? location, CancellationToken cancellationToken = default)
    {
        if (!draft.CanSave(DateOnly.FromDateTime(DateTime.UtcNow.AddHours(3)))) return (VisitActionResult.InvalidData, null);
        string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        SampleDraft.TryParseExpiry(draft.ExpiryText, DateOnly.MinValue, out var expiry);
        var (result, s) = await _http.CallAsync<CreatedDto>(HttpMethod.Post, "/samples", new
        {
            customerId = draft.CustomerId,
            visitId = draft.VisitId,
            productId = draft.ProductId,
            quantity = draft.Quantity,
            recipientName = Clean(draft.Recipient),
            batchNumber = Clean(draft.Batch),
            expiryDate = expiry?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            note = Clean(draft.Note),
            clientRef = draft.ClientRef,
            location = location is null ? null : new { latitude = location.Latitude, longitude = location.Longitude, accuracyMeters = location.AccuracyMeters, isMocked = location.IsMocked }
        }, cancellationToken);
        if (result != VisitActionResult.Success) return (result, null);
        if (s is null || s.Id == Guid.Empty || string.IsNullOrWhiteSpace(s.Number)) return (VisitActionResult.ServerError, null);
        return (result, new RecordedSample(s.Id, s.Number!, s.Replayed));
    }

    private sealed record CreatedDto(Guid Id, string? Number, bool Replayed);
    private sealed record ProductDto(Guid Id, string? Code, string? Name, string? Unit);
}
