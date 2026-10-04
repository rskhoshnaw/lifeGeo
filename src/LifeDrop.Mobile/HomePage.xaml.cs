using LifeDrop.Mobile.Auth;
using LifeDrop.Mobile.Auth.Models;
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
    }

    private async void OnLogoutClicked(object sender, EventArgs e)
    {
        bool confirm = await DisplayAlert("Logout", "Are you sure you want to log out?", "Yes", "No");
        if (!confirm) return;

        SetBusy(true);
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
