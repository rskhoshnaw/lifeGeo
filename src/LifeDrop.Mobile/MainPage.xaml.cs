using LifeDrop.Mobile.Localization;
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
        FlowDirection = AppLanguage.Direction;
        LanguageItem.Text = AppLanguage.CurrentName;
        ApiUrlEntry.Placeholder = Texts.Get("app.login.apiUrl");
        TenantEntry.Placeholder = Texts.Get("app.login.tenant");
        // A pasted link or deep-link into the company field is replaced by its code (plan step 4.7).
        TenantEntry.TextChanged += (_, e) =>
        {
            if (CompanyJoinLink.LooksLikeLink(e.NewTextValue) && CompanyJoinLink.TryGetCode(e.NewTextValue) is { } code) TenantEntry.Text = code;
        };
        QrScanBtn.Text = Texts.Get("app.login.scanQr");
        EmailEntry.Placeholder = Texts.Get("app.login.email");
        PasswordEntry.Placeholder = Texts.Get("app.login.password");
        DeviceLabelEntry.Placeholder = Texts.Get("app.login.deviceLabel");
        RegisterBtn.Text = Texts.Get("app.login.register");
        LoginBtn.Text = Texts.Get("app.login.submit");
        StatusLabel.Text = Texts.Get("app.login.checking");
        SupportLabel.Text = Texts.Get("app.support", ("phone", AppConfig.SupportPhone));

#if !DEBUG
        // Completely hide the URL field in Release
        ApiUrlEntry.IsVisible = false;
        ApiUrlEntry.Text = ""; 
#else
        ApiUrlEntry.Text = Preferences.Get("ApiUrl", "http://127.0.0.1:5256");
#endif
    }

    private async void OnLanguageClicked(object? sender, EventArgs e) => await AppLanguage.ChooseAsync(this, () => new MainPage());

    // Login and registration results in the user's language.
    private static string Describe(AuthStatus status) =>
        status == AuthStatus.Success ? "" : Texts.Get("app.auth." + status);

    // Called by MainActivity when the app is opened via a lifedrop:// deep link (plan step 4.7).
    public void ApplyJoinCode(string code) => TenantEntry.Text = code;

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        var pending = AppConfig.ConsumePendingJoinCode();
        if (pending is not null) TenantEntry.Text = pending;
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
#elif IOS
            ISecureCredentialStore store = new IosSecureCredentialStore();
#else
            ISecureCredentialStore store = null!;
#endif

            if (store == null)
            {
                StatusLabel.Text = Texts.Get("app.login.storageMissing");
                return;
            }

            var device = await store.LoadDeviceAsync();
            var session = await store.LoadSessionAsync();

            StatusLabel.Text = (device != null ? Texts.Get("app.login.registered", ("id", device.DeviceId)) : Texts.Get("app.login.notRegistered"))
                + "\n" + Texts.Get(session != null ? "app.login.signedIn" : "app.login.notSignedIn");
        }
        catch
        {
            StatusLabel.Text = Texts.Get("app.login.storageError");
        }
    }

    private void SetBusy(bool isBusy)
    {
        RegisterBtn.IsEnabled = !isBusy;
        LoginBtn.IsEnabled = !isBusy;
    }

    // Company code -> the company's own API address (plan step 4.2). Nothing about a company is built into the app: the bootstrap server
    // answers for the code, and the answer is kept on the phone. Changing to another company wipes the old company's session and offline data first.
    private async Task<(AuthApiClient? Client, ISecureCredentialStore? Store)> SetupServicesAsync(CancellationToken cancellationToken)
    {
#if ANDROID
        var store = new AndroidSecureCredentialStore(Android.App.Application.Context);
#elif IOS
        ISecureCredentialStore store = new IosSecureCredentialStore();
#else
        ISecureCredentialStore store = null!;
#endif
        var fallback = ApiUrlEntry.IsVisible && !string.IsNullOrWhiteSpace(ApiUrlEntry.Text) ? ApiUrlEntry.Text.Trim() : AppConfig.BootstrapUrl;
        // A development address typed into the debug field wins; otherwise the company code names the company's own server.
        var bootstrap = ApiUrlEntry.IsVisible && !string.IsNullOrWhiteSpace(ApiUrlEntry.Text) ? fallback : CompanyDirectory.ServerFor(TenantEntry.Text, AppConfig.BaseDomain, fallback);
#if DEBUG
        const bool allowInsecure = true;
#else
        const bool allowInsecure = false;
#endif
        using var lookupHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var lookup = await CompanyDirectory.ResolveAsync(lookupHttp, bootstrap, TenantEntry.Text, allowInsecure, cancellationToken);
        if (lookup.Status != CompanyLookupStatus.Found || lookup.Config is null)
        {
            StatusLabel.Text = lookup.Status switch
            {
                CompanyLookupStatus.InvalidCode => Texts.Get("app.login.companyInvalid"),
                CompanyLookupStatus.NotFound => Texts.Get("app.login.companyNotFound"),
                CompanyLookupStatus.NotConfigured => Texts.Get("app.login.notConfigured"),
                CompanyLookupStatus.InsecureAddress => Texts.Get("app.login.httpNotAllowed"),
                _ => Texts.Get("app.login.companyUnavailable") + (string.IsNullOrEmpty(lookup.Detail) ? "" : "\n(" + lookup.Detail + ")"),
            };
            return (null, null);
        }

        var config = lookup.Config;
        if (CompanyDirectory.IsCompanyChange(Preferences.Get("CompanyCode", (string?)null), config.TenantCode))
        {
            await store.ClearAllAsync(cancellationToken);
            TrackingHost.Reset();
        }
        Preferences.Set("CompanyCode", config.TenantCode);
        Preferences.Set("CompanyName", config.Name);
        Preferences.Set("ApiUrl", config.ApiBaseUrl);
        if (config.SupportPhone is { } phone) Preferences.Set("SupportPhone", phone);
        Preferences.Set("Industry", config.Industry);
        Texts.Profile = config.Industry == "food" ? "food" : "";

        return (new AuthApiClient(new HttpClient { BaseAddress = new Uri(config.ApiBaseUrl) }), store);
    }

    private async void OnQrScanClicked(object sender, EventArgs e)
    {
        var status = await Permissions.CheckStatusAsync<Permissions.Camera>();
        if (status != PermissionStatus.Granted)
            status = await Permissions.RequestAsync<Permissions.Camera>();
        if (status != PermissionStatus.Granted) return;
        await Navigation.PushAsync(new QrScanPage(code => TenantEntry.Text = code));
    }

    private async void OnRegisterClicked(object sender, EventArgs e)
    {
        SetBusy(true);
        StatusLabel.Text = Texts.Get("app.login.registering");

        try
        {
            using var lookupCts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var (apiClient, store) = await SetupServicesAsync(lookupCts.Token);
            if (apiClient == null || store == null) return;

            var installId = GetInstallId();
            var req = new RegisterDeviceRequest(
                TenantEntry.Text ?? "",
                EmailEntry.Text ?? "",
                PasswordEntry.Text ?? "",
                DeviceLabelEntry.Text ?? "",
                DeviceInfo.Platform == DevicePlatform.iOS ? "ios" : "android",
                installId,
                "1.0.0");

            var coord = new DeviceRegistrationCoordinator(apiClient, store);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

            var result = await coord.RegisterDeviceAsync(req, cts.Token);
            if (result.Status == AuthStatus.Success)
            {
                StatusLabel.Text = Texts.Get("app.login.registerDone");
            }
            else
            {
                StatusLabel.Text = Describe(result.Status);
            }
        }
        catch (OperationCanceledException)
        {
            StatusLabel.Text = Texts.Get("app.timeout");
        }
        catch
        {
            StatusLabel.Text = Texts.Get("app.unexpected");
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
        StatusLabel.Text = Texts.Get("app.login.loggingIn");

        try
        {
            using var lookupCts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var (apiClient, store) = await SetupServicesAsync(lookupCts.Token);
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
                // Navigate to HomePage
                AppLanguage.ShowRoot(new HomePage());
            }
            else
            {
                StatusLabel.Text = Describe(result.Status);
            }
        }
        catch (OperationCanceledException)
        {
            StatusLabel.Text = Texts.Get("app.timeout");
        }
        catch
        {
            StatusLabel.Text = Texts.Get("app.unexpected");
        }
        finally
        {
            PasswordEntry.Text = string.Empty;
            SetBusy(false);
        }
    }
}
