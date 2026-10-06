using System.Globalization;
using LifeDrop.Mobile.Auth;

namespace LifeDrop.Mobile.Visits;

// The specialised visit form (plan step 2.7): purpose, person met, products presented, needs and the fields of the visit's type. Saved while InVisit. Online only for now.
public interface IVisitDetailsApiClient
{
    Task<VisitActionResult> SaveAsync(Guid visitId, VisitDetailsDraft draft, CancellationToken cancellationToken = default);
}

// What the rep types. Which optional fields exist depends on the visit type (doctor, pharmacy, wholesaler; "other" has none).
public sealed class VisitDetailsDraft(string visitType)
{
    public static readonly string[] Purposes = ["product_intro", "order_follow_up", "collection", "complaint", "other"];
    public static readonly string[] Currencies = ["IQD", "USD"];

    public string VisitType { get; } = visitType;
    public string? Purpose { get; set; }
    public string? PersonMet { get; set; }
    public string? PersonRole { get; set; }
    public string? Needs { get; set; }
    public HashSet<Guid> ProductIds { get; } = [];
    public string? Specialty { get; set; }
    public string? DailyPatientsText { get; set; }
    public string? MonthlySalesText { get; set; }
    public string? Competitors { get; set; }
    public string? MonthlyVolumeText { get; set; }
    public string? PaymentTerms { get; set; }
    public string Currency { get; set; } = "IQD";

    public bool IsDoctor => VisitType == "doctor";
    public bool IsPharmacy => VisitType == "pharmacy";
    public bool IsWholesaler => VisitType == "wholesaler";

    private static string Ascii(string text) => new(text.Trim().Select(c => c switch
    {
        >= '٠' and <= '٩' => (char)('0' + (c - '٠')),
        >= '۰' and <= '۹' => (char)('0' + (c - '۰')),
        '٫' or ',' => '.',
        _ => c
    }).ToArray());

    // Empty = not given; null result with a non-empty text = not a number.
    public static (bool Ok, decimal? Value) ParseNumber(string? text, decimal max)
    {
        if (string.IsNullOrWhiteSpace(text)) return (true, null);
        if (!decimal.TryParse(Ascii(text), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)) return (false, null);
        return value < 0 || value > max || decimal.Round(value, 3) != value ? (false, null) : (true, value);
    }

    public int? DailyPatients => ParseNumber(DailyPatientsText, 10_000) is (true, { } v) && v == decimal.Truncate(v) ? (int)v : null;
    public decimal? MonthlySales => ParseNumber(MonthlySalesText, 999_999_999_999m).Value;
    public decimal? MonthlyVolume => ParseNumber(MonthlyVolumeText, 999_999_999_999m).Value;

    private bool NumbersOk => ParseNumber(MonthlySalesText, 999_999_999_999m).Ok && ParseNumber(MonthlyVolumeText, 999_999_999_999m).Ok &&
        (string.IsNullOrWhiteSpace(DailyPatientsText) || DailyPatients is not null);

    public bool CanSave => Purpose is not null && Purposes.Contains(Purpose) && NumbersOk && ProductIds.Count <= 50 && (PersonMet?.Trim().Length ?? 0) <= 100 &&
        (PersonRole?.Trim().Length ?? 0) <= 100 && (Needs?.Trim().Length ?? 0) <= 1000 && (Specialty?.Trim().Length ?? 0) <= 100 &&
        (Competitors?.Trim().Length ?? 0) <= 500 && (PaymentTerms?.Trim().Length ?? 0) <= 500;
}

public sealed class VisitDetailsApiClient : IVisitDetailsApiClient
{
    private readonly OrdersHttp _http;

    public VisitDetailsApiClient(HttpClient httpClient, IAuthSession session) =>
        _http = new OrdersHttp(httpClient ?? throw new ArgumentNullException(nameof(httpClient)), session ?? throw new ArgumentNullException(nameof(session)));

    public async Task<VisitActionResult> SaveAsync(Guid visitId, VisitDetailsDraft draft, CancellationToken cancellationToken = default)
    {
        if (!draft.CanSave) return VisitActionResult.InvalidData;
        string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        // Only the fields of the visit's type go to the server (the server refuses the others).
        var sales = draft.IsPharmacy ? draft.MonthlySales : null;
        var volume = draft.IsWholesaler ? draft.MonthlyVolume : null;
        var body = new
        {
            purpose = draft.Purpose,
            personMet = Clean(draft.PersonMet),
            personRole = Clean(draft.PersonRole),
            needs = Clean(draft.Needs),
            productIds = draft.ProductIds.ToList(),
            specialty = draft.IsDoctor ? Clean(draft.Specialty) : null,
            dailyPatients = draft.IsDoctor ? draft.DailyPatients : null,
            monthlySales = sales,
            competitors = draft.IsPharmacy ? Clean(draft.Competitors) : null,
            monthlyVolume = volume,
            paymentTerms = draft.IsWholesaler ? Clean(draft.PaymentTerms) : null,
            currency = sales is not null || volume is not null ? draft.Currency : null
        };
        return (await _http.CallAsync<object>(HttpMethod.Put, $"/visits/{visitId}/report/details", body, cancellationToken)).Result;
    }
}
