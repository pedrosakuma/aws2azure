#!/usr/bin/env python3
"""Tests for immutable stable-promotion plan validation."""

from __future__ import annotations

import json
import os
import pathlib
import shutil
import subprocess
import unittest


REPO_ROOT = pathlib.Path(__file__).resolve().parent.parent
TOOL = REPO_ROOT / "eng" / "release-promotion.py"
DIGEST = "sha256:" + "a" * 64
SHA = "1" * 40


def write_json(path: pathlib.Path, value: object) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(
        json.dumps(value, sort_keys=True, indent=2) + "\n",
        encoding="utf-8",
    )


class ReleasePromotionTests(unittest.TestCase):
    def setUp(self) -> None:
        self.root = (
            REPO_ROOT
            / "artifacts"
            / f"test-release-promotion-{os.getpid()}-{self._testMethodName}"
        )
        shutil.rmtree(self.root, ignore_errors=True)
        self.plan_path = self.root / "plan.json"
        self.plan = self.make_plan()
        write_json(self.plan_path, self.plan)

    def tearDown(self) -> None:
        shutil.rmtree(self.root, ignore_errors=True)
        try:
            (REPO_ROOT / "artifacts").rmdir()
        except OSError:
            pass

    def producer(self, workflow_path: str, run_id: int) -> dict[str, object]:
        return {
            "workflow_path": workflow_path,
            "run_id": run_id,
            "run_attempt": 1,
            "source_sha": SHA,
        }

    def artifact(self, name: str, artifact_id: int) -> dict[str, object]:
        return {
            "id": artifact_id,
            "name": name,
            "upload_digest": DIGEST,
        }

    def make_plan(self) -> dict[str, object]:
        observations = []
        for index, profile in enumerate(
            ("s3-basic-object-crud", "secretsmanager-basic-lifecycle")
        ):
            observations.append(
                {
                    "profile": profile,
                    "producer": self.producer(
                        ".github/workflows/rc-observation-real-azure.yml",
                        300 + index,
                    ),
                    "selection_artifact": self.artifact(
                        f"selection-{profile}", 400 + index
                    ),
                    "evidence_artifact": self.artifact(
                        f"evidence-{profile}", 500 + index
                    ),
                    "evidence_digest": "sha256:" + str(index + 1) * 64,
                }
            )
        return {
            "schema_version": 1,
            "repository": "pedrosakuma/aws2azure",
            "stable_tag": "v1.0.0",
            "candidate": {
                "identifier": "v1.0.0-rc.1",
                "source_sha": SHA,
                "identity_digest": DIGEST,
            },
            "archive": {
                "producer": self.producer(
                    ".github/workflows/release-candidate.yml", 100
                ),
                "artifact": self.artifact("archive", 200),
                "content_digest": DIGEST,
            },
            "ghcr": {
                "producer": self.producer(
                    ".github/workflows/release-candidate-image.yml", 101
                ),
                "artifact": self.artifact("ghcr", 201),
                "content_digest": DIGEST,
                "index_digest": DIGEST,
            },
            "observations": observations,
            "readiness_plan": "docs/releases/v1.0.0-readiness.json",
            "release_notes": "docs/releases/v1.0.0.md",
        }

    def run_tool(self, *, expect_success: bool = True) -> None:
        result = subprocess.run(
            ["python3", str(TOOL), str(self.plan_path)],
            cwd=REPO_ROOT,
            text=True,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            check=False,
        )
        if expect_success and result.returncode != 0:
            self.fail(f"tool failed:\nstdout={result.stdout}\nstderr={result.stderr}")
        if not expect_success and result.returncode == 0:
            self.fail(f"tool unexpectedly passed:\nstdout={result.stdout}")

    def test_exact_plan_passes(self) -> None:
        self.run_tool()

    def test_stable_candidate_and_profile_drift_fail(self) -> None:
        self.plan["stable_tag"] = "v1.0.1"
        write_json(self.plan_path, self.plan)
        self.run_tool(expect_success=False)

        self.plan = self.make_plan()
        self.plan["observations"][1]["profile"] = "s3-basic-object-crud"
        write_json(self.plan_path, self.plan)
        self.run_tool(expect_success=False)

    def test_unknown_field_and_wrong_workflow_fail(self) -> None:
        self.plan["unknown"] = True
        write_json(self.plan_path, self.plan)
        self.run_tool(expect_success=False)

        self.plan = self.make_plan()
        self.plan["archive"]["producer"]["workflow_path"] = (
            ".github/workflows/release.yml"
        )
        write_json(self.plan_path, self.plan)
        self.run_tool(expect_success=False)


if __name__ == "__main__":
    unittest.main(verbosity=2)
