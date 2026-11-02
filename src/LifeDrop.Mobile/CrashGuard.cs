using LifeDrop.Mobile.Auth;

namespace LifeDrop.Mobile;

// Crash protection and crash reports (plan step 5.12). An error in an `async void` handler (a button, a page appearing) has no caller to catch it, so on some phones it
// closed the app. Here the last-resort handlers keep the app open where that is safe (Android: the error is marked handled, except out-of-memory) and keep a short
// report (error type, the first lines of the stack, company code, e-mail typed at sign-in, app version, platform; no password or token) in the same outbox as the
// connection errors of step 5.10. The report is sent to the company's server later, so the manager sees the crash in the panel (Connection errors).
public static class CrashGuard
{
    private const int MaxMessage = 300;
    private static readonly Lazy<ClientErrorOutbox> OutboxHolder = new(() => new ClientErrorOutbox(Path.Combine(FileSystem.AppDataDirectory, "client-errors.json")));

    public static ClientErrorOutbox Outbox => OutboxHolder.Value;

    public static void Install()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Record(e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Record(e.Exception);
            e.SetObserved();
        };
#if ANDROID
        Android.Runtime.AndroidEnvironment.UnhandledExceptionRaiser += (_, e) =>
        {
            Record(e.Exception);
            if (e.Exception is not OutOfMemoryException) e.Handled = true;   // keep the app open; out-of-memory is not survivable
        };
#endif
    }

    // A short, safe text for the report: the error type and message and the first stack frames, cut to the size the server accepts.
    public static string Describe(Exception exception)
    {
        var frames = (exception.StackTrace ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(f => f.Trim());
        var text = "crash: " + exception.GetType().Name + ": " + exception.Message + " @ " + string.Join(" | ", frames);
        return text.Length <= MaxMessage ? text : text[..MaxMessage];
    }

    public static void Record(Exception? exception)
    {
        if (exception is null) return;
        try
        {
            Outbox.Add(new PendingClientError(DateTimeOffset.UtcNow, "other", "other", exception.GetType().Name, Describe(exception),
                Preferences.Get("CompanyCode", ""), Preferences.Get("LastEmail", ""), Preferences.Get("AppInstallId", ""), AppInfo.VersionString,
                DeviceInfo.Platform == DevicePlatform.iOS ? "ios" : "android"));
        }
        catch (Exception)
        {
            // Reporting a crash must never cause another one.
        }
    }

    // Sends the waiting reports to the company's server (after a crash the app is opened again and lands here from the home page).
    public static async Task FlushAsync()
    {
        try
        {
            var apiUrl = Preferences.Get("ApiUrl", "");
            var company = Preferences.Get("CompanyCode", "");
            if (string.IsNullOrEmpty(apiUrl) || string.IsNullOrEmpty(company) || Outbox.Pending().Count == 0) return;
            using var http = new HttpClient { BaseAddress = new Uri(apiUrl), Timeout = TimeSpan.FromSeconds(8) };
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            await ClientErrorSender.FlushAsync(http, Outbox, company, cts.Token);
        }
        catch (Exception)
        {
            // Try again at the next start.
        }
    }
}
