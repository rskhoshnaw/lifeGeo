using System.Globalization;
using System.Text;

namespace LifeDrop.Mobile.Visits;

// One list of the picker: the places directly below ParentId (the cities when ParentId is null).
public sealed class PlacePickerLevel
{
    public Guid? ParentId { get; init; }
    // Kind of the place above ("city", "neighbourhood", "street"); null for the list of cities.
    public string? ParentKind { get; init; }
    public IReadOnlyList<PlaceItem> Items { get; set; } = Array.Empty<PlaceItem>();
    public Guid? SelectedId { get; set; }
}

public enum PlaceAddResult { Added, UsedExisting, Failed }

// Choosing the place of a customer, step by step (plan step 1.23-b): city, then what is below it, down to an alley. Names the
// field user types are matched against the list first (so nobody adds "Ankawa" twice) and added to the tree only when
// they are new; an added name is part of the reference at once. The app does not add cities (a manager does).
public sealed class PlacePicker(ICustomersApiClient api)
{
    private readonly List<PlacePickerLevel> _levels = new();

    public IReadOnlyList<PlacePickerLevel> Levels => _levels;
    // The most precise place chosen, or null.
    public Guid? PlaceId => _levels.LastOrDefault(l => l.SelectedId is not null)?.SelectedId;
    // The last failed call; Success when everything worked.
    public VisitActionResult Result { get; private set; } = VisitActionResult.Success;

    // Starts with the cities and opens the lists along the path of a saved place.
    public async Task LoadAsync(IReadOnlyList<PlacePathItem>? path, CancellationToken cancellationToken = default)
    {
        _levels.Clear();
        Result = VisitActionResult.Success;
        if (!await OpenLevelAsync(null, null, cancellationToken)) return;
        foreach (var step in path ?? Array.Empty<PlacePathItem>())
        {
            var level = _levels[^1];
            if (level.Items.All(p => p.Id != step.Id)) break;   // the place was merged or deactivated since
            level.SelectedId = step.Id;
            if (!await OpenBelowAsync(level, step.Id, step.Kind, cancellationToken)) return;
        }
    }

    // Chooses a place in a list (or clears the choice with null) and opens the list below it; the lists deeper than this one go.
    public async Task SelectAsync(int level, Guid? placeId, CancellationToken cancellationToken = default)
    {
        if (level < 0 || level >= _levels.Count) return;
        var current = _levels[level];
        if (placeId is { } id && current.Items.All(p => p.Id != id)) return;
        _levels.RemoveRange(level + 1, _levels.Count - level - 1);
        current.SelectedId = placeId;
        if (placeId is { } chosen) await OpenBelowAsync(current, chosen, current.Items.First(p => p.Id == chosen).Kind, cancellationToken);
    }

    // Existing names of a list that contain the text, best matches first (names that start with it), at most max.
    public IReadOnlyList<PlaceItem> Suggest(int level, string text, string language, int max = 5)
    {
        var wanted = Normalize(text);
        if (level < 0 || level >= _levels.Count || wanted.Length == 0) return Array.Empty<PlaceItem>();
        return _levels[level].Items
            .Select(p => (Place: p, Names: new[] { p.Name, p.NameCkb, p.NameAr, p.NameEn }.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => Normalize(n!)).ToList()))
            .Where(x => x.Names.Any(n => n.Contains(wanted, StringComparison.Ordinal)))
            .OrderBy(x => x.Names.Any(n => n.StartsWith(wanted, StringComparison.Ordinal)) ? 0 : 1)
            .ThenBy(x => x.Place.DisplayName(language), StringComparer.CurrentCultureIgnoreCase)
            .Take(max)
            .Select(x => x.Place)
            .ToList();
    }

    // What can be added directly below the place above a list: a neighbourhood below a city, a street or an alley below a
    // neighbourhood, an alley below a street. Nothing in the list of cities or below an alley.
    public static IReadOnlyList<string> AddableKinds(PlacePickerLevel level) => level.ParentKind switch
    {
        "city" => ["neighbourhood"],
        "neighbourhood" => ["street", "alley"],
        "street" => ["alley"],
        _ => Array.Empty<string>()
    };

    // Adds a new name to the list at level, or chooses the existing one with the same name and kind. A name somebody else added
    // a moment ago (409) is chosen too.
    public async Task<PlaceAddResult> AddAsync(int level, string kind, string name, CancellationToken cancellationToken = default)
    {
        Result = VisitActionResult.Success;
        var text = name?.Trim() ?? "";
        if (level < 0 || level >= _levels.Count || text.Length == 0 || !AddableKinds(_levels[level]).Contains(kind) || _levels[level].ParentId is not { } parent)
        {
            Result = VisitActionResult.InvalidData;
            return PlaceAddResult.Failed;
        }

        var wanted = Normalize(text);
        PlaceItem? Existing() => _levels[level].Items.FirstOrDefault(p => p.Kind == kind && Normalize(p.Name) == wanted);
        if (Existing() is { } same)
        {
            await SelectAsync(level, same.Id, cancellationToken);
            return PlaceAddResult.UsedExisting;
        }

        var (created, id) = await api.CreatePlaceAsync(parent, kind, text, cancellationToken);
        if (created is not (VisitActionResult.Success or VisitActionResult.Conflict) || (created == VisitActionResult.Success && id is null))
        {
            Result = created == VisitActionResult.Success ? VisitActionResult.ServerError : created;
            return PlaceAddResult.Failed;
        }

        var (listed, items) = await api.GetPlacesAsync(parent, cancellationToken);
        if (listed != VisitActionResult.Success)
        {
            Result = listed;
            return PlaceAddResult.Failed;
        }
        _levels[level].Items = items;
        var target = id is { } newId ? items.FirstOrDefault(p => p.Id == newId) : Existing();
        if (target is null)
        {
            Result = VisitActionResult.ServerError;
            return PlaceAddResult.Failed;
        }
        await SelectAsync(level, target.Id, cancellationToken);
        return created == VisitActionResult.Success ? PlaceAddResult.Added : PlaceAddResult.UsedExisting;
    }

    // Opens the list below a chosen place; an alley has nothing below it.
    private async Task<bool> OpenBelowAsync(PlacePickerLevel level, Guid placeId, string kind, CancellationToken cancellationToken) =>
        kind == "alley" || await OpenLevelAsync(placeId, kind, cancellationToken);

    private async Task<bool> OpenLevelAsync(Guid? parentId, string? parentKind, CancellationToken cancellationToken)
    {
        var (result, items) = await api.GetPlacesAsync(parentId, cancellationToken);
        if (result != VisitActionResult.Success)
        {
            Result = result;
            return false;
        }
        _levels.Add(new PlacePickerLevel { ParentId = parentId, ParentKind = parentKind, Items = items });
        return true;
    }

    // Lower case, one kind of Kurdish/Persian yeh and kaf for the Arabic ones, no tatweel or zero-width joiner, single spaces.
    public static string Normalize(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text.Trim().ToLowerInvariant())
        {
            switch (c)
            {
                case 'ي' or 'ى': builder.Append('ی'); break;
                case 'ك': builder.Append('ک'); break;
                case 'ـ' or '‌' or '‍': break;
                case var _ when char.IsWhiteSpace(c): if (builder.Length > 0 && builder[^1] != ' ') builder.Append(' '); break;
                default: builder.Append(c); break;
            }
        }
        return builder.ToString().TrimEnd().Normalize(NormalizationForm.FormKC);
    }
}
