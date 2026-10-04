using Android.App;
using Android.Content;
using Android.OS;
using Android.Widget;
using LifeDrop.Mobile.Auth;
using LifeDrop.Mobile.Auth.Android;
using LifeDrop.Mobile.Auth.Models;
using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace LifeDrop.Mobile.Auth.Android.RegistrationHarness;

[Activity(Label = "@string/app_name", MainLauncher = true, Exported = true)]
public class MainActivity : Activity
{
    private EditText? _apiAddressInput;
    private EditText? _tenantInput;
    private EditText? _emailInput;
    private EditText? _passwordInput;
    private EditText? _deviceLabelInput;
    private Button? _registerButton;
    private Button? _loginButton;
    private Button? _testRefreshButton;
    private Button? _logoutButton;
    private TextView? _statusText;

    private class TestTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _utcNow;
        public TestTimeProvider(DateTimeOffset utcNow) => _utcNow = utcNow;
        public override DateTimeOffset GetUtcNow() => _utcNow;
    }

    protected override async void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        var layout = new LinearLayout(this) { Orientation = Orientation.Vertical };
        layout.SetPadding(32, 32, 32, 32);

        _apiAddressInput = new EditText(this) { Hint = "API Address (e.g. http://127.0.0.1:5256)", Text = "http://127.0.0.1:5256" };
        _tenantInput = new EditText(this) { Hint = "Tenant Code" };
        _emailInput = new EditText(this) { Hint = "Email" };
        _passwordInput = new EditText(this) { Hint = "Password", InputType = global::Android.Text.InputTypes.TextVariationPassword | global::Android.Text.InputTypes.ClassText };
        _deviceLabelInput = new EditText(this) { Hint = "Device Label", Text = "My Test Device" };
        
        _registerButton = new Button(this) { Text = "Register Device" };
        _registerButton.Click += async (s, e) => await OnRegisterClicked();

        _loginButton = new Button(this) { Text = "Login" };
        _loginButton.Click += async (s, e) => await OnLoginClicked();

        _testRefreshButton = new Button(this) { Text = "Test Refresh Once" };
        _testRefreshButton.Click += async (s, e) => await OnTestRefreshClicked();

        _logoutButton = new Button(this) { Text = "Logout" };
        _logoutButton.Click += async (s, e) => await OnLogoutClicked();

        _statusText = new TextView(this) { Text = "Checking local storage...", TextSize = 16 };
        _statusText.SetPadding(0, 32, 0, 0);

        layout.AddView(_apiAddressInput);
        layout.AddView(_tenantInput);
        layout.AddView(_emailInput);
        layout.AddView(_passwordInput);
        layout.AddView(_deviceLabelInput);
        layout.AddView(_registerButton);
        layout.AddView(_loginButton);
        layout.AddView(_testRefreshButton);
        layout.AddView(_logoutButton);
        layout.AddView(_statusText);

        SetContentView(layout);

        await CheckExistingRegistrationAsync();
    }

    private void UpdateStatus(string message)
    {
        RunOnUiThread(() => _statusText!.Text = message);
        global::Android.Util.Log.Info("REGISTRATION_HARNESS", message);
    }

    private async Task CheckExistingRegistrationAsync()
    {
        try
        {
            var store = new AndroidSecureCredentialStore(this);
            var device = await store.LoadDeviceAsync();
            var session = await store.LoadSessionAsync();

            string statusMsg = "";
            if (device != null)
            {
                statusMsg += $"Already registered.\nDevice ID: {device.DeviceId}";
            }
            else
            {
                statusMsg += "No device registered. Please enter details.";
            }

            if (session != null)
            {
                statusMsg += $"\nSession: {session.Status}\nDevice Bound: {session.IsDeviceBound}";
            }
            else
            {
                statusMsg += "\nSession: None";
            }

            UpdateStatus(statusMsg);
        }
        catch
        {
            UpdateStatus("Error checking registration. Storage could not be read.");
        }
    }

    private string? GetInstallId()
    {
        var prefs = GetSharedPreferences("HarnessPrefs", FileCreationMode.Private);
        var installId = prefs!.GetString("InstallId", null);
        if (installId == null)
        {
            installId = Guid.NewGuid().ToString();
            if (!prefs.Edit()!.PutString("InstallId", installId)!.Commit())
            {
                return null;
            }
        }
        return installId;
    }

    private void SetButtonsEnabled(bool enabled)
    {
        if (_registerButton != null) _registerButton.Enabled = enabled;
        if (_loginButton != null) _loginButton.Enabled = enabled;
        if (_testRefreshButton != null) _testRefreshButton.Enabled = enabled;
        if (_logoutButton != null) _logoutButton.Enabled = enabled;
    }

    private async Task OnRegisterClicked()
    {
        SetButtonsEnabled(false);
        UpdateStatus("Registering...");

        try
        {
            var apiUrl = _apiAddressInput!.Text;
            var tenant = _tenantInput!.Text;
            var email = _emailInput!.Text;
            var password = _passwordInput!.Text;
            var label = _deviceLabelInput!.Text;

            var installId = GetInstallId();
            if (installId == null)
            {
                UpdateStatus("Failed to persist unique install ID locally. Cannot safely proceed.");
                return;
            }

            if (string.IsNullOrWhiteSpace(apiUrl) || string.IsNullOrWhiteSpace(tenant) || string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password) || string.IsNullOrWhiteSpace(label))
            {
                UpdateStatus("Please fill all required fields.");
                return;
            }

            var httpClient = new HttpClient { BaseAddress = new Uri(apiUrl) };
            var apiClient = new AuthApiClient(httpClient);
            var store = new AndroidSecureCredentialStore(this);
            var coordinator = new DeviceRegistrationCoordinator(apiClient, store);

            var request = new RegisterDeviceRequest(tenant, email, password, label, "android", installId, "1.0.0");

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var result = await coordinator.RegisterDeviceAsync(request, cts.Token);

            if (result.Status == AuthStatus.Success)
            {
                UpdateStatus($"SUCCESS!\nDevice ID: {result.Data!.DeviceId}");
            }
            else
            {
                UpdateStatus($"FAILED: {result.Status}\nStatus code or error encountered.");
            }
        }
        catch (global::System.OperationCanceledException)
        {
            UpdateStatus("ERROR: The operation timed out or was canceled.");
        }
        catch
        {
            UpdateStatus("ERROR: An unexpected failure occurred.");
        }
        finally
        {
            if (_passwordInput != null)
            {
                _passwordInput.Text = string.Empty;
            }
            SetButtonsEnabled(true);
        }
    }

    private async Task OnLoginClicked()
    {
        SetButtonsEnabled(false);
        UpdateStatus("Logging in...");

        try
        {
            var apiUrl = _apiAddressInput!.Text;
            var tenant = _tenantInput!.Text;
            var email = _emailInput!.Text;
            var password = _passwordInput!.Text;

            if (string.IsNullOrWhiteSpace(apiUrl) || string.IsNullOrWhiteSpace(tenant) || string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
            {
                UpdateStatus("Please fill tenant, email, and password.");
                return;
            }

            var httpClient = new HttpClient { BaseAddress = new Uri(apiUrl) };
            var apiClient = new AuthApiClient(httpClient);
            var store = new AndroidSecureCredentialStore(this);
            var coordinator = new LoginCoordinator(apiClient, store);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var result = await coordinator.LoginAsync(tenant, email, password, cts.Token);

            if (result.Status == AuthStatus.Success)
            {
                UpdateStatus($"LOGIN SUCCESS!\nSession Bound: {result.Data!.IsDeviceBound}");
            }
            else
            {
                UpdateStatus($"LOGIN FAILED: {result.Status}\nStatus code or error encountered.");
            }
        }
        catch (global::System.OperationCanceledException)
        {
            UpdateStatus("ERROR: The login operation timed out or was canceled.");
        }
        catch
        {
            UpdateStatus("ERROR: An unexpected failure occurred during login.");
        }
        finally
        {
            if (_passwordInput != null)
            {
                _passwordInput.Text = string.Empty;
            }
            SetButtonsEnabled(true);
        }
    }

    private async Task OnTestRefreshClicked()
    {
        SetButtonsEnabled(false);
        UpdateStatus("Testing refresh...");

        try
        {
            var store = new AndroidSecureCredentialStore(this);
            var session = await store.LoadSessionAsync();
            if (session == null || session.Status != SessionStatus.Active || !session.IsDeviceBound)
            {
                UpdateStatus("ERROR: Requires an active, device-bound session to test refresh.");
                return;
            }

            var oldAccessToken = session.Tokens.AccessToken;

            var apiUrl = _apiAddressInput!.Text;
            if (string.IsNullOrWhiteSpace(apiUrl))
            {
                UpdateStatus("API URL is required.");
                return;
            }

            var httpClient = new HttpClient { BaseAddress = new Uri(apiUrl) };
            var apiClient = new AuthApiClient(httpClient);

            // Time trick: Provide time exactly 15 seconds before expiry to trigger refresh mechanism
            var fakeTime = session.Tokens.ExpiresAt.AddSeconds(-15);
            var testTimeProvider = new TestTimeProvider(fakeTime);

            var authSession = new AuthSession(store, apiClient, testTimeProvider);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var result = await authSession.GetAccessTokenAsync(cts.Token);

            var newSession = await store.LoadSessionAsync();
            if (newSession == null)
            {
                UpdateStatus($"REFRESH FAILED: Result={result.Status}\nSession disappeared!");
                return;
            }

            bool tokenChanged = newSession.Tokens.AccessToken != oldAccessToken;
            bool attemptIdEmpty = newSession.AttemptId == Guid.Empty;
            bool isActive = newSession.Status == SessionStatus.Active;

            if (result.Status == AuthStatus.Success && tokenChanged && isActive && attemptIdEmpty)
            {
                UpdateStatus($"REFRESH SUCCESS!\nActive: {isActive}\nToken Changed: {tokenChanged}\nAttempt ID Empty: {attemptIdEmpty}");
            }
            else
            {
                UpdateStatus($"REFRESH INCOMPLETE/FAILED:\nResult: {result.Status}\nActive: {isActive}\nToken Changed: {tokenChanged}\nAttempt ID Empty: {attemptIdEmpty}");
            }
        }
        catch (global::System.OperationCanceledException)
        {
            UpdateStatus("ERROR: The refresh operation timed out or was canceled.");
        }
        catch
        {
            UpdateStatus("ERROR: An unexpected failure occurred during refresh test.");
        }
        finally
        {
            SetButtonsEnabled(true);
        }
    }

    private async Task OnLogoutClicked()
    {
        SetButtonsEnabled(false);
        UpdateStatus("Logging out...");

        try
        {
            var store = new AndroidSecureCredentialStore(this);
            var session = await store.LoadSessionAsync();
            
            if (session == null || session.Status != SessionStatus.Active)
            {
                UpdateStatus("ERROR: Requires an active session to log out.");
                return;
            }

            var apiUrl = _apiAddressInput!.Text;
            if (string.IsNullOrWhiteSpace(apiUrl))
            {
                UpdateStatus("API URL is required.");
                return;
            }

            var httpClient = new HttpClient { BaseAddress = new Uri(apiUrl) };
            var apiClient = new AuthApiClient(httpClient);
            var coordinator = new LogoutCoordinator(apiClient, store);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var result = await coordinator.LogoutAsync(cts.Token);

            if (result.Status == AuthStatus.Success)
            {
                var postSession = await store.LoadSessionAsync();
                var postDevice = await store.LoadDeviceAsync();
                
                bool sessionCleared = postSession == null;
                bool devicePreserved = postDevice != null;

                UpdateStatus($"LOGOUT SUCCESS!\nSession Cleared: {sessionCleared}\nDevice Intact: {devicePreserved}");
            }
            else
            {
                UpdateStatus($"LOGOUT FAILED: {result.Status}\nStatus code or error encountered.");
            }
        }
        catch (global::System.OperationCanceledException)
        {
            UpdateStatus("ERROR: The logout operation timed out or was canceled.");
        }
        catch
        {
            UpdateStatus("ERROR: An unexpected failure occurred during logout.");
        }
        finally
        {
            SetButtonsEnabled(true);
        }
    }
}