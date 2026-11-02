using Foundation;
using UIKit;
#if FIREBASE
using Firebase.CloudMessaging;
using UserNotifications;
#endif

namespace LifeDrop.Mobile;

[Register("AppDelegate")]
public class AppDelegate : MauiUIApplicationDelegate
{
	protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();

	public override bool FinishedLaunching(UIApplication application, NSDictionary launchOptions)
	{
#if FIREBASE
		Firebase.Core.App.Configure();
		Messaging.SharedInstance.Delegate = new IosMessagingDelegate();
		UNUserNotificationCenter.Current.Delegate = new IosForegroundNotifications();
#endif
		var result = base.FinishedLaunching(application, launchOptions);
		// Started by the system because of a location change (plan step 5.15): continue the shift in the background.
		if (launchOptions?.ContainsKey(UIApplication.LaunchOptionsLocationKey) == true) _ = IosTrackingService.ResumeAfterRelaunchAsync();
		return result;
	}
}
