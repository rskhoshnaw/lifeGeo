using LifeDrop.Mobile.Tracking;
#if ANDROID
using Android.Content;
using Android.Content.PM;
using Android.Locations;
#elif IOS
using CoreLocation;
using Foundation;
using UIKit;
using UserNotifications;
#endif

namespace LifeDrop.Mobile;

// Reads what this phone allows (plan step 5.17): the location permission ("all the time" or not), precise location, the location switch, notifications, battery saving (Android), Background
// App Refresh (iPhone), the battery, and which app and phone this is. Used for the report sent while a shift is on and for the check before a shift starts. Never throws: a value it cannot
// read is left empty ("this phone cannot tell"), which is not treated as a problem.
public static class DeviceHealthReader
{
    public static async Task<ShiftHealth?> ReadAsync()
    {
        try
        {
            await Task.CompletedTask;
            var version = $"{AppInfo.VersionString} ({AppInfo.BuildString})";
            var model = $"{DeviceInfo.Manufacturer} {DeviceInfo.Model}".Trim();
            int? battery = null;
            bool? lowPower = null;
            try
            {
                if (Battery.Default.ChargeLevel is >= 0 and <= 1) battery = (int)Math.Round(Battery.Default.ChargeLevel * 100);
                lowPower = Battery.Default.EnergySaverStatus == EnergySaverStatus.On;
            }
            catch (Exception)
            {
                // No battery information on this phone.
            }
#if ANDROID
            var context = Android.App.Application.Context;
            static bool Granted(Context c, string permission) => c.CheckSelfPermission(permission) == Permission.Granted;
            var fine = Granted(context, Android.Manifest.Permission.AccessFineLocation);
            var coarse = Granted(context, Android.Manifest.Permission.AccessCoarseLocation);
            var background = !OperatingSystem.IsAndroidVersionAtLeast(29) || Granted(context, Android.Manifest.Permission.AccessBackgroundLocation);
            var permission = fine || coarse ? (background ? "always" : "when_in_use") : "denied";
            var manager = context.GetSystemService(Context.LocationService) as LocationManager;
            bool? services = manager is null ? null : OperatingSystem.IsAndroidVersionAtLeast(28) ? manager.IsLocationEnabled : manager.IsProviderEnabled(LocationManager.GpsProvider);
            bool? notifications = (context.GetSystemService(Context.NotificationService) as Android.App.NotificationManager)?.AreNotificationsEnabled();
            return new ShiftHealth(version, "android", DeviceInfo.VersionString, model, permission, fine, services, notifications, BatteryOptimization.IsExempt(context), null, battery, lowPower);
#elif IOS
            return await MainThread.InvokeOnMainThreadAsync(async () =>
            {
                var manager = new CLLocationManager();
                var status = manager.AuthorizationStatus;
                var permission = status switch
                {
                    CLAuthorizationStatus.AuthorizedAlways => "always",
                    CLAuthorizationStatus.AuthorizedWhenInUse => "when_in_use",
                    CLAuthorizationStatus.Denied or CLAuthorizationStatus.Restricted => "denied",
                    _ => "unknown"
                };
                bool? precise = status is CLAuthorizationStatus.AuthorizedAlways or CLAuthorizationStatus.AuthorizedWhenInUse ? manager.AccuracyAuthorization == CLAccuracyAuthorization.FullAccuracy : null;
                bool? notifications = null;
                try
                {
                    var settings = await UNUserNotificationCenter.Current.GetNotificationSettingsAsync();
                    notifications = settings.AuthorizationStatus is UNAuthorizationStatus.Authorized or UNAuthorizationStatus.Provisional or UNAuthorizationStatus.Ephemeral;
                }
                catch (Exception)
                {
                    // Leave it empty.
                }
                var refresh = UIApplication.SharedApplication.BackgroundRefreshStatus switch
                {
                    UIBackgroundRefreshStatus.Available => "available",
                    UIBackgroundRefreshStatus.Denied => "denied",
                    UIBackgroundRefreshStatus.Restricted => "restricted",
                    _ => null
                };
                return (ShiftHealth?)new ShiftHealth(version, "ios", DeviceInfo.VersionString, model, permission, precise, CLLocationManager.LocationServicesEnabled, notifications, null, refresh, battery,
                    NSProcessInfo.ProcessInfo.LowPowerModeEnabled);
            });
#else
            return await Task.FromResult<ShiftHealth?>(null);
#endif
        }
        catch (Exception)
        {
            return null;
        }
    }
}
