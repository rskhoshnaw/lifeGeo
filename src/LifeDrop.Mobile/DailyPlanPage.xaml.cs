using LifeDrop.Mobile.Localization;
using LifeDrop.Mobile.Visits;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace LifeDrop.Mobile;

// One row of the plan, already formatted for display.
public sealed record VisitRow(PlannedVisit Visit, string Customer, string Time, string Details, string Status);

// Daily plan (plan step 1.10, PRD §5.1): today's visits (Iraq date) from GET /visits. Loading, empty and
// error states come from DailyPlan; a failed refresh keeps the last list and says it may be out of date.
public partial class DailyPlanPage : ContentPage
{
    private DailyPlan? _plan;
    private IVisitsApiClient? _api;

    public DailyPlanPage()
    {
        InitializeComponent();
        FlowDirection = AppLanguage.Direction;
        Title = Texts.Get("app.plan.title");
        RetryBtn.Text = Texts.Get("app.tryAgain");
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        // Also after returning from a visit, so its new status shows.
        await LoadAsync();
    }

    private async void OnRefreshing(object? sender, EventArgs e)
    {
        await LoadAsync();
        Refresher.IsRefreshing = false;
    }

    private async void OnRetryClicked(object? sender, EventArgs e) => await LoadAsync();

    private async void OnVisitSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (e.CurrentSelection.FirstOrDefault() is not VisitRow row || _api is null) return;
        VisitList.SelectedItem = null;
        await Navigation.PushAsync(new VisitDetailsPage(new VisitLifecycle(_api, row.Visit, new MauiLocationSource(), outbox: TrackingHost.Outbox)));
    }

    private async Task LoadAsync()
    {
        if (_plan is null)
        {
            TrackingHost.GetOrCreate();
            _api = TrackingHost.VisitsApi;
            _plan = _api is null ? null : new DailyPlan(_api, TimeProvider.System, TrackingHost.Store);
        }
        // Steps saved offline go first, so the plan shows what the server now has.
        await TrackingHost.SyncQuietlyAsync();
        if (_plan is null)
        {
            ShowSessionEnded();
            return;
        }

        RetryBtn.IsEnabled = false;
        Spinner.IsVisible = Spinner.IsRunning = !Refresher.IsRefreshing;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await _plan.LoadAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            // State is unchanged; the user can retry.
        }
        finally
        {
            Spinner.IsVisible = Spinner.IsRunning = false;
            RetryBtn.IsEnabled = true;
        }
        Render();
    }

    private void Render()
    {
        var plan = _plan!;
        if (plan.Date != default) DateLabel.Text = Texts.Get("app.plan.today", ("date", plan.Date.ToString("dddd d MMMM yyyy", Texts.Culture)));
        VisitList.ItemsSource = plan.Visits.Select(ToRow).ToList();
        MessageLabel.Text = plan.Message;
        MessageLabel.IsVisible = plan.Message is not null;
        RetryBtn.IsVisible = plan.State == DailyPlanState.Error && plan.Error != PlanLoadStatus.SessionExpired;

        if (plan.Error == PlanLoadStatus.SessionExpired) ShowSessionEnded();
    }

    private void ShowSessionEnded()
    {
        MessageLabel.Text = Texts.Get("app.sessionEnded");
        MessageLabel.IsVisible = true;
        RetryBtn.IsVisible = false;
        AppLanguage.ShowRoot(new MainPage());
    }

    private static VisitRow ToRow(PlannedVisit v)
    {
        var customer = string.IsNullOrWhiteSpace(v.CustomerName) ? Texts.Get("app.customer") : v.CustomerName!;
        if (!string.IsNullOrWhiteSpace(v.CustomerCode)) customer += $" ({v.CustomerCode})";
        var details = DailyPlan.TypeLabel(v.VisitType);
        if (v.Priority == "high") details += " · " + Texts.Get("app.plan.highPriority");
        if (v.IsUnplanned) details += " · " + Texts.Get("app.plan.unplanned");
        // Steps saved on the phone move the status forward until the server has them.
        var outbox = TrackingHost.Outbox;
        var waiting = outbox?.HasPending(v.Id) == true;
        var status = waiting ? VisitLifecycle.LocalStatus(outbox!, v) : v.Status;
        return new VisitRow(v, customer, v.PlannedTime?.ToString("HH:mm") ?? Texts.Get("app.plan.anyTime"), details,
            DailyPlan.StatusLabel(status) + (waiting ? " · " + Texts.Get("app.plan.waiting") : ""));
    }
}
