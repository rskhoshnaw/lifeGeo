#if FIREBASE
using Firebase.CloudMessaging;
using Foundation;
using LifeDrop.Mobile.Offline;
using LifeDrop.Mobile.Visits;
using UIKit;
using UserNotifications;

namespace LifeDrop.Mobile;

// Firebase Cloud Messaging on iOS (plan step 3.8). Compiled only with GoogleService-Info.plist in the project (docs/IOS.md). The server sends the same data as on Android plus a system alert
// whose text comes from Localizable.strings (the phone's language), because iOS does not run a closed app for a data-only message.
internal static class IosPush
{
    // Asks for the notification permission once, registers with APNs and returns the Firebase token (null if refused or not available yet).
    public static async Task<string?> GetTokenAsync()
    {
        var answer = await UNUserNotificationCenter.Current.RequestAuthorizationAsync(UNAuthorizationOptions.Alert | UNAuthorizationOptions.Sound | UNAuthorizationOptions.Badge);
        if (!answer.Item1) return null;
        await MainThread.InvokeOnMainThreadAsync(() => UIApplication.SharedApplication.RegisterForRemoteNotifications());
        // The APNs token arrives a moment after registering; Firebase cannot give its token before that.
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                var token = await Messaging.SharedInstance.FetchTokenAsync();
                if (!string.IsNullOrEmpty(token)) return token;
            }
            catch (NSErrorException)
            {
            }
            await Task.Delay(TimeSpan.FromSeconds(2));
        }
        return null;
    }
}

// A refreshed token while the app is running is kept like on Android and registered at the next start.
internal sealed class IosMessagingDelegate : NSObject, IMessagingDelegate
{
    [Export("messaging:didReceiveRegistrationToken:")]
    public void DidReceiveRegistrationToken(Messaging messaging, string fcmToken)
    {
        try
        {
            PushTokenSync.RememberToken(new FileDurableStore(Path.Combine(FileSystem.AppDataDirectory, "offline")), fcmToken);
        }
        catch (Exception)
        {
            // The next start asks Firebase for the token again.
        }
    }
}

// A message that arrives while the app is open is shown as a banner too.
internal sealed class IosForegroundNotifications : UNUserNotificationCenterDelegate
{
    public override void WillPresentNotification(UNUserNotificationCenter center, UNNotification notification, Action<UNNotificationPresentationOptions> completionHandler) =>
        completionHandler(UNNotificationPresentationOptions.Banner | UNNotificationPresentationOptions.List | UNNotificationPresentationOptions.Sound);
}
#endif
