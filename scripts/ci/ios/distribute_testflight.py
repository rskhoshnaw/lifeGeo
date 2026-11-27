#!/usr/bin/env python3
"""After the IPA is uploaded (plan step 5.65): give the new build to the external TestFlight groups and send it to Beta App Review.

Why: external testers only get a build that was added to their group; before this step a new upload reached the internal testers only and the external ones kept the old build.
Uses the App Store Connect API with the same key as the upload (APPLE_ASC_ISSUER_ID, APPLE_ASC_KEY_ID, APPLE_ASC_PRIVATE_KEY; the key needs the App Manager role).

Environment: ISSUER, KEYID, PRIVKEY (the .p8 text), BUNDLE_ID, BUILD (CFBundleVersion of the upload),
             GROUPS (comma separated group names; empty = every external group), SUBMIT_REVIEW (true/false, default true).
Exit code 0 = done (or nothing to do), 1 = something failed (the message says what; the upload itself is already done)."""
import base64
import json
import os
import sys
import time
import urllib.error
import urllib.parse
import urllib.request

API = "https://api.appstoreconnect.apple.com"


def build_jwt(issuer, key_id, private_key_pem, now=None):
    """ES256 token for the App Store Connect API (valid 15 minutes)."""
    from cryptography.hazmat.primitives import hashes, serialization
    from cryptography.hazmat.primitives.asymmetric import ec
    from cryptography.hazmat.primitives.asymmetric.utils import decode_dss_signature

    def b64(data):
        return base64.urlsafe_b64encode(data).rstrip(b"=")

    now = int(now if now is not None else time.time())
    header = b64(json.dumps({"alg": "ES256", "kid": key_id, "typ": "JWT"}, separators=(",", ":")).encode())
    payload = b64(json.dumps({"iss": issuer, "iat": now, "exp": now + 900, "aud": "appstoreconnect-v1"}, separators=(",", ":")).encode())
    key = serialization.load_pem_private_key(private_key_pem.encode(), password=None)
    r, s = decode_dss_signature(key.sign(header + b"." + payload, ec.ECDSA(hashes.SHA256())))
    return (header + b"." + payload + b"." + b64(r.to_bytes(32, "big") + s.to_bytes(32, "big"))).decode()


class ApiError(Exception):
    def __init__(self, status, body):
        super().__init__(f"HTTP {status}: {body[:600]}")
        self.status = status
        self.body = body


class Api:
    def __init__(self, token):
        self.token = token

    def request(self, method, path, body=None):
        data = json.dumps(body).encode() if body is not None else None
        req = urllib.request.Request(API + path, data=data, method=method)
        req.add_header("Authorization", "Bearer " + self.token)
        if data is not None:
            req.add_header("Content-Type", "application/json")
        try:
            with urllib.request.urlopen(req, timeout=60) as response:
                raw = response.read()
        except urllib.error.HTTPError as error:
            raise ApiError(error.code, error.read().decode("utf-8", "replace")) from None
        return json.loads(raw) if raw else {}


def wanted_groups(groups, names):
    """The external groups to give the build to: those named in `names` (case-insensitive), or every external group when none is named. Unknown names are returned too."""
    external = [g for g in groups if not g["attributes"].get("isInternalGroup", False)]
    if not names:
        return external, []
    lowered = {n.lower() for n in names}
    chosen = [g for g in external if g["attributes"]["name"].lower() in lowered]
    found = {g["attributes"]["name"].lower() for g in chosen}
    return chosen, [n for n in names if n.lower() not in found]


def run(api, bundle_id, build_number, names, submit_review, sleep=time.sleep, say=print, wait_seconds=1800):
    apps = api.request("GET", "/v1/apps?" + urllib.parse.urlencode({"filter[bundleId]": bundle_id, "limit": 1}))["data"]
    if not apps:
        raise RuntimeError(f"no app with bundle id {bundle_id} in App Store Connect")
    app_id = apps[0]["id"]

    # The upload may still be processing: wait (the upload action already waits, this is the safety net).
    waited = 0
    while True:
        builds = api.request("GET", "/v1/builds?" + urllib.parse.urlencode({"filter[app]": app_id, "filter[version]": build_number, "limit": 5}))["data"]
        state = builds[0]["attributes"]["processingState"] if builds else "NOT_FOUND_YET"
        if state == "VALID":
            break
        if state in ("FAILED", "INVALID"):
            raise RuntimeError(f"build {build_number} was not accepted by Apple (processing state {state})")
        if waited >= wait_seconds:
            raise RuntimeError(f"build {build_number} is still '{state}' after {wait_seconds // 60} minutes")
        say(f"build {build_number}: {state}, waiting ...")
        sleep(30)
        waited += 30
    build_id = builds[0]["id"]

    groups = api.request("GET", f"/v1/apps/{app_id}/betaGroups?limit=200")["data"]
    chosen, missing = wanted_groups(groups, names)
    for name in missing:
        say(f"::warning::TestFlight group '{name}' does not exist (or is an internal group); skipped")
    if not chosen:
        say("no external TestFlight group to give the build to; nothing more to do")
        return {"build": build_id, "groups": [], "review": "not needed"}

    for group in chosen:
        api.request("POST", f"/v1/betaGroups/{group['id']}/relationships/builds", {"data": [{"type": "builds", "id": build_id}]})
        say(f"build {build_number} added to the group '{group['attributes']['name']}'")

    # Testers are told by TestFlight as soon as the build may be tested.
    detail = api.request("GET", f"/v1/builds/{build_id}/buildBetaDetail")["data"]
    if not detail["attributes"].get("autoNotifyEnabled", False):
        api.request("PATCH", f"/v1/buildBetaDetails/{detail['id']}", {"data": {"type": "buildBetaDetails", "id": detail["id"], "attributes": {"autoNotifyEnabled": True}}})
        say("testers will be notified automatically")

    review = "not sent (SUBMIT_REVIEW is false)"
    if submit_review:
        external_state = detail["attributes"].get("externalBuildState", "")
        if external_state in ("READY_FOR_BETA_SUBMISSION", ""):
            try:
                api.request("POST", "/v1/betaAppReviewSubmissions", {"data": {"type": "betaAppReviewSubmissions", "relationships": {"build": {"data": {"type": "builds", "id": build_id}}}}})
                review = "sent to Beta App Review"
            except ApiError as error:
                if error.status != 409:
                    raise
                review = "already sent or not needed (Apple answered 409)"
        else:
            review = f"nothing to send (the build is '{external_state}')"
    say(f"Beta App Review: {review}")
    return {"build": build_id, "groups": [g["attributes"]["name"] for g in chosen], "review": review}


def main():
    env = os.environ
    names = [n.strip() for n in env.get("GROUPS", "").split(",") if n.strip()]
    submit = env.get("SUBMIT_REVIEW", "true").strip().lower() != "false"
    try:
        api = Api(build_jwt(env["ISSUER"], env["KEYID"], env["PRIVKEY"]))
        result = run(api, env["BUNDLE_ID"], env["BUILD"], names, submit)
    except (ApiError, RuntimeError, KeyError) as error:
        print(f"::error::TestFlight distribution failed: {error}")
        print("The upload itself is done; give the build to the external group by hand in App Store Connect > TestFlight, or fix the cause and run the workflow again.")
        return 1
    summary = os.environ.get("GITHUB_STEP_SUMMARY")
    if summary:
        with open(summary, "a", encoding="utf-8") as out:
            out.write(f"### TestFlight\nBuild {env['BUILD']}: groups {', '.join(result['groups']) or '-'}; Beta App Review: {result['review']}\n")
    return 0


if __name__ == "__main__":
    sys.exit(main())
