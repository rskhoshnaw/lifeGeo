namespace LifeDrop.Mobile.Visits;

// What the field user searches for when adding customers to their own plan (plan step 1.25): a place (and everything below it)
// and/or a text in the customer's code or name and/or an area drawn on the map (plan step 1.27).
public sealed record SelfPlanQuery(DateOnly Day, Guid? PlaceId, string? Text, IReadOnlyList<AreaPoint>? Area = null);

// A corner of an area drawn on the map (plan step 1.27).
public sealed record AreaPoint(double Latitude, double Longitude);

// One customer of the list. Included = false: the server would leave it out (SkipReason: outside_hours | over_limit); the
// customer is shown but cannot be ticked, except outside_hours which only warns (the user knows the customer).
public sealed record SelfPlanCandidate(Guid CustomerId, string Code, string Name, string? Place, DateOnly? LastVisitDate, string WindowStatus, string CustomerPriority = "normal");

// Add customers to the caller's own plan: POST /visit-plan/preview and /visit-plan/generate with no assignee (= the caller).
// Online only; the server checks the caller's customers again.
public interface ISelfPlanApiClient
{
    Task<(VisitActionResult Result, IReadOnlyList<SelfPlanCandidate> Candidates)> SearchAsync(SelfPlanQuery query, CancellationToken cancellationToken = default);
    Task<(VisitActionResult Result, int Created)> AddAsync(SelfPlanQuery query, IReadOnlyList<Guid> customerIds, CancellationToken cancellationToken = default);
}

// Search, tick and add (plan step 1.25). Nothing is added without being ticked; at most MaxPerAdd at a time.
public sealed class SelfPlanner(ISelfPlanApiClient api)
{
    public const int MinTextLength = 2;
    public const int MaxPerAdd = 200;
    public const int MaxTextLength = 60;

    private readonly HashSet<Guid> _selected = [];

    public SelfPlanQuery? Query { get; private set; }
    public IReadOnlyList<SelfPlanCandidate> Candidates { get; private set; } = Array.Empty<SelfPlanCandidate>();
    public IReadOnlyCollection<Guid> Selected => _selected;
    public VisitActionResult Result { get; private set; } = VisitActionResult.Success;

    // A search needs a place or at least two letters, so the whole customer list is never fetched by accident.
    public static bool CanSearch(Guid? placeId, string? text, IReadOnlyList<AreaPoint>? area = null) =>
        placeId is not null || (text?.Trim().Length ?? 0) >= MinTextLength || AreaDrawing.IsValid(area);

    public async Task<VisitActionResult> SearchAsync(DateOnly day, Guid? placeId, string? text, IReadOnlyList<AreaPoint>? area = null, CancellationToken cancellationToken = default)
    {
        _selected.Clear();
        var trimmed = string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        if (trimmed is { Length: > MaxTextLength }) trimmed = trimmed[..MaxTextLength];
        if (area is not null && !AreaDrawing.IsValid(area)) area = null;
        if (!CanSearch(placeId, trimmed, area))
        {
            Candidates = Array.Empty<SelfPlanCandidate>();
            return Result = VisitActionResult.InvalidData;
        }
        var query = new SelfPlanQuery(day, placeId, trimmed, area);
        var (result, candidates) = await api.SearchAsync(query, cancellationToken);
        Result = result;
        Query = result == VisitActionResult.Success ? query : null;
        Candidates = result == VisitActionResult.Success ? candidates : Array.Empty<SelfPlanCandidate>();
        return result;
    }

    public bool IsSelected(Guid customerId) => _selected.Contains(customerId);

    // Returns the new state; a customer that is not in the list, or the 201st, cannot be ticked.
    public bool Toggle(Guid customerId)
    {
        if (_selected.Remove(customerId)) return false;
        if (_selected.Count >= MaxPerAdd || Candidates.All(c => c.CustomerId != customerId)) return false;
        _selected.Add(customerId);
        return true;
    }

    // Adds the ticked customers (in the order of the list) and removes them from the list; the rest stays for more.
    public async Task<(VisitActionResult Result, int Created)> AddAsync(CancellationToken cancellationToken = default)
    {
        if (Query is null || _selected.Count == 0) return (VisitActionResult.InvalidState, 0);
        var ids = Candidates.Where(c => _selected.Contains(c.CustomerId)).Select(c => c.CustomerId).ToList();
        var (result, created) = await api.AddAsync(Query, ids, cancellationToken);
        if (result == VisitActionResult.Success)
        {
            Candidates = Candidates.Where(c => !_selected.Contains(c.CustomerId)).ToList();
            _selected.Clear();
        }
        return (result, created);
    }
}
