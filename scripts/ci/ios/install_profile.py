#!/usr/bin/env python3
"""Decode the App Store provisioning profile, check it against the project and install it.

Reads APPLE_PROVISION_BASE64 from the environment (never from the command line). Prints facts about the profile,
never the secret. Stops with a clear message when the profile cannot work, instead of a vague Apple error later.
Writes GITHUB_OUTPUT (name, uuid, team) and RUNNER_TEMP/profile-cert-sha1.txt (the certificates the profile accepts).
"""
import base64, datetime, hashlib, os, pathlib, plistlib, re, shutil, subprocess, sys

def fail(message):
    print(f"::error::{message}")
    sys.exit(1)

b64 = "".join(os.environ.get("APPLE_PROVISION_BASE64", "").split())
if not b64:
    fail("Secret APPLE_PROVISION_BASE64 is empty. Put the base64 of the .mobileprovision file in it.")
temp = pathlib.Path(os.environ["RUNNER_TEMP"])
raw = temp / "profile.mobileprovision"
try:
    raw.write_bytes(base64.b64decode(b64))
except Exception as e:  # noqa: BLE001
    fail(f"APPLE_PROVISION_BASE64 is not valid base64 ({e}).")
decoded = subprocess.run(["security", "cms", "-D", "-i", str(raw)], capture_output=True)
if decoded.returncode != 0:
    fail("APPLE_PROVISION_BASE64 decodes, but it is not a provisioning profile (security cms cannot read it). Did you encode the right file?")
profile = plistlib.loads(decoded.stdout)

name, uuid = profile["Name"], profile["UUID"]
team = (profile.get("TeamIdentifier") or [""])[0]
entitlements = profile.get("Entitlements", {})
app_id = entitlements.get("application-identifier", "")
expires = profile["ExpirationDate"]
certs = [hashlib.sha1(c).hexdigest().upper() for c in profile.get("DeveloperCertificates", [])]

csproj = pathlib.Path("src/LifeDrop.Mobile/LifeDrop.Mobile.csproj").read_text(encoding="utf-8-sig")
bundle = re.search(r"<ApplicationId>([^<]+)</ApplicationId>", csproj).group(1).strip()

print(f"Profile name:      {name}")
print(f"Profile UUID:      {uuid}")
print(f"Team:              {team}")
print(f"Application id:    {app_id}")
print(f"Expires (UTC):     {expires}")
print(f"Platforms:         {profile.get('Platform')}")
print(f"Certificates:      {len(certs)} accepted")
print(f"Keychain groups:   {entitlements.get('keychain-access-groups')}")
print(f"Project bundle id: {bundle}")

if expires <= datetime.datetime.utcnow():
    fail(f"The profile '{name}' expired on {expires} UTC. Create a new App Store profile in the Apple developer portal.")
if "iOS" not in (profile.get("Platform") or []):
    fail(f"The profile '{name}' is not an iOS profile.")
if "ProvisionedDevices" in profile or profile.get("ProvisionsAllDevices") or entitlements.get("get-task-allow"):
    fail(f"The profile '{name}' is a development, ad-hoc or enterprise profile. TestFlight needs an *App Store* distribution profile.")
if app_id != f"{team}.{bundle}":
    fail(f"The profile is for '{app_id}' but the project's ApplicationId is '{bundle}' (expected '{team}.{bundle}'). Use a profile made for this bundle id.")

for folder in (pathlib.Path.home() / "Library/MobileDevice/Provisioning Profiles",
               pathlib.Path.home() / "Library/Developer/Xcode/UserData/Provisioning Profiles"):
    folder.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(raw, folder / f"{uuid}.mobileprovision")
(temp / "profile-cert-sha1.txt").write_text("\n".join(certs), encoding="utf-8")

with open(os.environ["GITHUB_OUTPUT"], "a", encoding="utf-8") as out:
    out.write(f"name={name}\nuuid={uuid}\nteam={team}\n")
print("Profile is valid for this app and installed.")
