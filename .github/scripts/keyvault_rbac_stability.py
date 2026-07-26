#!/usr/bin/env python3
"""Verify that newly assigned Key Vault RBAC identities are stably authorized."""

from __future__ import annotations

import argparse
import json
import re
import signal
import sys
import time
from contextlib import contextmanager
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


class ProbeRequestTimeout(TimeoutError):
    """Raised when the total Key Vault request budget expires."""


@dataclass(frozen=True)
class ProbeResult:
    status_code: int
    inner_error_code: str = ""
    request_id: str = ""
    region: str = ""


@dataclass(frozen=True)
class IdentityAuthorization:
    label: str
    authorization_value: str


class KeyVaultRbacStabilityGate:
    """Requires every identity to stay successful for one shared clean window."""

    def __init__(
        self,
        identities: tuple[str, ...],
        initial_successes: int,
        stability_seconds: float,
        started_at: float,
    ) -> None:
        if not identities:
            raise ValueError("at least one identity is required")
        if len(set(identities)) != len(identities):
            raise ValueError("identity labels must be unique")
        if initial_successes <= 0:
            raise ValueError("initial_successes must be positive")
        if stability_seconds < 0:
            raise ValueError("stability_seconds must be non-negative")
        self.identities = identities
        self.initial_successes = initial_successes
        self.stability_seconds = stability_seconds
        self.clean_since = started_at
        self.success_streaks = {identity: 0 for identity in identities}
        self.rbac_retries = 0
        self.first_forbidden_at: float | None = None
        self.last_forbidden_at: float | None = None

    @property
    def initial_burst_complete(self) -> bool:
        return all(
            successes >= self.initial_successes
            for successes in self.success_streaks.values()
        )

    def observe(
        self,
        identity: str,
        result: ProbeResult,
        observed_at: float,
    ) -> None:
        if identity not in self.success_streaks:
            raise ValueError("unknown identity label")
        if result.status_code == 200:
            self.success_streaks[identity] += 1
            return

        if (
            result.status_code == 403
            and result.inner_error_code == FORBIDDEN_BY_RBAC
        ):
            for label in self.success_streaks:
                self.success_streaks[label] = 0
            self.rbac_retries += 1
            if self.first_forbidden_at is None:
                self.first_forbidden_at = observed_at
            self.last_forbidden_at = observed_at
            self.clean_since = observed_at
            return

        inner = sanitize_diagnostic(result.inner_error_code) or "none"
        raise UnexpectedAuthorizationResponse(
            f"Key Vault setup probe returned unexpected HTTP "
            f"{result.status_code} (inner_error={inner})."
        )

    def is_ready(self, observed_at: float) -> bool:
        return (
            self.initial_burst_complete
            and observed_at - self.clean_since >= self.stability_seconds
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


@contextmanager
def total_request_timeout(timeout_seconds: float):
    started_at = time.monotonic()
    previous_handler = signal.getsignal(signal.SIGALRM)

    def raise_timeout(_signum, _frame) -> None:
        raise ProbeRequestTimeout()

    signal.signal(signal.SIGALRM, raise_timeout)
    previous_timer = signal.setitimer(signal.ITIMER_REAL, timeout_seconds)
    try:
        yield
    finally:
        signal.setitimer(signal.ITIMER_REAL, 0)
        signal.signal(signal.SIGALRM, previous_handler)
        if previous_timer[0] > 0:
            elapsed = time.monotonic() - started_at
            signal.setitimer(
                signal.ITIMER_REAL,
                max(0.0, previous_timer[0] - elapsed),
                previous_timer[1],
            )


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
        with total_request_timeout(timeout_seconds):
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
                    headers.get("x-ms-keyvault-region", "")
                    if headers is not None
                    else "",
                )
            except URLError as error:
                raise RuntimeError(
                    "Key Vault setup probe failed at the transport layer."
                ) from error
    except ProbeRequestTimeout as error:
        raise RuntimeError(
            "Key Vault setup probe exceeded its bounded request timeout."
        ) from error


def utc_now() -> datetime:
    return datetime.now(timezone.utc)


def format_utc(value: datetime | None) -> str:
    if value is None:
        return "none"
    return value.isoformat(timespec="milliseconds").replace("+00:00", "Z")


def load_identity_authorizations(
    specifications: list[str],
) -> list[IdentityAuthorization]:
    identities: list[IdentityAuthorization] = []
    labels: set[str] = set()
    for specification in specifications:
        label, separator, path = specification.partition("=")
        if (
            not separator
            or not path
            or not label
            or sanitize_diagnostic(label) != label
        ):
            raise ValueError(
                "authorization header must use a safe non-empty LABEL=PATH value"
            )
        if label in labels:
            raise ValueError("authorization header labels must be unique")
        labels.add(label)
        identities.append(
            IdentityAuthorization(label, read_authorization_value(path))
        )
    return identities


def timeout_error(args: argparse.Namespace, gate: KeyVaultRbacStabilityGate) -> TimeoutError:
    return TimeoutError(
        f"{args.label} Key Vault RBAC propagation did not reach a stable "
        f"ListSecrets authorization state within "
        f"{args.max_wait_seconds:g}s (rbac_retries={gate.rbac_retries})."
    )


def verify_stability(
    args: argparse.Namespace,
    identities: list[IdentityAuthorization],
    *,
    monotonic=time.monotonic,
    sleep=time.sleep,
    probe=probe_key_vault,
    now_utc=utc_now,
) -> None:
    list_url = build_list_secrets_url(args.vault_url)
    started_at = monotonic()
    deadline = started_at + args.max_wait_seconds
    gate = KeyVaultRbacStabilityGate(
        tuple(identity.label for identity in identities),
        args.initial_successes,
        args.stability_seconds,
        started_at,
    )
    first_forbidden_utc: datetime | None = None
    last_forbidden_utc: datetime | None = None

    while True:
        for identity in identities:
            before_probe = monotonic()
            remaining = deadline - before_probe
            if remaining <= 0:
                raise timeout_error(args, gate)
            result = probe(
                list_url,
                identity.authorization_value,
                min(args.request_timeout_seconds, remaining),
            )
            observed_at = monotonic()
            if observed_at > deadline:
                raise timeout_error(args, gate)
            expected_forbidden = (
                result.status_code == 403
                and result.inner_error_code == FORBIDDEN_BY_RBAC
            )
            gate.observe(identity.label, result, observed_at)
            if expected_forbidden:
                observed_utc = now_utc()
                if first_forbidden_utc is None:
                    first_forbidden_utc = observed_utc
                last_forbidden_utc = observed_utc
                request_id = sanitize_diagnostic(result.request_id) or "none"
                region = sanitize_diagnostic(result.region) or "none"
                print(
                    f"{identity.label} Key Vault ListSecrets observed transient "
                    f"{FORBIDDEN_BY_RBAC} at {format_utc(observed_utc)} "
                    f"(request_id={request_id}, region={region}); resetting the "
                    f"shared {args.stability_seconds:g}s clean authorization window.",
                    flush=True,
                )

        observed_at = monotonic()
        if observed_at > deadline:
            raise timeout_error(args, gate)
        if gate.is_ready(observed_at):
            observed_utc = now_utc()
            success_summary = ",".join(
                f"{label}:{gate.success_streaks[label]}"
                for label in gate.identities
            )
            print(
                f"{args.label} Key Vault ListSecrets authorization ready for "
                f"{len(identities)} identities at {format_utc(observed_utc)}; "
                f"successes={success_summary}; rbac_retries={gate.rbac_retries}; "
                f"first_forbidden={format_utc(first_forbidden_utc)}; "
                f"last_forbidden={format_utc(last_forbidden_utc)}.",
                flush=True,
            )
            return

        remaining = deadline - observed_at
        if remaining <= 0:
            raise timeout_error(args, gate)
        interval = (
            args.propagation_probe_interval_seconds
            if gate.initial_burst_complete
            else args.initial_probe_interval_seconds
        )
        sleep(min(interval, remaining))


def verify(args: argparse.Namespace) -> None:
    identities = load_identity_authorizations(args.authorization_header_file)
    verify_stability(args, identities)


def parse_args(argv: list[str]) -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("--label", required=True)
    parser.add_argument("--vault-url", required=True)
    parser.add_argument(
        "--authorization-header-file",
        action="append",
        required=True,
        metavar="LABEL=PATH",
    )
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
    if not args.label or sanitize_diagnostic(args.label) != args.label:
        parser.error("label must contain only safe diagnostic characters")
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
