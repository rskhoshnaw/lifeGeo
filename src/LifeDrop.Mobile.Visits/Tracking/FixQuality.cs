namespace LifeDrop.Mobile.Tracking;

// Decides which positions from the phone are worth keeping (plan step 5.12). Phones deliver fixes from several sources (GPS, Wi-Fi, mobile towers): a rough
// one can be hundreds of metres off, so two people standing together appeared in different places on the map, and two sources doubled the number of points.
// Rules: a good GPS fix (accuracy at most GoodAccuracyMeters) is kept; a rough fix is kept only when no good fix was kept for RoughAfter (so indoors, where only
// rough positions exist, the map still shows the person); a fix from the network (Wi-Fi, towers) is never "good" - its accuracy figure is a guess - and is kept only when
// no GPS fix was kept for RoughAfter; and no two fixes are kept closer than MinGap. The server keeps every point it receives and
// each point carries its accuracy, so nothing is hidden: the phone just stops sending the noise.
public sealed class FixQuality
{
    public const double GoodAccuracyMeters = 50;
    public static readonly TimeSpan MinGap = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan RoughAfter = TimeSpan.FromMinutes(3);

    private DateTimeOffset? _lastKept;
    private DateTimeOffset? _lastGood;
    private DateTimeOffset? _lastGps;

    // True when the fix should be added to the queue. Call once per fix, in order.
    public bool Keep(double accuracyMeters, DateTimeOffset capturedAt, bool fromNetwork = false)
    {
        if (!double.IsFinite(accuracyMeters) || accuracyMeters <= 0) return false;
        if (_lastKept is { } last && capturedAt - last < MinGap) return false;
        if (fromNetwork && _lastGps is { } gps && capturedAt - gps < RoughAfter) return false;
        var good = accuracyMeters <= GoodAccuracyMeters && !fromNetwork;
        if (!good && _lastGood is { } lastGood && capturedAt - lastGood < RoughAfter) return false;
        _lastKept = capturedAt;
        if (good) _lastGood = capturedAt;
        if (!fromNetwork) _lastGps = capturedAt;
        return true;
    }

    public void Reset()
    {
        _lastKept = null;
        _lastGood = null;
        _lastGps = null;
    }
}
