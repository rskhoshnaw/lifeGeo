using LifeDrop.Mobile.Auth;
using LifeDrop.Mobile.Visits;
using System;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

#if ANDROID
using LifeDrop.Mobile.Auth.Android;
#endif

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
        await Navigation.PushAsync(new VisitDetailsPage(new VisitLifecycle(_api, row.Visit, new MauiLocationSource())));
    }

    private async Task LoadAsync()
    {
        if (_plan is null)
        {
            _api = CreateApi();
            _plan = _api is null ? null : new DailyPlan(_api, TimeProvider.System);
        }
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
        if (plan.Date != default) DateLabel.Text = $"Today, {plan.Date:dddd d MMMM yyyy}";
        VisitList.ItemsSource = plan.Visits.Select(ToRow).ToList();
        MessageLabel.Text = plan.Message;
        MessageLabel.IsVisible = plan.Message is not null;
        RetryBtn.IsVisible = plan.State == DailyPlanState.Error && plan.Error != PlanLoadStatus.SessionExpired;

        if (plan.Error == PlanLoadStatus.SessionExpired) ShowSessionEnded();
    }

    private void ShowSessionEnded()
    {
        MessageLabel.Text = "Your session has ended. Please log in again.";
        MessageLabel.IsVisible = true;
        RetryBtn.IsVisible = false;
        Application.Current!.Windows[0].Page = new NavigationPage(new MainPage());
    }

    private static VisitRow ToRow(PlannedVisit v)
    {
        var customer = string.IsNullOrWhiteSpace(v.CustomerName) ? "Customer" : v.CustomerName!;
        if (!string.IsNullOrWhiteSpace(v.CustomerCode)) customer += $" ({v.CustomerCode})";
        var details = DailyPlan.TypeLabel(v.VisitType);
        if (v.Priority == "high") details += " · High priority";
        if (v.IsUnplanned) details += " · Unplanned";
        return new VisitRow(v, customer, v.PlannedTime?.ToString("HH:mm") ?? "Any time", details, DailyPlan.StatusLabel(v.Status));
    }

    private static IVisitsApiClient? CreateApi()
    {
        var url = Preferences.Get("ApiUrl", "");
        if (string.IsNullOrEmpty(url)) return null;
#if ANDROID
        var store = new AndroidSecureCredentialStore(Android.App.Application.Context);
        var http = new HttpClient { BaseAddress = new Uri(url), Timeout = TimeSpan.FromSeconds(15) };
        var session = new AuthSession(store, new AuthApiClient(http), TimeProvider.System);
        return new VisitsApiClient(http, session);
#else
        return null;
#endif
    }
}
