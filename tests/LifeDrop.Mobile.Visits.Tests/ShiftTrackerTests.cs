using LifeDrop.Mobile.Tracking;
using Xunit;

namespace LifeDrop.Mobile.Visits.Tests;

// Plan step 1.13 (decision D1): points only inside a shift; batches retried with the same ids; nothing lost or
// sent twice; points left after the end are still uploaded.
public class ShiftTrackerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 5, 5, 0, 0, TimeSpan.Zero);
    private static readonly ShiftInfo ShiftA = new(Guid.NewGuid(), Guid.NewGuid(), T0, null);

    private static ShiftTracker Create(FakeShiftApi api) => new(api, TimeProvider.System);

    private static void AddFixes(ShiftTracker tracker, int count)
    {
        for (var i = 0; i < count; i++) tracker.AddFix(36.19, 44.01, 10, T0.AddSeconds(30 * i), false);
    }

    [Fact]
    public async Task NoPointsOutsideAShift()
    {
        var tracker = Create(new FakeShiftApi());
        Assert.False(tracker.AddFix(36.19, 44.01, 10, T0, false));
        Assert.Equal(0, tracker.QueuedPoints);

        await tracker.StartAsync();
        Assert.Equal(ShiftState.On, tracker.State);
        Assert.True(tracker.AddFix(36.19, 44.01, 10, T0, false));
        Assert.Equal(1, tracker.QueuedPoints);
    }

    [Fact]
    public async Task UploadsInBatchesOf200()
    {
        var api = new FakeShiftApi();
        var tracker = Create(api);
        await tracker.StartAsync();
        AddFixes(tracker, 450);

        Assert.Equal(ShiftCallStatus.Success, await tracker.FlushAsync());
        Assert.Equal(new[] { 200, 200, 50 }, api.Uploads.Select(u => u.Count));
        Assert.Equal(0, tracker.QueuedPoints);
        Assert.Equal(450, tracker.SentPoints);
        Assert.All(api.Uploads, u => Assert.All(u, p => Assert.Equal(36.19, p.Latitude)));
    }

    [Fact]
    public async Task FailedBatchIsKeptAndRetriedWithTheSameIds()
    {
        var api = new FakeShiftApi();
        var tracker = Create(api);
        await tracker.StartAsync();
        AddFixes(tracker, 3);
        api.UploadResults.Enqueue(ShiftCallStatus.NetworkError);

        Assert.Equal(ShiftCallStatus.NetworkError, await tracker.FlushAsync());
        Assert.Equal(3, tracker.QueuedPoints);
        Assert.Equal(ShiftCallStatus.NetworkError, tracker.LastError);

        Assert.Equal(ShiftCallStatus.Success, await tracker.FlushAsync());
        Assert.Equal(0, tracker.QueuedPoints);
        Assert.Equal(api.Uploads[0].Select(p => p.ClientPointId), api.Uploads[1].Select(p => p.ClientPointId));
        Assert.Null(tracker.LastError);
    }

    [Fact]
    public async Task ConcurrentFlushesDoNotSendTheSamePointsTwice()
    {
        var gate = new TaskCompletionSource();
        var api = new FakeShiftApi { UploadGate = gate.Task };
        var tracker = Create(api);
        await tracker.StartAsync();
        AddFixes(tracker, 5);

        var first = tracker.FlushAsync();
        var second = tracker.FlushAsync();
        gate.SetResult();
        await Task.WhenAll(first, second);

        Assert.Single(api.Uploads);
        Assert.Equal(5, api.Uploads.Sum(u => u.Count));
    }

    [Fact]
    public async Task ShiftGoneOnServerDropsThePointsInsteadOfRetryingForever()
    {
        var api = new FakeShiftApi();
        var tracker = Create(api);
        await tracker.StartAsync();
        AddFixes(tracker, 2);
        api.UploadResults.Enqueue(ShiftCallStatus.NotFound);
        await tracker.FlushAsync();
        Assert.Equal(0, tracker.QueuedPoints);
    }

    [Fact]
    public async Task SessionExpiryKeepsPointsAndIsFlagged()
    {
        var api = new FakeShiftApi();
        var tracker = Create(api);
        await tracker.StartAsync();
        AddFixes(tracker, 2);
        api.UploadResults.Enqueue(ShiftCallStatus.SessionExpired);
        await tracker.FlushAsync();
        Assert.Equal(2, tracker.QueuedPoints);
        Assert.True(tracker.SessionExpired);
    }

    [Fact]
    public async Task EndStopsTrackingUploadsAndEndsOnServer()
    {
        var api = new FakeShiftApi();
        var tracker = Create(api);
        await tracker.StartAsync();
        AddFixes(tracker, 4);

        Assert.Equal(ShiftCallStatus.Success, await tracker.EndAsync());
        Assert.Equal(ShiftState.Off, tracker.State);
        Assert.Null(tracker.Shift);
        Assert.Equal(new[] { "start", "upload", $"end:{ShiftA.Id}" }, api.Calls);
        Assert.False(tracker.AddFix(36.19, 44.01, 10, T0, false));
    }

    [Fact]
    public async Task PointsLeftAfterEndAreUploadedLater()
    {
        var api = new FakeShiftApi();
        var tracker = Create(api);
        await tracker.StartAsync();
        AddFixes(tracker, 4);
        api.UploadResults.Enqueue(ShiftCallStatus.NetworkError);

        await tracker.EndAsync(); // upload fails, end succeeds
        Assert.Equal(ShiftState.Off, tracker.State);
        Assert.Equal(4, tracker.QueuedPoints);

        await tracker.FlushAsync();
        Assert.Equal(0, tracker.QueuedPoints);
        Assert.Equal(ShiftA.Id, api.UploadShiftIds.Last());
    }

    [Fact]
    public async Task EndFailingOnServerKeepsTheShiftOn()
    {
        var api = new FakeShiftApi { EndResult = ShiftCallStatus.NetworkError };
        var tracker = Create(api);
        await tracker.StartAsync();
        Assert.Equal(ShiftCallStatus.NetworkError, await tracker.EndAsync());
        Assert.Equal(ShiftState.On, tracker.State);
        Assert.True(tracker.AddFix(36.19, 44.01, 10, T0, false));
    }

    [Fact]
    public async Task StartRefusedWithoutDeviceStaysOff()
    {
        var api = new FakeShiftApi { StartResult = ShiftCallStatus.DeviceRequired };
        var tracker = Create(api);
        Assert.Equal(ShiftCallStatus.DeviceRequired, await tracker.StartAsync());
        Assert.Equal(ShiftState.Off, tracker.State);
    }

    [Fact]
    public async Task ResumeContinuesAnOpenShiftOrStaysOff()
    {
        var open = Create(new FakeShiftApi { Current = ShiftA });
        await open.ResumeAsync();
        Assert.Equal((ShiftState.On, ShiftA.Id), (open.State, open.Shift!.Id));

        var none = Create(new FakeShiftApi());
        await none.ResumeAsync();
        Assert.Equal(ShiftState.Off, none.State);
    }

    [Fact]
    public async Task FullQueueDropsOldestPointsAndCountsThem()
    {
        var tracker = Create(new FakeShiftApi());
        await tracker.StartAsync();
        AddFixes(tracker, ShiftTracker.MaxQueuedPoints + 7);
        Assert.Equal(ShiftTracker.MaxQueuedPoints, tracker.QueuedPoints);
        Assert.Equal(7, tracker.DroppedPoints);
    }

    private sealed class FakeShiftApi : IShiftApiClient
    {
        public List<string> Calls { get; } = new();
        public List<IReadOnlyList<TrackedPoint>> Uploads { get; } = new();
        public List<Guid> UploadShiftIds { get; } = new();
        public Queue<ShiftCallStatus> UploadResults { get; } = new();
        public ShiftCallStatus StartResult { get; init; } = ShiftCallStatus.Success;
        public ShiftCallStatus EndResult { get; init; } = ShiftCallStatus.Success;
        public ShiftInfo? Current { get; init; }
        public Task? UploadGate { get; init; }

        public Task<(ShiftCallStatus Status, ShiftInfo? Shift)> StartShiftAsync(CancellationToken cancellationToken = default)
        {
            Calls.Add("start");
            return Task.FromResult(StartResult == ShiftCallStatus.Success ? (StartResult, (ShiftInfo?)ShiftA) : (StartResult, null));
        }

        public Task<(ShiftCallStatus Status, ShiftInfo? Shift)> GetCurrentShiftAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Current is null ? (ShiftCallStatus.NotFound, (ShiftInfo?)null) : (ShiftCallStatus.Success, Current));

        public Task<ShiftCallStatus> EndShiftAsync(Guid shiftId, CancellationToken cancellationToken = default)
        {
            Calls.Add($"end:{shiftId}");
            return Task.FromResult(EndResult);
        }

        public async Task<(ShiftCallStatus Status, UploadResult? Result)> UploadPointsAsync(Guid shiftId, IReadOnlyList<TrackedPoint> points,
            CancellationToken cancellationToken = default)
        {
            if (UploadGate is not null) await UploadGate;
            Calls.Add("upload");
            Uploads.Add(points.ToList());
            UploadShiftIds.Add(shiftId);
            var status = UploadResults.TryDequeue(out var s) ? s : ShiftCallStatus.Success;
            return status == ShiftCallStatus.Success ? (status, new UploadResult(points.Count, 0, 0)) : (status, null);
        }
    }
}
