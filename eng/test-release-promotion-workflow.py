#!/usr/bin/env python3
"""Static trust-boundary tests for stable promotion orchestration."""

from __future__ import annotations

import pathlib
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
        self.assertIn("application/vnd.docker.distribution.manifest.list.v2+json", workflow)
        self.assertIn("application/vnd.oci.image.index.v1+json", workflow)
        self.assertIn('Content-Type: $index_media_type', workflow)
        self.assertIn("--data-binary @/tmp/rc-index.json", workflow)
        self.assertIn("--draft=false --latest", workflow)

    def test_read_only_gate_is_separate_from_write_scoped_promotion(self) -> None:
        workflow = PROMOTION.read_text(encoding="utf-8")
        gate = workflow.split("\n  promote:", 1)[0]
        promote = workflow.split("\n  promote:", 1)[1]
        self.assertIn("contents: read", gate)
        self.assertIn("actions: read", gate)
        self.assertNotIn("contents: write", gate)
        self.assertNotIn("packages: write", gate)
        self.assertIn("contents: write", promote)
        self.assertIn("packages: write", promote)

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
