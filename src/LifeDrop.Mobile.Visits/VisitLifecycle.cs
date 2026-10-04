namespace LifeDrop.Mobile.Visits;

public enum VisitStep { None, StartTrip, MarkArrived, CheckIn, ReportAndCheckOut }

public enum FixFailure { PermissionDenied, Unavailable, TimedOut }

public sealed record FixResult(DeviceFix? Fix, FixFailure? Failure)
{
    public static FixResult Ok(DeviceFix fix) => new(fix, null);
    public static FixResult Failed(FixFailure failure) => new(null, failure);
}

// A fresh GPS position from the device (never a cached last-known one).
public interface ILocationSource
{
    Task<FixResult> GetFixAsync(CancellationToken cancellationToken = default);
}

// One visit's details screen (plan steps 1.11, 1.12; PRD §5.2, §5.3, §6). Only the next step of the status machine
// is offered, so the user cannot skip a status. While a request runs every action is disabled and a second tap
// sends nothing. The status changes only after the server accepts; when the server reports another state, the
// visit is reloaded from the server instead of guessing.
// Report and check-out: a report that fails to save never starts check-out. When the report is saved but
// check-out is refused, that partial success is kept and only check-out is retried.
public sealed class VisitLifecycle
{
    public static readonly IReadOnlyList<string> Outcomes =
        ["products_presented", "order_intent", "follow_up_needed", "no_interest", "other"];
    public const int MinNoteLength = 3;
    public const int MaxNoteLength = 2000;

    private readonly IVisitsApiClient _api;
    private readonly ILocationSource? _location;
    private readonly Func<string> _newKey;
    private string? _checkInKey;
    private string? _checkOutKey;

    public VisitLifecycle(IVisitsApiClient api, PlannedVisit visit, ILocationSource? location = null, Func<string>? newKey = null)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
        Visit = visit ?? throw new ArgumentNullException(nameof(visit));
        _location = location;
        _newKey = newKey ?? (() => Guid.NewGuid().ToString("N"));
    }

    public PlannedVisit Visit { get; private set; }
    public bool IsBusy { get; private set; }
    public string? Message { get; private set; }
    public bool SessionExpired { get; private set; }
    // The report stored on the server for this visit, when known.
    public VisitReportDraft? SavedReport { get; private set; }
    public bool ReportSaved => SavedReport is not null;

    public VisitStep NextStep => Visit.Status switch
    {
        "Planned" => VisitStep.StartTrip,
        "EnRoute" => VisitStep.MarkArrived,
        "Arrived" => VisitStep.CheckIn,
        "InVisit" => VisitStep.ReportAndCheckOut,
        _ => VisitStep.None
    };

    // The single-button steps; report and check-out use SubmitReportAndCheckOutAsync / RetryCheckOutAsync.
    public bool CanRunNextStep => !IsBusy && (NextStep is VisitStep.StartTrip or VisitStep.MarkArrived ||
                                              (NextStep == VisitStep.CheckIn && _location is not null));
    public bool CanSubmitReport => !IsBusy && NextStep == VisitStep.ReportAndCheckOut && _location is not null;
    public bool CanRetryCheckOut => CanSubmitReport && ReportSaved;

    public string? NextStepLabel => NextStep switch
    {
        VisitStep.StartTrip => "Start trip",
        VisitStep.MarkArrived => "I have arrived",
        VisitStep.CheckIn => "Check in here",
        VisitStep.ReportAndCheckOut => "Save report and check out",
        _ => null
    };

    public bool IsFinished => Visit.Status is "Completed" or "Failed" or "Cancelled";

    // Client-side copy of the server rules (contract version 1), so obvious mistakes are caught before sending.
    public static string? ValidateReport(string? outcome, string? note)
    {
        if (outcome is null || !Outcomes.Contains(outcome)) return "Choose the result of the visit.";
        var trimmed = note?.Trim() ?? "";
        if (trimmed.Length < MinNoteLength || !trimmed.Any(char.IsLetterOrDigit)) return "Write a short note about the visit (at least 3 characters).";
        if (trimmed.Length > MaxNoteLength) return $"The note is too long (at most {MaxNoteLength} characters).";
        return null;
    }

    // Planned -> EnRoute -> Arrived -> InVisit (check-in with GPS). Returns false when nothing was sent.
    public Task<bool> RunNextStepAsync(CancellationToken cancellationToken = default)
    {
        if (!CanRunNextStep) return Task.FromResult(false);
        return RunAsync(async () =>
        {
            switch (NextStep)
            {
                case VisitStep.StartTrip:
                    await ApplyAsync(await _api.MarkEnRouteAsync(Visit.Id, cancellationToken), "EnRoute", cancellationToken);
                    break;
                case VisitStep.MarkArrived:
                    await ApplyAsync(await _api.MarkArrivedAsync(Visit.Id, cancellationToken), "Arrived", cancellationToken);
                    break;
                case VisitStep.CheckIn:
                    var fix = await GetFixAsync(cancellationToken);
                    if (fix is null) return;
                    _checkInKey ??= _newKey();
                    await ApplyAsync(await _api.CheckInAsync(Visit.Id, fix, _checkInKey, cancellationToken), "InVisit", cancellationToken);
                    break;
            }
        }, cancellationToken);
    }

    // Reads an already saved report (e.g. after reopening the screen) so check-out can be retried alone.
    public async Task LoadReportAsync(CancellationToken cancellationToken = default)
    {
        if (NextStep != VisitStep.ReportAndCheckOut) return;
        var (status, report) = await _api.GetReportAsync(Visit.Id, cancellationToken);
        if (status == ReportLoadStatus.Found) SavedReport = report;
        if (status == ReportLoadStatus.SessionExpired) SessionExpired = true;
    }

    // Saves the report, then checks out only if the report was saved. An unchanged saved report is not sent again.
    public Task<bool> SubmitReportAndCheckOutAsync(string? outcome, string? note, CancellationToken cancellationToken = default)
    {
        if (!CanSubmitReport) return Task.FromResult(false);
        var invalid = ValidateReport(outcome, note);
        if (invalid is not null)
        {
            Message = invalid;
            return Task.FromResult(false);
        }

        var draft = new VisitReportDraft(outcome!, note!.Trim());
        return RunAsync(async () =>
        {
            if (SavedReport != draft)
            {
                var saved = await _api.SaveReportAsync(Visit.Id, draft, cancellationToken);
                if (saved != VisitActionResult.Success)
                {
                    // Check-out is not attempted.
                    Message = "The report was not saved, so check-out was not started. " + Describe(saved);
                    await HandleCommonAsync(saved, cancellationToken);
                    return;
                }
                SavedReport = draft;
            }
            await CheckOutCoreAsync(cancellationToken);
        }, cancellationToken);
    }

    // After a partial success (report saved, check-out refused): tries check-out again without resending the report.
    public Task<bool> RetryCheckOutAsync(CancellationToken cancellationToken = default)
    {
        if (!CanRetryCheckOut) return Task.FromResult(false);
        return RunAsync(() => CheckOutCoreAsync(cancellationToken), cancellationToken);
    }

    private async Task CheckOutCoreAsync(CancellationToken cancellationToken)
    {
        var fix = await GetFixAsync(cancellationToken);
        if (fix is null)
        {
            Message = "Report saved. " + Message;
            return;
        }
        _checkOutKey ??= _newKey();
        var result = await _api.CheckOutAsync(Visit.Id, fix, _checkOutKey, cancellationToken);
        if (result == VisitActionResult.Success)
        {
            Visit = Visit with { Status = "Completed" };
            Message = "Report saved and visit completed.";
            return;
        }
        if (result == VisitActionResult.ReportRequired) SavedReport = null; // the server has no report; save it again
        Message = (ReportSaved ? "Report saved, but check-out was not accepted. " : "") + Describe(result);
        await HandleCommonAsync(result, cancellationToken);
    }

    private async Task<DeviceFix?> GetFixAsync(CancellationToken cancellationToken)
    {
        var result = await _location!.GetFixAsync(cancellationToken);
        if (result.Fix is not null) return result.Fix;
        Message = result.Failure switch
        {
            FixFailure.PermissionDenied => "Location permission is needed. Allow it in the phone settings and try again.",
            FixFailure.TimedOut => "Your position could not be found in time. Go outside or near a window and try again.",
            _ => "Location is off or unavailable. Turn on location and try again."
        };
        return null;
    }

    private async Task ApplyAsync(VisitActionResult result, string newStatus, CancellationToken cancellationToken)
    {
        if (result == VisitActionResult.Success)
        {
            Visit = Visit with { Status = newStatus };
            return;
        }
        Message = Describe(result);
        await HandleCommonAsync(result, cancellationToken);
    }

    // Effects shared by every action: reload when the visit changed elsewhere, flag an ended session.
    private async Task HandleCommonAsync(VisitActionResult result, CancellationToken cancellationToken)
    {
        if (result == VisitActionResult.SessionExpired) SessionExpired = true;
        if (result is VisitActionResult.InvalidState or VisitActionResult.Conflict)
        {
            Message = await ReloadAsync(cancellationToken)
                ? "This visit was changed elsewhere. Its current status is shown."
                : "This visit was changed elsewhere and could not be reloaded. Go back to the plan.";
        }
    }

    private async Task<bool> RunAsync(Func<Task> action, CancellationToken cancellationToken)
    {
        IsBusy = true;
        Message = null;
        try
        {
            await action();
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            Message = "Something went wrong. Nothing was changed on this screen; try again.";
            return true;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task<bool> ReloadAsync(CancellationToken cancellationToken)
    {
        var plan = await _api.GetDailyPlanAsync(Visit.ScheduleDate, cancellationToken);
        var current = plan.Status == PlanLoadStatus.Success ? plan.Visits.FirstOrDefault(v => v.Id == Visit.Id) : null;
        if (current is null) return false;
        Visit = current;
        return true;
    }

    public static string Describe(VisitActionResult result) => result switch
    {
        VisitActionResult.NetworkError => "No connection to the server. Nothing was changed on this screen; try again.",
        VisitActionResult.SessionExpired => "Your session has ended. Please log in again.",
        VisitActionResult.Forbidden or VisitActionResult.NotFound => "This visit is no longer assigned to you.",
        VisitActionResult.DeviceRequired => "This phone is not an approved device for your account. Contact your manager.",
        VisitActionResult.CustomerLocationMissing => "The customer has no saved location yet. Ask your manager to set it.",
        VisitActionResult.ReportRequired => "The report is missing on the server. Save it again.",
        VisitActionResult.InvalidData => "The server did not accept the data. Check the report and try again.",
        VisitActionResult.OutsideGeofence => "You are not at the customer's location. Go to the customer and try again.",
        VisitActionResult.GpsLowAccuracy => "Your position is not accurate enough. Wait a moment outdoors and try again.",
        VisitActionResult.GpsStale => "Your position was too old. Try again.",
        VisitActionResult.GpsClockSkew => "The phone's clock is wrong. Set the time automatically and try again.",
        VisitActionResult.GpsMocked => "A fake location app was detected. Turn it off to continue.",
        VisitActionResult.InvalidState or VisitActionResult.Conflict => "This visit was changed elsewhere.",
        _ => "The server could not record this step. Try again later."
    };
}
