namespace LifeDrop.Mobile.Tracking;

// The difference between the server's clock and the phone's clock (plan step 5.14). The server accepts a tracking point only when its time is between the start of the shift
// and now (+30 s). A phone whose clock is wrong (set by hand, wrong time zone, no network time) sent points the server refused, so the map stayed grey although the shift was on.
// Every answer of the server carries a Date header; the phone keeps the difference and, when it is 5 seconds or more, moves the time of a point by it. A difference
// below 5 seconds is ignored (the header has a resolution of one second) so a correct phone is never changed.
public sealed class ServerClock
{
    public static readonly TimeSpan Threshold = TimeSpan.FromSeconds(5);
    private readonly object _gate = new();
    private TimeSpan _offset;

    public static ServerClock Shared { get; } = new();

    // serverNow is the Date header of an answer (null when it has none); phoneNow is the phone's clock when the answer arrived.
    public void Observe(DateTimeOffset? serverNow, DateTimeOffset phoneNow)
    {
        if (serverNow is null) return;
        lock (_gate) _offset = serverNow.Value - phoneNow;
    }

    public TimeSpan Offset
    {
        get { lock (_gate) return _offset; }
    }

    public bool IsSkewed => Offset.Duration() >= Threshold;

    // The time as the server's clock reads it.
    public DateTimeOffset ToServerTime(DateTimeOffset phoneTime) => IsSkewed ? phoneTime + Offset : phoneTime;
}
