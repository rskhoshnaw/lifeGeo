using Android.OS;
using Android.Provider;
using LifeDrop.Mobile.Offline;

namespace LifeDrop.Mobile;

// Time since the phone booted, including deep sleep; changing the phone's clock does not affect it (decision D5).
// The boot count tells whether the phone restarted between two readings.
public sealed class AndroidMonotonicClock : IMonotonicClock
{
    public long ElapsedMs => SystemClock.ElapsedRealtime();

    public string? BootId
    {
        get
        {
            try
            {
                var resolver = global::Android.App.Application.Context.ContentResolver;
                return resolver is null ? null : Settings.Global.GetInt(resolver, "boot_count").ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            catch (Settings.SettingNotFoundException)
            {
                return null;
            }
        }
    }
}
