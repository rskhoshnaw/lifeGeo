namespace LifeDrop.Domain.Visits;

// A position reported by the phone for check-in or check-out (PRD §6.1).
public sealed record GpsFix(double Latitude, double Longitude, double AccuracyMeters, DateTimeOffset CapturedAt, bool IsMocked)
{
    public bool IsWellFormed =>
        double.IsFinite(Latitude) && double.IsFinite(Longitude) && double.IsFinite(AccuracyMeters) &&
        Latitude is >= -90 and <= 90 && Longitude is >= -180 and <= 180 &&
        !(Latitude == 0 && Longitude == 0) && AccuracyMeters > 0 && CapturedAt != default;
}

public enum LocationRejection { OutsideGeofence, LowAccuracy, StaleFix, ClockSkew, MockLocation }

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
        _ => "mock_location"
    };
}
