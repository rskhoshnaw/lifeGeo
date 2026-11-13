using LifeDrop.Mobile.Offline;
using LifeDrop.Mobile.Tracking;
using Xunit;

namespace LifeDrop.Mobile.Visits.Tests;

// Plan step 1.14: closing and reopening the app loses nothing and sends nothing twice.
public sealed class PersistenceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lifedrop-test-" + Guid.NewGuid().ToString("N"));
    private static readonly Guid ShiftId = Guid.NewGuid();

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private static TrackedPoint Point(int i) =>
        new(Guid.NewGuid(), 36.19, 44.01, 10, new DateTimeOffset(2026, 10, 5, 5, 0, 0, TimeSpan.Zero).AddSeconds(30 * i), false);

    [Fact]
    public void FileStoreWritesReadsAndDeletes()
    {
        var store = new FileDurableStore(_directory);
        Assert.Null(store.Read("a"));
        store.Write("a", "one");
        store.Write("a", "two");
        Assert.Equal("two", new FileDurableStore(_directory).Read("a"));
        store.Delete("a");
        Assert.Null(store.Read("a"));
        Assert.Throws<ArgumentException>(() => store.Write("../escape", "x"));
    }

    [Fact]
    public void QueuedPointsSurviveRestart()
    {
        var queue = new PointQueue(new FileDurableStore(_directory));
        queue.SetShift(ShiftId);
        var points = Enumerable.Range(0, 3).Select(Point).ToList();
        points.ForEach(queue.Add);

        var reopened = new PointQueue(new FileDurableStore(_directory));
        Assert.Equal(ShiftId, reopened.ShiftId);
        Assert.Equal(points.Select(p => p.ClientPointId), reopened.TakeBatch(10).Select(p => p.ClientPointId));
    }

    [Fact]
    public void PointsBeingSentWhenTheAppClosedAreSentAgainWithTheSameIds()
    {
        var queue = new PointQueue(new FileDurableStore(_directory));
        queue.SetShift(ShiftId);
        queue.Add(Point(0));
        queue.Add(Point(1));
        var inFlight = queue.TakeBatch(10);
        Assert.Equal(new PointCounts(0, 2, 0, 0, 0), queue.Counts);
        // the app is killed here, before the answer

        var reopened = new PointQueue(new FileDurableStore(_directory));
        Assert.Equal(new PointCounts(2, 0, 0, 0, 0), reopened.Counts);
        Assert.Equal(inFlight.Select(p => p.ClientPointId), reopened.TakeBatch(10).Select(p => p.ClientPointId));
    }

    [Fact]
    public void FourStatesAreCountedSeparately()
    {
        var queue = new PointQueue(new FileDurableStore(_directory));
        queue.SetShift(ShiftId);
        for (var i = 0; i < 5; i++) queue.Add(Point(i));
        var batch = queue.TakeBatch(3);
        Assert.Equal(new PointCounts(2, 3, 0, 0, 0), queue.Counts);
        queue.AnswerReceived(batch, accepted: 2, rejected: 1);
        Assert.Equal(new PointCounts(2, 0, 2, 1, 0), queue.Counts);
        Assert.Equal(new PointCounts(2, 0, 2, 1, 0), new PointQueue(new FileDurableStore(_directory)).Counts);
    }

    [Fact]
    public void DamagedFileIsIgnored()
    {
        new FileDurableStore(_directory).Write("tracking-queue", "{not json");
        Assert.Equal(0, new PointQueue(new FileDurableStore(_directory)).Count);
    }

    [Fact]
    public async Task TrackerAfterRestartUploadsPointsLeftFromTheShift()
    {
        var store = new FileDurableStore(_directory);
        var queue = new PointQueue(store);
        queue.SetShift(ShiftId);
        queue.Add(Point(0));
        queue.Add(Point(1));

        var api = new CountingShiftApi();
        var tracker = new ShiftTracker(api, TimeProvider.System, new PointQueue(store)); // a new process
        await tracker.FlushAsync();
        Assert.Equal((ShiftId, 2), (api.ShiftId, api.Sent));
        Assert.Equal(0, tracker.QueuedPoints);
    }

    // A phone call on mobile data: the system killed the app, the service restarted in a new process and the server cannot be reached for now. The shift this phone knows is open
    // goes on (the points wait in the queue); only an answer from the server that there is no open shift ends it.
    [Fact]
    public async Task AShiftOpenOnThePhoneContinuesAfterARestartWhenTheServerCannotBeReached()
    {
        var store = new FileDurableStore(_directory);
        var first = new ShiftTracker(new CurrentShiftApi(ShiftCallStatus.Success, new ShiftInfo(ShiftId, Guid.NewGuid(), DateTimeOffset.UtcNow, null)), TimeProvider.System, new PointQueue(store));
        Assert.Equal(ShiftCallStatus.Success, await first.StartAsync());
        Assert.True(first.AddFix(36.19, 44.01, 10, DateTimeOffset.UtcNow, false));

        // A new process, no data: the answer is a network error, not an exception.
        var offline = new ShiftTracker(new CurrentShiftApi(ShiftCallStatus.NetworkError, null), TimeProvider.System, new PointQueue(store));
        Assert.Equal(ShiftState.Off, offline.State);
        Assert.Equal(ShiftCallStatus.NetworkError, await offline.ResumeAsync());
        Assert.Equal(ShiftState.On, offline.State);
        Assert.True(offline.AddFix(36.19, 44.02, 10, DateTimeOffset.UtcNow, false));   // tracking goes on
        Assert.Equal(2, offline.QueuedPoints);

        // The server then says there is no open shift: tracking ends and the next process does not resume it.
        var closed = new ShiftTracker(new CurrentShiftApi(ShiftCallStatus.NotFound, null), TimeProvider.System, new PointQueue(store));
        await closed.ResumeAsync();
        Assert.Equal(ShiftState.Off, closed.State);
        var later = new ShiftTracker(new CurrentShiftApi(ShiftCallStatus.NetworkError, null), TimeProvider.System, new PointQueue(store));
        await later.ResumeAsync();
        Assert.Equal(ShiftState.Off, later.State);
    }

    // The same when the caller's time runs out (iPhone: the app is started in the background and gets a few seconds; Home page: a slow network): that is an exception, not a status.
    [Fact]
    public async Task AShiftOpenOnThePhoneContinuesWhenTheServerDoesNotAnswerInTime()
    {
        var store = new FileDurableStore(_directory);
        var first = new ShiftTracker(new CurrentShiftApi(ShiftCallStatus.Success, new ShiftInfo(ShiftId, Guid.NewGuid(), DateTimeOffset.UtcNow, null)), TimeProvider.System, new PointQueue(store));
        await first.StartAsync();
        var slow = new ShiftTracker(new CurrentShiftApi(ShiftCallStatus.Success, null, timesOut: true), TimeProvider.System, new PointQueue(store));
        Assert.Equal(ShiftCallStatus.NetworkError, await slow.ResumeAsync());
        Assert.Equal(ShiftState.On, slow.State);
    }

    [Fact]
    public async Task AnEndedShiftOrAnOldFileIsNotResumedWithoutTheServer()
    {
        var store = new FileDurableStore(_directory);
        var tracker = new ShiftTracker(new CurrentShiftApi(ShiftCallStatus.Success, new ShiftInfo(ShiftId, Guid.NewGuid(), DateTimeOffset.UtcNow, null)), TimeProvider.System, new PointQueue(store));
        await tracker.StartAsync();
        Assert.Equal(ShiftCallStatus.Success, await tracker.EndAsync());
        var restarted = new ShiftTracker(new CurrentShiftApi(ShiftCallStatus.NetworkError, null), TimeProvider.System, new PointQueue(store));
        await restarted.ResumeAsync();
        Assert.Equal(ShiftState.Off, restarted.State);   // the shift was ended on this phone

        // A queue file written before this field existed does not say the shift is open.
        new FileDurableStore(_directory).Write("tracking-queue", $"{{\"shiftId\":\"{ShiftId}\",\"points\":[],\"accepted\":0,\"rejected\":0,\"dropped\":0}}");
        var old = new ShiftTracker(new CurrentShiftApi(ShiftCallStatus.NetworkError, null), TimeProvider.System, new PointQueue(new FileDurableStore(_directory)));
        await old.ResumeAsync();
        Assert.Equal(ShiftState.Off, old.State);
    }

    private sealed class CurrentShiftApi(ShiftCallStatus current, ShiftInfo? shift, bool timesOut = false) : IShiftApiClient
    {
        public Task<(ShiftCallStatus Status, ShiftInfo? Shift)> StartShiftAsync(CancellationToken cancellationToken = default) => Task.FromResult((current, shift));
        public Task<(ShiftCallStatus Status, ShiftInfo? Shift)> GetCurrentShiftAsync(CancellationToken cancellationToken = default) =>
            timesOut ? throw new OperationCanceledException(cancellationToken) : Task.FromResult((current, shift));
        public Task<ShiftCallStatus> EndShiftAsync(Guid shiftId, CancellationToken cancellationToken = default) => Task.FromResult(ShiftCallStatus.Success);
        public Task<(ShiftCallStatus Status, UploadResult? Result)> UploadPointsAsync(Guid shiftId, IReadOnlyList<TrackedPoint> points, ShiftHealth? health = null, CancellationToken cancellationToken = default) =>
            Task.FromResult((ShiftCallStatus.Success, (UploadResult?)new UploadResult(points.Count, 0, 0)));
    }

    private sealed class CountingShiftApi : IShiftApiClient
    {
        public Guid? ShiftId { get; private set; }
        public int Sent { get; private set; }
        public Task<(ShiftCallStatus Status, ShiftInfo? Shift)> StartShiftAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<(ShiftCallStatus Status, ShiftInfo? Shift)> GetCurrentShiftAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ShiftCallStatus> EndShiftAsync(Guid shiftId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<(ShiftCallStatus Status, UploadResult? Result)> UploadPointsAsync(Guid shiftId, IReadOnlyList<TrackedPoint> points, ShiftHealth? health = null, CancellationToken cancellationToken = default)
        {
            ShiftId = shiftId;
            Sent += points.Count;
            return Task.FromResult((ShiftCallStatus.Success, (UploadResult?)new UploadResult(points.Count, 0, 0)));
        }
    }
}
