# Push messages (step 2.14)

Free Firebase Cloud Messaging only. SMS costs money per message, so it is not built.

## How it works
- The in-app message (`notifications`) is the source. A background job (`PushDispatchService`, every `Push:DispatchIntervalSeconds`, default 30, 0 = off) sends **one data-only push** per message to every token of that user: `kind`, `entityId`, `notificationId`. The app writes the localized text, so no text is hard-coded on the server.
- Sent once: marked `pushed_at`. No token for the user = marked, nothing sent. Dead token (`UNREGISTERED`) = deleted. Temporary failure = retried, given up after 5 tries. Messages older than 24 h are never pushed.
- The phone registers its token with `PUT /push-token` after sign-in and calls `POST /push-token/remove` on sign-out. A token belongs to one user: registering it again for another user moves it.
- Without a Firebase key the job does nothing and consumes nothing.

## Owner setup (once, free)
1. Firebase console → new project (Spark plan is free) → add the Android app (package id of `LifeDrop.Mobile`).
2. Project settings → Service accounts → generate a private key (JSON). Keep it secret; never commit it.
3. Give it to the API: `Push__Fcm__ServiceAccountFile=C:\path\key.json` (or `Push__Fcm__ServiceAccountJson` with the JSON text).
4. App side (not built yet, needs the Android SDK): add `google-services.json`, read the FCM token, call `PUT /push-token`, show a local notification from the data.
