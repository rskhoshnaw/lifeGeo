using System.Text;
using LifeDrop.Application.Roles;
using LifeDrop.Domain.Common;

namespace LifeDrop.Application.Places;

// Places tree of a company (plan step 1.23-b): city -> neighbourhood -> street -> alley. Reading needs customers.read.
// Any employee with customers.write adds a neighbourhood, street or alley: it is part of the reference at once, there is
// no approval step. places.manage (sales manager, supervisor) adds cities, fixes spelling, moves, deactivates, merges,
// imports and exports.
// Kind: city | neighbourhood | street | alley. Status: active | inactive. MergedInto: the place this one was merged into.
// ChildCount: active places directly below.
public sealed record PlaceDto(Guid Id, Guid? ParentId, string Kind, string Name, string? NameCkb, string? NameAr, string? NameEn, string Status,
    Guid? MergedInto = null, int ChildCount = 0,
    // Plan step 4.42: prices are hidden from field users for customers in this city.
    bool HidePrices = false);

// ParentId: null for a city, otherwise the place above (neighbourhood: a city, street: a neighbourhood, alley: a street
// or a neighbourhood).
public sealed record CreatePlaceRequest(Guid? ParentId, string Kind, string Name, string? NameCkb = null, string? NameAr = null, string? NameEn = null);

// Status: active | inactive. ParentId: when given and different, the place moves there (same kind rules as above).
public sealed record UpdatePlaceRequest(string Name, string? NameCkb, string? NameAr, string? NameEn, string Status, Guid? ParentId = null);

// Moves every customer and every place below to IntoPlaceId (same kind) and retires the place.
public sealed record MergePlaceRequest(Guid IntoPlaceId);

// One row of an import file. City is needed for every kind except a city (where it is ignored), Neighbourhood for a street and an
// alley, Street is optional for an alley. Missing places above are created. Existing places are skipped, never overwritten.
public sealed record ImportPlaceRow(string Kind, string Name, string? City = null, string? Neighbourhood = null, string? Street = null,
    string? NameCkb = null, string? NameAr = null, string? NameEn = null, bool Active = true);
public sealed record ImportPlacesRequest(IReadOnlyList<ImportPlaceRow> Rows);
public sealed record ImportPlacesResult(int Created, int Skipped);

// One line of the export for other projects: stable id, parent id and the names of the whole path.
public sealed record PlaceExportRow(Guid Id, Guid? ParentId, string Kind, string City, string? Neighbourhood, string? Street, string Name,
    string? NameCkb, string? NameAr, string? NameEn, string Status);

public enum PlaceResult { Success, Forbidden, InvalidData, NotFound, Duplicate, Conflict }

public interface IPlaceStore
{
    // Without includeInactive: active places only. parentId limits to the children of one place, kind to one kind.
    Task<IReadOnlyList<PlaceDto>> ListAsync(TenantId tenantId, Guid? parentId, string? kind, string? search, bool includeInactive, int limit,
        CancellationToken cancellationToken);
    // NotFound: unknown parent. InvalidData: the parent is inactive or of a kind that cannot hold this kind. Duplicate: the name
    // exists under that parent.
    Task<PlaceResult> CreateAsync(TenantId tenantId, UserId actorId, Guid id, CreatePlaceRequest request, CancellationToken cancellationToken);
    Task<PlaceResult> UpdateAsync(TenantId tenantId, UserId actorId, Guid id, UpdatePlaceRequest request, CancellationToken cancellationToken);
    Task<PlaceResult> MergeAsync(TenantId tenantId, UserId actorId, Guid id, Guid intoPlaceId, CancellationToken cancellationToken);
    Task<ImportPlacesResult> ImportAsync(TenantId tenantId, UserId actorId, IReadOnlyList<ImportPlaceRow> rows, CancellationToken cancellationToken);
    Task<IReadOnlyList<PlaceDto>> ListAllAsync(TenantId tenantId, bool includeInactive, CancellationToken cancellationToken);
}

public sealed class PlaceService(IPlaceStore store, IPermissionStore permissions)
{
    public const int MaxNameLength = 160;
    public const int MaxImportRows = 2000;
    public const int DefaultListLimit = 500;
    public const int MaxListLimit = 5000;
    public static readonly string[] Kinds = ["city", "neighbourhood", "street", "alley"];

    public static bool IsKind(string? kind) => kind is not null && Kinds.Contains(kind);

    public async Task<(PlaceResult Result, IReadOnlyList<PlaceDto> Items)> ListAsync(TenantId tenantId, UserId actorId, Guid? parentId, string? kind,
        string? search, bool includeInactive, int? limit, CancellationToken cancellationToken)
    {
        if (!await AllowedAsync(tenantId, actorId, PermissionKeys.CustomersRead, cancellationToken)) return (PlaceResult.Forbidden, Array.Empty<PlaceDto>());
        if ((kind is not null && !IsKind(kind)) || limit is < 1 or > MaxListLimit || (search is { Length: > MaxNameLength }))
            return (PlaceResult.InvalidData, Array.Empty<PlaceDto>());
        var all = includeInactive && await AllowedAsync(tenantId, actorId, PermissionKeys.PlacesManage, cancellationToken);
        var term = string.IsNullOrWhiteSpace(search) ? null : search.Trim();
        return (PlaceResult.Success, await store.ListAsync(tenantId, parentId, kind, term, all, limit ?? DefaultListLimit, cancellationToken));
    }

    // A city needs places.manage; a neighbourhood, street or alley can be added by any employee who may write customers.
    public async Task<(PlaceResult Result, Guid Id)> CreateAsync(TenantId tenantId, UserId actorId, CreatePlaceRequest request, CancellationToken cancellationToken)
    {
        if (!IsKind(request.Kind)) return (PlaceResult.InvalidData, Guid.Empty);
        var permission = request.Kind == "city" ? PermissionKeys.PlacesManage : PermissionKeys.CustomersWrite;
        if (!await AllowedAsync(tenantId, actorId, permission, cancellationToken)) return (PlaceResult.Forbidden, Guid.Empty);
        if (!IsName(request.Name) || !AreNames(request.NameCkb, request.NameAr, request.NameEn) ||
            (request.Kind == "city") != (request.ParentId is null) || request.ParentId == Guid.Empty)
            return (PlaceResult.InvalidData, Guid.Empty);

        var id = Guid.NewGuid();
        var result = await store.CreateAsync(tenantId, actorId, id, Clean(request), cancellationToken);
        return (result, result == PlaceResult.Success ? id : Guid.Empty);
    }

    public async Task<PlaceResult> UpdateAsync(TenantId tenantId, UserId actorId, Guid id, UpdatePlaceRequest request, CancellationToken cancellationToken)
    {
        if (!await AllowedAsync(tenantId, actorId, PermissionKeys.PlacesManage, cancellationToken)) return PlaceResult.Forbidden;
        if (id == Guid.Empty || request.ParentId == Guid.Empty || !IsName(request.Name) || request.Status is not ("active" or "inactive") ||
            !AreNames(request.NameCkb, request.NameAr, request.NameEn))
            return PlaceResult.InvalidData;
        return await store.UpdateAsync(tenantId, actorId, id,
            request with { Name = request.Name.Trim(), NameCkb = Trim(request.NameCkb), NameAr = Trim(request.NameAr), NameEn = Trim(request.NameEn) }, cancellationToken);
    }

    public async Task<PlaceResult> MergeAsync(TenantId tenantId, UserId actorId, Guid id, MergePlaceRequest request, CancellationToken cancellationToken)
    {
        if (!await AllowedAsync(tenantId, actorId, PermissionKeys.PlacesManage, cancellationToken)) return PlaceResult.Forbidden;
        if (id == Guid.Empty || request.IntoPlaceId == Guid.Empty || id == request.IntoPlaceId) return PlaceResult.InvalidData;
        return await store.MergeAsync(tenantId, actorId, id, request.IntoPlaceId, cancellationToken);
    }

    // All rows are checked first; one bad row rejects the whole request, so a file is never half imported.
    public async Task<(PlaceResult Result, ImportPlacesResult? Summary)> ImportAsync(TenantId tenantId, UserId actorId, ImportPlacesRequest request,
        CancellationToken cancellationToken)
    {
        if (!await AllowedAsync(tenantId, actorId, PermissionKeys.PlacesManage, cancellationToken)) return (PlaceResult.Forbidden, null);
        var rows = request.Rows;
        if (rows is null || rows.Count == 0 || rows.Count > MaxImportRows || rows.Any(r => !IsValidImportRow(r))) return (PlaceResult.InvalidData, null);
        var cleaned = rows.Select(r => r with
        {
            Name = r.Name.Trim(), City = r.Kind == "city" ? null : Trim(r.City), Neighbourhood = Trim(r.Neighbourhood), Street = Trim(r.Street),
            NameCkb = Trim(r.NameCkb), NameAr = Trim(r.NameAr), NameEn = Trim(r.NameEn)
        }).ToList();
        return (PlaceResult.Success, await store.ImportAsync(tenantId, actorId, cleaned, cancellationToken));
    }

    public static bool IsValidImportRow(ImportPlaceRow? row)
    {
        if (row is null || !IsKind(row.Kind) || !IsName(row.Name) || !AreNames(row.NameCkb, row.NameAr, row.NameEn, row.City, row.Neighbourhood, row.Street)) return false;
        var city = !string.IsNullOrWhiteSpace(row.City);
        var neighbourhood = !string.IsNullOrWhiteSpace(row.Neighbourhood);
        var street = !string.IsNullOrWhiteSpace(row.Street);
        return row.Kind switch
        {
            "city" => !neighbourhood && !street,   // the city column of a city row (as in an export) is ignored
            "neighbourhood" => city && !neighbourhood && !street,
            "street" => city && neighbourhood && !street,
            _ => city && neighbourhood   // alley: the street is optional
        };
    }

    // The whole tree for other projects, one row per place with the names of its path.
    public async Task<(PlaceResult Result, IReadOnlyList<PlaceExportRow> Rows)> ExportAsync(TenantId tenantId, UserId actorId, bool includeInactive,
        CancellationToken cancellationToken)
    {
        if (!await AllowedAsync(tenantId, actorId, PermissionKeys.PlacesManage, cancellationToken)) return (PlaceResult.Forbidden, Array.Empty<PlaceExportRow>());
        var all = await store.ListAllAsync(tenantId, includeInactive, cancellationToken);
        return (PlaceResult.Success, PlaceExport.Build(all));
    }

    private async Task<bool> AllowedAsync(TenantId tenantId, UserId actorId, string permission, CancellationToken cancellationToken) =>
        !tenantId.IsEmpty && !actorId.IsEmpty && await permissions.HasPermissionAsync(tenantId, actorId, permission, cancellationToken);

    private static bool IsName(string? name) => !string.IsNullOrWhiteSpace(name) && name.Trim().Length <= MaxNameLength;
    private static bool AreNames(params string?[] names) => names.All(n => n is null || n.Trim().Length <= MaxNameLength);
    private static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static CreatePlaceRequest Clean(CreatePlaceRequest r) => r with { Name = r.Name.Trim(), NameCkb = Trim(r.NameCkb), NameAr = Trim(r.NameAr), NameEn = Trim(r.NameEn) };
}

// Builds the export rows and the CSV text. The CSV has the same columns as the import file, plus id, parent_id and
// status, so an exported file can be edited and imported again.
public static class PlaceExport
{
    public const string CsvHeader = "id,parent_id,kind,city,neighbourhood,street,name,name_ckb,name_ar,name_en,status";

    public static IReadOnlyList<PlaceExportRow> Build(IReadOnlyList<PlaceDto> places)
    {
        var byId = places.ToDictionary(p => p.Id);
        string? NameOf(PlaceDto place, string kind)
        {
            // Walks up from the place to the ancestor of that kind (a place only knows ancestors in the given list).
            for (var current = place; ; )
            {
                if (current.Kind == kind) return current.Name;
                if (current.ParentId is not { } parent || !byId.TryGetValue(parent, out var next)) return null;
                current = next;
            }
        }

        var order = new Dictionary<string, int> { ["city"] = 0, ["neighbourhood"] = 1, ["street"] = 2, ["alley"] = 3 };
        return places
            .Select(p => new PlaceExportRow(p.Id, p.ParentId, p.Kind,
                NameOf(p, "city") ?? "", p.Kind == "neighbourhood" ? null : NameOf(p, "neighbourhood"), p.Kind is "street" ? null : NameOf(p, "street"),
                p.Name, p.NameCkb, p.NameAr, p.NameEn, p.Status))
            .OrderBy(r => r.City, StringComparer.Ordinal).ThenBy(r => order[r.Kind]).ThenBy(r => r.Neighbourhood ?? "", StringComparer.Ordinal)
            .ThenBy(r => r.Street ?? "", StringComparer.Ordinal).ThenBy(r => r.Name, StringComparer.Ordinal).ThenBy(r => r.Id)
            .ToList();
    }

    // UTF-8 text with a header; fields with commas, quotes or line breaks are quoted.
    public static string ToCsv(IEnumerable<PlaceExportRow> rows)
    {
        var text = new StringBuilder(CsvHeader).Append("\r\n");
        foreach (var r in rows)
            text.AppendJoin(',', Field(r.Id.ToString()), Field(r.ParentId?.ToString()), Field(r.Kind), Field(r.City), Field(r.Neighbourhood), Field(r.Street),
                Field(r.Name), Field(r.NameCkb), Field(r.NameAr), Field(r.NameEn), Field(r.Status)).Append("\r\n");
        return text.ToString();
    }

    private static string Field(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        // A leading = + - @ could be run as a formula by a spreadsheet: keep it as text.
        if (value[0] is '=' or '+' or '-' or '@') value = "'" + value;
        return value.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }
}
