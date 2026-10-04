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

// Foreground service that records the position about every 30 s while a shift is on (plan step 1.13, PRD §6.2,
// decision D1). A visible notification says tracking is active. Points are queued in the shared ShiftTracker and
// uploaded every minute. The service stops itself when no shift is on.
[Service(Exported = false, ForegroundServiceType = ForegroundService.TypeLocation)]
public sealed class TrackingService : Service, ILocationListener
{
    public const long IntervalMilliseconds = 30_000;
    private const int NotificationId = 1013;
    private const string ChannelId = "lifedrop_tracking";
    private static readonly TimeSpan UploadEvery = TimeSpan.FromMinutes(1);

    private LocationManager? _locations;
    private Timer? _uploadTimer;

    public static void Start(Context context)
    {
        var intent = new Intent(context, typeof(TrackingService));
        if (OperatingSystem.IsAndroidVersionAtLeast(26)) context.StartForegroundService(intent);
        else context.StartService(intent);
    }

    public static void Stop(Context context) => context.StopService(new Intent(context, typeof(TrackingService)));

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        var notification = BuildNotification();
        if (OperatingSystem.IsAndroidVersionAtLeast(29)) StartForeground(NotificationId, notification, ForegroundService.TypeLocation);
        else StartForeground(NotificationId, notification);

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
            _locations?.RequestLocationUpdates(LocationManager.GpsProvider, IntervalMilliseconds, 0f, this);
        }
        catch (Java.Lang.SecurityException)
        {
            StopSelf(); // location permission was withdrawn
            return StartCommandResult.NotSticky;
        }

        _uploadTimer ??= new Timer(_ => _ = FlushQuietlyAsync(), null, UploadEvery, UploadEvery);
        return StartCommandResult.Sticky;
    }

    public void OnLocationChanged(Location location)
    {
#pragma warning disable CA1422 // IsFromMockProvider is the only flag before Android 12
        var mocked = OperatingSystem.IsAndroidVersionAtLeast(31) ? location.IsMock : location.IsFromMockProvider;
#pragma warning restore CA1422
        TrackingHost.Tracker?.AddFix(location.Latitude, location.Longitude, location.HasAccuracy ? location.Accuracy : 9999,
            DateTimeOffset.FromUnixTimeMilliseconds(location.Time), mocked);
    }

    public void OnProviderDisabled(string provider) { }

    public void OnProviderEnabled(string provider) { }

    public void OnStatusChanged(string? provider, [GeneratedEnum] Availability status, Bundle? extras) { }

    public override void OnDestroy()
    {
        _locations?.RemoveUpdates(this);
        _uploadTimer?.Dispose();
        _uploadTimer = null;
        _ = FlushQuietlyAsync();
        base.OnDestroy();
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
            if (TrackingHost.Tracker is { } tracker) await tracker.FlushAsync();
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
            manager?.CreateNotificationChannel(new NotificationChannel(ChannelId, "Shift tracking", NotificationImportance.Low));
            builder = new Notification.Builder(this, ChannelId);
        }
        else
        {
#pragma warning disable CA1422
            builder = new Notification.Builder(this);
#pragma warning restore CA1422
        }
        return builder
            .SetContentTitle("Shift on")
            .SetContentText("LifeDrop is recording your location until you end the shift.")
            .SetSmallIcon(Android.Resource.Drawable.IcMenuMyLocation)
            .SetOngoing(true)
            .Build();
    }
}
