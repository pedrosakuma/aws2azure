#!/usr/bin/env python3
"""Run CI tests with a supervised, atomically rotated projected OIDC assertion."""

from __future__ import annotations

import argparse
import base64
from contextlib import contextmanager
import json
import os
from pathlib import Path
import re
import signal
import subprocess
import sys
import tempfile
import time
from urllib.error import HTTPError, URLError
from urllib.parse import parse_qsl, urlencode, urlsplit, urlunsplit
from urllib.request import HTTPRedirectHandler, Request, build_opener


AUTH_FAILURE = b"Entra ID token request failed with HTTP 401."
MAX_BYTES = 64 * 1024
REQUEST_BUDGET = 30
REFRESH_SECONDS = 240
EXPIRY_MARGIN = 60


class ProjectionError(RuntimeError):
    """Contains only a locally constructed, credential-free message."""


class Cancelled(RuntimeError):
    pass


class NoRedirect(HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


@contextmanager
def request_deadline():
    def expired(_signum, _frame):
        raise ProjectionError("OIDC request exceeded its 30-second budget.")

    previous = signal.signal(signal.SIGALRM, expired)
    signal.setitimer(signal.ITIMER_REAL, REQUEST_BUDGET)
    try:
        yield
    finally:
        signal.setitimer(signal.ITIMER_REAL, 0)
        signal.signal(signal.SIGALRM, previous)


def request_json(request):
    try:
        response = build_opener(NoRedirect()).open(request, timeout=REQUEST_BUDGET)
    except HTTPError as error:
        response = error
    except (URLError, OSError, ValueError):
        raise ProjectionError("OIDC diagnostic/request transport failed.") from None
    with response:
        body = response.read(MAX_BYTES + 1)
        if len(body) > MAX_BYTES:
            raise ProjectionError("OIDC response exceeded the size limit.")
        try:
            value = json.loads(body)
        except (ValueError, UnicodeError):
            raise ProjectionError("OIDC response was not valid JSON.") from None
        if not isinstance(value, dict):
            raise ProjectionError("OIDC response was not a JSON object.")
        return response.status, value


def validity(assertion, now):
    try:
        parts = assertion.split(".")
        if len(parts) != 3 or not all(parts):
            raise ValueError()
        claims = json.loads(base64.b64decode(
            parts[1] + "=" * (-len(parts[1]) % 4), altchars=b"-_", validate=True))
        values = {key: claims[key] for key in ("iat", "nbf", "exp")}
        if any(type(value) is not int or not 0 <= value <= 253402300799
               for value in values.values()):
            raise ValueError()
        if (values["iat"] > now + 5 or values["nbf"] > now + 5
                or values["exp"] <= now + EXPIRY_MARGIN + REQUEST_BUDGET
                or values["exp"] <= max(values["iat"], values["nbf"])):
            raise ValueError()
        return values
    except (ValueError, KeyError, TypeError, UnicodeError):
        raise ProjectionError("OIDC assertion has malformed or unusable validity claims.") from None


def acquire():
    url = os.environ.get("ACTIONS_ID_TOKEN_REQUEST_URL", "")
    request_token = os.environ.get("ACTIONS_ID_TOKEN_REQUEST_TOKEN", "")
    parsed = urlsplit(url)
    if parsed.scheme != "https" or not parsed.netloc or not request_token:
        raise ProjectionError("GitHub OIDC request environment is missing or invalid.")
    query = [(key, value) for key, value in parse_qsl(parsed.query)
             if key != "audience"]
    query.append(("audience", "api://AzureADTokenExchange"))
    url = urlunsplit(parsed._replace(query=urlencode(query)))
    with request_deadline():
        for attempt in range(1, 4):
            status, value = request_json(Request(
                url, headers={"Authorization": "Bearer " + request_token}))
            if status == 200:
                assertion = value.get("value")
                if not isinstance(assertion, str) or not assertion.strip():
                    raise ProjectionError("GitHub OIDC response contained no assertion.")
                return assertion
            if status not in (429, 500, 502, 503, 504) or attempt == 3:
                raise ProjectionError(f"GitHub OIDC request failed with HTTP {status}.")
            time.sleep(attempt)
    raise AssertionError("unreachable")


def atomic_write(path, assertion):
    path.parent.mkdir(mode=0o700, parents=True, exist_ok=True)
    fd, pending = tempfile.mkstemp(prefix=".oidc-", dir=path.parent)
    try:
        with os.fdopen(fd, "w", encoding="utf-8") as stream:
            stream.write(assertion)
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(pending, path)
    finally:
        Path(pending).unlink(missing_ok=True)


class Projection:
    def __init__(self, path, diagnostics):
        self.path = path
        self.diagnostics = diagnostics
        self.next_refresh = 0.0

    def record(self, event, **fields):
        record = {"event": event, "observed_at_epoch": int(time.time()), **fields}
        with self.diagnostics.open("a", encoding="utf-8") as stream:
            stream.write(json.dumps(record, sort_keys=True) + "\n")
        print(json.dumps(record, sort_keys=True), flush=True)

    def refresh(self):
        assertion = acquire()
        now = time.time()
        claims = validity(assertion, now)
        atomic_write(self.path, assertion)
        self.next_refresh = time.monotonic() + min(
            REFRESH_SECONDS, claims["exp"] - now - EXPIRY_MARGIN)
        # These are unverified scheduling hints, not a substitute for Entra validation.
        self.record("assertion_refreshed", **claims, remaining_seconds=int(claims["exp"] - now))

    def diagnose(self):
        """A separate exchange probe, never claimed to be the original proxy response."""
        tenant = os.environ.get("AZURE_TENANT_ID", "")
        client = os.environ.get("AZURE_CLIENT_ID", "")
        if not re.fullmatch(r"[0-9a-fA-F-]{36}", tenant) or not client:
            self.record("entra_probe_unavailable", reason="missing_identity")
            return
        try:
            assertion = self.path.read_text(encoding="utf-8")
            with request_deadline():
                status, response = request_json(Request(
                    f"https://login.microsoftonline.com/{tenant}/oauth2/v2.0/token",
                    data=urlencode({
                        "client_id": client, "grant_type": "client_credentials",
                        "scope": "https://vault.azure.net/.default",
                        "client_assertion_type": "urn:ietf:params:oauth:client-assertion-type:jwt-bearer",
                        "client_assertion": assertion,
                    }).encode(),
                    headers={"Content-Type": "application/x-www-form-urlencoded"}))
            codes = response.get("error_codes", [])
            if not isinstance(codes, list):
                codes = []
            codes = sorted({code for code in codes
                            if type(code) is int and 0 < code <= 2147483647})[:8]
            self.record("entra_probe", http_status=status, error_codes=codes)
        except (ProjectionError, OSError):
            self.record("entra_probe_unavailable", reason="request_failed")


def stop_process_group(process):
    # The test host may have exited while a proxy descendant still holds the group.
    try:
        os.killpg(process.pid, signal.SIGTERM)
    except ProcessLookupError:
        pass
    try:
        process.wait(timeout=5)
    except subprocess.TimeoutExpired:
        pass
    try:
        os.killpg(process.pid, signal.SIGKILL)
    except ProcessLookupError:
        pass
    process.wait(timeout=5)


def run_attempt(command, log_path, projection):
    if projection is not None:
        projection.refresh()
    with log_path.open("wb") as output, log_path.open("rb") as reader:
        process = subprocess.Popen(command, stdout=output, stderr=subprocess.STDOUT,
                                   start_new_session=True)
        try:
            while True:
                chunk = reader.read(64 * 1024)
                if chunk:
                    sys.stdout.buffer.write(chunk)
                    sys.stdout.buffer.flush()
                status = process.poll()
                if status is not None and not chunk:
                    return status
                if status is None and projection is not None:
                    if time.monotonic() >= projection.next_refresh:
                        projection.refresh()
                time.sleep(0.1)
        finally:
            stop_process_group(process)


def contains_auth_failure(path):
    tail = b""
    with path.open("rb") as stream:
        while chunk := stream.read(64 * 1024):
            if AUTH_FAILURE in tail + chunk:
                return True
            tail = chunk[-len(AUTH_FAILURE):]
    return False


def run(command, log_path, projection):
    for attempt in (1, 2):
        attempt_log = log_path.with_name(f"{log_path.stem}-{attempt}{log_path.suffix}")
        status = run_attempt(command, attempt_log, projection)
        if status == 0 or status < 0 or not contains_auth_failure(attempt_log):
            return status if status >= 0 else 128 - status
        if projection is not None:
            projection.diagnose()
        if attempt == 2:
            return status
        print("::warning::Retrying tests once after Entra HTTP 401; "
              "the projected assertion will be refreshed before restarting.", flush=True)
        time.sleep(1)
    raise AssertionError("unreachable")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--log-file", type=Path, required=True)
    parser.add_argument("--diagnostics", type=Path, required=True)
    parser.add_argument("command", nargs=argparse.REMAINDER)
    args = parser.parse_args()
    command = args.command[1:] if args.command[:1] == ["--"] else args.command
    if not command:
        parser.error("a test command is required")

    def cancelled(_signum, _frame):
        raise Cancelled()

    signal.signal(signal.SIGTERM, cancelled)
    signal.signal(signal.SIGINT, cancelled)
    projection = None
    try:
        args.log_file.parent.mkdir(parents=True, exist_ok=True)
        args.diagnostics.parent.mkdir(parents=True, exist_ok=True)
        if os.environ.get("AWS2AZURE_CI_WORKLOAD_IDENTITY") == "true":
            token_file = os.environ.get("AZURE_FEDERATED_TOKEN_FILE", "")
            if not token_file:
                raise ProjectionError("Projected assertion path is missing.")
            projection = Projection(Path(token_file), args.diagnostics)
        return run(command, args.log_file, projection)
    except ProjectionError as error:
        print(f"::error::{error}", flush=True)
        return 1
    except (OSError, ValueError):
        print("::error::Projected OIDC supervisor failed; details suppressed to protect credentials.",
              flush=True)
        return 1
    except Cancelled:
        print("::error::Projected OIDC supervisor cancelled.", flush=True)
        return 130
    finally:
        if projection is not None:
            projection.path.unlink(missing_ok=True)


if __name__ == "__main__":
    sys.exit(main())
