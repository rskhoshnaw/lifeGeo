using LifeDrop.Mobile.Localization;
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
    private static readonly string[] OutcomeChoices = ["products_presented", "order_intent", "order_placed", "follow_up_needed", "no_interest", "other"];

    private readonly VisitLifecycle _lifecycle;
    private bool _reportLoaded;

    public VisitDetailsPage(VisitLifecycle lifecycle)
    {
        InitializeComponent();
        _lifecycle = lifecycle;
        FlowDirection = AppLanguage.Direction;
        Title = Texts.Get("app.visit.title");
        CustomerBtn.Text = Texts.Get("app.customer.details");
        OrderBtn.Text = Texts.Get("app.order.button");
        FormBtn.Text = Texts.Get("app.form.button");
        CustomBtn.Text = Texts.Get("app.custom.button");
        ReportTitleLabel.Text = Texts.Get("app.visit.report");
        OutcomePicker.Title = Texts.Get("app.visit.result");
        NoteEditor.Placeholder = Texts.Get("app.visit.notePlaceholder");
        ReportSavedLabel.Text = Texts.Get("app.visit.reportSaved");
        SubmitBtn.Text = Texts.Get("app.visit.submit");
        RetryCheckOutBtn.Text = Texts.Get("app.visit.retryCheckOut");
        OutcomePicker.ItemsSource = OutcomeChoices.Select(c => Texts.Get("app.outcome." + c)).ToList();
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
            OutcomePicker.SelectedIndex = Array.IndexOf(OutcomeChoices, saved.Outcome);
            NoteEditor.Text = saved.Note;
        }
        Render();
        LeaveIfSessionEnded();
    }

    private async void OnCustomerClicked(object? sender, EventArgs e) =>
        await Navigation.PushAsync(new CustomerPage(_lifecycle.Visit.CustomerId, _lifecycle.Visit.CustomerName));

    private async void OnOrderClicked(object? sender, EventArgs e) =>
        await Navigation.PushAsync(new OrderPage(_lifecycle.Visit.CustomerId, _lifecycle.Visit.CustomerName, _lifecycle.Visit.Id));

    private async void OnFormClicked(object? sender, EventArgs e) =>
        await Navigation.PushAsync(new VisitFormPage(_lifecycle.Visit.Id, _lifecycle.Visit.CustomerName, _lifecycle.Visit.VisitType));

    private async void OnCustomClicked(object? sender, EventArgs e) =>
        await Navigation.PushAsync(new CustomFieldsPage(_lifecycle.Visit.Id, _lifecycle.Visit.CustomerName, _lifecycle.Visit.VisitType));

    private async void OnStepClicked(object? sender, EventArgs e) => await RunAsync(ct => _lifecycle.RunNextStepAsync(ct));

    private async void OnSubmitClicked(object? sender, EventArgs e)
    {
        var outcome = OutcomePicker.SelectedIndex >= 0 ? OutcomeChoices[OutcomePicker.SelectedIndex] : null;
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
            MessageLabel.Text = Texts.Get("app.timeout");
            MessageLabel.IsVisible = true;
        }
        LeaveIfSessionEnded();
    }

    private void LeaveIfSessionEnded()
    {
        if (_lifecycle.SessionExpired)
            AppLanguage.ShowRoot(new MainPage());
    }

    // "Check-in: saved on the phone", "Report: accepted", ... for steps made without internet.
    private static string DescribeStep(LifeDrop.Mobile.Offline.OutboxItem step)
    {
        var name = Texts.Get(step.Kind switch
        {
            LifeDrop.Mobile.Offline.OutboxKind.EnRoute => "app.step.EnRoute",
            LifeDrop.Mobile.Offline.OutboxKind.Arrive => "app.step.Arrive",
            LifeDrop.Mobile.Offline.OutboxKind.CheckIn => "app.step.CheckIn",
            LifeDrop.Mobile.Offline.OutboxKind.Report => "app.step.Report",
            _ => "app.step.CheckOut"
        });
        var state = step.State switch
        {
            LifeDrop.Mobile.Offline.SyncState.Local => Texts.Get("app.stepState.local"),
            LifeDrop.Mobile.Offline.SyncState.Sent => Texts.Get("app.stepState.sent"),
            LifeDrop.Mobile.Offline.SyncState.Accepted => Texts.Get(step.ReviewRequired ? "app.stepState.acceptedReview" : "app.stepState.accepted"),
            _ => Texts.Get("app.stepState.rejected", ("reason", VisitLifecycle.Describe(step.Answer ?? VisitActionResult.ServerError)))
        };
        return $"{name}: {state}";
    }

    private void Render()
    {
        var visit = _lifecycle.Visit;
        var customer = string.IsNullOrWhiteSpace(visit.CustomerName) ? Texts.Get("app.customer") : visit.CustomerName!;
        CustomerLabel.Text = string.IsNullOrWhiteSpace(visit.CustomerCode) ? customer : $"{customer} ({visit.CustomerCode})";
        DetailsLabel.Text = DailyPlan.TypeLabel(visit.VisitType) + (visit.Priority == "high" ? " · " + Texts.Get("app.plan.highPriority") : "")
            + (visit.IsUnplanned ? " · " + Texts.Get("app.plan.unplanned") : "");
        TimeLabel.Text = visit.PlannedTime is { } time ? Texts.Get("app.visit.plannedFor", ("time", time.ToString("HH:mm"))) : Texts.Get("app.visit.anyTimeToday");
        StatusLabel.Text = Texts.Get("app.visit.status", ("status", DailyPlan.StatusLabel(visit.Status)));
        ReasonLabel.Text = visit.ExceptionReason is null ? null : Texts.Get("app.visit.reason", ("reason", visit.ExceptionReason));
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
