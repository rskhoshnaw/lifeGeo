using CoreLocation;
using Foundation;
using LifeDrop.Mobile.Tracking;

namespace LifeDrop.Mobile;

// iOS counterpart of the Android TrackingService (plan step 3.8): records the position while a shift is on, also with the app in the background
// (UIBackgroundModes "location" with When-In-Use permission; iOS shows the blue location indicator, which is the visible sign that tracking is active).
// iOS cannot ask for "one fix every 3 minutes", so every update is delivered (no distance filter, 100 m accuracy keeps the battery use low) and fixes closer than 3 minutes
// are dropped. Fixes older than 2 minutes are dropped too: iOS hands over its last known position first, and that one is older than the shift, so the server refuses it
// (plan step 4.19). With a distance filter of 100 m a rep who stays indoors never got a second, fresh fix and the live map stayed empty.
// The queue and upload logic is the shared ShiftTracker. Unlike Android, iOS does not restart the app by itself after it is killed:
// the Home page resumes the shift when the rep opens the app again.
public static class IosTrackingService
{
    private static readonly TimeSpan MinGap = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan MaxFixAge = TimeSpan.FromMinutes(2);
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
                        DistanceFilter = -1,   // kCLDistanceFilterNone: report every update; the 3-minute gap below limits the number of points
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
            if (DateTimeOffset.UtcNow - capturedAt > MaxFixAge) continue; // the cached last known position, older than the shift
            if (capturedAt - _lastAccepted < MinGap) continue;
            var mocked = location.SourceInformation?.IsSimulatedBySoftware == true;
            tracker.AddFix(location.Coordinate.Latitude, location.Coordinate.Longitude, location.HorizontalAccuracy, capturedAt, mocked);
            _lastAccepted = capturedAt;
        }
    }
}
