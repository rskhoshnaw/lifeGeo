using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using LifeDrop.Mobile.Auth;

namespace LifeDrop.Mobile;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop,
    ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode |
    ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
// Handle lifedrop://join/<code> deep links (plan step 4.7).
[IntentFilter(
    new[] { Intent.ActionView },
    Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable },
    DataScheme = CompanyJoinLink.Scheme,
    DataHost = "join"
)]
public class MainActivity : MauiAppCompatActivity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        ApplyJoinIntent(Intent);
    }

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        ApplyJoinIntent(intent);
    }

    private static void ApplyJoinIntent(Intent? intent)
    {
        if (intent?.Action != Intent.ActionView) return;
        var code = CompanyJoinLink.TryGetCode(intent.DataString);
        if (code is null) return;
        if (Application.Current?.MainPage is MainPage page) page.ApplyJoinCode(code);
        else AppConfig.SetPendingJoinCode(code);
    }
}
