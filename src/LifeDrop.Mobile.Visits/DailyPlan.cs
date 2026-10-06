using LifeDrop.Mobile.Localization;
namespace LifeDrop.Mobile.Visits;

// Iraq has one time zone (UTC+3, no daylight saving). The fixed offset is the fallback when the device lacks
// time zone data.
public static class IraqTime
{
    private static readonly TimeZoneInfo Zone = Find();

    public static DateOnly Today(TimeProvider clock) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), Zone).DateTime);

    private static TimeZoneInfo Find()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Asia/Baghdad"); }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return TimeZoneInfo.CreateCustomTimeZone("Iraq", TimeSpan.FromHours(3), "Iraq", "Iraq");
        }
    }
}

public enum DailyPlanState { Idle, Loading, Loaded, Empty, Error }

// Screen state of the daily plan (plan step 1.10, PRD §5.1), kept free of UI types so it can be tested.
// A failed refresh keeps the visits already shown and marks them as possibly out of date.
public sealed class DailyPlan
{
    private const string CacheKey = "daily-plan";
    private static readonly System.Text.Json.JsonSerializerOptions Json = new(System.Text.Json.JsonSerializerDefaults.Web);
    private readonly IVisitsApiClient _api;
    private readonly TimeProvider _clock;
    private readonly LifeDrop.Mobile.Offline.IDurableStore? _cache;

    // With a cache, the last plan received today is kept on the phone and shown (as possibly out of date) when the
    // app starts without internet (plan step 1.14).
    public DailyPlan(IVisitsApiClient api, TimeProvider clock, LifeDrop.Mobile.Offline.IDurableStore? cache = null)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _cache = cache;
    }

    public DailyPlanState State { get; private set; } = DailyPlanState.Idle;
    public IReadOnlyList<PlannedVisit> Visits { get; private set; } = Array.Empty<PlannedVisit>();
    public DateOnly Date { get; private set; }
    public PlanLoadStatus? Error { get; private set; }
    // True when an error is shown over visits from an earlier successful load.
    public bool IsStale => State == DailyPlanState.Error && Visits.Count > 0;
    public bool IsLoading => State == DailyPlanState.Loading;

    public string? Message => State switch
    {
        DailyPlanState.Empty => Texts.Get("app.plan.empty"),
        DailyPlanState.Error => Texts.Get(Error switch
        {
            PlanLoadStatus.NetworkError => "app.plan.network",
            PlanLoadStatus.SessionExpired => "app.sessionEnded",
            _ => "app.plan.server"
        }) + (IsStale ? " " + Texts.Get("app.plan.stale") : ""),
        _ => null
    };

    // Loads today's plan (Iraq date). A call while a load is running is ignored, so a double tap sends one request.
    public async Task<bool> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (State == DailyPlanState.Loading) return false;
        var previous = State;
        State = DailyPlanState.Loading;
        var today = IraqTime.Today(_clock);
        try
        {
            var result = await _api.GetDailyPlanAsync(today, cancellationToken);
            if (result.Status == PlanLoadStatus.Success)
            {
                Date = today;
                Visits = result.Visits;
                Error = null;
                _cache?.Write(CacheKey, System.Text.Json.JsonSerializer.Serialize(new CachedPlan(today, result.Visits.ToList()), Json));
                State = Visits.Count == 0 ? DailyPlanState.Empty : DailyPlanState.Loaded;
            }
            else
            {
                // A new day must not show yesterday's visits as today's.
                if (Date != today) Visits = Array.Empty<PlannedVisit>();
                if (Visits.Count == 0 && ReadCache(today) is { } cached)
                {
                    Date = today;
                    Visits = cached;
                }
                Error = result.Status;
                State = DailyPlanState.Error;
            }
            return true;
        }
        catch (OperationCanceledException)
        {
            State = previous;
            throw;
        }
        catch (Exception)
        {
            // Never leave the screen stuck in Loading.
            if (Date != today) Visits = Array.Empty<PlannedVisit>();
            Error = PlanLoadStatus.ServerError;
            State = DailyPlanState.Error;
            return true;
        }
    }

    // Changing the plan (plan step 1.23) needs the server: it is not queued offline.
    // Moves one visit a place up (-1) or down (+1) and saves the order of the whole day; the list changes only after
    // the server accepted it.
    public async Task<VisitActionResult> MoveAsync(Guid visitId, int delta, CancellationToken cancellationToken = default)
    {
        var list = Visits.ToList();
        var from = list.FindIndex(v => v.Id == visitId);
        var to = from + delta;
        if (from < 0 || to < 0 || to >= list.Count || Date == default) return VisitActionResult.InvalidState;
        (list[from], list[to]) = (list[to], list[from]);

        var result = await _api.ReorderPlanAsync(Date, list.Select(v => v.Id).ToList(), cancellationToken);
        if (result == VisitActionResult.Success)
        {
            Visits = list.Select((v, i) => v with { SortOrder = i + 1 }).ToList();
            CacheVisits();
        }
        return result;
    }

    // Moves a visit to another day (for example when the customer is not available); it leaves today's list.
    public async Task<VisitActionResult> PostponeAsync(Guid visitId, DateOnly newDay, string? reason, CancellationToken cancellationToken = default)
    {
        if (newDay <= Date) return VisitActionResult.InvalidData;
        var result = await _api.PostponeAsync(visitId, newDay, string.IsNullOrWhiteSpace(reason) ? null : reason.Trim(), cancellationToken);
        if (result == VisitActionResult.Success)
        {
            Visits = Visits.Where(v => v.Id != visitId).ToList();
            State = Visits.Count == 0 ? DailyPlanState.Empty : DailyPlanState.Loaded;
            CacheVisits();
        }
        return result;
    }

    // The days offered for "another day": tomorrow and the following days.
    public static IReadOnlyList<DateOnly> PostponeChoices(DateOnly today, int days = 7) =>
        Enumerable.Range(1, days).Select(today.AddDays).ToList();

    // Only a visit that has not started its work can move, and not while steps for it wait on the phone.
    public static bool CanPostpone(PlannedVisit visit, bool hasPendingSteps) =>
        !hasPendingSteps && visit.Status is "Planned" or "EnRoute";

    // The message for a failed plan change, or null on success.
    public static string? ChangeMessage(VisitActionResult result) => result switch
    {
        VisitActionResult.Success => null,
        VisitActionResult.SessionExpired => Texts.Get("app.sessionEnded"),
        VisitActionResult.InvalidState or VisitActionResult.InvalidData => Texts.Get("app.plan.cannotPostpone"),
        VisitActionResult.ServerError or VisitActionResult.Forbidden => Texts.Get("app.plan.server"),
        _ => Texts.Get("app.plan.changeFailed")
    };

    // " · ★ manager priority: high" for a customer a manager marked high or low; nothing for normal.
    public static string PriorityNote(string priority) => priority switch
    {
        "high" => " · ★ " + Texts.Get("app.priority.high"),
        "low" => " · " + Texts.Get("app.priority.low"),
        _ => ""
    };

    // The extra text of a plan row: outside the customer's hours, moved before.
    public static string PlanNotes(PlannedVisit visit)
    {
        var notes = "";
        notes += PriorityNote(visit.CustomerPriority);
        if (visit.WindowStatus == "outside") notes += " · " + Texts.Get("app.plan.outsideHours");
        if (visit.PostponeCount > 0) notes += " · " + Texts.Get("app.plan.moved", ("count", visit.PostponeCount.ToString()));
        return notes;
    }

    private void CacheVisits() =>
        _cache?.Write(CacheKey, System.Text.Json.JsonSerializer.Serialize(new CachedPlan(Date, Visits.ToList()), Json));

    private IReadOnlyList<PlannedVisit>? ReadCache(DateOnly today)
    {
        var text = _cache?.Read(CacheKey);
        if (text is null) return null;
        try
        {
            var cached = System.Text.Json.JsonSerializer.Deserialize<CachedPlan>(text, Json);
            return cached is not null && cached.Date == today && cached.Visits is { Count: > 0 } ? cached.Visits : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private sealed record CachedPlan(DateOnly Date, List<PlannedVisit>? Visits);

    public static string StatusLabel(string status) => Texts.Has("app.status." + status) ? Texts.Get("app.status." + status) : status;

    public static string TypeLabel(string visitType) =>
        Texts.Get(visitType is "pharmacy" or "doctor" or "wholesaler" ? "app.type." + visitType : "app.type.other");
}
