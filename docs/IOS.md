# iOS (plan step 3.8)

Build, sign and TestFlight upload: `.github/workflows/ios.yml` (PR label `ios` = build only, `ios-testflight` = also upload; or Actions > iOS > Run workflow with the upload box). Helpers: `scripts/ci/ios/`.
Secrets used: `APPLE_CERT_P12_BASE64`, `APPLE_CERT_PASSWORD`, `APPLE_PROVISION_BASE64`, `APPLE_ASC_ISSUER_ID`, `APPLE_ASC_KEY_ID`, `APPLE_ASC_PRIVATE_KEY`.
The workflow pins workload set 10.0.401 and MAUI 10.0.20 (the runner has Xcode 26.6); move the pin when the runner image has Xcode 27.

Works on iOS: sign-in (Keychain), shift tracking in the background (CoreLocation, When-In-Use + background location, blue indicator), offline outbox, `lifedrop://join/<code>` links and QR scan.
Push messages (code written, needs the owner's Firebase file; see below): Firebase Cloud Messaging with the APNs key. The server already sends through FCM; for an iPhone it adds a system alert whose text is in `Platforms/iOS/Resources/<lang>.lproj/Localizable.strings` (same texts as `app.push.*` in `i18n/strings.json`).

## Push setup (owner, once)
1. Apple Developer > Keys: create a key with "Apple Push Notifications service (APNs)", environment "Sandbox & Production", "Team Scoped (All Topics)"; download the `.p8` (only once). Identifiers > `com.pixelo.geotrack` > tick Push Notifications > Save. Profiles > "GeoTrack Pixelo AppStore" > Edit > Save > Download, and update the secret `APPLE_PROVISION_BASE64`.
2. Firebase console (the Android project) > Project settings > Add app > iOS, bundle id `com.pixelo.geotrack` > download `GoogleService-Info.plist`. Project settings > Cloud Messaging > Apple app configuration > APNs Authentication Key > upload the `.p8` with its Key ID and Team ID `R285PY37J5`.
3. GitHub secret `IOS_GOOGLE_SERVICE_INFO_BASE64` = the plist in base64 (PowerShell: `[Convert]::ToBase64String([IO.File]::ReadAllBytes("path\GoogleService-Info.plist")) | Set-Clipboard`). The workflow writes it to `src/LifeDrop.Mobile/GoogleService-Info.plist` (git-ignored); with the file present the project adds the Firebase package and the `aps-environment` entitlement. A build that is not uploaded uses a dummy file when the secret is missing; an upload fails without the real one.
4. The server needs the same Firebase project's service-account key (`docs/PUSH.md` step 2-3).

Real iPhone checks pending: background tracking with the screen locked, link opening, camera QR, push (app open, in background, closed).

## Icon and name
The logo in `src/LifeDrop.Mobile/Resources/AppIcon/appiconfg.png` (transparent, on a white background from `appicon.svg`) is the icon on Android and iOS; `Resources/Splash/splash.png` is the splash screen; the panel uses `src/LifeDrop.Web/public/favicon.*`, `apple-touch-icon.png` and `icon-*.png`. The name on the phone is `GeoTrack by Pixelo` unless the build passes `-p:LifeDropAppName="Other name"`. A phone app has one fixed name and icon per build, so another company with its own name and icon (for example a food distributor) needs its own build with its own icon files and name; the company name shown inside the app after sign-in comes from the server.
