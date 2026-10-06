using System.Text.Json;
using LifeDrop.Mobile.Visits;

namespace LifeDrop.Mobile.Offline;

// Work kept on the phone while there is no internet (plan step 2.13, PRD §7): orders, payments, samples, follow-ups and the visit form. Visit steps (check-in, report, ...) have their
// own queue (Outbox, step 1.14). Everything here is sent with a reference of its own (ClientRef), so a lost answer never makes two orders, payments, samples or follow-ups.
public enum WorkKind { Order, Payment, Sample, FollowUpCreate, FollowUpStart, FollowUpComplete, FollowUpCancel, VisitForm }

public sealed record WorkItem(Guid Id, WorkKind Kind, string Title, Guid? VisitId, string PayloadJson, DateTimeOffset CreatedAt, SyncState State = SyncState.Local,
    VisitActionResult? Answer = null, DateTimeOffset? FinishedAt = null, int Attempts = 0);

// What each kind of work needs to be sent later (the fields of the form as the user left them, never a price: the server prices an order when it arrives).
public sealed record OrderPayload(Guid CustomerId, Guid WarehouseId, Guid? VisitId, Guid ClientRef, string? Note, IReadOnlyList<OrderLineInput> Lines);
public sealed record PaymentPayload(Guid CustomerId, Guid? OrderId, Guid ClientRef, string Method, string Currency, string? AmountText, string? Reference, string? Note);
public sealed record SamplePayload(Guid CustomerId, Guid? VisitId, Guid ClientRef, Guid? ProductId, string? QuantityText, string? Recipient, string? Batch, string? ExpiryText, string? Note, PhoneLocation? Location);
public sealed record FollowUpPayload(Guid CustomerId, Guid ClientRef, string? Subject, DateOnly? DueDate);
public sealed record FollowUpStepPayload(Guid FollowUpId, string? Text);
public sealed record VisitFormPayload(Guid VisitId, string VisitType, string? Purpose, string? PersonMet, string? PersonRole, string? Needs, IReadOnlyList<Guid> ProductIds, string? Specialty,
    string? DailyPatientsText, string? MonthlySalesText, string? Competitors, string? MonthlyVolumeText, string? PaymentTerms, string Currency);

public interface IWorkExecutor
{
    Task<VisitActionResult> ExecuteAsync(WorkItem item, CancellationToken cancellationToken);
}

public sealed record WorkCounts(int Waiting, int NeedsAttention, int Done);

// The phone's list of work waiting for the internet. Saved after every change, sent in the order it was made. A transient failure (no connection, server error, ended session) stops
// sending and keeps everything on the phone; a refusal marks only that item "needs attention" (an order does not depend on a payment), and the user may send it again or drop it.
// An item that belongs to a visit (the visit form) waits while an earlier step of that visit still waits in the visit queue.
public sealed class WorkQueue
{
    private const string Key = "work-queue";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static readonly TimeSpan KeepFinishedFor = TimeSpan.FromHours(48);

    private readonly TimeProvider _clock;
    private readonly IDurableStore? _store;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _syncing = new(1, 1);
    private List<WorkItem> _items = new();

    public WorkQueue(TimeProvider clock, IDurableStore? store = null)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _store = store;
        Load();
    }

    public bool SessionExpired { get; private set; }

    public IReadOnlyList<WorkItem> Items
    {
        get { lock (_gate) return _items.ToList(); }
    }

    public WorkCounts Counts
    {
        get
        {
            lock (_gate)
                return new WorkCounts(_items.Count(i => i.State is SyncState.Local or SyncState.Sent), _items.Count(i => i.State == SyncState.Rejected), _items.Count(i => i.State == SyncState.Accepted));
        }
    }

    public static string Serialize(object payload) => JsonSerializer.Serialize(payload, Json);
    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Json);

    public WorkItem Enqueue(WorkKind kind, string title, object payload, Guid? visitId = null)
    {
        var item = new WorkItem(Guid.NewGuid(), kind, title, visitId, Serialize(payload), _clock.GetUtcNow());
        lock (_gate)
        {
            _items.Add(item);
            Save();
        }
        return item;
    }

    // The user sends a refused item again (for example after a manager approved the customer).
    public bool Retry(Guid id)
    {
        lock (_gate)
        {
            var index = _items.FindIndex(i => i.Id == id && i.State == SyncState.Rejected);
            if (index < 0) return false;
            _items[index] = _items[index] with { State = SyncState.Local, Answer = null, FinishedAt = null };
            Save();
            return true;
        }
    }

    // The user gives up an item (it is never sent).
    public bool Drop(Guid id)
    {
        lock (_gate)
        {
            var removed = _items.RemoveAll(i => i.Id == id && i.State is SyncState.Rejected or SyncState.Local) > 0;
            if (removed) Save();
            return removed;
        }
    }

    // Sends waiting items in order. hasPendingVisitSteps: true while a visit still has steps waiting in the visit queue. Returns false when something transient stopped it.
    public async Task<bool> SyncAsync(IWorkExecutor executor, Func<Guid, bool>? hasPendingVisitSteps = null, CancellationToken cancellationToken = default)
    {
        await _syncing.WaitAsync(cancellationToken);
        try
        {
            Prune();
            foreach (var id in Items.Where(i => i.State is SyncState.Local or SyncState.Sent).Select(i => i.Id))
            {
                WorkItem? next;
                lock (_gate) next = _items.FirstOrDefault(i => i.Id == id && i.State is SyncState.Local or SyncState.Sent);
                if (next is null) continue;
                if (next.VisitId is { } visit && hasPendingVisitSteps?.Invoke(visit) == true) continue;   // its visit steps go first

                Update(next with { State = SyncState.Sent, Attempts = next.Attempts + 1 });
                VisitActionResult answer;
                try
                {
                    answer = await executor.ExecuteAsync(next, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    Update(next with { State = SyncState.Local });
                    throw;
                }
                if (answer is VisitActionResult.NetworkError or VisitActionResult.ServerError or VisitActionResult.SessionExpired)
                {
                    if (answer == VisitActionResult.SessionExpired) SessionExpired = true;
                    Update(next with { State = SyncState.Local, Answer = answer });
                    return false;
                }
                Update(next with { State = answer == VisitActionResult.Success ? SyncState.Accepted : SyncState.Rejected, Answer = answer, FinishedAt = _clock.GetUtcNow() });
            }
            return true;
        }
        finally
        {
            _syncing.Release();
        }
    }

    private void Update(WorkItem item)
    {
        lock (_gate)
        {
            var index = _items.FindIndex(i => i.Id == item.Id);
            if (index >= 0) _items[index] = item;
            Save();
        }
    }

    // Accepted items are kept for two days so the user can see them; refused ones stay until the user decides.
    private void Prune()
    {
        var cutoff = _clock.GetUtcNow() - KeepFinishedFor;
        lock (_gate)
        {
            if (_items.RemoveAll(i => i.State == SyncState.Accepted && i.FinishedAt < cutoff) > 0) Save();
        }
    }

    private void Save() => _store?.Write(Key, JsonSerializer.Serialize(_items, Json));

    private void Load()
    {
        var text = _store?.Read(Key);
        if (text is null) return;
        try
        {
            // An item that was being sent when the app closed is sent again; its ClientRef (or the follow-up's status) makes that safe.
            _items = (JsonSerializer.Deserialize<List<WorkItem>>(text, Json) ?? new()).Select(i => i.State == SyncState.Sent ? i with { State = SyncState.Local } : i).ToList();
        }
        catch (JsonException)
        {
            _items = new();
        }
    }
}

// Sends one kept item through the same clients the screens use; the answer is the server's. Items kept without internet carry the "offline" mark so the server may leave out a
// cancelled order or visit instead of refusing money, samples or orders that are still real.
public sealed class WorkExecutor(IOrdersApiClient orders, IPaymentsApiClient payments, ISamplesApiClient samples, IFollowUpsApiClient followUps, IVisitDetailsApiClient visitDetails) : IWorkExecutor
{
    public async Task<VisitActionResult> ExecuteAsync(WorkItem item, CancellationToken cancellationToken)
    {
        try
        {
            switch (item.Kind)
            {
                case WorkKind.Order:
                {
                    var p = WorkQueue.Deserialize<OrderPayload>(item.PayloadJson)!;
                    return (await orders.PlaceAsync(p.CustomerId, p.WarehouseId, p.VisitId, p.ClientRef, p.Note, p.Lines, cancellationToken, offline: true)).Result;
                }
                case WorkKind.Payment:
                {
                    var p = WorkQueue.Deserialize<PaymentPayload>(item.PayloadJson)!;
                    var draft = new PaymentDraft(p.CustomerId, p.OrderId, p.ClientRef) { Method = p.Method, Currency = p.Currency, AmountText = p.AmountText, Reference = p.Reference, Note = p.Note };
                    return (await payments.RecordAsync(draft, cancellationToken, offline: true)).Result;
                }
                case WorkKind.Sample:
                {
                    var p = WorkQueue.Deserialize<SamplePayload>(item.PayloadJson)!;
                    var draft = new SampleDraft(p.CustomerId, p.VisitId, p.ClientRef) { ProductId = p.ProductId, QuantityText = p.QuantityText, Recipient = p.Recipient, Batch = p.Batch, ExpiryText = p.ExpiryText, Note = p.Note };
                    return (await samples.RecordAsync(draft, p.Location, cancellationToken, offline: true)).Result;
                }
                case WorkKind.FollowUpCreate:
                {
                    var p = WorkQueue.Deserialize<FollowUpPayload>(item.PayloadJson)!;
                    return await followUps.CreateAsync(new FollowUpDraft(p.CustomerId, p.ClientRef) { Subject = p.Subject, DueDate = p.DueDate }, cancellationToken);
                }
                case WorkKind.FollowUpStart:
                    return await followUps.StartAsync(WorkQueue.Deserialize<FollowUpStepPayload>(item.PayloadJson)!.FollowUpId, cancellationToken);
                case WorkKind.FollowUpComplete:
                {
                    var p = WorkQueue.Deserialize<FollowUpStepPayload>(item.PayloadJson)!;
                    return await followUps.CompleteAsync(p.FollowUpId, p.Text ?? "", cancellationToken);
                }
                case WorkKind.FollowUpCancel:
                {
                    var p = WorkQueue.Deserialize<FollowUpStepPayload>(item.PayloadJson)!;
                    return await followUps.CancelAsync(p.FollowUpId, p.Text ?? "", cancellationToken);
                }
                case WorkKind.VisitForm:
                {
                    var p = WorkQueue.Deserialize<VisitFormPayload>(item.PayloadJson)!;
                    var draft = new VisitDetailsDraft(p.VisitType) { Purpose = p.Purpose, PersonMet = p.PersonMet, PersonRole = p.PersonRole, Needs = p.Needs, Specialty = p.Specialty,
                        DailyPatientsText = p.DailyPatientsText, MonthlySalesText = p.MonthlySalesText, Competitors = p.Competitors, MonthlyVolumeText = p.MonthlyVolumeText, PaymentTerms = p.PaymentTerms, Currency = p.Currency };
                    foreach (var product in p.ProductIds) draft.ProductIds.Add(product);
                    return await visitDetails.SaveAsync(p.VisitId, draft, cancellationToken);
                }
                default:
                    return VisitActionResult.InvalidData;
            }
        }
        catch (JsonException)
        {
            return VisitActionResult.InvalidData;   // a damaged item can never be sent: the user sees it and drops it
        }
    }
}

// Lists the phone keeps for the workday so that an order can be written without internet: the catalogue (without prices) and the warehouses (plan step 2.13, PRD §7).
public sealed class ReferenceCache(IDurableStore? store)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public void SaveProducts(IReadOnlyList<PricedProduct> products) => Write("cache-products", products.Select(p => p with { ListPrice = 0, Price = 0 }).ToList());
    public IReadOnlyList<PricedProduct> Products => Read<PricedProduct>("cache-products");
    public void SaveWarehouses(IReadOnlyList<OrderWarehouse> warehouses) => Write("cache-warehouses", warehouses);
    public IReadOnlyList<OrderWarehouse> Warehouses => Read<OrderWarehouse>("cache-warehouses");

    private void Write<T>(string key, IReadOnlyList<T> items) => store?.Write(key, JsonSerializer.Serialize(items, Json));

    private IReadOnlyList<T> Read<T>(string key)
    {
        try
        {
            var text = store?.Read(key);
            return text is null ? Array.Empty<T>() : JsonSerializer.Deserialize<List<T>>(text, Json) ?? new();
        }
        catch (JsonException)
        {
            return Array.Empty<T>();
        }
    }
}
