using CoreLocation;
using Foundation;
using LifeDrop.Mobile.Tracking;

namespace LifeDrop.Mobile;

// iOS counterpart of the Android TrackingService (plan step 3.8): records the position while a shift is on, also with the app in the background
// (UIBackgroundModes "location" with When-In-Use permission; iOS shows the blue location indicator, which is the visible sign that tracking is active).
// iOS cannot ask for "one fix every 3 minutes", so updates arrive as the phone moves (at least 100 m) and fixes closer than 3 minutes are dropped.
// The queue and upload logic is the shared ShiftTracker. Unlike Android, iOS does not restart the app by itself after it is killed:
// the Home page resumes the shift when the rep opens the app again.
public static class IosTrackingService
{
    private static readonly TimeSpan MinGap = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan UploadEvery = TimeSpan.FromMinutes(3);
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
                        DesiredAccuracy = CLLocation.AccuracyHundredMeters,
                        DistanceFilter = 100,
                        PausesLocationUpdatesAutomatically = false,
                        AllowsBackgroundLocationUpdates = true,
                        ShowsBackgroundLocationIndicator = true
                    };
                    _manager.LocationsUpdated += OnLocations;
                }
                _lastAccepted = DateTimeOffset.MinValue;
                _manager.StartUpdatingLocation();
                _uploadTimer ??= new Timer(_ => _ = TrackingHost.SyncQuietlyAsync(), null, UploadEvery, UploadEvery);
            }
        });
    }

    public static void Stop()
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            lock (Gate)
            {
                _manager?.StopUpdatingLocation();
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
            if (capturedAt - _lastAccepted < MinGap) continue;
            var mocked = location.SourceInformation?.IsSimulatedBySoftware == true;
            tracker.AddFix(location.Coordinate.Latitude, location.Coordinate.Longitude, location.HorizontalAccuracy, capturedAt, mocked);
            _lastAccepted = capturedAt;
        }
    }
}
