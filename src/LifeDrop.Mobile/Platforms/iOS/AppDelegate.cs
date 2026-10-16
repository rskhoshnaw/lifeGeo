using Foundation;
#if FIREBASE
using Firebase.CloudMessaging;
using UIKit;
using UserNotifications;
#endif

namespace LifeDrop.Mobile;

[Register("AppDelegate")]
public class AppDelegate : MauiUIApplicationDelegate
{
	protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();

#if FIREBASE
	public override bool FinishedLaunching(UIApplication application, NSDictionary launchOptions)
	{
		Firebase.Core.App.Configure();
		Messaging.SharedInstance.Delegate = new IosMessagingDelegate();
		UNUserNotificationCenter.Current.Delegate = new IosForegroundNotifications();
		return base.FinishedLaunching(application, launchOptions);
	}
#endif
}
