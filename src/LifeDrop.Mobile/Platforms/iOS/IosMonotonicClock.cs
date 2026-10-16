using LifeDrop.Mobile.Offline;
using System.Runtime.InteropServices;

namespace LifeDrop.Mobile;

// Time that keeps counting while the phone sleeps and ignores changes of the phone's clock (decision D5). CLOCK_MONOTONIC_RAW on Apple platforms is mach_continuous_time.
// iOS has no boot counter, so BootId is null; the outbox then relies on the elapsed time alone.
public sealed class IosMonotonicClock : IMonotonicClock
{
    private const int ClockMonotonicRaw = 4;

    [DllImport("libSystem.dylib")]
    private static extern ulong clock_gettime_nsec_np(int clockId);

    public long ElapsedMs => (long)(clock_gettime_nsec_np(ClockMonotonicRaw) / 1_000_000UL);

    public string? BootId => null;
}
