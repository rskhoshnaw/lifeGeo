using Foundation;
using LifeDrop.Mobile.Auth;
using UIKit;

namespace LifeDrop.Mobile;

[Register("SceneDelegate")]
public class SceneDelegate : MauiUISceneDelegate
{
    // lifedrop://join/<code> links (plan step 4.7): the app was started by the link (cold start) ...
    public override void WillConnect(UIScene scene, UISceneSession session, UISceneConnectionOptions connectionOptions)
    {
        base.WillConnect(scene, session, connectionOptions);
        Apply(connectionOptions.UrlContexts);
    }

    // ... or was already running.
    public override void OpenUrlContexts(UIScene scene, NSSet<UIOpenUrlContext> urlContexts)
    {
        base.OpenUrlContexts(scene, urlContexts);
        Apply(urlContexts);
    }

    private static void Apply(NSSet<UIOpenUrlContext>? contexts)
    {
        if (contexts is null) return;
        foreach (UIOpenUrlContext context in contexts)
        {
            var code = CompanyJoinLink.TryGetCode(context.Url?.AbsoluteString);
            if (code is null) continue;
            if (Microsoft.Maui.Controls.Application.Current?.MainPage is MainPage page) page.ApplyJoinCode(code);
            else AppConfig.SetPendingJoinCode(code);
            return;
        }
    }
}
