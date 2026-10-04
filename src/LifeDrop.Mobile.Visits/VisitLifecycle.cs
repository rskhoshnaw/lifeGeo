namespace LifeDrop.Mobile.Visits;

public enum VisitStep { None, StartTrip, MarkArrived, CheckIn, ReportAndCheckOut }

// One visit's details screen (plan step 1.11, PRD §5.2). The only action offered is the next step of the
// status machine, so the user cannot skip a status. While a request runs the action is disabled and a second
// tap sends nothing. The status changes only after the server accepts; when the server reports another state,
// the visit is reloaded from the server instead of guessing.
public sealed class VisitLifecycle
{
    private readonly IVisitsApiClient _api;

    public VisitLifecycle(IVisitsApiClient api, PlannedVisit visit)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
        Visit = visit ?? throw new ArgumentNullException(nameof(visit));
    }

    public PlannedVisit Visit { get; private set; }
    public bool IsBusy { get; private set; }
    public string? Message { get; private set; }
    public bool SessionExpired { get; private set; }

    public VisitStep NextStep => Visit.Status switch
    {
        "Planned" => VisitStep.StartTrip,
        "EnRoute" => VisitStep.MarkArrived,
        "Arrived" => VisitStep.CheckIn,
        "InVisit" => VisitStep.ReportAndCheckOut,
        _ => VisitStep.None
    };

    // Steps 1.7/1.8 need GPS on the device; they come with plan step 1.12.
    public bool CanRunNextStep => !IsBusy && NextStep is VisitStep.StartTrip or VisitStep.MarkArrived;

    public string? NextStepLabel => NextStep switch
    {
        VisitStep.StartTrip => "Start trip",
        VisitStep.MarkArrived => "I have arrived",
        VisitStep.CheckIn => "Check in",
        VisitStep.ReportAndCheckOut => "Report and check out",
        _ => null
    };

    public bool IsFinished => Visit.Status is "Completed" or "Failed" or "Cancelled";

    // Runs the next step. Returns false when nothing was sent (busy, or no step available here).
    public async Task<bool> RunNextStepAsync(CancellationToken cancellationToken = default)
    {
        if (!CanRunNextStep) return false;
        var step = NextStep;
        IsBusy = true;
        Message = null;
        try
        {
            var result = step == VisitStep.StartTrip
                ? await _api.MarkEnRouteAsync(Visit.Id, cancellationToken)
                : await _api.MarkArrivedAsync(Visit.Id, cancellationToken);

            switch (result)
            {
                case VisitActionResult.Success:
                    Visit = Visit with { Status = step == VisitStep.StartTrip ? "EnRoute" : "Arrived" };
                    break;
                case VisitActionResult.InvalidState:
                case VisitActionResult.Conflict:
                    // Another device or a manager changed the visit, or an earlier request succeeded without a reply.
                    var reloaded = await ReloadAsync(cancellationToken);
                    Message = reloaded
                        ? "This visit was changed elsewhere. Its current status is shown."
                        : "This visit was changed elsewhere and could not be reloaded. Go back to the plan.";
                    break;
                case VisitActionResult.SessionExpired:
                    SessionExpired = true;
                    Message = "Your session has ended. Please log in again.";
                    break;
                case VisitActionResult.NetworkError:
                    Message = "No connection to the server. Nothing was changed on this screen; try again.";
                    break;
                case VisitActionResult.Forbidden:
                case VisitActionResult.NotFound:
                    Message = "This visit is no longer assigned to you.";
                    break;
                default:
                    Message = "The server could not record this step. Try again later.";
                    break;
            }
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            Message = "The server could not record this step. Try again later.";
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
}
