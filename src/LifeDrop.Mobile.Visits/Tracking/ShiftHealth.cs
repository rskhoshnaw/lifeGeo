namespace LifeDrop.Mobile.Tracking;

// What the phone knows about its own ability to keep tracking (plan step 5.17): sent to the server about once a minute while a shift is on, shown to the manager next to the live
// map, and used by the phone itself to refuse a shift that could not be followed. LocationPermission is "always", "when_in_use", "denied" or "unknown". A value of null means
// "this phone cannot tell" and is never treated as a problem.
public sealed record ShiftHealth(string? AppVersion, string? Platform, string? OsVersion, string? DeviceModel, string LocationPermission, bool? PreciseLocation,
    bool? LocationServices, bool? Notifications, bool? BatteryExempt, string? BackgroundRefresh, int? BatteryPercent, bool? LowPower);

public enum MissingPermission { LocationServices, LocationAlways, PreciseLocation, Notifications, BatteryOptimization, BackgroundRefresh }

// What must be allowed before a shift may start (owner request 2026-11-05: a user who did not allow everything is not followed, so the shift does not start).
public static class PermissionGate
{
    public static IReadOnlyList<MissingPermission> Missing(ShiftHealth health)
    {
        var missing = new List<MissingPermission>();
        if (health.LocationServices == false) missing.Add(MissingPermission.LocationServices);
        // Only "all the time" lets the system keep (Android) or restart (iPhone) the tracking when the app is closed or the screen is off.
        if (health.LocationPermission != "always") missing.Add(MissingPermission.LocationAlways);
        if (health.PreciseLocation == false) missing.Add(MissingPermission.PreciseLocation);
        if (health.Notifications == false) missing.Add(MissingPermission.Notifications);
        if (health.Platform == "android" && health.BatteryExempt == false) missing.Add(MissingPermission.BatteryOptimization);
        if (health.Platform == "ios" && health.BackgroundRefresh is "denied" or "restricted") missing.Add(MissingPermission.BackgroundRefresh);
        return missing;
    }
}
