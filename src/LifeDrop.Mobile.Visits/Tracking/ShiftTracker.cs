namespace LifeDrop.Mobile.Tracking;

public enum ShiftState { Off, On }

// Shift and tracking on the phone (plan step 1.13, PRD §6.2, decision D1). Points are collected only while a shift
// is on. They wait in a queue and are uploaded in batches; a batch that fails stays queued and is retried with the
// same ids, so nothing is lost or stored twice. Points left after the shift ends are still uploaded (the server
// accepts points captured before the end). With a durable PointQueue the points also survive closing the app
// (plan step 1.14).
public sealed class ShiftTracker
{
    public const int BatchSize = 200;
    public const int MaxQueuedPoints = PointQueue.MaxPoints;

    private readonly IShiftApiClient _api;
    private readonly TimeProvider _clock;
    private readonly PointQueue _queue;
    private readonly SemaphoreSlim _uploading = new(1, 1);
    private readonly SemaphoreSlim _changing = new(1, 1);

    public ShiftTracker(IShiftApiClient api, TimeProvider clock, PointQueue? queue = null)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _queue = queue ?? new PointQueue();
    }

    public ShiftState State { get; private set; } = ShiftState.Off;
    public ShiftInfo? Shift { get; private set; }
    public DateTimeOffset? LastFixAt { get; private set; }
    public DateTimeOffset? LastUploadAt { get; private set; }
    public ShiftCallStatus? LastError { get; private set; }
    public bool SessionExpired { get; private set; }

    // Points by state: on the phone, sent and waiting for an answer, accepted, rejected, dropped (queue full).
    public PointCounts Counts => _queue.Counts;
    public int QueuedPoints => _queue.Count;
    public int SentPoints => _queue.Counts.Accepted;
    public int DroppedPoints => _queue.Counts.Dropped;

    // Starts a shift, or takes over the one already open on the server.
    public async Task<ShiftCallStatus> StartAsync(CancellationToken cancellationToken = default)
    {
        await _changing.WaitAsync(cancellationToken);
        try
        {
            if (State == ShiftState.On) return ShiftCallStatus.Success;
            var (status, shift) = await _api.StartShiftAsync(cancellationToken);
            Remember(status);
            if (status == ShiftCallStatus.Success) TurnOn(shift!);
            return status;
        }
        finally
        {
            _changing.Release();
        }
    }

    // After the app restarts: continue a shift that is still open on the server.
    public async Task<ShiftCallStatus> ResumeAsync(CancellationToken cancellationToken = default)
    {
        await _changing.WaitAsync(cancellationToken);
        try
        {
            var (status, shift) = await _api.GetCurrentShiftAsync(cancellationToken);
            if (status == ShiftCallStatus.Success) TurnOn(shift!);
            else if (status == ShiftCallStatus.NotFound) TurnOff();
            else Remember(status);
            return status;
        }
        finally
        {
            _changing.Release();
        }
    }

    // A new position from the device. Ignored when no shift is on (tracking only inside a shift).
    public bool AddFix(double latitude, double longitude, double accuracyMeters, DateTimeOffset capturedAt, bool isMocked)
    {
        if (State != ShiftState.On) return false;
        _queue.Add(new TrackedPoint(Guid.NewGuid(), latitude, longitude, accuracyMeters, capturedAt, isMocked));
        LastFixAt = capturedAt;
        return true;
    }

    // Uploads queued points in batches until the queue is empty or a call fails. Concurrent calls wait for the
    // running one instead of sending the same points twice. Works for points left from an ended shift too.
    public async Task<ShiftCallStatus> FlushAsync(CancellationToken cancellationToken = default)
    {
        await _uploading.WaitAsync(cancellationToken);
        try
        {
            while (_queue.ShiftId is { } shiftId && _queue.Count > 0)
            {
                var batch = _queue.TakeBatch(BatchSize);
                (ShiftCallStatus Status, UploadResult? Result) answer;
                try
                {
                    answer = await _api.UploadPointsAsync(shiftId, batch, cancellationToken);
                }
                catch
                {
                    _queue.SendFailed(batch);
                    throw;
                }
                Remember(answer.Status);
                switch (answer.Status)
                {
                    case ShiftCallStatus.Success:
                        // Accepted, duplicates (already stored) and rejected (never acceptable) all leave the queue.
                        _queue.AnswerReceived(batch, answer.Result!.Accepted, answer.Result.Rejected);
                        LastUploadAt = _clock.GetUtcNow();
                        break;
                    case ShiftCallStatus.NotFound:
                    case ShiftCallStatus.DeviceMismatch:
                    case ShiftCallStatus.InvalidData:
                        // The shift is gone or belongs to another device: these points can never be stored.
                        _queue.AnswerReceived(batch, 0, batch.Count);
                        break;
                    default:
                        _queue.SendFailed(batch); // network, server or session: keep the points and retry later
                        return answer.Status;
                }
            }
            return ShiftCallStatus.Success;
        }
        finally
        {
            _uploading.Release();
        }
    }

    // Uploads what it can, then ends the shift. Tracking stops at once; points still queued are uploaded later.
    public async Task<ShiftCallStatus> EndAsync(CancellationToken cancellationToken = default)
    {
        await _changing.WaitAsync(cancellationToken);
        try
        {
            if (State == ShiftState.Off || Shift is null) return ShiftCallStatus.Success;
            var shiftId = Shift.Id;
            State = ShiftState.Off; // no more points from this moment
            await FlushAsync(cancellationToken);
            var status = await _api.EndShiftAsync(shiftId, cancellationToken);
            Remember(status);
            if (status is ShiftCallStatus.Success or ShiftCallStatus.NotFound)
            {
                Shift = null;
                return ShiftCallStatus.Success;
            }
            State = ShiftState.On; // not ended on the server: keep tracking until it is
            return status;
        }
        finally
        {
            _changing.Release();
        }
    }

    private void TurnOn(ShiftInfo shift)
    {
        _queue.SetShift(shift.Id);
        Shift = shift;
        State = ShiftState.On;
    }

    private void TurnOff()
    {
        Shift = null;
        State = ShiftState.Off;
    }

    private void Remember(ShiftCallStatus status)
    {
        LastError = status == ShiftCallStatus.Success ? null : status;
        if (status == ShiftCallStatus.SessionExpired) SessionExpired = true;
    }
}
