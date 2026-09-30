#!/usr/bin/env python3
"""Offline fault-injection tests for the real-Azure projected OIDC supervisor."""

import base64
from contextlib import redirect_stdout
import importlib.util
import io
import json
import os
from pathlib import Path
import signal
import subprocess
import sys
import tempfile
import time
import unittest
from unittest.mock import patch
from urllib.error import HTTPError, URLError


ROOT = Path(__file__).resolve().parents[1]
SCRIPT = ROOT / ".github/scripts/run-with-projected-oidc.py"
SPEC = importlib.util.spec_from_file_location("projected_oidc", SCRIPT)
oidc = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(oidc)


def jwt(**changes):
    claims = {"iat": 1000, "nbf": 1000, "exp": 1600,
              "sub": "private-subject", "secret": "never-print-me", **changes}
    payload = base64.urlsafe_b64encode(json.dumps(claims).encode()).decode().rstrip("=")
    return "header." + payload + ".signature"


class ProjectedOidcTests(unittest.TestCase):
    def setUp(self):
        temp = tempfile.TemporaryDirectory(prefix="aws2azure-oidc-test-")
        self.addCleanup(temp.cleanup)
        self.root = Path(temp.name)
        self.token = self.root / "private" / "assertion.jwt"
        self.diagnostics = self.root / "oidc.jsonl"
        self.projection = oidc.Projection(self.token, self.diagnostics)
        self.log = self.root / "attempt.log"
        self.env = {
            "ACTIONS_ID_TOKEN_REQUEST_URL": "https://oidc.test/token?x=y",
            "ACTIONS_ID_TOKEN_REQUEST_TOKEN": "private-request-token",
            "AZURE_TENANT_ID": "00000000-0000-0000-0000-000000000000",
            "AZURE_CLIENT_ID": "private-client",
        }

    def test_validity_rejects_missing_future_expired_and_malformed_claims(self):
        for token in ("not-a-jwt", "a.%%%%.c", "a.W10.c", jwt(iat=None),
                      jwt(iat=True), jwt(iat=1006), jwt(nbf=1006), jwt(exp=1090),
                      jwt(exp=999), jwt(exp=9999999999999999999999)):
            with self.subTest(token=token):
                with self.assertRaises(oidc.ProjectionError):
                    oidc.validity(token, 1000)
        self.assertEqual(oidc.validity(jwt(), 1000),
                         {"iat": 1000, "nbf": 1000, "exp": 1600})

    def test_refresh_is_atomic_private_and_logs_only_validity(self):
        output = io.StringIO()
        with patch.object(oidc, "acquire", side_effect=[jwt(), jwt(exp=1200)]), \
                patch.object(oidc.time, "time", return_value=1000), \
                patch.object(oidc.time, "monotonic", return_value=50), \
                redirect_stdout(output):
            self.projection.refresh()
            self.assertEqual(self.projection.next_refresh, 290)
            with self.token.open() as original:
                self.projection.refresh()
                self.assertEqual(original.read(), jwt())
            self.assertEqual(self.projection.next_refresh, 190)
        self.assertEqual(self.token.read_text(), jwt(exp=1200))
        self.assertEqual(self.token.stat().st_mode & 0o777, 0o600)
        self.assertEqual(self.token.parent.stat().st_mode & 0o777, 0o700)
        self.assertEqual(list(self.token.parent.glob(".oidc-*")), [])
        records = [json.loads(line) for line in self.diagnostics.read_text().splitlines()]
        self.assertEqual(len(records), 2)
        self.assertEqual(set(records[0]), {"event", "observed_at_epoch", "iat", "nbf",
                                           "exp", "remaining_seconds"})
        for secret in ("private-subject", "never-print-me", jwt()):
            self.assertNotIn(secret, output.getvalue() + self.diagnostics.read_text())

    def test_failed_atomic_replace_keeps_previous_assertion_and_removes_temporary(self):
        oidc.atomic_write(self.token, "old")
        with patch.object(oidc.os, "replace", side_effect=OSError("private details")):
            with self.assertRaises(OSError):
                oidc.atomic_write(self.token, "new")
        self.assertEqual(self.token.read_text(), "old")
        self.assertEqual(list(self.token.parent.glob(".oidc-*")), [])

    def test_acquire_uses_audience_and_bounded_transient_retries(self):
        with patch.dict(os.environ, self.env, clear=True), \
                patch.object(oidc, "request_json",
                             side_effect=[(503, {}), (429, {}), (200, {"value": jwt()})]) as request, \
                patch.object(oidc.time, "sleep") as sleep:
            self.assertEqual(oidc.acquire(), jwt())
        self.assertEqual(request.call_count, 3)
        self.assertEqual([call.args[0] for call in sleep.call_args_list], [1, 2])
        sent = request.call_args.args[0]
        self.assertIn("audience=api%3A%2F%2FAzureADTokenExchange", sent.full_url)
        self.assertEqual(sent.get_header("Authorization"), "Bearer private-request-token")
        self.assertEqual(signal.getitimer(signal.ITIMER_REAL)[0], 0)

    def test_acquire_does_not_retry_auth_or_accept_empty_success(self):
        for result in ((401, {"error_description": "private"}), (200, {"value": ""})):
            with patch.dict(os.environ, self.env, clear=True), \
                    patch.object(oidc, "request_json", return_value=result) as request:
                with self.assertRaises(oidc.ProjectionError) as error:
                    oidc.acquire()
                self.assertNotIn("private", str(error.exception))
                self.assertEqual(request.call_count, 1)

    def test_network_deadline_is_total_not_just_a_socket_timeout(self):
        with patch.object(oidc, "REQUEST_BUDGET", 0.02):
            with self.assertRaisesRegex(oidc.ProjectionError, "budget"):
                with oidc.request_deadline():
                    time.sleep(0.1)
        self.assertEqual(signal.getitimer(signal.ITIMER_REAL)[0], 0)

    def test_transport_json_size_and_redirect_failures_never_leak_response(self):
        class Response(io.BytesIO):
            status = 200
        for data in (b"private invalid body", b"[]", b"x" * (oidc.MAX_BYTES + 1)):
            with patch.object(oidc, "build_opener") as opener:
                opener.return_value.open.return_value = Response(data)
                with self.assertRaises(oidc.ProjectionError) as error:
                    oidc.request_json(oidc.Request("https://oidc.test"))
                self.assertNotIn("private", str(error.exception))
        with patch.object(oidc, "build_opener") as opener:
            opener.return_value.open.side_effect = URLError("private url/token")
            with self.assertRaises(oidc.ProjectionError) as error:
                oidc.request_json(oidc.Request("https://oidc.test"))
            self.assertNotIn("private", str(error.exception))
        self.assertIsNone(oidc.NoRedirect().redirect_request(None, None, 302, "", {},
                                                          "https://other.test"))
        with patch.object(oidc, "build_opener") as opener:
            opener.return_value.open.side_effect = HTTPError(
                "https://oidc.test", 401, "private", {}, io.BytesIO(b'{"error_codes":[700024]}'))
            self.assertEqual(oidc.request_json(oidc.Request("https://oidc.test")),
                             (401, {"error_codes": [700024]}))

    def test_diagnostic_probe_retains_numeric_codes_only(self):
        oidc.atomic_write(self.token, jwt())
        output = io.StringIO()
        with patch.dict(os.environ, self.env, clear=True), \
                patch.object(oidc, "request_json", return_value=(401, {
                    "error_codes": [700024, 700024, "secret", True, -1, {"x": "secret"}],
                    "error_description": "private raw provider details",
                    "access_token": "private-access-token",
                })) as request, redirect_stdout(output):
            self.projection.diagnose()
        record = json.loads(self.diagnostics.read_text())
        self.assertEqual(record["error_codes"], [700024])
        self.assertEqual(record["event"], "entra_probe")
        self.assertEqual(record["http_status"], 401)
        for secret in ("private", jwt(), "secret"):
            self.assertNotIn(secret, output.getvalue() + self.diagnostics.read_text())
        self.assertIn(b"client_assertion=", request.call_args.args[0].data)

    def test_probe_transport_failure_is_explicit_and_does_not_raise(self):
        oidc.atomic_write(self.token, jwt())
        with patch.dict(os.environ, self.env, clear=True), \
                patch.object(oidc, "request_json", side_effect=oidc.ProjectionError("failure")), \
                redirect_stdout(io.StringIO()):
            self.projection.diagnose()
        self.assertEqual(json.loads(self.diagnostics.read_text())["event"],
                         "entra_probe_unavailable")

    def test_no_identity_cli_preserves_exit_code_without_network(self):
        with patch.dict(os.environ, {}, clear=True):
            result = subprocess.run(
                [sys.executable, str(SCRIPT), "--log-file", str(self.log),
                 "--diagnostics", str(self.diagnostics), "--", sys.executable, "-c",
                 "import sys; sys.exit(7)"], capture_output=True, timeout=10)
        self.assertEqual(result.returncode, 7, result.stderr)
        self.assertFalse(self.token.exists())
        self.assertFalse(self.diagnostics.exists())
        self.assertTrue((self.root / "attempt-1.log").exists())
        self.assertFalse((self.root / "attempt-2.log").exists())

    def test_401_retries_once_with_fresh_assertion_and_preserves_both_logs(self):
        tokens_seen = []
        projection = unittest.mock.Mock()
        projection.next_refresh = float("inf")
        projection.refresh.side_effect = lambda: tokens_seen.append("fresh")
        command = [sys.executable, "-c",
                   "import sys; print('Entra ID token request failed with HTTP 401.'); sys.exit(9)"]
        result = oidc.run(command, self.log, projection)
        self.assertEqual(result, 9)
        self.assertEqual(tokens_seen, ["fresh", "fresh"])
        self.assertEqual(projection.diagnose.call_count, 2)
        self.assertTrue((self.root / "attempt-1.log").exists())
        self.assertTrue((self.root / "attempt-2.log").exists())

    def test_refresh_failure_prevents_command_start(self):
        projection = unittest.mock.Mock()
        projection.refresh.side_effect = oidc.ProjectionError("refresh failed")
        with patch.object(oidc.subprocess, "Popen") as start:
            with self.assertRaises(oidc.ProjectionError):
                oidc.run(["unused"], self.log, projection)
        start.assert_not_called()

    def test_refresh_failure_stops_running_process(self):
        pid_file = self.root / "pid"
        projection = unittest.mock.Mock()
        def refresh():
            if pid_file.exists():
                raise oidc.ProjectionError("refresh failed")
            projection.next_refresh = time.monotonic() + 0.1
        projection.refresh.side_effect = refresh
        command = [sys.executable, "-c",
                   "import os,time,pathlib; pathlib.Path(" + repr(str(pid_file)) +
                   ").write_text(str(os.getpid())); time.sleep(30)"]
        with self.assertRaises(oidc.ProjectionError):
            oidc.run(command, self.log, projection)
        self.assertTrue(pid_file.exists())
        with self.assertRaises(ProcessLookupError):
            os.kill(int(pid_file.read_text()), 0)

    def test_refresh_occurs_during_long_running_command(self):
        projection = unittest.mock.Mock()
        projection.refresh.side_effect = lambda: setattr(
            projection, "next_refresh", time.monotonic() + 0.1)
        self.assertEqual(oidc.run(
            [sys.executable, "-c", "import time; time.sleep(0.5)"],
            self.log, projection), 0)
        self.assertGreaterEqual(projection.refresh.call_count, 2)

    def test_cancellation_kills_child_and_cleans_token(self):
        pid_file = self.root / "pid"
        # Import the real entrypoint in a subprocess, replacing only acquisition.
        driver = (
            "import runpy,time,json,base64;"
            f"m=runpy.run_path({str(SCRIPT)!r});"
            "p=base64.urlsafe_b64encode(json.dumps("
            "{'iat':int(time.time()),'nbf':int(time.time()),'exp':int(time.time())+600}"
            ").encode()).decode().rstrip('=');"
            "m['main'].__globals__['acquire']=lambda:'a.'+p+'.s';"
            "raise SystemExit(m['main']())"
        )
        child = ("import os,time,pathlib;pathlib.Path(" + repr(str(pid_file)) +
                 ").write_text(str(os.getpid()));time.sleep(30)")
        process = subprocess.Popen(
            [sys.executable, "-c", driver, "--log-file", str(self.log),
             "--diagnostics", str(self.diagnostics), "--", sys.executable, "-c", child],
            env={**os.environ, "AWS2AZURE_CI_WORKLOAD_IDENTITY": "true",
                 "AZURE_FEDERATED_TOKEN_FILE": str(self.token)},
            stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        try:
            deadline = time.monotonic() + 5
            while not pid_file.exists() and process.poll() is None and time.monotonic() < deadline:
                time.sleep(0.02)
            self.assertTrue(pid_file.exists())
            process.terminate()
            stdout, stderr = process.communicate(timeout=10)
            self.assertEqual(process.returncode, 130, stderr)
            self.assertIn(b"cancelled", stdout)
            self.assertFalse(self.token.exists())
            with self.assertRaises(ProcessLookupError):
                os.kill(int(pid_file.read_text()), 0)
        finally:
            if process.poll() is None:
                process.terminate()
                process.communicate(timeout=10)

    def test_workflow_wires_both_stages_and_artifact_modes(self):
        workflow = (ROOT / ".github/workflows/integration-real-azure.yml").read_text()
        self.assertEqual(workflow.count("python3 .github/scripts/run-with-projected-oidc.py"), 2)
        self.assertEqual(workflow.count("AWS2AZURE_CI_WORKLOAD_IDENTITY:"), 2)
        self.assertEqual(workflow.count("TestResults/real-azure/oidc-*.jsonl"), 2)
        self.assertNotIn("run_with_entra_401_retry", workflow)
        self.assertNotIn("ACTIONS_ID_TOKEN_REQUEST_TOKEN", workflow)
        for step in ("Run planned integration conformance tests",
                     "Export shared happy-path case evidence"):
            text = workflow.split("- name: " + step, 1)[1].split("\n      - name:", 1)[0]
            self.assertIn("if: always()", text)
            self.assertIn("set +e", text)
            self.assertIn('echo "exit_code=$status" >> "$GITHUB_OUTPUT"', text)


if __name__ == "__main__":
    unittest.main()
