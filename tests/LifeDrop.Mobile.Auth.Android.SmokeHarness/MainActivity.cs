using Android.App;
using Android.Content;
using Android.OS;
using Android.Widget;
using LifeDrop.Mobile.Auth.Models;
using LifeDrop.Mobile.Auth.Android;
using System;
using System.Threading.Tasks;

namespace LifeDrop.Mobile.Auth.Android.SmokeHarness;

[Activity(Label = "@string/app_name", MainLauncher = true, Exported = true)]
public class MainActivity : Activity
{
    private TextView? _statusText;

    protected override async void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        var layout = new LinearLayout(this) { Orientation = Orientation.Vertical };
        layout.SetPadding(32, 32, 32, 32);
        _statusText = new TextView(this) { Text = "Initializing...", TextSize = 16 };
        layout.AddView(_statusText);
        SetContentView(layout);

        try
        {
            await RunSmokeHarnessAsync();
        }
        catch (Exception ex)
        {
            UpdateStatus($"FATAL ERROR: {ex.Message}");
        }
    }

    private void UpdateStatus(string message)
    {
        RunOnUiThread(() => _statusText!.Text = message);
        global::Android.Util.Log.Info("SMOKE_HARNESS", message);
    }

    private async Task RunSmokeHarnessAsync()
    {
        var testPrefs = GetSharedPreferences("SmokeHarnessState", FileCreationMode.Private);
        int currentStep = testPrefs!.GetInt("TestStep", 0);
        int lastPid = testPrefs.GetInt("LastPid", -1);
        int myPid = global::Android.OS.Process.MyPid();

        // Production constructor uses the real AndroidKeyStore and SharedPreferences
        var store = new AndroidSecureCredentialStore(this);

        if (currentStep == 0)
        {
            UpdateStatus("Running Step 1...");
            
            // 1a. Save Device Registration
            var device = new RegisteredDevice(Guid.NewGuid(), "synthetic_secret", "tenant", "test@test.com");
            await store.SaveDeviceAsync(device);

            // 1b. Save Active Session
            var activeSession = new StoredSession(
                new AuthTokens("access_old", "refresh_old", "Bearer", DateTime.UtcNow.AddMinutes(15), DateTime.UtcNow.AddDays(7)),
                "tenant", "test@test.com", false, null, SessionStatus.Active, Guid.NewGuid(), Guid.Empty
            );
            await store.SaveSessionAsync(activeSession);

            // 1c. Mark RefreshPending
            var attemptId = Guid.NewGuid();
            var pendingSession = activeSession with { Status = SessionStatus.RefreshPending, AttemptId = attemptId };
            bool marked = await store.MarkSessionRefreshingAsync(activeSession, pendingSession);

            if (!marked)
            {
                UpdateStatus("FAIL: MarkSessionRefreshingAsync returned false.");
                return;
            }

            // Save test state for next run
            bool committedState = testPrefs.Edit()!
                .PutInt("TestStep", 1)!
                .PutString("ExpectedSessionId", pendingSession.SessionId.ToString())!
                .PutString("ExpectedAttemptId", attemptId.ToString())!
                .PutInt("LastPid", myPid)!
                .Commit();

            if (!committedState)
            {
                UpdateStatus("FAIL: Failed to commit SmokeHarnessState for Step 1.");
                return;
            }

            UpdateStatus($"PASS: Saved Session and marked RefreshPending.\n\nACTION REQUIRED: Force-stop the app and relaunch it.");
        }
        else if (currentStep == 1)
        {
            if (lastPid == myPid)
            {
                UpdateStatus("Instruction: Please force-stop and relaunch the app. Same process ID detected, which does not test persistence across process restart.");
                return;
            }

            UpdateStatus("Running Step 2...");
            
            var expectedSessionId = Guid.Parse(testPrefs.GetString("ExpectedSessionId", "")!);
            var expectedAttemptId = Guid.Parse(testPrefs.GetString("ExpectedAttemptId", "")!);

            // 2a. Verify Recovery
            var loaded = await store.LoadSessionAsync();
            if (loaded == null)
            {
                UpdateStatus("FAIL: Failed to recover session after restart.");
                return;
            }
            if (loaded.SessionId != expectedSessionId || loaded.AttemptId != expectedAttemptId || loaded.Status != SessionStatus.RefreshPending)
            {
                UpdateStatus($"FAIL: Recovered state mismatch.\nExpected: {expectedSessionId}, {expectedAttemptId}, RefreshPending\nActual: {loaded.SessionId}, {loaded.AttemptId}, {loaded.Status}");
                return;
            }

            // 2b. Commit New Active Token Pair
            var newTokens = new AuthTokens("access_new", "refresh_new", "Bearer", DateTime.UtcNow.AddMinutes(15), DateTime.UtcNow.AddDays(7));
            var newSession = loaded with { Status = SessionStatus.Active, AttemptId = Guid.Empty, Tokens = newTokens };
            
            bool committed = await store.CommitSessionRefreshAsync(loaded, newSession);
            if (!committed)
            {
                UpdateStatus("FAIL: CommitSessionRefreshAsync returned false.");
                return;
            }

            bool committedState = testPrefs.Edit()!
                .PutInt("TestStep", 2)!
                .PutInt("LastPid", myPid)!
                .Commit();
                
            if (!committedState)
            {
                UpdateStatus("FAIL: Failed to commit SmokeHarnessState for Step 2.");
                return;
            }
            UpdateStatus($"PASS: Recovered pending state and committed new session.\n\nACTION REQUIRED: Force-stop the app and relaunch it.");
        }
        else if (currentStep == 2)
        {
            if (lastPid == myPid)
            {
                UpdateStatus("Instruction: Please force-stop and relaunch the app. Same process ID detected, which does not test persistence across process restart.");
                return;
            }

            UpdateStatus("Running Step 3...");

            var expectedSessionId = Guid.Parse(testPrefs.GetString("ExpectedSessionId", "")!);

            // 3a. Verify new session persisted and data matches
            var loaded = await store.LoadSessionAsync();
            if (loaded == null || loaded.SessionId != expectedSessionId || loaded.Status != SessionStatus.Active)
            {
                UpdateStatus("FAIL: Failed to recover committed active session after restart.");
                return;
            }
            if (loaded.Tokens.AccessToken != "access_new" || loaded.Tokens.RefreshToken != "refresh_new")
            {
                UpdateStatus("FAIL: Recovered session tokens do not match expected synthetic values.");
                return;
            }

            // 3b. Clear session and verify absent
            await store.ClearSessionAsync();
            var cleared = await store.LoadSessionAsync();
            if (cleared != null)
            {
                UpdateStatus("FAIL: Session was not cleared.");
                return;
            }

            // 3c. Verify device registration remains present and data matches
            var device = await store.LoadDeviceAsync();
            if (device == null || device.TenantCode != "tenant")
            {
                UpdateStatus("FAIL: Device registration was lost or corrupted during session clear.");
                return;
            }
            if (device.DeviceSecret != "synthetic_secret")
            {
                UpdateStatus("FAIL: Recovered device secret does not match expected synthetic value.");
                return;
            }

            // Cleanup
            if (!testPrefs.Edit()!.Clear()!.Commit())
            {
                UpdateStatus("FAIL: Failed to clear SmokeHarnessState.");
                return;
            }
            await store.ClearAllAsync();

            UpdateStatus("SMOKE TEST COMPLETE: ALL PASS\n(Persistence across process restart verified)");
        }
    }
}