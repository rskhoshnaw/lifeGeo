using Android.Content;
using Android.OS;
using Android.Provider;

namespace LifeDrop.Mobile;

// Battery saving ("Doze", the maker's app killer) stops a phone that lies still from sending its position: the map turns grey. An app on the "not optimised" list is left alone (step 5.14).
public static class BatteryOptimization
{
    public static bool IsExempt(Context context)
    {
        try
        {
            return (context.GetSystemService(Context.PowerService) as PowerManager)?.IsIgnoringBatteryOptimizations(context.PackageName) ?? true;
        }
        catch (Exception)
        {
            return true;   // cannot tell: do not nag
        }
    }

    // Opens the system's question "Allow the app to always run in the background?".
    public static void RequestExemption(Context context)
    {
        try
        {
            var intent = new Intent(Settings.ActionRequestIgnoreBatteryOptimizations, Android.Net.Uri.Parse("package:" + context.PackageName));
            intent.AddFlags(ActivityFlags.NewTask);
            context.StartActivity(intent);
        }
        catch (Exception)
        {
            // Some phones have no such screen; the user can still set it by hand.
        }
    }
}
