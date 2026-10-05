using Xunit;

namespace LifeDrop.Mobile.Visits.Tests;

public class PlanChangeTests
{
    private static readonly DateOnly Today = new(2026, 10, 5);

    private static PlannedVisit Visit(string name, string status = "Planned", string window = "none", int moved = 0) => new(Guid.NewGuid(), Guid.NewGuid(), name, "C", status,
        Today, "pharmacy", "normal", null, false, null, null, moved, null, window);

    private static async Task<(DailyPlan Plan, PlanChangeApi Api, PlannedVisit[] Visits)> LoadedAsync(params string[] names)
    {
        var visits = names.Select(n => Visit(n)).ToArray();
        var api = new PlanChangeApi(visits);
        var plan = new DailyPlan(api, new FixedClock(new DateTimeOffset(2026, 10, 5, 6, 0, 0, TimeSpan.Zero)));
        await plan.LoadAsync();
        return (plan, api, visits);
    }

    [Fact]
    public async Task Move_SwapsWithTheNeighbourAndSendsTheWholeDayInTheNewOrder()
    {
        var (plan, api, v) = await LoadedAsync("A", "B", "C");

        Assert.Equal(VisitActionResult.Success, await plan.MoveAsync(v[2].Id, -1));

        Assert.Equal(new[] { v[0].Id, v[2].Id, v[1].Id }, plan.Visits.Select(x => x.Id).ToArray());
        Assert.Equal(new int?[] { 1, 2, 3 }, plan.Visits.Select(x => x.SortOrder).ToArray());
        Assert.Equal(Today, api.ReorderedDay);
        Assert.Equal(new[] { v[0].Id, v[2].Id, v[1].Id }, api.ReorderedIds!.ToArray());
    }

    [Fact]
    public async Task Move_OutsideTheListDoesNothingAndNeverCallsTheServer()
    {
        var (plan, api, v) = await LoadedAsync("A", "B");
        Assert.Equal(VisitActionResult.InvalidState, await plan.MoveAsync(v[0].Id, -1));
        Assert.Equal(VisitActionResult.InvalidState, await plan.MoveAsync(v[1].Id, 1));
        Assert.Equal(VisitActionResult.InvalidState, await plan.MoveAsync(Guid.NewGuid(), 1));
        Assert.Null(api.ReorderedIds);
    }

    [Theory]
    [InlineData(VisitActionResult.NetworkError)]
    [InlineData(VisitActionResult.Conflict)]
    public async Task Move_KeepsTheOldOrderWhenTheServerDoesNotAcceptIt(VisitActionResult failure)
    {
        var (plan, api, v) = await LoadedAsync("A", "B");
        api.Result = failure;
        Assert.Equal(failure, await plan.MoveAsync(v[1].Id, -1));
        Assert.Equal(new[] { v[0].Id, v[1].Id }, plan.Visits.Select(x => x.Id).ToArray());
    }

    [Fact]
    public async Task Postpone_RemovesTheVisitFromTodayAndSendsDayAndTrimmedReason()
    {
        var (plan, api, v) = await LoadedAsync("A", "B");

        Assert.Equal(VisitActionResult.Success, await plan.PostponeAsync(v[0].Id, Today.AddDays(2), "  Not there  "));
        Assert.Equal((v[0].Id, Today.AddDays(2), "Not there"), (api.PostponedId!.Value, api.PostponedDay!.Value, api.PostponedReason));
        Assert.Equal(new[] { v[1].Id }, plan.Visits.Select(x => x.Id).ToArray());

        Assert.Equal(VisitActionResult.Success, await plan.PostponeAsync(v[1].Id, Today.AddDays(1), "   "));
        Assert.Null(api.PostponedReason);
        Assert.Equal(DailyPlanState.Empty, plan.State);
    }

    [Fact]
    public async Task Postpone_ToTodayOrThePastIsRefusedLocallyAndAFailureKeepsTheVisit()
    {
        var (plan, api, v) = await LoadedAsync("A");
        Assert.Equal(VisitActionResult.InvalidData, await plan.PostponeAsync(v[0].Id, Today, null));
        Assert.Equal(VisitActionResult.InvalidData, await plan.PostponeAsync(v[0].Id, Today.AddDays(-1), null));
        Assert.Null(api.PostponedId);

        api.Result = VisitActionResult.InvalidState;
        Assert.Equal(VisitActionResult.InvalidState, await plan.PostponeAsync(v[0].Id, Today.AddDays(1), null));
        Assert.Single(plan.Visits);
    }

    [Fact]
    public void PostponeChoices_AreTheNextSevenDaysStartingTomorrow()
    {
        var choices = DailyPlan.PostponeChoices(Today);
        Assert.Equal(7, choices.Count);
        Assert.Equal((Today.AddDays(1), Today.AddDays(7)), (choices[0], choices[^1]));
    }

    [Theory]
    [InlineData("Planned", false, true)]
    [InlineData("EnRoute", false, true)]
    [InlineData("Arrived", false, false)]
    [InlineData("InVisit", false, false)]
    [InlineData("Completed", false, false)]
    [InlineData("Planned", true, false)]    // steps waiting on the phone
    public void CanPostpone_OnlyBeforeArrivalAndWithoutWaitingSteps(string status, bool pending, bool expected) =>
        Assert.Equal(expected, DailyPlan.CanPostpone(Visit("A", status), pending));

    [Fact]
    public void PlanNotes_MentionOutsideHoursAndEarlierMoves()
    {
        Assert.Equal("", DailyPlan.PlanNotes(Visit("A")));
        Assert.Equal(" · outside customer hours", DailyPlan.PlanNotes(Visit("A", window: "outside")));
        Assert.Equal(" · moved 2×", DailyPlan.PlanNotes(Visit("A", moved: 2)));
        Assert.Equal(" · outside customer hours · moved 1×", DailyPlan.PlanNotes(Visit("A", window: "outside", moved: 1)));
        Assert.Equal("", DailyPlan.PlanNotes(Visit("A", window: "within")));
    }

    [Theory]
    [InlineData(VisitActionResult.Success, null)]
    [InlineData(VisitActionResult.NetworkError, "Could not change the plan.")]
    [InlineData(VisitActionResult.Conflict, "Could not change the plan.")]
    [InlineData(VisitActionResult.InvalidState, "This visit cannot be moved now.")]
    [InlineData(VisitActionResult.SessionExpired, "Your session has ended.")]
    public void ChangeMessage_NamesTheProblem(VisitActionResult result, string? expectedStart)
    {
        var message = DailyPlan.ChangeMessage(result);
        if (expectedStart is null) Assert.Null(message);
        else Assert.StartsWith(expectedStart, message);
    }

    private sealed class PlanChangeApi(IReadOnlyList<PlannedVisit> visits) : IVisitsApiClient
    {
        public VisitActionResult Result { get; set; } = VisitActionResult.Success;
        public DateOnly? ReorderedDay { get; private set; }
        public IReadOnlyList<Guid>? ReorderedIds { get; private set; }
        public Guid? PostponedId { get; private set; }
        public DateOnly? PostponedDay { get; private set; }
        public string? PostponedReason { get; private set; }

        public Task<PlanLoadResult> GetDailyPlanAsync(DateOnly? scheduleDate = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(PlanLoadResult.Ok(visits));

        public Task<VisitActionResult> ReorderPlanAsync(DateOnly scheduleDate, IReadOnlyList<Guid> visitIds, CancellationToken cancellationToken = default)
        {
            ReorderedDay = scheduleDate;
            ReorderedIds = visitIds;
            return Task.FromResult(Result);
        }

        public Task<VisitActionResult> PostponeAsync(Guid visitId, DateOnly newDate, string? reason, CancellationToken cancellationToken = default)
        {
            PostponedId = visitId;
            PostponedDay = newDate;
            PostponedReason = reason;
            return Task.FromResult(Result);
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
