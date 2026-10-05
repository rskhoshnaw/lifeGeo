namespace LifeDrop.Mobile.Visits;

public sealed record NewCustomerRequest(string Name, string Type, string? Phone, string? Address, Guid? PlaceId, DeviceFix? Location);

// Create: the customer itself. Profile and Location: null when nothing was to be saved, otherwise the answer; a failure
// there leaves the customer created (CustomerId is set) and the user finishes in the customer's details.
public sealed record NewCustomerOutcome(VisitActionResult Create, Guid? CustomerId, string? Code, VisitActionResult? Profile, VisitActionResult? Location)
{
    public bool Complete => Create == VisitActionResult.Success && Profile is null or VisitActionResult.Success && Location is null or VisitActionResult.Success;
}

// Codes of customers a field user registers: N, the day, and four letters or digits without look-alikes (O/0, I/1/L), so
// two phones registering on the same day almost never pick the same code. The server refuses a repeated one (409).
public static class CustomerCodes
{
    private const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

    public static string Generate(DateOnly today, Func<int, int> next)
    {
        var suffix = new char[4];
        for (var i = 0; i < suffix.Length; i++) suffix[i] = Alphabet[next(Alphabet.Length)];
        return $"N{today:yyMMdd}-{new string(suffix)}";
    }
}

// Registering a customer in the field (plan step 1.23-b): create, then phone/address/place, then the GPS position. Online only.
public sealed class NewCustomerFlow(ICustomersApiClient api, TimeProvider clock, Func<int, int>? random = null)
{
    public const int MaxAttempts = 5;
    public const int MaxNameLength = 200;
    public const double MaxLocationAccuracyMeters = 100;
    public static readonly string[] Types = ["Pharmacy", "Doctor", "Wholesaler", "Other"];

    private readonly Func<int, int> _random = random ?? Random.Shared.Next;

    public static bool IsValid(NewCustomerRequest request) =>
        !string.IsNullOrWhiteSpace(request.Name) && request.Name.Trim().Length <= MaxNameLength && Types.Contains(request.Type);

    public async Task<NewCustomerOutcome> CreateAsync(NewCustomerRequest request, CancellationToken cancellationToken = default)
    {
        if (!IsValid(request)) return new NewCustomerOutcome(VisitActionResult.InvalidData, null, null, null, null);
        if (request.Location is { AccuracyMeters: > MaxLocationAccuracyMeters })
            return new NewCustomerOutcome(VisitActionResult.GpsLowAccuracy, null, null, null, null);   // nothing is created with a position the server would refuse

        var today = IraqTime.Today(clock);
        string? code = null;
        Guid? id = null;
        var created = VisitActionResult.Conflict;
        for (var attempt = 0; attempt < MaxAttempts && created == VisitActionResult.Conflict; attempt++)
        {
            code = CustomerCodes.Generate(today, _random);
            (created, id) = await api.CreateCustomerAsync(request.Type, code, request.Name.Trim(), cancellationToken);
        }
        if (created != VisitActionResult.Success || id is not { } customerId) return new NewCustomerOutcome(created, null, null, null, null);

        var phone = string.IsNullOrWhiteSpace(request.Phone) ? null : request.Phone.Trim();
        var address = string.IsNullOrWhiteSpace(request.Address) ? null : request.Address.Trim();
        VisitActionResult? profile = null, location = null;
        if (phone is not null || address is not null || request.PlaceId is not null)
            profile = await api.SetProfileAsync(customerId, phone, address, request.PlaceId, cancellationToken);
        if (request.Location is { } fix)
            location = await api.SetLocationAsync(customerId, fix, cancellationToken);
        return new NewCustomerOutcome(created, customerId, code, profile, location);
    }
}
