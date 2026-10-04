using LifeDrop.Mobile.Offline;
using Xunit;

namespace LifeDrop.Mobile.Visits.Tests;

// Plan step 1.14 (decision D5): steps made without internet wait on the phone, survive restarts, are sent in order
// and never twice; the four states are visible.
public sealed class OutboxTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 5, 8, 0, 0, TimeSpan.Zero);
    private static readonly DeviceFix Fix = new(36.1911, 44.0092, 8, T0, false);
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lifedrop-outbox-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private static PlannedVisit Visit(string status) => new(Guid.NewGuid(), Guid.NewGuid(), "Pharmacy", "C1", status,
        new DateOnly(2026, 10, 5), "pharmacy", "normal", null, false, null);

    private Outbox Create(FakeApi api, FakeMonotonic? monotonic = null, FakeClock? clock = null) =>
        new(api, monotonic ?? new FakeMonotonic(), clock ?? new FakeClock(T0), new FileDurableStore(_directory));

    [Fact]
    public async Task OfflineVisitIsSavedLocallyAndSentInOrderWhenOnline()
    {
        var api = new FakeApi { Offline = true };
        var outbox = Create(api);
        var keys = 0;
        var lifecycle = new VisitLifecycle(api, Visit("Planned"), new FakeLocation(), () => $"k{++keys}", outbox);

        await lifecycle.RunNextStepAsync(); // en-route: no internet -> saved
        Assert.Equal(("EnRoute", VisitLifecycle.OfflineMessage), (lifecycle.Visit.Status, lifecycle.Message));
        await lifecycle.RunNextStepAsync(); // arrive: waits behind the first step, not sent
        await lifecycle.RunNextStepAsync(); // check-in
        await lifecycle.SubmitReportAndCheckOutAsync("products_presented", "Presented the range");
        Assert.Equal("Completed", lifecycle.Visit.Status);
        Assert.True(lifecycle.WaitingToSend);
        Assert.Equal(new OutboxCounts(5, 0, 0, 0), outbox.Counts);
        // Only the first step tried the network
        Assert.Equal(new[] { "en-route" }, api.Calls);

        api.Offline = false;
        Assert.True(await outbox.SyncAsync());
        Assert.Equal(new[] { "en-route", "en-route", "arrive", "start-offline:k3", "report", "complete-offline:k5" }, api.Calls);
        Assert.Equal(new OutboxCounts(0, 0, 5, 0), outbox.Counts);
        Assert.False(lifecycle.WaitingToSend);
    }

    [Fact]
    public async Task StepsSurviveAppRestart()
    {
        var api = new FakeApi { Offline = true };
        var outbox = Create(api);
        outbox.Enqueue(Guid.NewGuid(), new DateOnly(2026, 10, 5), OutboxKind.CheckIn, "key-1", Fix);

        var reopened = Create(api); // new process
        Assert.Equal(new OutboxCounts(1, 0, 0, 0), reopened.Counts);
        api.Offline = false;
        await reopened.SyncAsync();
        Assert.Equal(new[] { "start-offline:key-1" }, api.Calls);
    }

    [Fact]
    public async Task NoInternetDuringSyncKeepsStepsOnThePhone()
    {
        var api = new FakeApi { Offline = true };
        var outbox = Create(api);
        outbox.Enqueue(Guid.NewGuid(), new DateOnly(2026, 10, 5), OutboxKind.EnRoute, "a");
        Assert.False(await outbox.SyncAsync());
        Assert.Equal(new OutboxCounts(1, 0, 0, 0), outbox.Counts);
    }

    [Fact]
    public async Task RefusedStepRejectsTheLaterStepsOfThatVisitOnly()
    {
        var api = new FakeApi();
        api.CheckpointAnswers.Enqueue(VisitActionResult.OutsideGeofence);
        var outbox = Create(api);
        Guid visit = Guid.NewGuid(), other = Guid.NewGuid();
        var date = new DateOnly(2026, 10, 5);
        outbox.Enqueue(visit, date, OutboxKind.CheckIn, "a", Fix);
        outbox.Enqueue(visit, date, OutboxKind.Report, "b", report: new VisitReportDraft("other", "Note"));
        outbox.Enqueue(visit, date, OutboxKind.CheckOut, "c", Fix);
        outbox.Enqueue(other, date, OutboxKind.EnRoute, "d");

        Assert.True(await outbox.SyncAsync());
        Assert.Equal(new[] { "start-offline:a", "en-route" }, api.Calls); // report and check-out never sent
        var states = outbox.ForVisit(visit).Select(i => (i.Kind, i.State, i.Answer)).ToArray();
        Assert.Equal((OutboxKind.CheckIn, SyncState.Rejected, (VisitActionResult?)VisitActionResult.OutsideGeofence), states[0]);
        Assert.All(states.Skip(1), s => Assert.Equal(SyncState.Rejected, s.State));
        Assert.Equal(SyncState.Accepted, outbox.ForVisit(other).Single().State);
    }

    [Fact]
    public async Task ReviewRequiredIsKeptOnTheAcceptedStep()
    {
        var api = new FakeApi { ReviewRequired = true };
        var outbox = Create(api);
        outbox.Enqueue(Guid.NewGuid(), new DateOnly(2026, 10, 5), OutboxKind.CheckIn, "a", Fix);
        await outbox.SyncAsync();
        Assert.True(outbox.Items.Single().ReviewRequired);
    }

    [Fact]
    public async Task ElapsedTimeIsSentFromTheMonotonicClockAndDroppedAfterAPhoneRestart()
    {
        var monotonic = new FakeMonotonic { ElapsedMs = 1_000_000, BootId = "boot-1" };
        var api = new FakeApi { Offline = true };
        var outbox = Create(api, monotonic);
        outbox.Enqueue(Guid.NewGuid(), new DateOnly(2026, 10, 5), OutboxKind.CheckIn, "a", Fix);
        outbox.Enqueue(Guid.NewGuid(), new DateOnly(2026, 10, 5), OutboxKind.CheckIn, "b", Fix);

        monotonic.ElapsedMs += 3_600_000; // an hour later, same boot
        api.Offline = false;
        api.FailAfter = 1;                // the second send fails: no internet again
        await outbox.SyncAsync();
        Assert.Equal(3_600_000, api.ElapsedSent[0]);

        monotonic.BootId = "boot-2";      // phone restarted
        monotonic.ElapsedMs = 5_000;
        api.FailAfter = null;
        await outbox.SyncAsync();
        Assert.Null(api.ElapsedSent[^1]);
    }

    [Fact]
    public async Task LostReplyForEnRouteIsRecognisedAsDone()
    {
        var visit = Visit("Planned");
        var api = new FakeApi { EnRouteAnswer = VisitActionResult.InvalidState, Plan = [visit with { Status = "Arrived" }] };
        var outbox = Create(api);
        outbox.Enqueue(visit.Id, visit.ScheduleDate, OutboxKind.EnRoute, "a");
        await outbox.SyncAsync();
        Assert.Equal(SyncState.Accepted, outbox.Items.Single().State);

        api.Plan = [visit with { Status = "Cancelled" }];
        outbox.Enqueue(visit.Id, visit.ScheduleDate, OutboxKind.EnRoute, "b");
        await outbox.SyncAsync();
        Assert.Equal(SyncState.Rejected, outbox.Items.Last().State);
    }

    [Fact]
    public void ReopenedScreenShowsTheLocalStatus()
    {
        var api = new FakeApi();
        var outbox = Create(api);
        var visit = Visit("Arrived");
        outbox.Enqueue(visit.Id, visit.ScheduleDate, OutboxKind.CheckIn, "a", Fix);
        var lifecycle = new VisitLifecycle(api, visit, new FakeLocation(), outbox: outbox);
        Assert.Equal("InVisit", lifecycle.Visit.Status);
        Assert.True(lifecycle.WaitingToSend);
    }

    [Fact]
    public async Task FinishedStepsAreRemovedAfter48Hours()
    {
        var clock = new FakeClock(T0);
        var api = new FakeApi();
        var outbox = Create(api, clock: clock);
        outbox.Enqueue(Guid.NewGuid(), new DateOnly(2026, 10, 5), OutboxKind.EnRoute, "a");
        await outbox.SyncAsync();
        clock.Now = T0.AddHours(49);
        await outbox.SyncAsync();
        Assert.Empty(outbox.Items);
    }

    [Fact]
    public async Task OnlineStepsDoNotUseTheOutbox()
    {
        var api = new FakeApi();
        var outbox = Create(api);
        var lifecycle = new VisitLifecycle(api, Visit("Planned"), new FakeLocation(), outbox: outbox);
        await lifecycle.RunNextStepAsync();
        Assert.Equal("EnRoute", lifecycle.Visit.Status);
        Assert.Empty(outbox.Items);
        Assert.Null(lifecycle.Message);
    }

    private sealed class FakeLocation : ILocationSource
    {
        public Task<FixResult> GetFixAsync(CancellationToken cancellationToken = default) => Task.FromResult(FixResult.Ok(Fix));
    }

    private sealed class FakeMonotonic : IMonotonicClock
    {
        public long ElapsedMs { get; set; } = 10_000;
        public string? BootId { get; set; } = "boot";
    }

    private sealed class FakeClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FakeApi : IVisitsApiClient
    {
        public bool Offline { get; set; }
        public int? FailAfter { get; set; }
        public bool ReviewRequired { get; init; }
        public VisitActionResult EnRouteAnswer { get; init; } = VisitActionResult.Success;
        public PlannedVisit[] Plan { get; set; } = [];
        public Queue<VisitActionResult> CheckpointAnswers { get; } = new();
        public List<string> Calls { get; } = new();
        public List<long?> ElapsedSent { get; } = new();

        private VisitActionResult? Down()
        {
            if (Offline) return VisitActionResult.NetworkError;
            if (FailAfter is { } n && Calls.Count(c => c.Contains("offline")) >= n) return VisitActionResult.NetworkError;
            return null;
        }

        public Task<PlanLoadResult> GetDailyPlanAsync(DateOnly? scheduleDate = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(PlanLoadResult.Ok(Plan));

        public Task<VisitActionResult> MarkEnRouteAsync(Guid visitId, CancellationToken cancellationToken = default)
        {
            Calls.Add("en-route");
            return Task.FromResult(Down() ?? EnRouteAnswer);
        }

        public Task<VisitActionResult> MarkArrivedAsync(Guid visitId, CancellationToken cancellationToken = default)
        {
            Calls.Add("arrive");
            return Task.FromResult(Down() ?? VisitActionResult.Success);
        }

        public Task<VisitActionResult> CheckInAsync(Guid visitId, DeviceFix fix, string idempotencyKey, CancellationToken cancellationToken = default)
        {
            Calls.Add($"start:{idempotencyKey}");
            return Task.FromResult(Down() ?? VisitActionResult.Success);
        }

        public Task<VisitActionResult> CheckOutAsync(Guid visitId, DeviceFix fix, string idempotencyKey, CancellationToken cancellationToken = default)
        {
            Calls.Add($"complete:{idempotencyKey}");
            return Task.FromResult(Down() ?? VisitActionResult.Success);
        }

        public Task<VisitActionResult> SaveReportAsync(Guid visitId, VisitReportDraft report, CancellationToken cancellationToken = default)
        {
            Calls.Add("report");
            return Task.FromResult(Down() ?? VisitActionResult.Success);
        }

        public Task<(VisitActionResult Result, bool ReviewRequired)> SendOfflineCheckpointAsync(Guid visitId, bool checkOut, DeviceFix fix,
            long? elapsedSinceCaptureMs, string idempotencyKey, CancellationToken cancellationToken = default)
        {
            var down = Down();
            Calls.Add($"{(checkOut ? "complete" : "start")}-offline:{idempotencyKey}");
            if (down is { } d) return Task.FromResult((d, false));
            ElapsedSent.Add(elapsedSinceCaptureMs);
            var answer = CheckpointAnswers.TryDequeue(out var a) ? a : VisitActionResult.Success;
            return Task.FromResult((answer, ReviewRequired));
        }
    }
}
