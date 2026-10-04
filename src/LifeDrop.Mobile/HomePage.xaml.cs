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
    }

    private void SetBusy(bool isBusy)
    {
        LogoutBtn.IsEnabled = !isBusy;
        PlanBtn.IsEnabled = !isBusy;
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
            ShiftDetailLabel.Text = "Tracking is not available on this device.";
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
                    ShiftDetailLabel.Text = "Location permission is needed to start a shift.";
                    ShiftDetailLabel.IsVisible = true;
                    return;
                }
                var status = await tracker.StartAsync(cts.Token);
                if (status == ShiftCallStatus.Success) StartTrackingService();
                else ShowShiftError(status);
            }
            else
            {
                if (!await DisplayAlertAsync("End shift", "Stop tracking and end your shift?", "End shift", "Cancel")) return;
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
        ShiftStatusLabel.Text = on ? $"Shift: on since {tracker!.Shift!.StartedAt.ToOffset(TimeSpan.FromHours(3)):HH:mm}" : "Shift: off";
        ShiftBtn.Text = on ? "End shift" : "Start shift";
        if (tracker is not null && (on || tracker.QueuedPoints > 0))
        {
            // The four states are shown separately (plan step 1.14).
            var p = tracker.Counts;
            ShiftDetailLabel.Text = $"Points — on the phone: {p.Local} · sent: {p.Sent} · accepted: {p.Accepted} · rejected: {p.Rejected}";
            ShiftDetailLabel.IsVisible = true;
        }
        if (TrackingHost.Outbox is { } outbox && outbox.Items.Count > 0)
        {
            var v = outbox.Counts;
            ShiftDetailLabel.Text = (ShiftDetailLabel.IsVisible ? ShiftDetailLabel.Text + "\n" : "") +
                $"Visit steps — on the phone: {v.Local} · sent: {v.Sent} · accepted: {v.Accepted} · rejected: {v.Rejected}";
            ShiftDetailLabel.IsVisible = true;
        }
    }

    private void ShowShiftError(ShiftCallStatus status)
    {
        ShiftDetailLabel.Text = status switch
        {
            ShiftCallStatus.DeviceRequired => "This phone is not an approved device for your account.",
            ShiftCallStatus.SessionExpired => "Your session has ended. Please log in again.",
            ShiftCallStatus.NetworkError => "No connection to the server. Try again.",
            _ => "The server could not change the shift. Try again later."
        };
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

    private async void OnLogoutClicked(object sender, EventArgs e)
    {
        // Steps or points not yet sent are lost at logout; say so first.
        var unsent = (TrackingHost.Outbox?.Counts is { } c ? c.Local + c.Sent : 0) + (TrackingHost.Tracker?.QueuedPoints ?? 0);
        var question = unsent > 0
            ? $"{unsent} items saved on this phone have not reached the server yet and will be lost. Log out anyway?"
            : "Are you sure you want to log out?";
        bool confirm = await DisplayAlert("Logout", question, "Yes", "No");
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
        StatusLabel.Text = "Logging out...";

        try
        {
            var url = Preferences.Get("ApiUrl", "");
            if (string.IsNullOrEmpty(url))
            {
                StatusLabel.Text = "API URL not found.";
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
                Application.Current!.Windows[0].Page = new NavigationPage(new MainPage());
            }
            else
            {
                StatusLabel.Text = $"LOGOUT FAILED: {result.Status}";
            }
        }
        catch (OperationCanceledException)
        {
            StatusLabel.Text = "Timeout or canceled.";
        }
        catch
        {
            StatusLabel.Text = "Unexpected error during logout.";
        }
        finally
        {
            SetBusy(false);
        }
    }
}
