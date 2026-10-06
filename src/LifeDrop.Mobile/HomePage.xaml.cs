using LifeDrop.Mobile.Localization;
using LifeDrop.Mobile.Auth;
using LifeDrop.Mobile.Auth.Models;
using LifeDrop.Mobile.Tracking;
using System.Threading.Tasks;
using System.Net.Http;
using System.Threading;
using System;

#if ANDROID
using LifeDrop.Mobile.Auth.Android;
#endif

namespace LifeDrop.Mobile;

public partial class HomePage : ContentPage
{
    public HomePage()
    {
        InitializeComponent();
        FlowDirection = AppLanguage.Direction;
        Title = Texts.Get("app.home.title");
        LanguageItem.Text = AppLanguage.CurrentName;
        WelcomeLabel.Text = Texts.Get("app.home.welcome");
        StatusLabel.Text = Texts.Get("app.login.signedIn");
        PlanBtn.Text = Texts.Get("app.home.plan");
        NewCustomerBtn.Text = Texts.Get("app.home.newCustomer");
        WarehouseBtn.Text = Texts.Get("app.home.warehouse");
        DeliveriesBtn.Text = Texts.Get("app.home.deliveries");
        PaymentsBtn.Text = Texts.Get("app.home.payments");
        LogoutBtn.Text = Texts.Get("app.home.logout");
        RenderShift();
    }

    private async void OnLanguageClicked(object? sender, EventArgs e) => await AppLanguage.ChooseAsync(this, () => new HomePage());

    private void SetBusy(bool isBusy)
    {
        LogoutBtn.IsEnabled = !isBusy;
        PlanBtn.IsEnabled = !isBusy;
        NewCustomerBtn.IsEnabled = !isBusy;
        WarehouseBtn.IsEnabled = !isBusy;
        DeliveriesBtn.IsEnabled = !isBusy;
        PaymentsBtn.IsEnabled = !isBusy;
        ShiftBtn.IsEnabled = !isBusy;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        // Continue a shift that is still open on the server (app restarted).
        var tracker = TrackingHost.GetOrCreate();
        if (tracker is not null && tracker.State == ShiftState.Off)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                if (await tracker.ResumeAsync(cts.Token) == ShiftCallStatus.Success && tracker.State == ShiftState.On) StartTrackingService();
                await TrackingHost.SyncQuietlyAsync();
            }
            catch (OperationCanceledException)
            {
            }
        }
        RenderShift();
    }

    // Start and end of the shift (plan step 1.13, decision D1): tracking runs only between the two.
    private async void OnShiftClicked(object? sender, EventArgs e)
    {
        var tracker = TrackingHost.GetOrCreate();
        if (tracker is null)
        {
            ShiftDetailLabel.Text = Texts.Get("app.home.trackingUnavailable");
            ShiftDetailLabel.IsVisible = true;
            return;
        }

        SetBusy(true);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            if (tracker.State == ShiftState.Off)
            {
                if (!await EnsureTrackingPermissionsAsync())
                {
                    ShiftDetailLabel.Text = Texts.Get("app.home.locationNeeded");
                    ShiftDetailLabel.IsVisible = true;
                    return;
                }
                var status = await tracker.StartAsync(cts.Token);
                if (status == ShiftCallStatus.Success) StartTrackingService();
                else ShowShiftError(status);
            }
            else
            {
                if (!await DisplayAlertAsync(Texts.Get("app.home.endShift"), Texts.Get("app.home.endShiftQuestion"), Texts.Get("app.home.endShift"), Texts.Get("app.cancel"))) return;
                var status = await tracker.EndAsync(cts.Token);
                if (status == ShiftCallStatus.Success) StopTrackingService();
                else ShowShiftError(status);
            }
        }
        catch (OperationCanceledException)
        {
            ShowShiftError(ShiftCallStatus.NetworkError);
        }
        finally
        {
            SetBusy(false);
            RenderShift();
        }
    }

    private void RenderShift()
    {
        var tracker = TrackingHost.Tracker;
        var on = tracker?.State == ShiftState.On;
        ShiftStatusLabel.Text = on ? Texts.Get("app.home.shiftOn", ("time", tracker!.Shift!.StartedAt.ToOffset(TimeSpan.FromHours(3)).ToString("HH:mm"))) : Texts.Get("app.home.shiftOff");
        ShiftBtn.Text = Texts.Get(on ? "app.home.endShift" : "app.home.startShift");
        if (tracker is not null && (on || tracker.QueuedPoints > 0))
        {
            // The four states are shown separately (plan step 1.14).
            var p = tracker.Counts;
            ShiftDetailLabel.Text = Texts.Get("app.home.points", ("local", p.Local), ("sent", p.Sent), ("accepted", p.Accepted), ("rejected", p.Rejected));
            ShiftDetailLabel.IsVisible = true;
        }
        if (TrackingHost.Outbox is { } outbox && outbox.Items.Count > 0)
        {
            var v = outbox.Counts;
            ShiftDetailLabel.Text = (ShiftDetailLabel.IsVisible ? ShiftDetailLabel.Text + "\n" : "") +
                Texts.Get("app.home.steps", ("local", v.Local), ("sent", v.Sent), ("accepted", v.Accepted), ("rejected", v.Rejected));
            ShiftDetailLabel.IsVisible = true;
        }
    }

    private void ShowShiftError(ShiftCallStatus status)
    {
        ShiftDetailLabel.Text = Texts.Get(status switch
        {
            ShiftCallStatus.DeviceRequired => "app.shift.deviceRequired",
            ShiftCallStatus.SessionExpired => "app.sessionEnded",
            ShiftCallStatus.NetworkError => "app.shift.network",
            _ => "app.shift.server"
        });
        ShiftDetailLabel.IsVisible = true;
    }

    private static async Task<bool> EnsureTrackingPermissionsAsync()
    {
        var location = await Permissions.CheckStatusAsync<Permissions.LocationWhenInUse>();
        if (location != PermissionStatus.Granted) location = await Permissions.RequestAsync<Permissions.LocationWhenInUse>();
        if (location != PermissionStatus.Granted) return false;
#if ANDROID
        // Android 13+: the tracking notification needs this; tracking itself works without it.
        if (OperatingSystem.IsAndroidVersionAtLeast(33) &&
            await Permissions.CheckStatusAsync<Permissions.PostNotifications>() != PermissionStatus.Granted)
            await Permissions.RequestAsync<Permissions.PostNotifications>();
#endif
        return true;
    }

    private static void StartTrackingService()
    {
#if ANDROID
        TrackingService.Start(Android.App.Application.Context);
#endif
    }

    private static void StopTrackingService()
    {
#if ANDROID
        TrackingService.Stop(Android.App.Application.Context);
#endif
    }

    private async void OnPlanClicked(object? sender, EventArgs e) => await Navigation.PushAsync(new DailyPlanPage());

    private async void OnWarehouseClicked(object? sender, EventArgs e) => await Navigation.PushAsync(new WarehouseOrdersPage());

    private async void OnDeliveriesClicked(object? sender, EventArgs e) => await Navigation.PushAsync(new DeliveriesPage());

    private async void OnPaymentsClicked(object? sender, EventArgs e) => await Navigation.PushAsync(new MyPaymentsPage());

    private async void OnNewCustomerClicked(object? sender, EventArgs e) => await Navigation.PushAsync(new NewCustomerPage());

    private async void OnLogoutClicked(object sender, EventArgs e)
    {
        // Steps or points not yet sent are lost at logout; say so first.
        var unsent = (TrackingHost.Outbox?.Counts is { } c ? c.Local + c.Sent : 0) + (TrackingHost.Tracker?.QueuedPoints ?? 0);
        var question = unsent > 0
            ? Texts.Get("app.home.logoutUnsent", ("count", unsent))
            : Texts.Get("app.home.logoutQuestion");
        bool confirm = await DisplayAlertAsync(Texts.Get("app.home.logout"), question, Texts.Get("app.yes"), Texts.Get("app.no"));
        if (!confirm) return;

        SetBusy(true);
        // An open shift is ended before logout; tracking never continues without a signed-in user.
        if (TrackingHost.Tracker is { State: ShiftState.On } tracker)
        {
            using var endCts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try { await tracker.EndAsync(endCts.Token); } catch (OperationCanceledException) { }
        }
        StopTrackingService();
        TrackingHost.Reset();
        StatusLabel.Text = Texts.Get("app.home.loggingOut");

        try
        {
            var url = Preferences.Get("ApiUrl", "");
            if (string.IsNullOrEmpty(url))
            {
                StatusLabel.Text = Texts.Get("app.home.urlMissing");
                SetBusy(false);
                return;
            }

            var client = new HttpClient { BaseAddress = new Uri(url) };
            var apiClient = new AuthApiClient(client);

#if ANDROID
            var store = new AndroidSecureCredentialStore(Android.App.Application.Context);
#else
            ISecureCredentialStore store = null!;
#endif
            var coord = new LogoutCoordinator(apiClient, store);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

            var result = await coord.LogoutAsync(cts.Token);
            if (result.Status == AuthStatus.Success)
            {
                // Return to login screen
                AppLanguage.ShowRoot(new MainPage());
            }
            else
            {
                StatusLabel.Text = Texts.Get("app.home.logoutFailed");
            }
        }
        catch (OperationCanceledException)
        {
            StatusLabel.Text = Texts.Get("app.timeout");
        }
        catch
        {
            StatusLabel.Text = Texts.Get("app.home.logoutFailed");
        }
        finally
        {
            SetBusy(false);
        }
    }
}
