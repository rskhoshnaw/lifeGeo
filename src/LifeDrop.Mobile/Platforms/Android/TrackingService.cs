using LifeDrop.Mobile.Localization;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Locations;
using Android.OS;
using Android.Runtime;
using LifeDrop.Mobile.Tracking;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace LifeDrop.Mobile;

// Foreground service that records the position about every minute while a shift is on (plan step 1.13, PRD §6.2,
// decision D1; every minute since step 5.12). A visible notification says tracking is active. Points are queued in the shared ShiftTracker and
// uploaded every minute. The service stops itself when no shift is on.
[Service(Exported = false, ForegroundServiceType = ForegroundService.TypeLocation)]
public sealed class TrackingService : Service, ILocationListener
{
    public const long IntervalMilliseconds = 60_000;   // owner request 2026-11-02: a fresher map (was 180 s, decision 2026-10-07)
    private const int NotificationId = 1013;
    private const string ChannelId = "lifedrop_tracking";
    private static readonly TimeSpan UploadEvery = TimeSpan.FromMinutes(1);

    private LocationManager? _locations;
    private Timer? _uploadTimer;
    private PowerManager.WakeLock? _wakeLock;   // keeps the processor awake between fixes, so the phone in a pocket or on a desk keeps sending (step 5.14)
    private readonly FixQuality _quality = new();   // drops rough fixes while a good one is recent (step 5.12)

    // Android 12+ refuses to start a foreground service from the background, and refuses a location service without the location permission; both used to close the app.
    public static void Start(Context context)
    {
        try
        {
            var intent = new Intent(context, typeof(TrackingService));
            if (OperatingSystem.IsAndroidVersionAtLeast(26)) context.StartForegroundService(intent);
            else context.StartService(intent);
        }
        catch (Exception e) when (e is Java.Lang.SecurityException or Java.Lang.IllegalStateException)
        {
            CrashGuard.Record(e);   // tracking could not start now; the home page tries again when it opens
        }
    }

    public static void Stop(Context context) => context.StopService(new Intent(context, typeof(TrackingService)));

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        AppLanguage.Load(); // the service can start before the app screen
        try
        {
            var notification = BuildNotification();
            if (OperatingSystem.IsAndroidVersionAtLeast(29)) StartForeground(NotificationId, notification, ForegroundService.TypeLocation);
            else StartForeground(NotificationId, notification);
        }
        catch (Exception e) when (e is Java.Lang.SecurityException or Java.Lang.IllegalStateException)
        {
            // No location permission, or the system does not allow a foreground service right now: stop quietly instead of closing the app.
            CrashGuard.Record(e);
            StopSelf();
            return StartCommandResult.NotSticky;
        }

        _quality.Reset();
        var tracker = TrackingHost.GetOrCreate();
        if (tracker is null)
        {
            StopSelf();
            return StartCommandResult.NotSticky;
        }
        // Restarted by the system after the process was killed: continue only if the shift is still open.
        if (tracker.State == ShiftState.Off) _ = ResumeOrStopAsync(tracker);

        try
        {
            _locations ??= (LocationManager?)GetSystemService(LocationService);
            // GPS alone gives no position indoors (office, home, a shop with a thick roof), so the first fix of a shift could take an hour. The network provider (Wi-Fi and
            // mobile towers) answers within seconds with a rougher position; both feed the same queue and the server keeps every point (plan step 4.18).
            foreach (var provider in new[] { LocationManager.GpsProvider, LocationManager.NetworkProvider })
                if (_locations is not null && _locations.IsProviderEnabled(provider))
                    _locations.RequestLocationUpdates(provider, IntervalMilliseconds, 0f, this);
        }
        catch (Java.Lang.SecurityException)
        {
            StopSelf(); // location permission was withdrawn
            return StartCommandResult.NotSticky;
        }

        HoldWakeLock();
        _uploadTimer ??= new Timer(_ => _ = FlushQuietlyAsync(), null, UploadEvery, UploadEvery);
        return StartCommandResult.Sticky;
    }

    public void OnLocationChanged(Android.Locations.Location location)
    {
#pragma warning disable CA1422 // IsFromMockProvider is the only flag before Android 12
        var mocked = OperatingSystem.IsAndroidVersionAtLeast(31) ? location.Mock : location.IsFromMockProvider;
#pragma warning restore CA1422
        var accuracy = location.HasAccuracy ? location.Accuracy : 9999;
        var capturedAt = DateTimeOffset.FromUnixTimeMilliseconds(location.Time);
        // A phone with a wrong clock: the age of the fix (from the monotonic clock, which a wrong wall clock does not touch) is taken from the server's time instead (step 5.14).
        if (ServerClock.Shared.IsSkewed)
        {
            var age = TimeSpan.FromMilliseconds(Math.Max(0, (SystemClock.ElapsedRealtimeNanos() - location.ElapsedRealtimeNanos) / 1_000_000.0));
            capturedAt = ServerClock.Shared.ToServerTime(DateTimeOffset.UtcNow) - age;
        }
        if (!_quality.Keep(accuracy, capturedAt, fromNetwork: location.Provider == LocationManager.NetworkProvider)) return;
        TrackingHost.Tracker?.AddFix(location.Latitude, location.Longitude, accuracy, capturedAt, mocked);
    }

    public void OnProviderDisabled(string provider) { }

    public void OnProviderEnabled(string provider) { }

    public void OnStatusChanged(string? provider, [GeneratedEnum] Availability status, Bundle? extras) { }

    public override void OnDestroy()
    {
        ReleaseWakeLock();
        _locations?.RemoveUpdates(this);
        _uploadTimer?.Dispose();
        _uploadTimer = null;
        _ = FlushQuietlyAsync();
        base.OnDestroy();
    }

    private void HoldWakeLock()
    {
        try
        {
            _wakeLock ??= (GetSystemService(PowerService) as PowerManager)?.NewWakeLock(WakeLockFlags.Partial, "lifedrop:tracking");
            if (_wakeLock is { IsHeld: false }) _wakeLock.Acquire(12 * 60 * 60 * 1000L);   // a shift is shorter than 12 hours; the service renews it at every start
        }
        catch (Exception)
        {
            // Without it tracking still works, only less steadily when the phone sleeps.
        }
    }

    private void ReleaseWakeLock()
    {
        try
        {
            if (_wakeLock is { IsHeld: true }) _wakeLock.Release();
        }
        catch (Exception)
        {
        }
        _wakeLock = null;
    }

    private async Task ResumeOrStopAsync(ShiftTracker tracker)
    {
        try
        {
            await tracker.ResumeAsync();
        }
        catch (Exception)
        {
            // Network trouble: keep the service; the next start or the Home page resumes the shift.
            return;
        }
        if (tracker.State == ShiftState.Off) StopSelf();
    }

    private static async Task FlushQuietlyAsync()
    {
        try
        {
            await TrackingHost.SyncQuietlyAsync(); // tracking points and visit steps saved offline
        }
        catch (Exception)
        {
            // Points stay queued and are retried on the next tick.
        }
    }

    private Notification BuildNotification()
    {
        Notification.Builder builder;
        if (OperatingSystem.IsAndroidVersionAtLeast(26))
        {
            var manager = (NotificationManager?)GetSystemService(NotificationService);
            manager?.CreateNotificationChannel(new NotificationChannel(ChannelId, Texts.Get("app.tracking.channel"), NotificationImportance.Low));
            builder = new Notification.Builder(this, ChannelId);
        }
        else
        {
#pragma warning disable CA1422
            builder = new Notification.Builder(this);
#pragma warning restore CA1422
        }
        return builder
            .SetContentTitle(Texts.Get("app.tracking.title"))
            .SetContentText(Texts.Get("app.tracking.text"))
            .SetSmallIcon(Android.Resource.Drawable.IcMenuMyLocation)
            .SetOngoing(true)
            .Build();
    }
}
