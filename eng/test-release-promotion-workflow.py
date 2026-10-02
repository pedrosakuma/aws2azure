#!/usr/bin/env python3
"""Static trust-boundary tests for stable promotion orchestration."""

from __future__ import annotations

import pathlib
import os
import subprocess
import textwrap
import unittest


REPO_ROOT = pathlib.Path(__file__).resolve().parent.parent
PROMOTION = REPO_ROOT / ".github" / "workflows" / "release-candidate-promote.yml"
PERF = REPO_ROOT / ".github" / "workflows" / "release-candidate-perf.yml"
LEGACY_RELEASE = REPO_ROOT / ".github" / "workflows" / "release.yml"


class ReleasePromotionWorkflowTests(unittest.TestCase):
    def test_promotion_reuses_payloads_and_never_builds_or_clobbers(self) -> None:
        workflow = PROMOTION.read_text(encoding="utf-8")
        self.assertNotIn("dotnet publish", workflow)
        self.assertNotIn("docker build", workflow)
        self.assertNotIn("--clobber", workflow)
        self.assertIn("release-readiness-gate.py gate", workflow)
        self.assertIn("release-candidate-manifest.py finalize", workflow)
        self.assertIn(".workflow_run.id == $run_id", workflow)
        self.assertIn(".digest == $digest", workflow)
        self.assertIn("--target \"$CANDIDATE_SHA\"", workflow)
        self.assertIn("--draft", workflow)
        self.assertGreaterEqual(
            workflow.count('--repo "$GITHUB_REPOSITORY"'),
            4,
        )
        self.assertIn("application/vnd.docker.distribution.manifest.list.v2+json", workflow)
        self.assertIn("application/vnd.oci.image.index.v1+json", workflow)
        self.assertIn('Content-Type: $index_media_type', workflow)
        self.assertIn("--data-binary @/tmp/rc-index.json", workflow)
        self.assertIn("--draft=false", workflow)
        self.assertIn("--latest", workflow)
        self.assertIn("validate-persisted-format-release.py", workflow)
        self.assertIn("--baseline-root previous-stable-source", workflow)
        self.assertIn("--release-notes artifacts/promotion-release-notes.md", workflow)

    def test_read_only_gate_is_separate_from_write_scoped_promotion(self) -> None:
        workflow = PROMOTION.read_text(encoding="utf-8")
        gate = workflow.split("\n  promote:", 1)[0]
        promote = workflow.split("\n  promote:", 1)[1]
        self.assertIn("contents: read", gate)
        self.assertIn("actions: read", gate)
        self.assertNotIn("contents: write", gate)
        self.assertNotIn("packages: write", gate)
        self.assertIn("contents: read", promote)
        self.assertNotIn("contents: write", promote)
        self.assertIn("packages: write", promote)

    def test_dedicated_token_is_confined_to_release_publication(self) -> None:
        workflow = PROMOTION.read_text(encoding="utf-8")
        gate, promote = workflow.split("\n  promote:", 1)
        self.assertNotIn("RELEASE_PUBLISH_TOKEN", gate)
        privileged = (
            "Require dedicated release publication token",
            "Create draft release from exact RC archives",
            "Publish stable release",
        )
        for step in promote.split("      - name: ")[1:]:
            name = step.splitlines()[0]
            if name in privileged:
                self.assertIn("GH_TOKEN: ${{ secrets.RELEASE_PUBLISH_TOKEN }}", step)
                self.assertNotIn("GH_TOKEN: ${{ github.token }}", step)
            else:
                self.assertNotIn("secrets.RELEASE_PUBLISH_TOKEN", step)
        self.assertEqual(3, workflow.count("secrets.RELEASE_PUBLISH_TOKEN"))
        self.assertNotIn("secrets.RELEASE_PUBLISH_TOKEN ||", workflow)
        self.assertIn("persist-credentials: false", promote)

    def test_missing_publication_token_fails_before_checkout_or_writes(self) -> None:
        promote = PROMOTION.read_text(encoding="utf-8").split("\n  promote:", 1)[1]
        guard = promote.split("      - name: Require dedicated release publication token", 1)[1]
        guard = guard.split("\n      - uses:", 1)[0]
        script = textwrap.dedent(guard.split("        run: |\n", 1)[1])
        result = subprocess.run(
            ["bash", "-c", script], env={**os.environ, "GH_TOKEN": ""},
            text=True, capture_output=True, check=False,
        )
        self.assertNotEqual(0, result.returncode)
        self.assertIn("Configure RELEASE_PUBLISH_TOKEN", result.stdout)
        self.assertLess(promote.index("Require dedicated release publication token"),
                        promote.index("uses: actions/checkout"))

    def test_explicit_decision_is_required_before_history_and_rechecked_before_writes(self) -> None:
        workflow = PROMOTION.read_text(encoding="utf-8")
        self.assertIn('REF_PROTECTED: ${{ github.ref_protected }}', workflow)
        self.assertIn('release-promotion.py "$PLAN_PATH" --require-decision', workflow)
        self.assertIn("validate-historical-release-evidence", workflow)
        self.assertNotIn("validate-rc-observation \\", workflow)
        self.assertIn('sha256sum "$zip"', workflow)
        self.assertLess(workflow.index("--require-decision"), workflow.index("Download exact immutable inputs"))
        self.assertLess(workflow.index("validate-historical-release-evidence"), workflow.index("--gate-history"))
        self.assertIn('cp "$(jq -r .evidence_decision "$PLAN_PATH")"', workflow)
        promote = workflow.split("\n  promote:", 1)[1]
        self.assertLess(promote.index("--gate-history"), promote.index("Preflight non-clobbering"))
        self.assertIn("ref: ${{ inputs.orchestration_sha }}", promote)
        self.assertEqual(2, workflow.count('git/ref/heads/main" --jq .object.sha'))

    def test_legacy_release_rejects_v1_rebuilds(self) -> None:
        workflow = LEGACY_RELEASE.read_text(encoding="utf-8")
        self.assertIn("Reject promotion-managed stable tags", workflow)
        self.assertIn("release-candidate-promote.yml", workflow)
        self.assertIn("rebuilding is forbidden", workflow)

    def test_candidate_perf_overlays_only_the_reviewed_harness(self) -> None:
        workflow = PERF.read_text(encoding="utf-8")
        self.assertIn("ref: ${{ inputs.candidate_id }}", workflow)
        self.assertIn("git -C candidate-source rev-parse HEAD", workflow)
        self.assertIn("orchestration/tests/Aws2Azure.PerfTests/", workflow)
        self.assertIn("candidate-source/tests/Aws2Azure.PerfTests/", workflow)
        self.assertIn("git -C candidate-source status --short --", workflow)
        self.assertIn("src Directory.Build.props global.json Dockerfile docker", workflow)
        self.assertIn("working-directory: candidate-source", workflow)
        self.assertIn('"release_candidate_gate"', workflow)


if __name__ == "__main__":
    unittest.main(verbosity=2)
