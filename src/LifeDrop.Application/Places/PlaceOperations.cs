using LifeDrop.Application.Roles;
using LifeDrop.Domain.Common;

namespace LifeDrop.Application.Places;

// Reference list of cities, neighbourhoods and streets (plan step 1.23-b). Reading needs customers.read, suggesting a
// name customers.write (the name is usable at once as 'suggested'), and everything else places.manage.
public sealed record CityDto(Guid Id, string Name, string? NameCkb, string? NameAr, string? NameEn, string Status);

// Kind: neighbourhood | street. Status: active | suggested | inactive. MergedInto: the place this one was merged into.
public sealed record PlaceDto(Guid Id, Guid CityId, string Kind, string Name, string? NameCkb, string? NameAr, string? NameEn, string Status,
    Guid? MergedInto = null);

public sealed record CreateCityRequest(string Name, string? NameCkb = null, string? NameAr = null, string? NameEn = null);
public sealed record CreatePlaceRequest(Guid CityId, string Kind, string Name, string? NameCkb = null, string? NameAr = null, string? NameEn = null);

// Status: active | inactive (a suggestion is approved by setting it to active).
public sealed record UpdatePlaceRequest(string Name, string? NameCkb, string? NameAr, string? NameEn, string Status);

// Moves every customer of the place to IntoPlaceId (same city and kind) and retires the place.
public sealed record MergePlaceRequest(Guid IntoPlaceId);

// One row of the CSV export (scripts/local/Export-CityPlaces.ps1): the city is found or created by name.
public sealed record ImportPlaceRow(string City, string Kind, string Name, string? NameCkb = null, string? NameAr = null,
    string? NameEn = null, string? Osm = null, bool Active = true);
public sealed record ImportPlacesRequest(IReadOnlyList<ImportPlaceRow> Rows);
public sealed record ImportPlacesResult(int CitiesCreated, int PlacesCreated, int Skipped);

public enum PlaceResult { Success, Forbidden, InvalidData, NotFound, Duplicate, Conflict }

public interface IPlaceStore
{
    Task<IReadOnlyList<CityDto>> ListCitiesAsync(TenantId tenantId, bool includeInactive, CancellationToken cancellationToken);
    // Without includeInactive: active and suggested places that were not merged away.
    Task<IReadOnlyList<PlaceDto>> ListPlacesAsync(TenantId tenantId, Guid? cityId, string? kind, string? search, bool includeInactive,
        int limit, CancellationToken cancellationToken);
    Task<PlaceResult> CreateCityAsync(TenantId tenantId, UserId actorId, Guid id, CreateCityRequest request, CancellationToken cancellationToken);
    Task<PlaceResult> CreatePlaceAsync(TenantId tenantId, UserId actorId, Guid id, CreatePlaceRequest request, string status, CancellationToken cancellationToken);
    Task<PlaceResult> UpdatePlaceAsync(TenantId tenantId, UserId actorId, Guid id, UpdatePlaceRequest request, CancellationToken cancellationToken);
    Task<PlaceResult> MergePlaceAsync(TenantId tenantId, UserId actorId, Guid id, Guid intoPlaceId, CancellationToken cancellationToken);
    Task<ImportPlacesResult> ImportAsync(TenantId tenantId, UserId actorId, IReadOnlyList<ImportPlaceRow> rows, CancellationToken cancellationToken);
}

public sealed class PlaceService(IPlaceStore store, IPermissionStore permissions)
{
    public const int MaxCityNameLength = 120;
    public const int MaxPlaceNameLength = 160;
    public const int MaxImportRows = 2000;
    public const int DefaultListLimit = 500;
    public const int MaxListLimit = 2000;

    public static bool IsKind(string? kind) => kind is "neighbourhood" or "street";

    public async Task<(PlaceResult Result, IReadOnlyList<CityDto> Items)> ListCitiesAsync(TenantId tenantId, UserId actorId, bool includeInactive,
        CancellationToken cancellationToken)
    {
        if (!await AllowedAsync(tenantId, actorId, PermissionKeys.CustomersRead, cancellationToken)) return (PlaceResult.Forbidden, Array.Empty<CityDto>());
        var all = includeInactive && await AllowedAsync(tenantId, actorId, PermissionKeys.PlacesManage, cancellationToken);
        return (PlaceResult.Success, await store.ListCitiesAsync(tenantId, all, cancellationToken));
    }

    public async Task<(PlaceResult Result, IReadOnlyList<PlaceDto> Items)> ListPlacesAsync(TenantId tenantId, UserId actorId, Guid? cityId, string? kind,
        string? search, bool includeInactive, int? limit, CancellationToken cancellationToken)
    {
        if (!await AllowedAsync(tenantId, actorId, PermissionKeys.CustomersRead, cancellationToken)) return (PlaceResult.Forbidden, Array.Empty<PlaceDto>());
        if (kind is not null && !IsKind(kind)) return (PlaceResult.InvalidData, Array.Empty<PlaceDto>());
        if (limit is < 1 or > MaxListLimit) return (PlaceResult.InvalidData, Array.Empty<PlaceDto>());
        var all = includeInactive && await AllowedAsync(tenantId, actorId, PermissionKeys.PlacesManage, cancellationToken);
        var term = string.IsNullOrWhiteSpace(search) ? null : search.Trim();
        if (term is { Length: > MaxPlaceNameLength }) return (PlaceResult.InvalidData, Array.Empty<PlaceDto>());
        return (PlaceResult.Success, await store.ListPlacesAsync(tenantId, cityId, kind, term, all, limit ?? DefaultListLimit, cancellationToken));
    }

    public async Task<(PlaceResult Result, Guid Id)> CreateCityAsync(TenantId tenantId, UserId actorId, CreateCityRequest request, CancellationToken cancellationToken)
    {
        if (!await AllowedAsync(tenantId, actorId, PermissionKeys.PlacesManage, cancellationToken)) return (PlaceResult.Forbidden, Guid.Empty);
        if (!IsName(request.Name, MaxCityNameLength) || !AreNames(MaxCityNameLength, request.NameCkb, request.NameAr, request.NameEn))
            return (PlaceResult.InvalidData, Guid.Empty);
        var id = Guid.NewGuid();
        var result = await store.CreateCityAsync(tenantId, actorId, id, Clean(request), cancellationToken);
        return (result, result == PlaceResult.Success ? id : Guid.Empty);
    }

    // A manager adds an active name; a field user's name is 'suggested' (usable at once, a manager reviews it later).
    public async Task<(PlaceResult Result, Guid Id)> CreatePlaceAsync(TenantId tenantId, UserId actorId, CreatePlaceRequest request, CancellationToken cancellationToken)
    {
        var manager = await AllowedAsync(tenantId, actorId, PermissionKeys.PlacesManage, cancellationToken);
        if (!manager && !await AllowedAsync(tenantId, actorId, PermissionKeys.CustomersWrite, cancellationToken)) return (PlaceResult.Forbidden, Guid.Empty);
        if (request.CityId == Guid.Empty || !IsKind(request.Kind) || !IsName(request.Name, MaxPlaceNameLength) ||
            !AreNames(MaxPlaceNameLength, request.NameCkb, request.NameAr, request.NameEn))
            return (PlaceResult.InvalidData, Guid.Empty);
        var id = Guid.NewGuid();
        var result = await store.CreatePlaceAsync(tenantId, actorId, id, Clean(request), manager ? "active" : "suggested", cancellationToken);
        return (result, result == PlaceResult.Success ? id : Guid.Empty);
    }

    public async Task<PlaceResult> UpdatePlaceAsync(TenantId tenantId, UserId actorId, Guid id, UpdatePlaceRequest request, CancellationToken cancellationToken)
    {
        if (!await AllowedAsync(tenantId, actorId, PermissionKeys.PlacesManage, cancellationToken)) return PlaceResult.Forbidden;
        if (id == Guid.Empty || !IsName(request.Name, MaxPlaceNameLength) || request.Status is not ("active" or "inactive") ||
            !AreNames(MaxPlaceNameLength, request.NameCkb, request.NameAr, request.NameEn))
            return PlaceResult.InvalidData;
        return await store.UpdatePlaceAsync(tenantId, actorId, id, Clean(request), cancellationToken);
    }

    public async Task<PlaceResult> MergePlaceAsync(TenantId tenantId, UserId actorId, Guid id, MergePlaceRequest request, CancellationToken cancellationToken)
    {
        if (!await AllowedAsync(tenantId, actorId, PermissionKeys.PlacesManage, cancellationToken)) return PlaceResult.Forbidden;
        if (id == Guid.Empty || request.IntoPlaceId == Guid.Empty || id == request.IntoPlaceId) return PlaceResult.InvalidData;
        return await store.MergePlaceAsync(tenantId, actorId, id, request.IntoPlaceId, cancellationToken);
    }

    // All rows are checked first; one bad row rejects the whole request, so a file is never half imported.
    public async Task<(PlaceResult Result, ImportPlacesResult? Summary)> ImportAsync(TenantId tenantId, UserId actorId, ImportPlacesRequest request,
        CancellationToken cancellationToken)
    {
        if (!await AllowedAsync(tenantId, actorId, PermissionKeys.PlacesManage, cancellationToken)) return (PlaceResult.Forbidden, null);
        var rows = request.Rows;
        if (rows is null || rows.Count == 0 || rows.Count > MaxImportRows) return (PlaceResult.InvalidData, null);
        foreach (var row in rows)
        {
            if (row is null || !IsName(row.City, MaxCityNameLength) || !IsKind(row.Kind) || !IsName(row.Name, MaxPlaceNameLength) ||
                !AreNames(MaxPlaceNameLength, row.NameCkb, row.NameAr, row.NameEn) || (row.Osm is { Length: > 40 }))
                return (PlaceResult.InvalidData, null);
        }
        var cleaned = rows.Select(r => r with
        {
            City = r.City.Trim(), Name = r.Name.Trim(), NameCkb = Trim(r.NameCkb), NameAr = Trim(r.NameAr), NameEn = Trim(r.NameEn), Osm = Trim(r.Osm)
        }).ToList();
        return (PlaceResult.Success, await store.ImportAsync(tenantId, actorId, cleaned, cancellationToken));
    }

    private async Task<bool> AllowedAsync(TenantId tenantId, UserId actorId, string permission, CancellationToken cancellationToken) =>
        !tenantId.IsEmpty && !actorId.IsEmpty && await permissions.HasPermissionAsync(tenantId, actorId, permission, cancellationToken);

    private static bool IsName(string? name, int max) => !string.IsNullOrWhiteSpace(name) && name.Trim().Length <= max;
    private static bool AreNames(int max, params string?[] names) => names.All(n => n is null || n.Trim().Length <= max);
    private static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static CreateCityRequest Clean(CreateCityRequest r) => new(r.Name.Trim(), Trim(r.NameCkb), Trim(r.NameAr), Trim(r.NameEn));
    private static CreatePlaceRequest Clean(CreatePlaceRequest r) => r with { Name = r.Name.Trim(), NameCkb = Trim(r.NameCkb), NameAr = Trim(r.NameAr), NameEn = Trim(r.NameEn) };
    private static UpdatePlaceRequest Clean(UpdatePlaceRequest r) => r with { Name = r.Name.Trim(), NameCkb = Trim(r.NameCkb), NameAr = Trim(r.NameAr), NameEn = Trim(r.NameEn) };
}
