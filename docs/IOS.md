# iOS (plan step 3.8)

Build, sign and TestFlight upload: `.github/workflows/ios.yml` (PR label `ios` = build only, `ios-testflight` = also upload; or Actions > iOS > Run workflow with the upload box). Helpers: `scripts/ci/ios/`.
Secrets used: `APPLE_CERT_P12_BASE64`, `APPLE_CERT_PASSWORD`, `APPLE_PROVISION_BASE64`, `APPLE_ASC_ISSUER_ID`, `APPLE_ASC_KEY_ID`, `APPLE_ASC_PRIVATE_KEY`.
The workflow pins workload set 10.0.401 and MAUI 10.0.20 (the runner has Xcode 26.6); move the pin when the runner image has Xcode 27.

Works on iOS: sign-in (Keychain), shift tracking in the background (CoreLocation, When-In-Use + background location, blue indicator), offline outbox, `lifedrop://join/<code>` links and QR scan.
Not yet: push messages. They need, from the owner: an APNs key (.p8) from the Apple Developer account, uploaded in the Firebase project (Cloud Messaging > Apple app), and `GoogleService-Info.plist` for bundle id `com.pixelo.geotrack`. After that the app gets the Firebase iOS messaging package and `PushHost` an `IOS` branch (the server already sends through FCM).
Real iPhone checks pending: background tracking with the screen locked, link opening, camera QR.
