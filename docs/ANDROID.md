# Android release (Google Play)

Package name: `com.pixelo.geotrack`. Build: Actions > **Android release** > Run workflow (manual). It produces a signed app bundle (`.aab`) and keeps it one day as a download on the run page (the repository is public).

## One-time setup (owner)
1. **Upload keystore** (the key that proves updates come from you; if it is lost Play can reset it, but keep two copies). On the server:
   ```bash
   mkdir -p /root/keys && cd /root/keys
   docker run --rm -it -v /root/keys:/keys eclipse-temurin:21-jdk keytool -genkeypair -v -keystore /keys/upload.keystore -alias lifedrop-upload -keyalg RSA -keysize 2048 -validity 10000
   ```
   It asks for a keystore password, your name and country, and the key password (press Enter to use the same one). Write both passwords in a password manager.
2. Copy the file to your PC (PowerShell): `scp root@187.126.3.172:/root/keys/upload.keystore C:\keys\upload.keystore`, keep a backup elsewhere, then base64 it: `[Convert]::ToBase64String([IO.File]::ReadAllBytes("C:\keys\upload.keystore")) | Set-Clipboard`.
3. GitHub secrets (Settings > Secrets and variables > Actions): `ANDROID_KEYSTORE_BASE64` (that text), `ANDROID_KEYSTORE_PASSWORD`, `ANDROID_KEY_ALIAS` (`lifedrop-upload`), `ANDROID_KEY_PASSWORD`, and `ANDROID_GOOGLE_SERVICES_JSON_BASE64` (the `google-services.json` of the Firebase Android app for `com.pixelo.geotrack`, base64 as in `docs/IOS.md`).
4. Play Console: create the app (name "GeoTrack by Pixelo", default language, App, Free), then in the app's page: **Test and release > Internal testing > Create new release**, turn on **Play App Signing**, upload the `.aab`, add testers (e-mails), and roll out.

## What Play asks before production
- Privacy policy URL (the panel page), Data safety form, content rating, target audience, ads declaration.
- **Foreground service permission declaration**: `FOREGROUND_SERVICE_LOCATION` (shift tracking while a shift is on, with a visible notification). Describe it as work-visit tracking started by the user.
- A personal developer account must run a closed test with at least 12 testers for 14 days before production access (check the current rule in Play Console).
- No background-location permission is used (foreground service only), which keeps the review simpler.
