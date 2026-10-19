#if ANDROID && FIREBASE
using Android.App;
using Android.Content;
using Android.Gms.Tasks;
using Firebase.Messaging;
using LifeDrop.Mobile.Localization;
using LifeDrop.Mobile.Offline;
using LifeDrop.Mobile.Visits;

namespace LifeDrop.Mobile;

// Firebase Cloud Messaging on Android (plan step 2.14). Compiled only with google-services.json in the project (docs/PUSH.md). The server sends data-only
// messages (kind, entityId, notificationId); this service writes the text in the language of the app and shows it as a notification.
[Service(Exported = false)]
[IntentFilter(new[] { "com.google.firebase.MESSAGING_EVENT" })]
public sealed class LifeDropMessagingService : FirebaseMessagingService
{
    private const string ChannelId = "lifedrop_messages";

    // A new token while the app is not running: kept in the same folder as the app's offline data and registered at the next start.
    public override void OnNewToken(string token)
    {
        base.OnNewToken(token);
        try
        {
            PushTokenSync.RememberToken(new FileDurableStore(Path.Combine(FileSystem.AppDataDirectory, "offline")), token);
        }
        catch (Exception)
        {
            // The next start asks the system for the token again.
        }
    }

    public override void OnMessageReceived(RemoteMessage message)
    {
        base.OnMessageReceived(message);
        var data = message.Data;
        if (data is null || !data.TryGetValue("kind", out var kind)) return;
        AppLanguage.Load();
        // A manager's message (plan step 4.16) carries its own text; the other kinds are written here from the kind, in the language of the app.
        string? text = null;
        if (kind == "manager.message") data.TryGetValue("entityId", out text);
        else if (PushMessages.TextKey(kind) is { } key) text = Texts.Get(key);
        if (string.IsNullOrWhiteSpace(text)) return;
        var manager = (NotificationManager?)GetSystemService(NotificationService);
        if (manager is null) return;
        Notification.Builder builder;
        if (OperatingSystem.IsAndroidVersionAtLeast(26))
        {
            manager.CreateNotificationChannel(new NotificationChannel(ChannelId, Texts.Get("app.push.channel"), NotificationImportance.Default));
            builder = new Notification.Builder(this, ChannelId);
        }
        else
        {
#pragma warning disable CA1422
            builder = new Notification.Builder(this);
#pragma warning restore CA1422
        }
        var open = new Intent(this, typeof(MainActivity)).AddFlags(ActivityFlags.ClearTop | ActivityFlags.SingleTop);
        var pending = PendingIntent.GetActivity(this, 0, open, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
        var notification = builder
            .SetContentTitle("LifeDrop")
            .SetContentText(text)
            .SetSmallIcon(Android.Resource.Drawable.IcDialogInfo)
            .SetContentIntent(pending)
            .SetAutoCancel(true)
            .Build();
        // One notification per server message: the same message twice replaces itself.
        var id = data.TryGetValue("notificationId", out var nid) ? nid.GetHashCode() : Environment.TickCount;
        manager.Notify(id, notification);
    }
}

// Asks Firebase for the current token (the Java task is turned into a .NET one).
internal static class FirebaseTokenSource
{
    public static Task<string?> GetTokenAsync()
    {
        var source = new TaskCompletionSource<string?>();
        FirebaseMessaging.Instance.GetToken().AddOnCompleteListener(new Listener(source));
        return source.Task;
    }

    private sealed class Listener(TaskCompletionSource<string?> source) : Java.Lang.Object, IOnCompleteListener
    {
        public void OnComplete(Android.Gms.Tasks.Task task) => source.TrySetResult(task.IsSuccessful ? task.Result?.ToString() : null);
    }
}
#endif
