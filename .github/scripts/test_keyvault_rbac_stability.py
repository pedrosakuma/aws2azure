#!/usr/bin/env python3

import json
import unittest

from keyvault_rbac_stability import (
    FORBIDDEN_BY_RBAC,
    KeyVaultRbacStabilityGate,
    ProbeResult,
    UnexpectedAuthorizationResponse,
    build_list_secrets_url,
    classify_response,
    sanitize_diagnostic,
)


class KeyVaultRbacStabilityTests(unittest.TestCase):
    def test_initial_success_burst_is_required(self) -> None:
        gate = KeyVaultRbacStabilityGate(initial_successes=3, stability_seconds=300)

        self.assertFalse(gate.observe(ProbeResult(200), 1))
        self.assertFalse(gate.observe(ProbeResult(200), 2))
        self.assertTrue(gate.observe(ProbeResult(200), 3))
        self.assertEqual(0, gate.rbac_retries)

    def test_forbidden_by_rbac_requires_full_clean_window(self) -> None:
        gate = KeyVaultRbacStabilityGate(initial_successes=2, stability_seconds=300)

        self.assertFalse(
            gate.observe(ProbeResult(403, FORBIDDEN_BY_RBAC), 10)
        )
        self.assertFalse(gate.observe(ProbeResult(200), 309.999))
        self.assertTrue(gate.observe(ProbeResult(200), 310))
        self.assertEqual(1, gate.rbac_retries)

    def test_later_forbidden_resets_clean_window(self) -> None:
        gate = KeyVaultRbacStabilityGate(initial_successes=2, stability_seconds=300)

        gate.observe(ProbeResult(403, FORBIDDEN_BY_RBAC), 10)
        self.assertFalse(gate.observe(ProbeResult(200), 200))
        gate.observe(ProbeResult(403, FORBIDDEN_BY_RBAC), 250)
        self.assertFalse(gate.observe(ProbeResult(200), 549.999))
        self.assertTrue(gate.observe(ProbeResult(200), 550))
        self.assertEqual(2, gate.rbac_retries)

    def test_non_rbac_forbidden_fails_closed(self) -> None:
        gate = KeyVaultRbacStabilityGate(initial_successes=2, stability_seconds=300)

        with self.assertRaisesRegex(
            UnexpectedAuthorizationResponse,
            "inner_error=ForbiddenByFirewall",
        ):
            gate.observe(ProbeResult(403, "ForbiddenByFirewall"), 1)

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
            "https://example.vault.azure.net/secrets?api-version=7.4",
            build_list_secrets_url("https://example.vault.azure.net/"),
        )
        with self.assertRaises(ValueError):
            build_list_secrets_url("https://user:secret@example.vault.azure.net/")


if __name__ == "__main__":
    unittest.main(verbosity=2)
