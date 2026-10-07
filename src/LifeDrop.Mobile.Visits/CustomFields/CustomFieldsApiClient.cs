using System.Globalization;
using LifeDrop.Mobile.Auth;

namespace LifeDrop.Mobile.Visits;

// The extra fields a company adds to the visit report (plan step 3.2, part 2): the definitions come from the server, the rep answers while the visit is in progress.
// Every answer is a string; the server checks it again against the field type.
public sealed record CustomField(Guid Id, string Key, string Type, string AppliesTo, string? LabelEn, string? LabelCkb, string? LabelAr, string? LabelFa, IReadOnlyList<string> Options,
    bool IsRequired, int SortOrder, bool IsActive)
{
    // The label in the app's language, else English, else any label the manager wrote, else the key.
    public string LabelFor(string language)
    {
        var preferred = language switch { "ckb" => LabelCkb, "ar" => LabelAr, "fa" => LabelFa, _ => LabelEn };
        return FirstText(preferred, LabelEn, LabelCkb, LabelAr, LabelFa) ?? Key;
    }

    private static string? FirstText(params string?[] texts) => texts.FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));
}

public sealed record CustomAnswerValue(Guid FieldId, string Key, string? Value);
public sealed record CustomAnswersDto(Guid VisitId, IReadOnlyList<CustomAnswerValue> Answers, DateTimeOffset UpdatedAt);

public interface ICustomFieldsApiClient
{
    // The active fields for the visit's type (and for all types), in the manager's order.
    Task<(VisitActionResult Result, IReadOnlyList<CustomField> Fields)> GetFieldsAsync(string visitType, CancellationToken cancellationToken = default);
    // NotFound when nothing was saved yet.
    Task<(VisitActionResult Result, IReadOnlyDictionary<Guid, string> Answers)> GetAnswersAsync(Guid visitId, CancellationToken cancellationToken = default);
    Task<VisitActionResult> SaveAsync(Guid visitId, CustomAnswersDraft draft, CancellationToken cancellationToken = default);
}

// What the rep types. The same rules as the server: number with at most three decimals, one of the choices, yes/no, an ISO date, text up to 1000 characters; the required
// fields must be answered. An empty text means "not answered".
public sealed class CustomAnswersDraft(IReadOnlyList<CustomField> fields)
{
    public const int MaxText = 1000;
    public const decimal MaxNumber = 999_999_999_999m;

    private readonly Dictionary<Guid, string> _values = [];

    public IReadOnlyList<CustomField> Fields { get; } = fields;

    public void Set(Guid fieldId, string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) _values.Remove(fieldId);
        else _values[fieldId] = text.Trim();
    }

    public string? Get(Guid fieldId) => _values.GetValueOrDefault(fieldId);

    private static string Ascii(string text) => new(text.Select(c => c switch
    {
        >= '٠' and <= '٩' => (char)('0' + (c - '٠')),
        >= '۰' and <= '۹' => (char)('0' + (c - '۰')),
        '٫' or ',' => '.',
        _ => c
    }).ToArray());

    // The stored form of a valid answer, or null.
    public static string? Normalise(CustomField field, string text)
    {
        text = text.Trim();
        switch (field.Type)
        {
            case "text": return text.Length <= MaxText ? text : null;
            case "number":
                if (!decimal.TryParse(Ascii(text), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var n) || Math.Abs(n) > MaxNumber || decimal.Round(n, 3) != n) return null;
                return n.ToString("0.###", CultureInfo.InvariantCulture);
            case "choice": return field.Options.Contains(text) ? text : null;
            case "yes_no": return text is "true" or "false" ? text : null;
            case "date": return DateOnly.TryParseExact(Ascii(text), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null;
            default: return null;
        }
    }

    public bool IsValid(CustomField field) => !_values.TryGetValue(field.Id, out var text) || Normalise(field, text) is not null;
    public bool IsMissing(CustomField field) => field.IsRequired && !_values.ContainsKey(field.Id);

    public bool CanSave => Fields.All(f => IsValid(f) && !IsMissing(f));

    // The answers the server gets: normalised, and only for the fields of this draft.
    public IReadOnlyList<(Guid FieldId, string Value)> ToAnswers() =>
        Fields.Where(f => _values.ContainsKey(f.Id)).Select(f => (f.Id, Normalise(f, _values[f.Id])!)).ToList();
}

public sealed class CustomFieldsApiClient : ICustomFieldsApiClient
{
    private readonly OrdersHttp _http;

    public CustomFieldsApiClient(HttpClient httpClient, IAuthSession session) =>
        _http = new OrdersHttp(httpClient ?? throw new ArgumentNullException(nameof(httpClient)), session ?? throw new ArgumentNullException(nameof(session)));

    public async Task<(VisitActionResult Result, IReadOnlyList<CustomField> Fields)> GetFieldsAsync(string visitType, CancellationToken cancellationToken = default)
    {
        var (result, items) = await _http.CallAsync<List<CustomField>>(HttpMethod.Get, $"/form-fields?visitType={Uri.EscapeDataString(visitType)}", null, cancellationToken);
        if (result != VisitActionResult.Success) return (result, Array.Empty<CustomField>());
        if (items is null || items.Any(f => f is null || f.Id == Guid.Empty || string.IsNullOrWhiteSpace(f.Key) || f.Options is null)) return (VisitActionResult.ServerError, Array.Empty<CustomField>());
        return (result, items);
    }

    public async Task<(VisitActionResult Result, IReadOnlyDictionary<Guid, string> Answers)> GetAnswersAsync(Guid visitId, CancellationToken cancellationToken = default)
    {
        var (result, dto) = await _http.CallAsync<CustomAnswersDto>(HttpMethod.Get, $"/visits/{visitId}/report/custom", null, cancellationToken);
        if (result != VisitActionResult.Success) return (result, new Dictionary<Guid, string>());
        if (dto?.Answers is null) return (VisitActionResult.ServerError, new Dictionary<Guid, string>());
        return (result, dto.Answers.Where(a => a.Value is not null).ToDictionary(a => a.FieldId, a => a.Value!));
    }

    public async Task<VisitActionResult> SaveAsync(Guid visitId, CustomAnswersDraft draft, CancellationToken cancellationToken = default)
    {
        if (!draft.CanSave) return VisitActionResult.InvalidData;
        var body = new { answers = draft.ToAnswers().Select(a => new { fieldId = a.FieldId, value = a.Value }).ToList() };
        return (await _http.CallAsync<object>(HttpMethod.Put, $"/visits/{visitId}/report/custom", body, cancellationToken)).Result;
    }
}
