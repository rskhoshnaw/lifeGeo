import os
import sys
import unittest

sys.path.insert(0, os.path.dirname(__file__))
import distribute_testflight as d  # noqa: E402


class FakeApi:
    """Answers the few App Store Connect calls the script makes and records every write."""

    def __init__(self, groups, states=("VALID",), external_state="READY_FOR_BETA_SUBMISSION", auto_notify=False, review_status=201):
        self.groups, self.states, self.external_state, self.auto_notify, self.review_status = groups, list(states), external_state, auto_notify, review_status
        self.writes = []

    def request(self, method, path, body=None):
        if method == "GET" and path.startswith("/v1/apps?"):
            return {"data": [{"id": "APP"}]}
        if method == "GET" and path.startswith("/v1/builds?"):
            state = self.states.pop(0) if len(self.states) > 1 else self.states[0]
            return {"data": [{"id": "BUILD", "attributes": {"processingState": state}}]}
        if method == "GET" and path.endswith("/betaGroups?limit=200"):
            return {"data": self.groups}
        if method == "GET" and path.endswith("/buildBetaDetail"):
            return {"data": {"id": "DETAIL", "attributes": {"autoNotifyEnabled": self.auto_notify, "externalBuildState": self.external_state}}}
        self.writes.append((method, path))
        if path == "/v1/betaAppReviewSubmissions" and self.review_status == 409:
            raise d.ApiError(409, "already submitted")
        return {}


def group(gid, name, internal=False):
    return {"id": gid, "attributes": {"name": name, "isInternalGroup": internal}}


GROUPS = [group("G1", "Team", internal=True), group("G2", "Field staff"), group("G3", "Managers")]


class DistributeTests(unittest.TestCase):
    def run_script(self, api, names=(), submit=True):
        said = []
        result = d.run(api, "com.pixelo.geotrack", "1234", list(names), submit, sleep=lambda s: None, say=said.append)
        return result, said

    def test_every_external_group_gets_the_build_and_review_is_sent_internal_groups_are_left_alone(self):
        api = FakeApi(GROUPS)
        result, _ = self.run_script(api)
        self.assertEqual(result["groups"], ["Field staff", "Managers"])
        added = [w for w in api.writes if w[1].endswith("/relationships/builds")]
        self.assertEqual([w[1] for w in added], ["/v1/betaGroups/G2/relationships/builds", "/v1/betaGroups/G3/relationships/builds"])
        self.assertIn(("POST", "/v1/betaAppReviewSubmissions"), api.writes)
        self.assertIn(("PATCH", "/v1/buildBetaDetails/DETAIL"), api.writes)   # testers are notified by TestFlight

    def test_only_the_named_groups_and_a_missing_name_is_reported(self):
        api = FakeApi(GROUPS)
        result, said = self.run_script(api, names=["field staff", "Nobody"])
        self.assertEqual(result["groups"], ["Field staff"])
        self.assertTrue(any("Nobody" in line for line in said))

    def test_waits_while_apple_processes_and_gives_up_on_a_refused_build(self):
        api = FakeApi(GROUPS, states=["PROCESSING", "PROCESSING", "VALID"])
        result, said = self.run_script(api)
        self.assertEqual(len([line for line in said if "waiting" in line]), 2)
        self.assertEqual(result["groups"], ["Field staff", "Managers"])
        with self.assertRaises(RuntimeError):
            self.run_script(FakeApi(GROUPS, states=["INVALID"]))

    def test_review_is_not_sent_twice_or_when_not_wanted_or_not_needed(self):
        api = FakeApi(GROUPS, review_status=409)
        result, _ = self.run_script(api)
        self.assertIn("409", result["review"])                                   # Apple says it is sent already: not an error
        api = FakeApi(GROUPS, external_state="IN_BETA_REVIEW")
        self.run_script(api)
        self.assertNotIn(("POST", "/v1/betaAppReviewSubmissions"), api.writes)
        api = FakeApi(GROUPS)
        self.run_script(api, submit=False)
        self.assertNotIn(("POST", "/v1/betaAppReviewSubmissions"), api.writes)

    def test_no_external_group_means_nothing_to_do(self):
        api = FakeApi([group("G1", "Team", internal=True)])
        result, _ = self.run_script(api)
        self.assertEqual(result["groups"], [])
        self.assertEqual(api.writes, [])

    def test_the_token_is_a_three_part_es256_jwt(self):
        from cryptography.hazmat.primitives import serialization
        from cryptography.hazmat.primitives.asymmetric import ec
        pem = ec.generate_private_key(ec.SECP256R1()).private_bytes(serialization.Encoding.PEM, serialization.PrivateFormat.PKCS8, serialization.NoEncryption()).decode()
        token = d.build_jwt("issuer-id", "KEYID12345", pem, now=1_700_000_000)
        header, payload, signature = token.split(".")
        self.assertEqual(len(signature), 86)   # 64 raw bytes (r || s) in base64url


if __name__ == "__main__":
    unittest.main()
