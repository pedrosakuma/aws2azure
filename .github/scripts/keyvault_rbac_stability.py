#!/usr/bin/env python3
"""Verify that a newly assigned Key Vault RBAC identity is stably authorized."""

from __future__ import annotations

import argparse
import json
import re
import sys
import time
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path
from urllib.error import HTTPError, URLError
from urllib.parse import urlsplit, urlunsplit
from urllib.request import HTTPRedirectHandler, Request, build_opener


FORBIDDEN_BY_RBAC = "ForbiddenByRbac"
MAX_RESPONSE_BYTES = 64 * 1024
SAFE_DIAGNOSTIC = re.compile(r"[^A-Za-z0-9._:-]")


class NoRedirectHandler(HTTPRedirectHandler):
    def redirect_request(
        self,
        request,
        file_pointer,
        code,
        message,
        headers,
        new_url,
    ):
        return None


URL_OPENER = build_opener(NoRedirectHandler())


class UnexpectedAuthorizationResponse(RuntimeError):
    """Raised when a Key Vault readiness response is not setup propagation."""


@dataclass(frozen=True)
class ProbeResult:
    status_code: int
    inner_error_code: str = ""
    request_id: str = ""
    region: str = ""


class KeyVaultRbacStabilityGate:
    """Requires either an initial success burst or a clean post-403 window."""

    def __init__(self, initial_successes: int, stability_seconds: float) -> None:
        if initial_successes <= 0:
            raise ValueError("initial_successes must be positive")
        if stability_seconds < 0:
            raise ValueError("stability_seconds must be non-negative")
        self.initial_successes = initial_successes
        self.stability_seconds = stability_seconds
        self.success_streak = 0
        self.rbac_retries = 0
        self.first_forbidden_at: float | None = None
        self.last_forbidden_at: float | None = None

    @property
    def observed_propagation(self) -> bool:
        return self.last_forbidden_at is not None

    def observe(self, result: ProbeResult, observed_at: float) -> bool:
        if result.status_code == 200:
            self.success_streak += 1
            if self.last_forbidden_at is None:
                return self.success_streak >= self.initial_successes
            return observed_at - self.last_forbidden_at >= self.stability_seconds

        if (
            result.status_code == 403
            and result.inner_error_code == FORBIDDEN_BY_RBAC
        ):
            self.success_streak = 0
            self.rbac_retries += 1
            if self.first_forbidden_at is None:
                self.first_forbidden_at = observed_at
            self.last_forbidden_at = observed_at
            return False

        inner = sanitize_diagnostic(result.inner_error_code) or "none"
        raise UnexpectedAuthorizationResponse(
            f"Key Vault setup probe returned unexpected HTTP "
            f"{result.status_code} (inner_error={inner})."
        )


def sanitize_diagnostic(value: str) -> str:
    return SAFE_DIAGNOSTIC.sub("", value)[:128]


def read_authorization_value(path: str) -> str:
    raw = Path(path).read_text(encoding="utf-8")
    line = raw.rstrip("\r\n")
    if "\r" in line or "\n" in line:
        raise ValueError("authorization header file must contain exactly one line")
    prefix = "Authorization: Bearer "
    if not line.startswith(prefix):
        raise ValueError("authorization header file has an unexpected format")
    token = line[len(prefix) :]
    if not token or any(character.isspace() for character in token):
        raise ValueError("authorization header file contains an invalid bearer token")
    return "Bearer " + token


def build_list_secrets_url(vault_url: str) -> str:
    parsed = urlsplit(vault_url)
    if (
        parsed.scheme != "https"
        or not parsed.netloc
        or parsed.username is not None
        or parsed.password is not None
        or parsed.query
        or parsed.fragment
        or parsed.path not in ("", "/")
    ):
        raise ValueError("vault URL must be an HTTPS Key Vault origin")
    return urlunsplit((parsed.scheme, parsed.netloc, "/secrets", "api-version=7.4", ""))


def classify_response(status_code: int, body: bytes) -> str:
    if status_code != 403:
        return ""
    if len(body) > MAX_RESPONSE_BYTES:
        return ""
    try:
        payload = json.loads(body)
    except (json.JSONDecodeError, UnicodeDecodeError):
        return ""
    error = payload.get("error")
    if not isinstance(error, dict):
        return ""
    inner = error.get("innererror")
    if not isinstance(inner, dict):
        inner = error.get("innerError")
    if not isinstance(inner, dict):
        return ""
    code = inner.get("code")
    return code if isinstance(code, str) else ""


def probe_key_vault(
    list_url: str,
    authorization_value: str,
    timeout_seconds: float,
) -> ProbeResult:
    request = Request(
        list_url,
        headers={
            "Authorization": authorization_value,
            "Accept": "application/json",
            "Connection": "close",
        },
        method="GET",
    )
    try:
        with URL_OPENER.open(request, timeout=timeout_seconds) as response:
            response.read(MAX_RESPONSE_BYTES + 1)
            return ProbeResult(
                response.status,
                request_id=response.headers.get("x-ms-request-id", ""),
                region=response.headers.get("x-ms-keyvault-region", ""),
            )
    except HTTPError as error:
        body = error.read(MAX_RESPONSE_BYTES + 1)
        headers = error.headers
        return ProbeResult(
            error.code,
            classify_response(error.code, body),
            headers.get("x-ms-request-id", "") if headers is not None else "",
            headers.get("x-ms-keyvault-region", "") if headers is not None else "",
        )
    except URLError as error:
        raise RuntimeError("Key Vault setup probe failed at the transport layer.") from error


def utc_now() -> datetime:
    return datetime.now(timezone.utc)


def format_utc(value: datetime | None) -> str:
    if value is None:
        return "none"
    return value.isoformat(timespec="milliseconds").replace("+00:00", "Z")


def verify(args: argparse.Namespace) -> None:
    authorization_value = read_authorization_value(args.authorization_header_file)
    list_url = build_list_secrets_url(args.vault_url)
    gate = KeyVaultRbacStabilityGate(
        args.initial_successes,
        args.stability_seconds,
    )
    started_at = time.monotonic()
    deadline = started_at + args.max_wait_seconds
    first_forbidden_utc: datetime | None = None
    last_forbidden_utc: datetime | None = None

    while True:
        result = probe_key_vault(
            list_url,
            authorization_value,
            args.request_timeout_seconds,
        )
        observed_at = time.monotonic()
        observed_utc = utc_now()
        expected_forbidden = (
            result.status_code == 403
            and result.inner_error_code == FORBIDDEN_BY_RBAC
        )
        ready = gate.observe(result, observed_at)
        if expected_forbidden:
            if first_forbidden_utc is None:
                first_forbidden_utc = observed_utc
            last_forbidden_utc = observed_utc
            request_id = sanitize_diagnostic(result.request_id) or "none"
            region = sanitize_diagnostic(result.region) or "none"
            print(
                f"{args.label} Key Vault ListSecrets observed transient "
                f"{FORBIDDEN_BY_RBAC} at {format_utc(observed_utc)} "
                f"(request_id={request_id}, region={region}); resetting the "
                f"{args.stability_seconds:g}s clean authorization window.",
                flush=True,
            )

        if ready:
            print(
                f"{args.label} Key Vault ListSecrets authorization ready at "
                f"{format_utc(observed_utc)}; initial_successes="
                f"{gate.success_streak}; rbac_retries={gate.rbac_retries}; "
                f"first_forbidden={format_utc(first_forbidden_utc)}; "
                f"last_forbidden={format_utc(last_forbidden_utc)}.",
                flush=True,
            )
            return

        now = time.monotonic()
        if now >= deadline:
            raise TimeoutError(
                f"{args.label} Key Vault RBAC propagation did not reach a stable "
                f"ListSecrets authorization state within "
                f"{args.max_wait_seconds:g}s (rbac_retries={gate.rbac_retries})."
            )
        interval = (
            args.propagation_probe_interval_seconds
            if gate.observed_propagation
            else args.initial_probe_interval_seconds
        )
        time.sleep(min(interval, max(0.0, deadline - now)))


def parse_args(argv: list[str]) -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("--label", required=True)
    parser.add_argument("--vault-url", required=True)
    parser.add_argument("--authorization-header-file", required=True)
    parser.add_argument("--initial-successes", type=int, default=8)
    parser.add_argument("--stability-seconds", type=float, default=300)
    parser.add_argument("--max-wait-seconds", type=float, default=900)
    parser.add_argument("--initial-probe-interval-seconds", type=float, default=0.25)
    parser.add_argument(
        "--propagation-probe-interval-seconds",
        type=float,
        default=5,
    )
    parser.add_argument("--request-timeout-seconds", type=float, default=30)
    args = parser.parse_args(argv)
    for name in (
        "max_wait_seconds",
        "initial_probe_interval_seconds",
        "propagation_probe_interval_seconds",
        "request_timeout_seconds",
    ):
        if getattr(args, name) <= 0:
            parser.error(name.replace("_", "-") + " must be positive")
    return args


def main(argv: list[str]) -> int:
    try:
        verify(parse_args(argv))
    except (
        OSError,
        RuntimeError,
        TimeoutError,
        UnexpectedAuthorizationResponse,
        ValueError,
    ) as error:
        print(f"error: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
