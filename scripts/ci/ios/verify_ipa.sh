#!/usr/bin/env bash
# Find the IPA that dotnet publish made and check what is really inside before anything is uploaded.
# Usage: verify_ipa.sh <bundle id> <expected profile uuid>
set -euo pipefail
bundle="$1"; profile_uuid="$2"
ipa="$(find src/LifeDrop.Mobile/bin/Release -name '*.ipa' -type f -print | head -n 1)"
if [ -z "$ipa" ]; then
  echo "::error::dotnet publish finished but no .ipa was made under src/LifeDrop.Mobile/bin/Release."
  find src/LifeDrop.Mobile/bin/Release -maxdepth 4 -type d | head -20
  exit 1
fi
echo "IPA: $ipa ($(du -h "$ipa" | cut -f1))"
work="$(mktemp -d)"; unzip -q "$ipa" -d "$work"
app="$(find "$work/Payload" -maxdepth 1 -name '*.app' -type d | head -n 1)"
plist="$app/Info.plist"
read_plist() { /usr/libexec/PlistBuddy -c "Print :$1" "$plist" 2>/dev/null || echo "(missing)"; }
echo "CFBundleIdentifier:         $(read_plist CFBundleIdentifier)"
echo "CFBundleShortVersionString: $(read_plist CFBundleShortVersionString)"
echo "CFBundleVersion:            $(read_plist CFBundleVersion)"
echo "MinimumOSVersion:           $(read_plist MinimumOSVersion)"
echo "DTXcode:                    $(read_plist DTXcode)"
echo "DTPlatformVersion:          $(read_plist DTPlatformVersion)"
[ "$(read_plist CFBundleIdentifier)" = "$bundle" ] || { echo "::error::The IPA bundle id is not $bundle."; exit 1; }
security cms -D -i "$app/embedded.mobileprovision" > "$work/embedded.plist"
embedded="$(/usr/libexec/PlistBuddy -c 'Print :UUID' "$work/embedded.plist")"
echo "Embedded profile UUID:      $embedded"
[ "$embedded" = "$profile_uuid" ] || { echo "::error::The IPA carries another provisioning profile than the one that was installed."; exit 1; }
codesign -dvv "$app" 2>&1 | grep -E "Identifier|Authority|TeamIdentifier|Signature" || true
codesign --verify --strict --verbose=1 "$app"
echo "ipa=$ipa" >> "$GITHUB_OUTPUT"
