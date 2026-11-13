using CoreLocation;
using Foundation;
using LifeDrop.Mobile.Tracking;

namespace LifeDrop.Mobile;

// iOS counterpart of the Android TrackingService (plan step 3.8): records the position while a shift is on, also with the app in the background
// (UIBackgroundModes "location" with When-In-Use permission; iOS shows the blue location indicator, which is the visible sign that tracking is active).
// iOS cannot ask for "one fix every minute", so every update is delivered (no distance filter, accuracy of about 10 m; it was 100 m, which put people standing together in different places)
// and fixes closer than a minute are dropped (FixQuality also drops rough fixes while a good one is recent). Fixes older than 2 minutes are dropped too: iOS hands over its last known position first, and that one is older than the shift, so the server refuses it
// (plan step 4.19). With a distance filter of 100 m a rep who stays indoors never got a second, fresh fix and the live map stayed empty.
// The queue and upload logic is the shared ShiftTracker. Unlike Android, iOS does not restart the app by itself after it is killed:
// the Home page resumes the shift when the rep opens the app again.
public static class IosTrackingService
{
    private static readonly TimeSpan MinGap = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan MaxFixAge = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan UploadEvery = TimeSpan.FromMinutes(1);
    private static readonly FixQuality Quality = new();
    private static readonly object Gate = new();
    private static CLLocationManager? _manager;
    private static Timer? _uploadTimer;
    private static DateTimeOffset _lastAccepted = DateTimeOffset.MinValue;

    public static void Start()
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            lock (Gate)
            {
                if (_manager is null)
                {
                    _manager = new CLLocationManager
                    {
                        DesiredAccuracy = CLLocation.AccuracyNearestTenMeters,
                        DistanceFilter = -1,   // kCLDistanceFilterNone: report every update; the one-minute gap below limits the number of points
                        PausesLocationUpdatesAutomatically = false,
                        AllowsBackgroundLocationUpdates = true,
                        ShowsBackgroundLocationIndicator = true
                    };
                    _manager.LocationsUpdated += OnLocations;
                }
                _lastAccepted = DateTimeOffset.MinValue;
                Quality.Reset();
                _manager.StartUpdatingLocation();
                // With "Always" permission iOS starts the app again after it was closed (by the system or by swiping it away) when the phone moves a few hundred metres; the app
                // then continues the shift (AppDelegate -> ResumeAfterRelaunchAsync). Without the permission this does nothing (plan step 5.15).
                if (CLLocationManager.SignificantLocationChangeMonitoringAvailable) _manager.StartMonitoringSignificantLocationChanges();
                _uploadTimer ??= new Timer(_ => _ = TrackingHost.SyncQuietlyAsync(), null, UploadEvery, UploadEvery);
            }
        });
    }

    // The system started the app again because the phone moved (see Start): continue the open shift without waiting for the user to open the app.
    public static async Task ResumeAfterRelaunchAsync()
    {
        try
        {
            var tracker = TrackingHost.GetOrCreate();
            if (tracker is null) return;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await tracker.ResumeAsync(cts.Token);
            if (tracker.State == ShiftState.On) Start();   // also when the server could not be reached but this phone knows the shift is open
        }
        catch (Exception)
        {
            // The home page tries again when the user opens the app.
        }
    }

    public static void Stop()
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            lock (Gate)
            {
                _manager?.StopUpdatingLocation();
                _manager?.StopMonitoringSignificantLocationChanges();
                _uploadTimer?.Dispose();
                _uploadTimer = null;
            }
            _ = TrackingHost.SyncQuietlyAsync();
        });
    }

    private static void OnLocations(object? sender, CLLocationsUpdatedEventArgs e)
    {
        var tracker = TrackingHost.Tracker;
        if (tracker is null) return;
        foreach (var location in e.Locations)
        {
            if (location.HorizontalAccuracy < 0) continue; // invalid fix
            var capturedAt = new DateTimeOffset((DateTime)location.Timestamp, TimeSpan.Zero);
            if (DateTimeOffset.UtcNow - capturedAt > MaxFixAge) continue; // the cached last known position, older than the shift
            if (capturedAt - _lastAccepted < MinGap) continue;
            if (!Quality.Keep(location.HorizontalAccuracy, capturedAt)) continue;
            var mocked = location.SourceInformation?.IsSimulatedBySoftware == true;
            tracker.AddFix(location.Coordinate.Latitude, location.Coordinate.Longitude, location.HorizontalAccuracy, ServerClock.Shared.ToServerTime(capturedAt), mocked);   // a phone with a wrong clock is moved to the server's time (step 5.14)
            _lastAccepted = capturedAt;
        }
    }
}
