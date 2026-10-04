namespace LifeDrop.Domain.Visits;

// A position reported by the phone for check-in or check-out (PRD §6.1).
public sealed record GpsFix(double Latitude, double Longitude, double AccuracyMeters, DateTimeOffset CapturedAt, bool IsMocked)
{
    public bool IsWellFormed =>
        double.IsFinite(Latitude) && double.IsFinite(Longitude) && double.IsFinite(AccuracyMeters) &&
        Latitude is >= -90 and <= 90 && Longitude is >= -180 and <= 180 &&
        !(Latitude == 0 && Longitude == 0) && AccuracyMeters > 0 && CapturedAt != default;
}

public enum LocationRejection { OutsideGeofence, LowAccuracy, StaleFix, ClockSkew, MockLocation, OfflineExpired }

// Server-side acceptance rules for a check-in/out position (PRD §6.1, §6.3). The phone never decides.
public static class LocationCheckPolicy
{
    public const double MaxAccuracyMeters = 50;
    public static readonly TimeSpan MaxFixAge = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan MaxFutureSkew = TimeSpan.FromSeconds(30);

    // Returns null when the position is accepted. Checks run from the most to the least suspicious signal.
    public static LocationRejection? Evaluate(GpsFix fix, DateTimeOffset serverNow, double distanceMeters, int radiusMeters)
    {
        if (fix.IsMocked) return LocationRejection.MockLocation;
        if (fix.CapturedAt - serverNow > MaxFutureSkew) return LocationRejection.ClockSkew;
        if (serverNow - fix.CapturedAt > MaxFixAge) return LocationRejection.StaleFix;
        if (fix.AccuracyMeters > MaxAccuracyMeters) return LocationRejection.LowAccuracy;
        if (distanceMeters > radiusMeters) return LocationRejection.OutsideGeofence;
        return null;
    }

    public static string ToContract(this LocationRejection rejection) => rejection switch
    {
        LocationRejection.OutsideGeofence => "outside_geofence",
        LocationRejection.LowAccuracy => "low_accuracy",
        LocationRejection.StaleFix => "stale_fix",
        LocationRejection.ClockSkew => "clock_skew",
        LocationRejection.OfflineExpired => "offline_expired",
        _ => "mock_location"
    };
}

// Why an accepted offline check-in/out still needs a manager's review (plan step 1.14, decision D5).
public enum OfflineReviewReason { ClockMismatch, NoTrackingNearby }

// Offline check-in / check-out (decision D5, 2026-10-04): saved on the phone without internet and sent later.
// The same position rules apply except freshness: the fix may be up to 24 h old when it arrives. Two signals that
// the phone cannot easily fake send it to a manager's review instead of rejecting it:
// - the capture time implied by the phone's monotonic clock (time since boot, unaffected by changing the phone's
//   clock) must agree with the GPS capture time within 2 minutes;
// - the shift's tracking path must have a point near the customer within 10 minutes of the capture time.
public static class OfflineCheckPolicy
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);
    public static readonly TimeSpan MaxClockDifference = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan TrackingWindow = TimeSpan.FromMinutes(10);
    // Extra distance allowed for a tracking point, on top of the radius and the fix's accuracy.
    public const double TrackingMarginMeters = 50;

    public static LocationRejection? Evaluate(GpsFix fix, DateTimeOffset serverNow, double distanceMeters, int radiusMeters)
    {
        if (fix.IsMocked) return LocationRejection.MockLocation;
        if (fix.CapturedAt - serverNow > LocationCheckPolicy.MaxFutureSkew) return LocationRejection.ClockSkew;
        if (serverNow - fix.CapturedAt > MaxAge) return LocationRejection.OfflineExpired;
        if (fix.AccuracyMeters > LocationCheckPolicy.MaxAccuracyMeters) return LocationRejection.LowAccuracy;
        if (distanceMeters > radiusMeters) return LocationRejection.OutsideGeofence;
        return null;
    }

    // elapsedSinceCapture is measured on the phone with its monotonic clock; null when the phone did not send it.
    public static bool ClockAgrees(DateTimeOffset capturedAt, DateTimeOffset serverNow, TimeSpan? elapsedSinceCapture) =>
        elapsedSinceCapture is { } elapsed && elapsed >= TimeSpan.Zero &&
        (serverNow - elapsed - capturedAt).Duration() <= MaxClockDifference;

    public static string ToContract(this OfflineReviewReason reason) => reason switch
    {
        OfflineReviewReason.ClockMismatch => "clock_mismatch",
        _ => "no_tracking_nearby"
    };
}
