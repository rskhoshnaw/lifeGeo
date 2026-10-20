using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using LifeDrop.Mobile.Auth;
using LifeDrop.Mobile.Auth.Models;

namespace LifeDrop.Mobile.Visits;

// One place of the tree city -> neighbourhood -> street -> alley (plan step 1.23-b). Kind: city | neighbourhood | street | alley.
public sealed record PlaceItem(Guid Id, Guid? ParentId, string Kind, string Name, string? NameCkb, string? NameAr, string? NameEn, int ChildCount = 0)
{
    // The name in the app language when the place has one, otherwise the main name.
    public string DisplayName(string language)
    {
        var name = language switch { "ckb" => NameCkb, "ar" => NameAr, "en" => NameEn, _ => null };
        return string.IsNullOrWhiteSpace(name) ? Name : name;
    }
}

public sealed record PlacePathItem(Guid Id, string Kind, string Name);

// A customer as returned by GET /customers/{id}: contact details and the most precise place with the ones above it.
public sealed record CustomerProfile(Guid Id, string Code, string Name, string Type, string? Phone, string? Address, Guid? PlaceId,
    IReadOnlyList<PlacePathItem>? PlacePath, string? ContactName = null, string? Phone2 = null, string? Notes = null,
    // Doctor details (plan step 4.27-b); set for a customer of type Doctor.
    DoctorDetails? Doctor = null);

// One row of the customer search (plan step 4.15).
public sealed record CustomerListItem(Guid Id, string Code, string Name, string Type, string? Phone);

// Customers and places for the field user (plan step 1.23-b): edit a customer's profile, register a new customer, pick or add
// a neighbourhood, street or alley. Answers use VisitActionResult: Conflict = the name or code already exists,
// InvalidData = refused data, Forbidden = no permission. Online only: nothing is queued.
// Doctor details of a customer (plan step 4.27-b): all optional. Specialty is one of DoctorOptions.Specialties, CustomerClass one of DoctorOptions.Classes.
public sealed record DoctorDetails(string? ClinicName, string? Specialty, bool? Cooperates, bool? Dealer, string? CustomerClass)
{
    public bool IsEmpty => string.IsNullOrWhiteSpace(ClinicName) && Specialty is null && Cooperates is null && Dealer is null && CustomerClass is null;
}

public static class DoctorOptions
{
    // The texts are specialty.<key> in i18n/strings.json; the same fixed list as the server.
    public static readonly IReadOnlyList<string> Specialties = ["orthopedics", "neuro_spine", "pediatrics", "women_obstetrics", "surgery", "kidney_urology", "ent", "other"];
    public static readonly IReadOnlyList<string> Classes = ["A+", "A", "B", "C"];
}

public interface ICustomersApiClient
{
    Task<(VisitActionResult Result, CustomerProfile? Customer)> GetCustomerAsync(Guid customerId, CancellationToken cancellationToken = default);
    Task<VisitActionResult> SetProfileAsync(Guid customerId, string? phone, string? address, Guid? placeId, CancellationToken cancellationToken = default);
    // parentId null lists the cities. Active places only.
    Task<(VisitActionResult Result, IReadOnlyList<PlaceItem> Places)> GetPlacesAsync(Guid? parentId, CancellationToken cancellationToken = default);
    Task<(VisitActionResult Result, Guid? Id)> CreatePlaceAsync(Guid parentId, string kind, string name, CancellationToken cancellationToken = default);
    Task<(VisitActionResult Result, Guid? Id)> CreateCustomerAsync(string type, string code, string name, CancellationToken cancellationToken = default);
    Task<VisitActionResult> SetLocationAsync(Guid customerId, DeviceFix fix, CancellationToken cancellationToken = default);
    // Contact person, second phone and note (plan step 4.15); null or empty clears. Own route, so a client that does not send them never clears them.
    Task<VisitActionResult> SetContactAsync(Guid customerId, string? contactName, string? phone2, string? notes, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
    // Doctor details (plan step 4.27-b); null clears a field. Own route, like the contact.
    Task<VisitActionResult> SetDoctorAsync(Guid customerId, DoctorDetails details, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
    // Type, code and name of a customer (PUT /customers/{id}); the server refuses what the user may not change (a sales rep cannot make a doctor, a scientific rep only touches doctors).
    Task<VisitActionResult> EditCustomerAsync(Guid customerId, string type, string code, string name, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
    // The permission keys of the signed-in user (GET /me): the form uses them to offer only the customer types the user may register.
    Task<(VisitActionResult Result, IReadOnlyList<string> Permissions)> GetMyPermissionsAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
    // Customers in the user's area whose name, code or phone matches the text (two characters or more), at most 50.
    Task<(VisitActionResult Result, IReadOnlyList<CustomerListItem> Items)> SearchCustomersAsync(string search, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
}

public sealed class CustomersApiClient : ICustomersApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly IAuthSession _session;

    public CustomersApiClient(HttpClient httpClient, IAuthSession session)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _session = session ?? throw new ArgumentNullException(nameof(session));
    }

    public async Task<(VisitActionResult Result, CustomerProfile? Customer)> GetCustomerAsync(Guid customerId, CancellationToken cancellationToken = default)
    {
        var (result, dto) = await CallAsync<CustomerDto>(HttpMethod.Get, $"/customers/{customerId}", null, cancellationToken);
        if (result != VisitActionResult.Success) return (result, null);
        return dto is null || dto.Id == Guid.Empty || string.IsNullOrWhiteSpace(dto.Code)
            ? (VisitActionResult.ServerError, null)
            : (result, new CustomerProfile(dto.Id, dto.Code, dto.Name ?? "", dto.Type ?? "", dto.Phone, dto.Address, dto.PlaceId, dto.PlacePath, dto.ContactName, dto.Phone2, dto.Notes,
                new DoctorDetails(dto.ClinicName, dto.Specialty, dto.Cooperates, dto.Dealer, dto.CustomerClass)));
    }

    public async Task<VisitActionResult> SetProfileAsync(Guid customerId, string? phone, string? address, Guid? placeId, CancellationToken cancellationToken = default) =>
        (await CallAsync<object>(HttpMethod.Put, $"/customers/{customerId}/profile", new { phone, address, placeId }, cancellationToken)).Result;

    public async Task<(VisitActionResult Result, IReadOnlyList<PlaceItem> Places)> GetPlacesAsync(Guid? parentId, CancellationToken cancellationToken = default)
    {
        var path = parentId is { } parent ? $"/places?parentId={parent}&limit=2000" : "/places?kind=city&limit=2000";
        var (result, items) = await CallAsync<List<PlaceDto>>(HttpMethod.Get, path, null, cancellationToken);
        if (result != VisitActionResult.Success) return (result, Array.Empty<PlaceItem>());
        if (items is null || items.Any(p => p is null || p.Id == Guid.Empty || string.IsNullOrWhiteSpace(p.Name) || string.IsNullOrWhiteSpace(p.Kind)))
            return (VisitActionResult.ServerError, Array.Empty<PlaceItem>());
        return (result, items.Select(p => new PlaceItem(p.Id, p.ParentId, p.Kind!, p.Name!, p.NameCkb, p.NameAr, p.NameEn, p.ChildCount)).ToList());
    }

    public async Task<(VisitActionResult Result, Guid? Id)> CreatePlaceAsync(Guid parentId, string kind, string name, CancellationToken cancellationToken = default)
    {
        var (result, created) = await CallAsync<CreatedDto>(HttpMethod.Post, "/places", new { parentId, kind, name }, cancellationToken);
        return result == VisitActionResult.Success && created?.Id is { } id && id != Guid.Empty ? (result, id) : (result == VisitActionResult.Success ? VisitActionResult.ServerError : result, null);
    }

    public async Task<(VisitActionResult Result, Guid? Id)> CreateCustomerAsync(string type, string code, string name, CancellationToken cancellationToken = default)
    {
        // The server answers 201 with the new id as a bare JSON string.
        var (result, id) = await CallAsync<Guid>(HttpMethod.Post, "/customers", new { type, code, name }, cancellationToken);
        return result == VisitActionResult.Success && id != Guid.Empty ? (result, id) : (result == VisitActionResult.Success ? VisitActionResult.ServerError : result, null);
    }

    public async Task<VisitActionResult> SetContactAsync(Guid customerId, string? contactName, string? phone2, string? notes, CancellationToken cancellationToken = default) =>
        (await CallAsync<object>(HttpMethod.Put, $"/customers/{customerId}/contact", new { contactName, phone2, notes }, cancellationToken)).Result;

    public async Task<VisitActionResult> EditCustomerAsync(Guid customerId, string type, string code, string name, CancellationToken cancellationToken = default) =>
        (await CallAsync<object>(HttpMethod.Put, $"/customers/{customerId}", new { type, code, name }, cancellationToken)).Result;

    public async Task<VisitActionResult> SetDoctorAsync(Guid customerId, DoctorDetails details, CancellationToken cancellationToken = default) =>
        (await CallAsync<object>(HttpMethod.Put, $"/customers/{customerId}/doctor",
            new { clinicName = details.ClinicName, specialty = details.Specialty, cooperates = details.Cooperates, dealer = details.Dealer, customerClass = details.CustomerClass }, cancellationToken)).Result;

    public async Task<(VisitActionResult Result, IReadOnlyList<string> Permissions)> GetMyPermissionsAsync(CancellationToken cancellationToken = default)
    {
        var (result, me) = await CallAsync<MeDto>(HttpMethod.Get, "/me", null, cancellationToken);
        if (result != VisitActionResult.Success) return (result, Array.Empty<string>());
        return me?.Permissions is null ? (VisitActionResult.ServerError, Array.Empty<string>()) : (result, me.Permissions);
    }

    public async Task<(VisitActionResult Result, IReadOnlyList<CustomerListItem> Items)> SearchCustomersAsync(string search, CancellationToken cancellationToken = default)
    {
        var (result, items) = await CallAsync<List<CustomerDto>>(HttpMethod.Get, $"/customers?limit=50&q={Uri.EscapeDataString(search.Trim())}", null, cancellationToken);
        if (result != VisitActionResult.Success) return (result, Array.Empty<CustomerListItem>());
        if (items is null || items.Any(c => c is null || c.Id == Guid.Empty || string.IsNullOrWhiteSpace(c.Code))) return (VisitActionResult.ServerError, Array.Empty<CustomerListItem>());
        return (result, items.Select(c => new CustomerListItem(c.Id, c.Code!, c.Name ?? "", c.Type ?? "", c.Phone)).ToList());
    }

    public async Task<VisitActionResult> SetLocationAsync(Guid customerId, DeviceFix fix, CancellationToken cancellationToken = default) =>
        (await CallAsync<object>(HttpMethod.Put, $"/customers/{customerId}/location",
            new { latitude = fix.Latitude, longitude = fix.Longitude, source = "gps", accuracyMeters = fix.AccuracyMeters }, cancellationToken)).Result;

    // One authorised call. 2xx with a body is read as T (a body that does not fit is a server error).
    private async Task<(VisitActionResult Result, T? Data)> CallAsync<T>(HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        var token = await _session.GetAccessTokenAsync(cancellationToken);
        if (!token.IsSuccess || string.IsNullOrEmpty(token.Data))
            return (token.Status == AuthStatus.NetworkOrServerError ? VisitActionResult.NetworkError : VisitActionResult.SessionExpired, default);

        try
        {
            using var request = new HttpRequestMessage(method, path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Data);
            if (body is not null) request.Content = JsonContent.Create(body);
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                if (typeof(T) == typeof(object) || response.StatusCode == HttpStatusCode.NoContent) return (VisitActionResult.Success, default);
                try
                {
                    return (VisitActionResult.Success, await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken));
                }
                catch (Exception e) when (e is JsonException or NotSupportedException)
                {
                    return (VisitActionResult.ServerError, default);
                }
            }

            return (response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => VisitActionResult.SessionExpired,
                HttpStatusCode.Forbidden => VisitActionResult.Forbidden,
                HttpStatusCode.NotFound => VisitActionResult.NotFound,
                HttpStatusCode.BadRequest => VisitActionResult.InvalidData,
                HttpStatusCode.Conflict => VisitActionResult.Conflict,
                _ => VisitActionResult.ServerError
            }, default);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e) when (e is OperationCanceledException or HttpRequestException)
        {
            return (VisitActionResult.NetworkError, default);
        }
    }

    private sealed record MeDto(string[]? Permissions);
    private sealed record CustomerDto(Guid Id, string? Type, string? Code, string? Name, string? Phone, string? Address, Guid? PlaceId, List<PlacePathItem>? PlacePath,
        string? ContactName = null, string? Phone2 = null, string? Notes = null,
        string? ClinicName = null, string? Specialty = null, bool? Cooperates = null, bool? Dealer = null, string? CustomerClass = null);
    private sealed record PlaceDto(Guid Id, Guid? ParentId, string? Kind, string? Name, string? NameCkb, string? NameAr, string? NameEn, int ChildCount);
    private sealed record CreatedDto(Guid Id);
}
