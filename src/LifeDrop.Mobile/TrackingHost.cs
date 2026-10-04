using LifeDrop.Mobile.Auth;
using LifeDrop.Mobile.Tracking;
using System;
using System.Net.Http;

#if ANDROID
using LifeDrop.Mobile.Auth.Android;
#endif

namespace LifeDrop.Mobile;

// The one shift tracker of the app process, shared by the Home page and the Android tracking service (step 1.13).
public static class TrackingHost
{
    private static readonly object Gate = new();
    private static ShiftTracker? _tracker;

    public static ShiftTracker? Tracker
    {
        get { lock (Gate) return _tracker; }
    }

    // Null when the app is not configured (no API address) or not on Android.
    public static ShiftTracker? GetOrCreate()
    {
        lock (Gate)
        {
            if (_tracker is not null) return _tracker;
            var url = Preferences.Get("ApiUrl", "");
            if (string.IsNullOrEmpty(url)) return null;
#if ANDROID
            var store = new AndroidSecureCredentialStore(Android.App.Application.Context);
            var http = new HttpClient { BaseAddress = new Uri(url), Timeout = TimeSpan.FromSeconds(20) };
            var session = new AuthSession(store, new AuthApiClient(http), TimeProvider.System);
            _tracker = new ShiftTracker(new ShiftApiClient(http, session), TimeProvider.System);
            return _tracker;
#else
            return null;
#endif
        }
    }

    // After logout the next user must not reuse this tracker.
    public static void Reset()
    {
        lock (Gate) _tracker = null;
    }
}
