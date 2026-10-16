#!/usr/bin/env python3
"""Pick the signing identity from the keychain instead of typing its name in the workflow.

Takes the distribution identities that are really in the keychain and keeps the one the provisioning profile accepts.
Writes CODESIGN_KEY to GITHUB_ENV (escaped for MSBuild: comma and semicolon would split the property).
"""
import os, pathlib, re, subprocess, sys

def fail(message):
    print(f"::error::{message}")
    sys.exit(1)

listing = subprocess.run(["security", "find-identity", "-v", "-p", "codesigning"], capture_output=True, text=True).stdout
identities = [(m.group(1).upper(), m.group(2)) for m in re.finditer(r'^\s*\d+\)\s+([0-9A-Fa-f]{40})\s+"(.+)"\s*$', listing, re.M)]
print(f"Signing identities in the keychain: {len(identities)}")
for sha, name in identities:
    print(f"  {sha[:8]}…  {name}")
if not identities:
    fail("The keychain has no usable signing identity. The .p12 must contain the certificate AND its private key (export it from Keychain Access as .p12), and APPLE_CERT_PASSWORD must match it.")
distribution = [(s, n) for s, n in identities if n.startswith(("Apple Distribution", "iPhone Distribution"))]
if not distribution:
    fail("No 'Apple Distribution' identity in the keychain (found a different kind of certificate). TestFlight needs an Apple Distribution certificate.")
accepted = set(pathlib.Path(os.environ["RUNNER_TEMP"], "profile-cert-sha1.txt").read_text().split())
match = [(s, n) for s, n in distribution if s in accepted]
if not match:
    fail("The distribution certificate in the keychain is not one of the certificates inside the provisioning profile. "
         "Recreate the profile in the Apple developer portal choosing THIS certificate, and update APPLE_PROVISION_BASE64.")
sha, name = match[0]
print(f"Using identity: {name}")
escaped = name.replace("%", "%25").replace(";", "%3B").replace(",", "%2C")
with open(os.environ["GITHUB_ENV"], "a", encoding="utf-8") as env:
    env.write(f"CODESIGN_KEY={escaped}\n")
