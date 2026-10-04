using LifeDrop.Mobile.Auth;
using LifeDrop.Mobile.Auth.Models;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System;

#if ANDROID
using LifeDrop.Mobile.Auth.Android;
#endif

namespace LifeDrop.Mobile;

public partial class MainPage : ContentPage
{
    public MainPage()
    {
        InitializeComponent();

#if !DEBUG
        // Completely hide the URL field in Release
        ApiUrlEntry.IsVisible = false;
        ApiUrlEntry.Text = ""; 
#else
        ApiUrlEntry.Text = Preferences.Get("ApiUrl", "http://127.0.0.1:5256");
#endif
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await CheckStatusAsync();
    }

    private string GetInstallId()
    {
        var id = Preferences.Get("AppInstallId", (string?)null);
        if (string.IsNullOrEmpty(id))
        {
            id = Guid.NewGuid().ToString();
            Preferences.Set("AppInstallId", id);
        }
        return id!;
    }

    private async Task CheckStatusAsync()
    {
        try
        {
#if ANDROID
            var store = new AndroidSecureCredentialStore(Android.App.Application.Context);
#else
            ISecureCredentialStore store = null!; // Stubs for other platforms not yet ready
#endif

            if (store == null)
            {
                StatusLabel.Text = "Platform storage not configured.";
                return;
            }

            var device = await store.LoadDeviceAsync();
            var session = await store.LoadSessionAsync();

            string status = "";
            if (device != null)
                status += $"Registered. ID: {device.DeviceId}\n";
            else
                status += "Not Registered.\n";

            if (session != null)
                status += $"Session: {session.Status} (Bound: {session.IsDeviceBound})";
            else
                status += "Session: None";

            StatusLabel.Text = status;
        }
        catch
        {
            StatusLabel.Text = "Status: Storage error.";
        }
    }

    private void SetBusy(bool isBusy)
    {
        RegisterBtn.IsEnabled = !isBusy;
        LoginBtn.IsEnabled = !isBusy;
    }

    private (AuthApiClient? Client, ISecureCredentialStore? Store) SetupServices()
    {
        var url = ApiUrlEntry.Text;
        if (string.IsNullOrWhiteSpace(url))
        {
            StatusLabel.Text = "API URL required.";
            return (null, null);
        }

#if !DEBUG
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            StatusLabel.Text = "HTTP is not allowed in Release.";
            return (null, null);
        }
#endif

        var client = new HttpClient { BaseAddress = new Uri(url) };
        var apiClient = new AuthApiClient(client);

        Preferences.Set("ApiUrl", url);

#if ANDROID
        var store = new AndroidSecureCredentialStore(Android.App.Application.Context);
#else
        ISecureCredentialStore store = null!;
#endif

        return (apiClient, store);
    }

    private async void OnRegisterClicked(object sender, EventArgs e)
    {
        SetBusy(true);
        StatusLabel.Text = "Registering...";

        try
        {
            var (apiClient, store) = SetupServices();
            if (apiClient == null || store == null) return;

            var installId = GetInstallId();
            var req = new RegisterDeviceRequest(
                TenantEntry.Text ?? "",
                EmailEntry.Text ?? "",
                PasswordEntry.Text ?? "",
                DeviceLabelEntry.Text ?? "",
                "android",
                installId,
                "1.0.0");

            var coord = new DeviceRegistrationCoordinator(apiClient, store);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

            var result = await coord.RegisterDeviceAsync(req, cts.Token);
            if (result.Status == AuthStatus.Success)
            {
                StatusLabel.Text = $"REGISTER SUCCESS!\nNew Device ID: {result.Data!.DeviceId}\nPending Admin Approval.";
            }
            else
            {
                StatusLabel.Text = $"REGISTER FAILED: {result.Status}";
            }
        }
        catch (OperationCanceledException)
        {
            StatusLabel.Text = "Timeout or canceled.";
        }
        catch
        {
            StatusLabel.Text = "Unexpected error.";
        }
        finally
        {
            PasswordEntry.Text = string.Empty;
            SetBusy(false);
        }
    }

    private async void OnLoginClicked(object sender, EventArgs e)
    {
        SetBusy(true);
        StatusLabel.Text = "Logging in...";

        try
        {
            var (apiClient, store) = SetupServices();
            if (apiClient == null || store == null) return;

            var coord = new LoginCoordinator(apiClient, store);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

            var result = await coord.LoginAsync(
                TenantEntry.Text ?? "",
                EmailEntry.Text ?? "",
                PasswordEntry.Text ?? "",
                cts.Token);

            if (result.Status == AuthStatus.Success)
            {
                StatusLabel.Text = $"LOGIN SUCCESS!\nDevice Bound: {result.Data!.IsDeviceBound}";
                // Navigate to HomePage
                Application.Current!.Windows[0].Page = new NavigationPage(new HomePage());
            }
            else
            {
                StatusLabel.Text = $"LOGIN FAILED: {result.Status}";
            }
        }
        catch (OperationCanceledException)
        {
            StatusLabel.Text = "Timeout or canceled.";
        }
        catch
        {
            StatusLabel.Text = "Unexpected error.";
        }
        finally
        {
            PasswordEntry.Text = string.Empty;
            SetBusy(false);
        }
    }
}
