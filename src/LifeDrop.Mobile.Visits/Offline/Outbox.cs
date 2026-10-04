using System.Text.Json;
using LifeDrop.Mobile.Visits;

namespace LifeDrop.Mobile.Offline;

public enum OutboxKind { EnRoute, Arrive, CheckIn, Report, CheckOut }

// One visit step saved on the phone while there was no internet (plan step 1.14, decision D5).
public sealed record OutboxItem(
    Guid Id,
    Guid VisitId,
    DateOnly ScheduleDate,
    OutboxKind Kind,
    DateTimeOffset CreatedAt,
    string IdempotencyKey,
    DeviceFix? Fix = null,
    long? CapturedMonotonicMs = null,
    string? BootId = null,
    VisitReportDraft? Report = null,
    SyncState State = SyncState.Local,
    VisitActionResult? Answer = null,
    bool ReviewRequired = false);

// The phone's monotonic clock: time since boot, including sleep, unaffected by changing the phone's clock.
// BootId changes on every restart of the phone, which makes earlier readings meaningless.
public interface IMonotonicClock
{
    long ElapsedMs { get; }
    string? BootId { get; }
}

public sealed record OutboxCounts(int Local, int Sent, int Accepted, int Rejected);

// Visit steps waiting for the internet (plan step 1.14, PRD §7, decision D5). Saved after every change, sent in the
// order they were made, and never twice: check-in/out carry an idempotency key; a repeated report is the same
// upsert; a repeated en-route/arrive is recognised by the visit already being past that step. A transient failure
// (no connection, server error, ended session) stops sending and keeps the step on the phone; a refusal marks the
// step rejected and every later step of the same visit too.
public sealed class Outbox
{
    private const string Key = "visit-outbox";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    // Finished items are kept for a while so the user can see the result, then removed.
    public static readonly TimeSpan KeepFinishedFor = TimeSpan.FromHours(48);

    private readonly IVisitsApiClient _api;
    private readonly IMonotonicClock _monotonic;
    private readonly TimeProvider _clock;
    private readonly IDurableStore? _store;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _syncing = new(1, 1);
    private List<OutboxItem> _items = new();

    public Outbox(IVisitsApiClient api, IMonotonicClock monotonic, TimeProvider clock, IDurableStore? store = null)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _monotonic = monotonic ?? throw new ArgumentNullException(nameof(monotonic));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _store = store;
        Load();
    }

    public bool SessionExpired { get; private set; }

    public IReadOnlyList<OutboxItem> Items
    {
        get { lock (_gate) return _items.ToList(); }
    }

    public IReadOnlyList<OutboxItem> ForVisit(Guid visitId)
    {
        lock (_gate) return _items.Where(i => i.VisitId == visitId).ToList();
    }

    // True when an earlier step of this visit still waits, so a new step must wait behind it.
    public bool HasPending(Guid visitId)
    {
        lock (_gate) return _items.Any(i => i.VisitId == visitId && i.State is SyncState.Local or SyncState.Sent);
    }

    public OutboxCounts Counts
    {
        get
        {
            lock (_gate)
                return new OutboxCounts(_items.Count(i => i.State == SyncState.Local), _items.Count(i => i.State == SyncState.Sent),
                    _items.Count(i => i.State == SyncState.Accepted), _items.Count(i => i.State == SyncState.Rejected));
        }
    }

    // Saves a step on the phone. For check-in/out the monotonic time is read now, right after the fix was taken.
    public OutboxItem Enqueue(Guid visitId, DateOnly scheduleDate, OutboxKind kind, string idempotencyKey, DeviceFix? fix = null,
        VisitReportDraft? report = null, long? capturedMonotonicMs = null, string? bootId = null)
    {
        if (kind is OutboxKind.CheckIn or OutboxKind.CheckOut && fix is null) throw new ArgumentException("A position is required.", nameof(fix));
        if (kind == OutboxKind.Report && report is null) throw new ArgumentException("A report is required.", nameof(report));
        var item = new OutboxItem(Guid.NewGuid(), visitId, scheduleDate, kind, _clock.GetUtcNow(), idempotencyKey, fix,
            fix is null ? null : capturedMonotonicMs ?? _monotonic.ElapsedMs, fix is null ? null : bootId ?? _monotonic.BootId, report);
        lock (_gate)
        {
            _items.Add(item);
            Save();
        }
        return item;
    }

    // Sends waiting steps in order. Returns false when something transient stopped it (try again later).
    public async Task<bool> SyncAsync(CancellationToken cancellationToken = default)
    {
        await _syncing.WaitAsync(cancellationToken);
        try
        {
            Prune();
            while (true)
            {
                OutboxItem? next;
                lock (_gate) next = _items.FirstOrDefault(i => i.State is SyncState.Local or SyncState.Sent);
                if (next is null) return true;

                Update(next with { State = SyncState.Sent });
                var (answer, review) = await SendAsync(next, cancellationToken);
                if (answer is VisitActionResult.NetworkError or VisitActionResult.ServerError or VisitActionResult.SessionExpired)
                {
                    if (answer == VisitActionResult.SessionExpired) SessionExpired = true;
                    Update(next with { State = SyncState.Local, Answer = answer });
                    return false;
                }
                if (answer == VisitActionResult.Success)
                {
                    Update(next with { State = SyncState.Accepted, Answer = answer, ReviewRequired = review });
                    continue;
                }
                Reject(next, answer);
            }
        }
        catch (OperationCanceledException)
        {
            // Not sent: back to the phone-only state.
            lock (_gate)
            {
                _items = _items.Select(i => i.State == SyncState.Sent ? i with { State = SyncState.Local } : i).ToList();
                Save();
            }
            throw;
        }
        finally
        {
            _syncing.Release();
        }
    }

    private async Task<(VisitActionResult Answer, bool Review)> SendAsync(OutboxItem item, CancellationToken cancellationToken)
    {
        switch (item.Kind)
        {
            case OutboxKind.EnRoute:
            case OutboxKind.Arrive:
                var result = item.Kind == OutboxKind.EnRoute
                    ? await _api.MarkEnRouteAsync(item.VisitId, cancellationToken)
                    : await _api.MarkArrivedAsync(item.VisitId, cancellationToken);
                // A reply lost earlier: the visit is already at or past this step.
                if (result == VisitActionResult.InvalidState && await AlreadyPastAsync(item, cancellationToken)) return (VisitActionResult.Success, false);
                return (result, false);
            case OutboxKind.Report:
                return (await _api.SaveReportAsync(item.VisitId, item.Report!, cancellationToken), false);
            default:
                return await _api.SendOfflineCheckpointAsync(item.VisitId, item.Kind == OutboxKind.CheckOut, item.Fix!,
                    ElapsedSinceCapture(item), item.IdempotencyKey, cancellationToken);
        }
    }

    // Null when the phone restarted since the capture (the reading no longer means anything); the server then asks
    // a manager to review the step.
    private long? ElapsedSinceCapture(OutboxItem item) =>
        item.CapturedMonotonicMs is { } captured && item.BootId is not null && item.BootId == _monotonic.BootId && _monotonic.ElapsedMs >= captured
            ? _monotonic.ElapsedMs - captured
            : null;

    private async Task<bool> AlreadyPastAsync(OutboxItem item, CancellationToken cancellationToken)
    {
        var plan = await _api.GetDailyPlanAsync(item.ScheduleDate, cancellationToken);
        var visit = plan.Status == PlanLoadStatus.Success ? plan.Visits.FirstOrDefault(v => v.Id == item.VisitId) : null;
        var target = item.Kind == OutboxKind.EnRoute ? 1 : 2;
        return visit is not null && Rank(visit.Status) >= target;
    }

    private static int Rank(string status) => status switch
    {
        "Planned" => 0,
        "EnRoute" => 1,
        "Arrived" => 2,
        "InVisit" => 3,
        "Completed" => 4,
        _ => -1 // Failed, Cancelled: the step did not happen
    };

    private void Reject(OutboxItem item, VisitActionResult answer)
    {
        lock (_gate)
        {
            var index = _items.FindIndex(i => i.Id == item.Id);
            _items[index] = item with { State = SyncState.Rejected, Answer = answer };
            // Later steps of the same visit depend on this one.
            for (var i = index + 1; i < _items.Count; i++)
                if (_items[i].VisitId == item.VisitId && _items[i].State is SyncState.Local or SyncState.Sent)
                    _items[i] = _items[i] with { State = SyncState.Rejected, Answer = VisitActionResult.InvalidState };
            Save();
        }
    }

    private void Update(OutboxItem item)
    {
        lock (_gate)
        {
            var index = _items.FindIndex(i => i.Id == item.Id);
            if (index >= 0) _items[index] = item;
            Save();
        }
    }

    private void Prune()
    {
        var cutoff = _clock.GetUtcNow() - KeepFinishedFor;
        lock (_gate)
        {
            if (_items.RemoveAll(i => i.State is SyncState.Accepted or SyncState.Rejected && i.CreatedAt < cutoff) > 0) Save();
        }
    }

    private void Save() => _store?.Write(Key, JsonSerializer.Serialize(_items, Json));

    private void Load()
    {
        var text = _store?.Read(Key);
        if (text is null) return;
        try
        {
            // A step that was being sent when the app closed is sent again; its key or the visit's state prevents a repeat.
            _items = (JsonSerializer.Deserialize<List<OutboxItem>>(text, Json) ?? new())
                .Select(i => i.State == SyncState.Sent ? i with { State = SyncState.Local } : i).ToList();
        }
        catch (JsonException)
        {
            _items = new();
        }
    }
}
