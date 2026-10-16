using LifeDrop.Mobile.Visits;

namespace LifeDrop.Mobile;

// Push messages on the phone (plan step 2.14): after sign-in the app asks the system for its Firebase token and registers it with the server; at sign-out it
// is removed. Everything that needs the Firebase library is compiled only when google-services.json is present in the project (docs/PUSH.md); without it
// the app works as before and EnsureAsync does nothing.
public static class PushHost
{
    private static bool _asked;

    public static async Task EnsureAsync()
    {
        var sync = TrackingHost.PushSync;
        if (sync is null) return;
#if (ANDROID || IOS) && FIREBASE
        try
        {
            if (!_asked)
            {
                _asked = true;
#if ANDROID
                // Android 13 and later: showing a message needs the user's yes once.
                await Permissions.RequestAsync<Permissions.PostNotifications>();
#endif
            }
#if ANDROID
            var token = await FirebaseTokenSource.GetTokenAsync();
#else
            var token = await IosPush.GetTokenAsync();
#endif
            if (!string.IsNullOrEmpty(token)) sync.TokenReceived(token);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await sync.EnsureRegisteredAsync(cts.Token);
        }
        catch (Exception)
        {
            // Push is a convenience: the next start tries again.
        }
#else
        _ = _asked;
        await Task.CompletedTask;
#endif
    }

    // At sign-out, before the session ends.
    public static async Task RemoveAsync()
    {
        if (TrackingHost.PushSync is not { } sync) return;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await sync.RemoveAsync(cts.Token);
        }
        catch (Exception)
        {
            // Best effort; the token moves to the next user who signs in on this phone.
        }
    }
}
