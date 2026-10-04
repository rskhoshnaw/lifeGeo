using Xunit;

namespace LifeDrop.Mobile.Visits.Tests;

// Plan step 1.12: check-in with GPS, report, check-out; a failed report never starts check-out, and a partial
// success (report saved, check-out refused) is kept and retried alone.
public class CheckInReportCheckOutTests
{
    private static readonly DeviceFix Fix = new(36.1911, 44.0092, 8, new DateTimeOffset(2026, 10, 5, 8, 0, 0, TimeSpan.Zero), false);

    private static PlannedVisit Visit(string status) => new(Guid.NewGuid(), Guid.NewGuid(), "Pharmacy", "C1", status,
        new DateOnly(2026, 10, 5), "pharmacy", "normal", null, false, null);

    private static VisitLifecycle Create(FakeApi api, string status, FakeLocation? location = null)
    {
        var keys = 0;
        return new VisitLifecycle(api, Visit(status), location ?? new FakeLocation(), () => $"key-{++keys}");
    }

    [Fact]
    public async Task CheckInSendsFreshFixWithKeyAndMovesToInVisit()
    {
        var api = new FakeApi();
        var lifecycle = Create(api, "Arrived");
        Assert.True(lifecycle.CanRunNextStep);

        await lifecycle.RunNextStepAsync();

        Assert.Equal("InVisit", lifecycle.Visit.Status);
        Assert.Equal(new[] { "start:key-1" }, api.Calls);
        Assert.Equal(Fix, api.LastFix);
    }

    [Fact]
    public async Task CheckInRetryAfterNetworkErrorReusesTheSameKey()
    {
        var api = new FakeApi();
        api.CheckIn.Enqueue(VisitActionResult.NetworkError);
        var lifecycle = Create(api, "Arrived");

        await lifecycle.RunNextStepAsync();
        Assert.Equal("Arrived", lifecycle.Visit.Status);
        Assert.StartsWith("No connection", lifecycle.Message);
        await lifecycle.RunNextStepAsync();

        Assert.Equal("InVisit", lifecycle.Visit.Status);
        Assert.Equal(new[] { "start:key-1", "start:key-1" }, api.Calls);
    }

    [Theory]
    [InlineData(VisitActionResult.OutsideGeofence, "You are not at the customer's location.")]
    [InlineData(VisitActionResult.GpsLowAccuracy, "Your position is not accurate enough.")]
    [InlineData(VisitActionResult.GpsMocked, "A fake location app was detected.")]
    [InlineData(VisitActionResult.GpsClockSkew, "The phone's clock is wrong.")]
    [InlineData(VisitActionResult.DeviceRequired, "This phone is not an approved device")]
    [InlineData(VisitActionResult.CustomerLocationMissing, "The customer has no saved location yet.")]
    public async Task CheckInRefusalsKeepStatusAndExplain(VisitActionResult refusal, string message)
    {
        var api = new FakeApi();
        api.CheckIn.Enqueue(refusal);
        var lifecycle = Create(api, "Arrived");
        await lifecycle.RunNextStepAsync();
        Assert.Equal("Arrived", lifecycle.Visit.Status);
        Assert.StartsWith(message, lifecycle.Message);
        Assert.True(lifecycle.CanRunNextStep);
    }

    [Theory]
    [InlineData(FixFailure.PermissionDenied, "Location permission is needed.")]
    [InlineData(FixFailure.Unavailable, "Location is off or unavailable.")]
    [InlineData(FixFailure.TimedOut, "Your position could not be found in time.")]
    public async Task NoFixSendsNothing(FixFailure failure, string message)
    {
        var api = new FakeApi();
        var lifecycle = Create(api, "Arrived", new FakeLocation(FixResult.Failed(failure)));
        await lifecycle.RunNextStepAsync();
        Assert.Empty(api.Calls);
        Assert.StartsWith(message, lifecycle.Message);
    }

    [Fact]
    public void WithoutLocationSourceCheckInAndCheckOutAreNotOffered()
    {
        var api = new FakeApi();
        Assert.False(new VisitLifecycle(api, Visit("Arrived")).CanRunNextStep);
        Assert.False(new VisitLifecycle(api, Visit("InVisit")).CanSubmitReport);
    }

    [Theory]
    [InlineData(null, "Presented the range", "Choose the result of the visit.")]
    [InlineData("order_placed", "Presented the range", "Choose the result of the visit.")]
    [InlineData("other", "  ", "Write a short note")]
    [InlineData("other", "...", "Write a short note")]
    [InlineData("other", "ab", "Write a short note")]
    public async Task InvalidReportIsNotSent(string? outcome, string note, string message)
    {
        var api = new FakeApi();
        var lifecycle = Create(api, "InVisit");
        Assert.False(await lifecycle.SubmitReportAndCheckOutAsync(outcome, note));
        Assert.Empty(api.Calls);
        Assert.StartsWith(message, lifecycle.Message);
    }

    [Fact]
    public void ReportValidationMatchesServerLimits()
    {
        Assert.Null(VisitLifecycle.ValidateReport("no_interest", "abc"));
        Assert.Null(VisitLifecycle.ValidateReport("no_interest", new string('a', 2000)));
        Assert.StartsWith("The note is too long", VisitLifecycle.ValidateReport("no_interest", new string('a', 2001)));
    }

    [Fact]
    public async Task ReportThenCheckOutCompletesTheVisit()
    {
        var api = new FakeApi();
        var lifecycle = Create(api, "InVisit");

        Assert.True(await lifecycle.SubmitReportAndCheckOutAsync("products_presented", "  Presented the range  "));

        Assert.Equal(new[] { "report", "complete:key-1" }, api.Calls);
        Assert.Equal(new VisitReportDraft("products_presented", "Presented the range"), api.LastReport);
        Assert.Equal("Completed", lifecycle.Visit.Status);
        Assert.True(lifecycle.IsFinished);
        Assert.Equal("Report saved and visit completed.", lifecycle.Message);
    }

    [Theory]
    [InlineData(VisitActionResult.NetworkError)]
    [InlineData(VisitActionResult.InvalidData)]
    [InlineData(VisitActionResult.ServerError)]
    public async Task FailedReportNeverStartsCheckOut(VisitActionResult failure)
    {
        var api = new FakeApi();
        api.Report.Enqueue(failure);
        var lifecycle = Create(api, "InVisit");

        await lifecycle.SubmitReportAndCheckOutAsync("other", "Closed early");

        Assert.Equal(new[] { "report" }, api.Calls);
        Assert.False(lifecycle.ReportSaved);
        Assert.Equal("InVisit", lifecycle.Visit.Status);
        Assert.StartsWith("The report was not saved, so check-out was not started.", lifecycle.Message);
    }

    [Fact]
    public async Task PartialSuccessIsShownAndOnlyCheckOutIsRetried()
    {
        var api = new FakeApi();
        api.CheckOut.Enqueue(VisitActionResult.OutsideGeofence);
        var lifecycle = Create(api, "InVisit");

        await lifecycle.SubmitReportAndCheckOutAsync("follow_up_needed", "Call back next week");
        Assert.True(lifecycle.ReportSaved);
        Assert.True(lifecycle.CanRetryCheckOut);
        Assert.Equal("InVisit", lifecycle.Visit.Status);
        Assert.Equal("Report saved, but check-out was not accepted. You are not at the customer's location. Go to the customer and try again.",
            lifecycle.Message);

        Assert.True(await lifecycle.RetryCheckOutAsync());
        Assert.Equal(new[] { "report", "complete:key-1", "complete:key-1" }, api.Calls);
        Assert.Equal("Completed", lifecycle.Visit.Status);
    }

    [Fact]
    public async Task UnchangedSavedReportIsNotSentAgainButAnEditedOneIs()
    {
        var api = new FakeApi();
        api.CheckOut.Enqueue(VisitActionResult.GpsStale);
        api.CheckOut.Enqueue(VisitActionResult.GpsStale);
        var lifecycle = Create(api, "InVisit");

        await lifecycle.SubmitReportAndCheckOutAsync("other", "First note");
        await lifecycle.SubmitReportAndCheckOutAsync("other", "First note");
        await lifecycle.SubmitReportAndCheckOutAsync("other", "Edited note");

        Assert.Equal(new[] { "report", "complete:key-1", "complete:key-1", "report", "complete:key-1" }, api.Calls);
        Assert.Equal("Completed", lifecycle.Visit.Status);
    }

    [Fact]
    public async Task ReportRequiredFromServerForgetsTheSavedReport()
    {
        var api = new FakeApi();
        api.CheckOut.Enqueue(VisitActionResult.ReportRequired);
        var lifecycle = Create(api, "InVisit");

        await lifecycle.SubmitReportAndCheckOutAsync("other", "A note");
        Assert.False(lifecycle.ReportSaved);
        Assert.False(lifecycle.CanRetryCheckOut);
        Assert.Contains("The report is missing on the server.", lifecycle.Message);
    }

    [Fact]
    public async Task NoFixAtCheckOutKeepsSavedReport()
    {
        var api = new FakeApi();
        var lifecycle = Create(api, "InVisit", new FakeLocation(FixResult.Failed(FixFailure.Unavailable)));
        await lifecycle.SubmitReportAndCheckOutAsync("other", "A note");
        Assert.Equal(new[] { "report" }, api.Calls);
        Assert.True(lifecycle.ReportSaved);
        Assert.StartsWith("Report saved. Location is off", lifecycle.Message);
    }

    [Fact]
    public async Task SavedReportIsLoadedSoCheckOutCanBeRetriedAfterReopening()
    {
        var api = new FakeApi { StoredReport = new VisitReportDraft("no_interest", "Not interested") };
        var lifecycle = Create(api, "InVisit");
        await lifecycle.LoadReportAsync();
        Assert.True(lifecycle.CanRetryCheckOut);
        await lifecycle.RetryCheckOutAsync();
        Assert.Equal(new[] { "complete:key-1" }, api.Calls);
    }

    [Fact]
    public async Task CheckOutAfterVisitChangedElsewhereShowsServerStatus()
    {
        var api = new FakeApi();
        api.CheckOut.Enqueue(VisitActionResult.InvalidState);
        var lifecycle = Create(api, "InVisit");
        api.Plan = () => PlanLoadResult.Ok([lifecycle.Visit with { Status = "Cancelled" }]);

        await lifecycle.SubmitReportAndCheckOutAsync("other", "A note");
        Assert.Equal("Cancelled", lifecycle.Visit.Status);
        Assert.False(lifecycle.CanSubmitReport);
    }

    [Fact]
    public async Task SecondSubmitWhileSendingSendsNothing()
    {
        var gate = new TaskCompletionSource<VisitActionResult>();
        var api = new FakeApi { ReportGate = gate.Task };
        var lifecycle = Create(api, "InVisit");

        var first = lifecycle.SubmitReportAndCheckOutAsync("other", "A note");
        Assert.False(lifecycle.CanSubmitReport);
        Assert.False(await lifecycle.SubmitReportAndCheckOutAsync("other", "A note"));
        Assert.False(await lifecycle.RetryCheckOutAsync());
        gate.SetResult(VisitActionResult.Success);
        await first;
        Assert.Equal(new[] { "report", "complete:key-1" }, api.Calls);
    }

    private sealed class FakeLocation(FixResult? result = null) : ILocationSource
    {
        public Task<FixResult> GetFixAsync(CancellationToken cancellationToken = default) => Task.FromResult(result ?? FixResult.Ok(Fix));
    }

    private sealed class FakeApi : IVisitsApiClient
    {
        public List<string> Calls { get; } = new();
        public Queue<VisitActionResult> CheckIn { get; } = new();
        public Queue<VisitActionResult> CheckOut { get; } = new();
        public Queue<VisitActionResult> Report { get; } = new();
        public Task<VisitActionResult>? ReportGate { get; init; }
        public VisitReportDraft? StoredReport { get; init; }
        public DeviceFix? LastFix { get; private set; }
        public VisitReportDraft? LastReport { get; private set; }
        public Func<PlanLoadResult> Plan { get; set; } = () => PlanLoadResult.Ok([]);

        public Task<PlanLoadResult> GetDailyPlanAsync(DateOnly? scheduleDate = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(Plan());

        public Task<VisitActionResult> CheckInAsync(Guid visitId, DeviceFix fix, string idempotencyKey, CancellationToken cancellationToken = default)
        {
            Calls.Add($"start:{idempotencyKey}");
            LastFix = fix;
            return Task.FromResult(CheckIn.TryDequeue(out var r) ? r : VisitActionResult.Success);
        }

        public Task<VisitActionResult> CheckOutAsync(Guid visitId, DeviceFix fix, string idempotencyKey, CancellationToken cancellationToken = default)
        {
            Calls.Add($"complete:{idempotencyKey}");
            return Task.FromResult(CheckOut.TryDequeue(out var r) ? r : VisitActionResult.Success);
        }

        public Task<VisitActionResult> SaveReportAsync(Guid visitId, VisitReportDraft report, CancellationToken cancellationToken = default)
        {
            Calls.Add("report");
            LastReport = report;
            return ReportGate ?? Task.FromResult(Report.TryDequeue(out var r) ? r : VisitActionResult.Success);
        }

        public Task<(ReportLoadStatus Status, VisitReportDraft? Report)> GetReportAsync(Guid visitId, CancellationToken cancellationToken = default) =>
            Task.FromResult(StoredReport is null ? (ReportLoadStatus.None, (VisitReportDraft?)null) : (ReportLoadStatus.Found, StoredReport));
    }
}
