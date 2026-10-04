using LifeDrop.Mobile.Visits;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace LifeDrop.Mobile;

// Visit details and its next step (plan step 1.11, PRD §5.2): only "Start trip" (Planned) and "I have arrived"
// (EnRoute) run here; the button is disabled while the request runs. Check-in, report and check-out come with
// step 1.12.
public partial class VisitDetailsPage : ContentPage
{
    private readonly VisitLifecycle _lifecycle;

    public VisitDetailsPage(VisitLifecycle lifecycle)
    {
        InitializeComponent();
        _lifecycle = lifecycle;
        Render();
    }

    private async void OnStepClicked(object? sender, EventArgs e)
    {
        if (!_lifecycle.CanRunNextStep) return;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var run = _lifecycle.RunNextStepAsync(cts.Token);
        Render(); // shows the busy state at once
        var timedOut = false;
        try
        {
            await run;
        }
        catch (OperationCanceledException)
        {
            timedOut = true; // the status shown is unchanged and the user can retry
        }
        Render();
        if (timedOut)
        {
            MessageLabel.Text = "The server did not answer in time. Try again.";
            MessageLabel.IsVisible = true;
        }

        if (_lifecycle.SessionExpired)
            Application.Current!.Windows[0].Page = new NavigationPage(new MainPage());
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

        StepBtn.Text = _lifecycle.NextStepLabel ?? "";
        StepBtn.IsVisible = _lifecycle.NextStepLabel is not null;
        StepBtn.IsEnabled = _lifecycle.CanRunNextStep;
        Spinner.IsVisible = Spinner.IsRunning = _lifecycle.IsBusy;

        var later = _lifecycle.NextStep is VisitStep.CheckIn or VisitStep.ReportAndCheckOut;
        NoteLabel.Text = later ? "Check-in, report and check-out with GPS will be available in the next app version." : null;
        NoteLabel.IsVisible = later;

        MessageLabel.Text = _lifecycle.Message;
        MessageLabel.IsVisible = _lifecycle.Message is not null;
    }
}
