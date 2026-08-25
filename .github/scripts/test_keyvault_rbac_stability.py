#!/usr/bin/env python3

import json
import time
import unittest
from argparse import Namespace
from pathlib import Path

from keyvault_rbac_stability import (
    FORBIDDEN_BY_RBAC,
    IdentityAuthorization,
    KeyVaultRbacStabilityGate,
    ProbeRequestTimeout,
    ProbeResult,
    UnexpectedAuthorizationResponse,
    build_list_secrets_url,
    classify_response,
    sanitize_diagnostic,
    total_request_timeout,
    verify_stability,
)


class FakeClock:
    def __init__(self) -> None:
        self.now = 0.0
        self.sleeps: list[float] = []

    def monotonic(self) -> float:
        return self.now

    def sleep(self, seconds: float) -> None:
        self.sleeps.append(seconds)
        self.now += seconds


def arguments(**overrides) -> Namespace:
    values = {
        "label": "runtime-identities",
        "vault_url": "https://example.vault.azure.net/",
        "initial_successes": 8,
        "stability_seconds": 300.0,
        "max_wait_seconds": 900.0,
        "initial_probe_interval_seconds": 0.25,
        "propagation_probe_interval_seconds": 5.0,
        "request_timeout_seconds": 30.0,
    }
    values.update(overrides)
    return Namespace(**values)


class KeyVaultRbacStabilityTests(unittest.TestCase):
    def test_full_clean_window_is_required_after_initial_success_burst(self) -> None:
        gate = KeyVaultRbacStabilityGate(
            ("runtime-a", "runtime-b"),
            initial_successes=2,
            stability_seconds=300,
            started_at=10,
        )

        for observed_at in (10, 11):
            gate.observe("runtime-a", ProbeResult(200), observed_at)
            gate.observe("runtime-b", ProbeResult(200), observed_at)

        self.assertFalse(gate.is_ready(309.999))
        self.assertTrue(gate.is_ready(310))
        self.assertEqual(0, gate.rbac_retries)

    def test_forbidden_by_rbac_requires_full_clean_window(self) -> None:
        gate = KeyVaultRbacStabilityGate(
            ("runtime-a", "runtime-b"),
            initial_successes=1,
            stability_seconds=300,
            started_at=0,
        )

        gate.observe("runtime-a", ProbeResult(403, FORBIDDEN_BY_RBAC), 10)
        gate.observe("runtime-a", ProbeResult(200), 309.999)
        gate.observe("runtime-b", ProbeResult(200), 309.999)
        self.assertFalse(gate.is_ready(309.999))
        self.assertTrue(gate.is_ready(310))
        self.assertEqual(1, gate.rbac_retries)

    def test_later_forbidden_resets_clean_window(self) -> None:
        gate = KeyVaultRbacStabilityGate(
            ("runtime-a", "runtime-b"),
            initial_successes=1,
            stability_seconds=300,
            started_at=0,
        )

        gate.observe("runtime-a", ProbeResult(200), 200)
        gate.observe("runtime-b", ProbeResult(200), 200)
        gate.observe("runtime-b", ProbeResult(403, FORBIDDEN_BY_RBAC), 250)
        gate.observe("runtime-a", ProbeResult(200), 549.999)
        gate.observe("runtime-b", ProbeResult(200), 549.999)
        self.assertFalse(gate.is_ready(549.999))
        self.assertTrue(gate.is_ready(550))
        self.assertEqual(1, gate.success_streaks["runtime-a"])
        self.assertEqual(1, gate.rbac_retries)

    def test_non_rbac_forbidden_fails_closed(self) -> None:
        gate = KeyVaultRbacStabilityGate(
            ("runtime-a",),
            initial_successes=2,
            stability_seconds=300,
            started_at=0,
        )

        with self.assertRaisesRegex(
            UnexpectedAuthorizationResponse,
            "inner_error=ForbiddenByFirewall",
        ):
            gate.observe("runtime-a", ProbeResult(403, "ForbiddenByFirewall"), 1)

    def test_sparse_late_denial_after_eight_successes_cannot_pass(self) -> None:
        clock = FakeClock()
        successes = {"runtime-a": 0, "runtime-b": 0}

        def probe(_url: str, authorization: str, _timeout: float) -> ProbeResult:
            label = authorization
            successes[label] += 1
            if label == "runtime-a" and successes[label] == 9:
                return ProbeResult(403, FORBIDDEN_BY_RBAC)
            return ProbeResult(200)

        with self.assertRaises(TimeoutError):
            verify_stability(
                arguments(
                    stability_seconds=10,
                    max_wait_seconds=12,
                    initial_probe_interval_seconds=1,
                    propagation_probe_interval_seconds=1,
                ),
                [
                    IdentityAuthorization("runtime-a", "runtime-a"),
                    IdentityAuthorization("runtime-b", "runtime-b"),
                ],
                monotonic=clock.monotonic,
                sleep=clock.sleep,
                probe=probe,
            )

        self.assertGreaterEqual(successes["runtime-a"], 9)

    def test_success_completing_after_deadline_is_rejected(self) -> None:
        clock = FakeClock()
        request_timeouts: list[float] = []

        def probe(_url: str, _authorization: str, timeout: float) -> ProbeResult:
            request_timeouts.append(timeout)
            clock.now += 10.001
            return ProbeResult(200)

        with self.assertRaises(TimeoutError):
            verify_stability(
                arguments(
                    initial_successes=1,
                    stability_seconds=0,
                    max_wait_seconds=10,
                    request_timeout_seconds=30,
                ),
                [IdentityAuthorization("runtime-a", "authorization")],
                monotonic=clock.monotonic,
                sleep=clock.sleep,
                probe=probe,
            )

        self.assertEqual([10], request_timeouts)

    def test_total_request_timeout_interrupts_slow_response_processing(self) -> None:
        started_at = time.monotonic()

        with self.assertRaises(ProbeRequestTimeout):
            with total_request_timeout(0.02):
                time.sleep(1)

        self.assertLess(time.monotonic() - started_at, 0.5)

    def test_request_timeout_and_sleep_are_capped_to_remaining_time(self) -> None:
        clock = FakeClock()
        request_timeouts: list[float] = []

        def probe(_url: str, _authorization: str, timeout: float) -> ProbeResult:
            request_timeouts.append(timeout)
            if len(request_timeouts) == 1:
                clock.now = 1.5
            return ProbeResult(200)

        with self.assertRaises(TimeoutError):
            verify_stability(
                arguments(
                    initial_successes=1,
                    stability_seconds=10,
                    max_wait_seconds=2,
                    propagation_probe_interval_seconds=5,
                    request_timeout_seconds=30,
                ),
                [
                    IdentityAuthorization("runtime-a", "authorization-a"),
                    IdentityAuthorization("runtime-b", "authorization-b"),
                ],
                monotonic=clock.monotonic,
                sleep=clock.sleep,
                probe=probe,
            )

        self.assertEqual([2, 0.5], request_timeouts)
        self.assertEqual([0.5], clock.sleeps)

    def test_workflow_uses_one_shared_gate_for_both_runtime_identities(self) -> None:
        workflow = (
            Path(__file__).parents[1] / "workflows" / "workload-load-real-azure.yml"
        ).read_text(encoding="utf-8")

        self.assertEqual(
            1,
            workflow.count(
                "python3 ./.github/scripts/keyvault_rbac_stability.py"
            ),
        )
        self.assertIn(
            '--authorization-header-file "runtime-a=$header_a"',
            workflow,
        )
        self.assertIn(
            '--authorization-header-file "runtime-b=$header_b"',
            workflow,
        )
        self.assertIn("token_setup_deadline=$((SECONDS + 300))", workflow)
        self.assertIn('--max-time "$remaining"', workflow)
        self.assertIn("token parsing completed after", workflow)

    def test_only_exact_key_vault_inner_code_is_classified_as_propagation(self) -> None:
        body = json.dumps(
            {"error": {"code": "Forbidden", "innererror": {"code": FORBIDDEN_BY_RBAC}}}
        ).encode()
        malformed = b'{"error":{"innererror":{"code":"ForbiddenByRbac"}}'

        self.assertEqual(FORBIDDEN_BY_RBAC, classify_response(403, body))
        self.assertEqual("", classify_response(401, body))
        self.assertEqual("", classify_response(403, malformed))

    def test_diagnostics_and_url_validation_do_not_accept_unsafe_input(self) -> None:
        self.assertEqual(
            "request-idregion",
            sanitize_diagnostic("request-id\n<region>"),
        )
        self.assertEqual(
            "https://example.vault.azure.net/secrets?api-version=7.6",
            build_list_secrets_url("https://example.vault.azure.net/"),
        )
        with self.assertRaises(ValueError):
            build_list_secrets_url("https://user:secret@example.vault.azure.net/")


if __name__ == "__main__":
    unittest.main(verbosity=2)
