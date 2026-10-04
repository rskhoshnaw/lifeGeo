using System.Text.Json;
using LifeDrop.Mobile.Offline;

namespace LifeDrop.Mobile.Tracking;

// Totals for the points of the current shift as the user sees them (plan step 1.14).
public sealed record PointCounts(int Local, int Sent, int Accepted, int Rejected, int Dropped);

// Queue of tracking points waiting for upload (plan steps 1.13, 1.14). With a durable store it is saved after every
// change, so closing the app loses nothing. A point leaves the queue only after the server answered for it; points
// that were being sent when the app closed are sent again with the same ids and the server ignores the repeats.
public sealed class PointQueue
{
    public const int MaxPoints = 5000;
    private const string Key = "tracking-queue";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IDurableStore? _store;
    private readonly object _gate = new();
    private readonly List<TrackedPoint> _points = new();
    private readonly HashSet<Guid> _sending = new();
    private Guid? _shiftId;
    private int _accepted, _rejected, _dropped;

    public PointQueue(IDurableStore? store = null)
    {
        _store = store;
        Load();
    }

    public Guid? ShiftId
    {
        get { lock (_gate) return _shiftId; }
    }

    public int Count
    {
        get { lock (_gate) return _points.Count; }
    }

    public PointCounts Counts
    {
        get
        {
            lock (_gate) return new PointCounts(_points.Count - _sending.Count, _sending.Count, _accepted, _rejected, _dropped);
        }
    }

    // A different shift starts: points of the earlier one that could not be uploaded are dropped and counted.
    public void SetShift(Guid shiftId)
    {
        lock (_gate)
        {
            if (_shiftId == shiftId) return;
            if (_shiftId is not null) _dropped += _points.Count;
            _points.Clear();
            _sending.Clear();
            _shiftId = shiftId;
            _accepted = _rejected = 0;
            Save();
        }
    }

    public void Add(TrackedPoint point)
    {
        lock (_gate)
        {
            _points.Add(point);
            while (_points.Count > MaxPoints)
            {
                _sending.Remove(_points[0].ClientPointId);
                _points.RemoveAt(0);
                _dropped++;
            }
            Save();
        }
    }

    // The oldest points, marked as sent until AnswerReceived or SendFailed.
    public IReadOnlyList<TrackedPoint> TakeBatch(int max)
    {
        lock (_gate)
        {
            var batch = _points.Take(max).ToList();
            foreach (var point in batch) _sending.Add(point.ClientPointId);
            return batch;
        }
    }

    // The server answered for the batch: the points leave the queue and the totals are updated.
    public void AnswerReceived(IReadOnlyList<TrackedPoint> batch, int accepted, int rejected)
    {
        lock (_gate)
        {
            var ids = batch.Select(p => p.ClientPointId).ToHashSet();
            _points.RemoveAll(p => ids.Contains(p.ClientPointId));
            _sending.ExceptWith(ids);
            _accepted += accepted;
            _rejected += rejected;
            Save();
        }
    }

    // No answer: the points stay local and are sent again later with the same ids.
    public void SendFailed(IReadOnlyList<TrackedPoint> batch)
    {
        lock (_gate) _sending.ExceptWith(batch.Select(p => p.ClientPointId));
    }

    private void Save()
    {
        _store?.Write(Key, JsonSerializer.Serialize(new Snapshot(_shiftId, _points, _accepted, _rejected, _dropped), Json));
    }

    private void Load()
    {
        var text = _store?.Read(Key);
        if (text is null) return;
        try
        {
            var snapshot = JsonSerializer.Deserialize<Snapshot>(text, Json);
            if (snapshot is null) return;
            _shiftId = snapshot.ShiftId;
            _points.AddRange(snapshot.Points ?? []);
            (_accepted, _rejected, _dropped) = (snapshot.Accepted, snapshot.Rejected, snapshot.Dropped);
        }
        catch (JsonException)
        {
            // A damaged file is ignored rather than stopping the app.
        }
    }

    private sealed record Snapshot(Guid? ShiftId, List<TrackedPoint>? Points, int Accepted, int Rejected, int Dropped);
}
