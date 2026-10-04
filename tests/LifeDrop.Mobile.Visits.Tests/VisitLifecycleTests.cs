using Xunit;

namespace LifeDrop.Mobile.Visits.Tests;

public class VisitLifecycleTests
{
    private static PlannedVisit Visit(string status) => new(Guid.NewGuid(), Guid.NewGuid(), "Pharmacy", "C1", status,
        new DateOnly(2026, 10, 5), "pharmacy", "normal", null, false, null);

    [Theory]
    [InlineData("Planned", VisitStep.StartTrip, true)]
    [InlineData("EnRoute", VisitStep.MarkArrived, true)]
    [InlineData("Arrived", VisitStep.CheckIn, false)]
    [InlineData("InVisit", VisitStep.ReportAndCheckOut, false)]
    [InlineData("Completed", VisitStep.None, false)]
    [InlineData("Failed", VisitStep.None, false)]
    [InlineData("Cancelled", VisitStep.None, false)]
    public void OnlyTheNextStepIsOffered(string status, VisitStep step, bool runnableHere)
    {
        var lifecycle = new VisitLifecycle(new FakeApi(), Visit(status));
        Assert.Equal(step, lifecycle.NextStep);
        Assert.Equal(runnableHere, lifecycle.CanRunNextStep);
    }

    [Fact]
    public async Task PlannedToEnRouteToArrivedOneStepAtATime()
    {
        var api = new FakeApi();
        var lifecycle = new VisitLifecycle(api, Visit("Planned"));

        Assert.True(await lifecycle.RunNextStepAsync());
        Assert.Equal("EnRoute", lifecycle.Visit.Status);
        Assert.True(await lifecycle.RunNextStepAsync());
        Assert.Equal("Arrived", lifecycle.Visit.Status);
        Assert.False(await lifecycle.RunNextStepAsync()); // check-in is not run from here
        Assert.Equal(new[] { "en-route", "arrive" }, api.Calls);
        Assert.Null(lifecycle.Message);
    }

    [Fact]
    public async Task SecondTapWhileSendingSendsNothing()
    {
        var gate = new TaskCompletionSource<VisitActionResult>();
        var api = new FakeApi { EnRoute = () => gate.Task };
        var lifecycle = new VisitLifecycle(api, Visit("Planned"));

        var first = lifecycle.RunNextStepAsync();
        Assert.True(lifecycle.IsBusy);
        Assert.False(lifecycle.CanRunNextStep);
        Assert.False(await lifecycle.RunNextStepAsync());
        gate.SetResult(VisitActionResult.Success);
        Assert.True(await first);
        Assert.Single(api.Calls);
        Assert.False(lifecycle.IsBusy);
        Assert.Equal("EnRoute", lifecycle.Visit.Status);
    }

    [Fact]
    public async Task NetworkErrorKeepsStatusAndAllowsRetry()
    {
        var api = new FakeApi { EnRoute = () => Task.FromResult(VisitActionResult.NetworkError) };
        var lifecycle = new VisitLifecycle(api, Visit("Planned"));

        await lifecycle.RunNextStepAsync();
        Assert.Equal("Planned", lifecycle.Visit.Status);
        Assert.StartsWith("No connection", lifecycle.Message);
        Assert.True(lifecycle.CanRunNextStep);
    }

    [Theory]
    [InlineData(VisitActionResult.InvalidState)]
    [InlineData(VisitActionResult.Conflict)]
    public async Task StateChangedElsewhereShowsTheServerStatus(VisitActionResult result)
    {
        var visit = Visit("Planned");
        var api = new FakeApi
        {
            EnRoute = () => Task.FromResult(result),
            Plan = () => PlanLoadResult.Ok([visit with { Status = "Cancelled", ExceptionReason = "Closed" }])
        };
        var lifecycle = new VisitLifecycle(api, visit);

        await lifecycle.RunNextStepAsync();
        Assert.Equal("Cancelled", lifecycle.Visit.Status);
        Assert.True(lifecycle.IsFinished);
        Assert.False(lifecycle.CanRunNextStep);
        Assert.StartsWith("This visit was changed elsewhere. Its current status is shown.", lifecycle.Message);
        Assert.Equal(visit.ScheduleDate, api.PlanDate);
    }

    [Fact]
    public async Task StateChangedElsewhereButReloadFailsDoesNotGuess()
    {
        var api = new FakeApi
        {
            EnRoute = () => Task.FromResult(VisitActionResult.InvalidState),
            Plan = () => PlanLoadResult.Failed(PlanLoadStatus.NetworkError)
        };
        var lifecycle = new VisitLifecycle(api, Visit("Planned"));
        await lifecycle.RunNextStepAsync();
        Assert.Equal("Planned", lifecycle.Visit.Status);
        Assert.Contains("could not be reloaded", lifecycle.Message);
    }

    [Theory]
    [InlineData(VisitActionResult.SessionExpired, "Your session has ended.", true)]
    [InlineData(VisitActionResult.Forbidden, "This visit is no longer assigned to you.", false)]
    [InlineData(VisitActionResult.NotFound, "This visit is no longer assigned to you.", false)]
    [InlineData(VisitActionResult.ServerError, "The server could not record this step.", false)]
    public async Task OtherFailuresKeepStatus(VisitActionResult result, string message, bool sessionExpired)
    {
        var lifecycle = new VisitLifecycle(new FakeApi { Arrive = () => Task.FromResult(result) }, Visit("EnRoute"));
        await lifecycle.RunNextStepAsync();
        Assert.Equal("EnRoute", lifecycle.Visit.Status);
        Assert.StartsWith(message, lifecycle.Message);
        Assert.Equal(sessionExpired, lifecycle.SessionExpired);
    }

    [Fact]
    public async Task UnexpectedExceptionReleasesBusy()
    {
        var lifecycle = new VisitLifecycle(new FakeApi { EnRoute = () => throw new InvalidOperationException() }, Visit("Planned"));
        await lifecycle.RunNextStepAsync();
        Assert.False(lifecycle.IsBusy);
        Assert.Equal("Planned", lifecycle.Visit.Status);
    }

    private sealed class FakeApi : IVisitsApiClient
    {
        public List<string> Calls { get; } = new();
        public DateOnly? PlanDate { get; private set; }
        public Func<Task<VisitActionResult>> EnRoute { get; init; } = () => Task.FromResult(VisitActionResult.Success);
        public Func<Task<VisitActionResult>> Arrive { get; init; } = () => Task.FromResult(VisitActionResult.Success);
        public Func<PlanLoadResult> Plan { get; init; } = () => PlanLoadResult.Ok([]);

        public Task<PlanLoadResult> GetDailyPlanAsync(DateOnly? scheduleDate = null, CancellationToken cancellationToken = default)
        {
            PlanDate = scheduleDate;
            return Task.FromResult(Plan());
        }

        public Task<VisitActionResult> MarkEnRouteAsync(Guid visitId, CancellationToken cancellationToken = default)
        {
            Calls.Add("en-route");
            return EnRoute();
        }

        public Task<VisitActionResult> MarkArrivedAsync(Guid visitId, CancellationToken cancellationToken = default)
        {
            Calls.Add("arrive");
            return Arrive();
        }
    }
}
