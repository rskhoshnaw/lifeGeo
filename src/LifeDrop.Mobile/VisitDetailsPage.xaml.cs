using LifeDrop.Mobile.Visits;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace LifeDrop.Mobile;

// Visit details and its next step (plan steps 1.11, 1.12; PRD §5.2, §5.3): start trip, arrive, check in with GPS,
// then the report and check-out. Every button is disabled while a request runs. A report that is not saved never
// starts check-out; when the report is saved but check-out is refused, only check-out is offered again.
public partial class VisitDetailsPage : ContentPage
{
    private static readonly (string Contract, string Label)[] OutcomeChoices =
    [
        ("products_presented", "Products presented"),
        ("order_intent", "Customer intends to order"),
        ("follow_up_needed", "Follow-up needed"),
        ("no_interest", "No interest"),
        ("other", "Other")
    ];

    private readonly VisitLifecycle _lifecycle;
    private bool _reportLoaded;

    public VisitDetailsPage(VisitLifecycle lifecycle)
    {
        InitializeComponent();
        _lifecycle = lifecycle;
        OutcomePicker.ItemsSource = OutcomeChoices.Select(c => c.Label).ToList();
        Render();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        // Send steps saved without internet, then show their new state.
        if (_lifecycle.WaitingToSend)
        {
            await TrackingHost.SyncQuietlyAsync();
            Render();
        }
        if (_reportLoaded || _lifecycle.NextStep != VisitStep.ReportAndCheckOut) return;
        _reportLoaded = true;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await _lifecycle.LoadReportAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Without the saved report the user simply saves it again.
        }
        if (_lifecycle.SavedReport is { } saved)
        {
            OutcomePicker.SelectedIndex = Array.FindIndex(OutcomeChoices, c => c.Contract == saved.Outcome);
            NoteEditor.Text = saved.Note;
        }
        Render();
        LeaveIfSessionEnded();
    }

    private async void OnStepClicked(object? sender, EventArgs e) => await RunAsync(ct => _lifecycle.RunNextStepAsync(ct));

    private async void OnSubmitClicked(object? sender, EventArgs e)
    {
        var outcome = OutcomePicker.SelectedIndex >= 0 ? OutcomeChoices[OutcomePicker.SelectedIndex].Contract : null;
        await RunAsync(ct => _lifecycle.SubmitReportAndCheckOutAsync(outcome, NoteEditor.Text, ct));
    }

    private async void OnRetryCheckOutClicked(object? sender, EventArgs e) => await RunAsync(ct => _lifecycle.RetryCheckOutAsync(ct));

    // GPS (up to 15 s) plus the request; the screen stays usable after a timeout.
    private async Task RunAsync(Func<CancellationToken, Task<bool>> action)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        var run = action(cts.Token);
        Render(); // shows the busy state at once
        var timedOut = false;
        try
        {
            await run;
        }
        catch (OperationCanceledException)
        {
            timedOut = true;
        }
        Render();
        if (timedOut)
        {
            MessageLabel.Text = "The server did not answer in time. Try again.";
            MessageLabel.IsVisible = true;
        }
        LeaveIfSessionEnded();
    }

    private void LeaveIfSessionEnded()
    {
        if (_lifecycle.SessionExpired)
            Application.Current!.Windows[0].Page = new NavigationPage(new MainPage());
    }

    // "Check-in: saved on the phone", "Report: accepted", ... for steps made without internet.
    private static string DescribeStep(LifeDrop.Mobile.Offline.OutboxItem step)
    {
        var name = step.Kind switch
        {
            LifeDrop.Mobile.Offline.OutboxKind.EnRoute => "Start trip",
            LifeDrop.Mobile.Offline.OutboxKind.Arrive => "Arrival",
            LifeDrop.Mobile.Offline.OutboxKind.CheckIn => "Check-in",
            LifeDrop.Mobile.Offline.OutboxKind.Report => "Report",
            _ => "Check-out"
        };
        var state = step.State switch
        {
            LifeDrop.Mobile.Offline.SyncState.Local => "saved on the phone",
            LifeDrop.Mobile.Offline.SyncState.Sent => "sent, waiting for the server",
            LifeDrop.Mobile.Offline.SyncState.Accepted => step.ReviewRequired ? "accepted, a manager will review it" : "accepted",
            _ => "rejected — " + VisitLifecycle.Describe(step.Answer ?? VisitActionResult.ServerError)
        };
        return $"{name}: {state}";
    }

    private void Render()
    {
        var visit = _lifecycle.Visit;
        var customer = string.IsNullOrWhiteSpace(visit.CustomerName) ? "Customer" : visit.CustomerName!;
        CustomerLabel.Text = string.IsNullOrWhiteSpace(visit.CustomerCode) ? customer : $"{customer} ({visit.CustomerCode})";
        DetailsLabel.Text = DailyPlan.TypeLabel(visit.VisitType) + (visit.Priority == "high" ? " · High priority" : "")
            + (visit.IsUnplanned ? " · Unplanned" : "");
        TimeLabel.Text = visit.PlannedTime is { } time ? $"Planned for {time:HH:mm}" : "Any time today";
        StatusLabel.Text = $"Status: {DailyPlan.StatusLabel(visit.Status)}";
        ReasonLabel.Text = visit.ExceptionReason is null ? null : $"Reason: {visit.ExceptionReason}";
        ReasonLabel.IsVisible = visit.ExceptionReason is not null;

        var singleStep = _lifecycle.NextStep is VisitStep.StartTrip or VisitStep.MarkArrived or VisitStep.CheckIn;
        StepBtn.Text = _lifecycle.NextStepLabel ?? "";
        StepBtn.IsVisible = singleStep;
        StepBtn.IsEnabled = _lifecycle.CanRunNextStep;

        var inVisit = _lifecycle.NextStep == VisitStep.ReportAndCheckOut;
        ReportForm.IsVisible = inVisit;
        OutcomePicker.IsEnabled = NoteEditor.IsEnabled = !_lifecycle.IsBusy;
        SubmitBtn.IsEnabled = _lifecycle.CanSubmitReport;
        ReportSavedLabel.IsVisible = _lifecycle.ReportSaved;
        RetryCheckOutBtn.IsVisible = _lifecycle.ReportSaved;
        RetryCheckOutBtn.IsEnabled = _lifecycle.CanRetryCheckOut;

        var steps = _lifecycle.PendingSteps;
        StepsLabel.Text = string.Join("\n", steps.Select(DescribeStep));
        StepsLabel.IsVisible = steps.Count > 0;

        Spinner.IsVisible = Spinner.IsRunning = _lifecycle.IsBusy;
        MessageLabel.Text = _lifecycle.Message;
        MessageLabel.IsVisible = _lifecycle.Message is not null;
    }
}
