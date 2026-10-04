using Xunit;

namespace LifeDrop.Mobile.Visits.Tests;

public class DailyPlanTests
{
    private static PlannedVisit Visit(string name) => new(Guid.NewGuid(), Guid.NewGuid(), name, "C", "Planned",
        new DateOnly(2026, 10, 5), "pharmacy", "normal", null, false, null);

    [Fact]
    public async Task RequestsTodayInBaghdadNotUtc()
    {
        // 22:30 UTC on 4 Oct is 01:30 on 5 Oct in Baghdad
        var api = new ScriptedApi(PlanLoadResult.Ok([]));
        var plan = new DailyPlan(api, new FixedClock(new DateTimeOffset(2026, 10, 4, 22, 30, 0, TimeSpan.Zero)));

        await plan.LoadAsync();

        Assert.Equal(new DateOnly(2026, 10, 5), api.Requested.Single());
        Assert.Equal(new DateOnly(2026, 10, 5), plan.Date);
    }

    [Fact]
    public async Task EmptyDayShowsEmptyStateNotError()
    {
        var plan = new DailyPlan(new ScriptedApi(PlanLoadResult.Ok([])), new FixedClock(DateTimeOffset.UtcNow));
        await plan.LoadAsync();
        Assert.Equal(DailyPlanState.Empty, plan.State);
        Assert.Null(plan.Error);
        Assert.Equal("No visits planned for today.", plan.Message);
    }

    [Fact]
    public async Task LoadedVisitsAreShownInServerOrder()
    {
        var visits = new[] { Visit("A"), Visit("B") };
        var plan = new DailyPlan(new ScriptedApi(PlanLoadResult.Ok(visits)), new FixedClock(DateTimeOffset.UtcNow));
        await plan.LoadAsync();
        Assert.Equal(DailyPlanState.Loaded, plan.State);
        Assert.Equal(visits, plan.Visits);
        Assert.Null(plan.Message);
    }

    [Theory]
    [InlineData(PlanLoadStatus.NetworkError, "No connection to the server.")]
    [InlineData(PlanLoadStatus.SessionExpired, "Your session has ended.")]
    [InlineData(PlanLoadStatus.ServerError, "The server could not load your plan.")]
    public async Task EachFailureHasItsOwnMessage(PlanLoadStatus status, string expectedStart)
    {
        var plan = new DailyPlan(new ScriptedApi(PlanLoadResult.Failed(status)), new FixedClock(DateTimeOffset.UtcNow));
        await plan.LoadAsync();
        Assert.Equal(DailyPlanState.Error, plan.State);
        Assert.Equal(status, plan.Error);
        Assert.StartsWith(expectedStart, plan.Message);
        Assert.False(plan.IsStale);
        Assert.Empty(plan.Visits);
    }

    [Fact]
    public async Task FailedRefreshKeepsVisitsAndSaysTheyMayBeOutOfDate()
    {
        var visits = new[] { Visit("A") };
        var plan = new DailyPlan(new ScriptedApi(PlanLoadResult.Ok(visits), PlanLoadResult.Failed(PlanLoadStatus.NetworkError)),
            new FixedClock(DateTimeOffset.UtcNow));
        await plan.LoadAsync();
        await plan.LoadAsync();
        Assert.Equal(DailyPlanState.Error, plan.State);
        Assert.True(plan.IsStale);
        Assert.Equal(visits, plan.Visits);
        Assert.EndsWith("The list below may be out of date.", plan.Message);
    }

    [Fact]
    public async Task YesterdaysVisitsAreNotKeptAfterMidnightInBaghdad()
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 10, 4, 20, 0, 0, TimeSpan.Zero)); // 23:00 Baghdad
        var plan = new DailyPlan(new ScriptedApi(PlanLoadResult.Ok([Visit("A")]), PlanLoadResult.Failed(PlanLoadStatus.NetworkError)), clock);
        await plan.LoadAsync();
        clock.Now = clock.Now.AddHours(2); // 01:00 next day
        await plan.LoadAsync();
        Assert.Empty(plan.Visits);
        Assert.False(plan.IsStale);
    }

    [Fact]
    public async Task SecondLoadWhileLoadingSendsNoRequest()
    {
        var gate = new TaskCompletionSource<PlanLoadResult>();
        var api = new ScriptedApi(() => gate.Task);
        var plan = new DailyPlan(api, new FixedClock(DateTimeOffset.UtcNow));

        var first = plan.LoadAsync();
        Assert.True(plan.IsLoading);
        Assert.False(await plan.LoadAsync());
        gate.SetResult(PlanLoadResult.Ok([]));
        Assert.True(await first);
        Assert.Single(api.Requested);
        Assert.Equal(DailyPlanState.Empty, plan.State);
    }

    [Fact]
    public async Task UnexpectedExceptionEndsInErrorNotLoading()
    {
        var plan = new DailyPlan(new ScriptedApi(() => throw new InvalidOperationException("bug")), new FixedClock(DateTimeOffset.UtcNow));
        await plan.LoadAsync();
        Assert.Equal(DailyPlanState.Error, plan.State);
        Assert.Equal(PlanLoadStatus.ServerError, plan.Error);
    }

    [Fact]
    public async Task CancellationRestoresPreviousState()
    {
        var plan = new DailyPlan(new ScriptedApi(() => throw new OperationCanceledException()), new FixedClock(DateTimeOffset.UtcNow));
        await Assert.ThrowsAsync<OperationCanceledException>(() => plan.LoadAsync());
        Assert.Equal(DailyPlanState.Idle, plan.State);
    }

    [Fact]
    public async Task CachedPlanIsShownAfterRestartWithoutInternetButNotOnAnotherDay()
    {
        var directory = Path.Combine(Path.GetTempPath(), "lifedrop-plan-" + Guid.NewGuid().ToString("N"));
        try
        {
            var clock = new FixedClock(new DateTimeOffset(2026, 10, 5, 6, 0, 0, TimeSpan.Zero));
            var visits = new[] { Visit("A") };
            await new DailyPlan(new ScriptedApi(PlanLoadResult.Ok(visits)), clock, new LifeDrop.Mobile.Offline.FileDurableStore(directory)).LoadAsync();

            // A new process, no internet
            var offline = new DailyPlan(new ScriptedApi(PlanLoadResult.Failed(PlanLoadStatus.NetworkError)), clock,
                new LifeDrop.Mobile.Offline.FileDurableStore(directory));
            await offline.LoadAsync();
            Assert.Equal(visits.Select(v => v.Id), offline.Visits.Select(v => v.Id));
            Assert.True(offline.IsStale);

            clock.Now = clock.Now.AddDays(1);
            var nextDay = new DailyPlan(new ScriptedApi(PlanLoadResult.Failed(PlanLoadStatus.NetworkError)), clock,
                new LifeDrop.Mobile.Offline.FileDurableStore(directory));
            await nextDay.LoadAsync();
            Assert.Empty(nextDay.Visits);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("EnRoute", "On the way")]
    [InlineData("InVisit", "In visit")]
    [InlineData("Unknown", "Unknown")]
    public void StatusLabels(string status, string label) => Assert.Equal(label, DailyPlan.StatusLabel(status));

    private sealed class ScriptedApi : IVisitsApiClient
    {
        private readonly Queue<Func<Task<PlanLoadResult>>> _responses = new();
        public List<DateOnly?> Requested { get; } = new();

        public ScriptedApi(params PlanLoadResult[] results)
        {
            foreach (var r in results) _responses.Enqueue(() => Task.FromResult(r));
        }

        public ScriptedApi(Func<Task<PlanLoadResult>> respond) => _responses.Enqueue(respond);

        public Task<PlanLoadResult> GetDailyPlanAsync(DateOnly? scheduleDate = null, CancellationToken cancellationToken = default)
        {
            Requested.Add(scheduleDate);
            return _responses.Dequeue()();
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
