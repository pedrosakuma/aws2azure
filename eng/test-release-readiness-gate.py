#!/usr/bin/env python3
"""Tests for the read-only immutable release-readiness gate."""

from __future__ import annotations

import json
import os
import pathlib
import shutil
import subprocess
import unittest


REPO_ROOT = pathlib.Path(__file__).resolve().parent.parent
TOOL = REPO_ROOT / "eng" / "release-readiness-gate.py"
SOURCE_SHA = "c885a4b7bfbc35390a32b98139495c19dfb7da0b"
IDENTITY_DIGEST = "sha256:" + "b" * 64
REPOSITORY = "pedrosakuma/aws2azure"
PROFILES = ("s3-basic-object-crud", "secretsmanager-basic-lifecycle")
WORKFLOWS = {
    "ci": ".github/workflows/ci.yml",
    "aot": ".github/workflows/sealed-runtime.yml",
    "conformance": ".github/workflows/conformance.yml",
    "real-azure": ".github/workflows/integration-real-azure.yml",
    "perf": ".github/workflows/release-candidate-perf.yml",
    "footprint": ".github/workflows/footprint.yml",
    "profile": ".github/workflows/qualification-real-azure.yml",
    "observation": ".github/workflows/rc-observation-real-azure.yml",
}


def write_json(path: pathlib.Path, value: object) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(
        json.dumps(value, sort_keys=True, indent=2) + "\n",
        encoding="utf-8",
    )


class ReleaseReadinessGateTests(unittest.TestCase):
    def setUp(self) -> None:
        self.root = (
            REPO_ROOT
            / "artifacts"
            / f"test-release-readiness-{os.getpid()}-{self._testMethodName}"
        )
        shutil.rmtree(self.root, ignore_errors=True)
        self.runs = self.root / "runs"
        self.plan_path = self.root / "plan.json"
        self.plan = self.make_plan()
        write_json(self.plan_path, self.plan)
        for gate in self.plan["gates"]:
            write_json(
                self.runs / f"{gate['run_id']}.json",
                {
                    "id": gate["run_id"],
                    "run_attempt": gate["run_attempt"],
                    "event": gate["event"],
                    "status": "completed",
                    "conclusion": "success",
                    "head_sha": gate["expected_head_sha"],
                    "path": gate["workflow_path"],
                    "html_url": (
                        f"https://github.com/{REPOSITORY}/actions/runs/"
                        f"{gate['run_id']}"
                    ),
                    "repository": REPOSITORY,
                },
            )
            if gate["candidate_receipt"] is not None:
                receipt = gate["candidate_receipt"]
                write_json(
                    self.runs / f"artifact-{receipt['artifact_id']}.json",
                    {
                        "id": receipt["artifact_id"],
                        "name": receipt["artifact_name"],
                        "digest": receipt["artifact_upload_digest"],
                        "expired": False,
                        "workflow_run": {"id": gate["run_id"]},
                    },
                )
                write_json(
                    self.runs / receipt["receipt_name"],
                    {
                        "schema_version": 1,
                        "artifact_kind": "release_candidate_gate",
                        "repository": REPOSITORY,
                        "workflow_path": gate["workflow_path"],
                        "run_id": gate["run_id"],
                        "run_attempt": gate["run_attempt"],
                        "candidate_source_sha": SOURCE_SHA,
                        "orchestration_source_sha": gate["expected_head_sha"],
                        "verdict": "pass",
                    },
                )

    def tearDown(self) -> None:
        shutil.rmtree(self.root, ignore_errors=True)
        try:
            (REPO_ROOT / "artifacts").rmdir()
        except OSError:
            pass

    def make_plan(self) -> dict[str, object]:
        gates: list[dict[str, object]] = []
        next_run = 100
        for category in ("ci", "aot", "conformance", "perf", "footprint"):
            next_run += 1
            if category == "perf":
                gates.append(
                    self.gate(
                        category,
                        next_run,
                        expected_head_sha="3" * 40,
                        candidate_receipt={
                            "artifact_id": 900,
                            "artifact_name": "release-candidate-perf",
                            "artifact_upload_digest": "sha256:" + "9" * 64,
                            "receipt_name": "release-candidate-perf.json",
                        },
                    )
                )
            else:
                gates.append(self.gate(category, next_run))
        for category in ("real-azure", "profile"):
            for profile in PROFILES:
                next_run += 1
                gates.append(self.gate(category, next_run, profile))
        next_run += 1
        gates.append(
            self.gate(
                "observation",
                next_run,
                expected_head_sha="1" * 40,
            )
        )
        return {
            "schema_version": 1,
            "repository": REPOSITORY,
            "candidate_source_sha": SOURCE_SHA,
            "candidate_identity_digest": IDENTITY_DIGEST,
            "gates": gates,
        }

    def gate(
        self,
        category: str,
        run_id: int,
        profile: str | None = None,
        *,
        expected_head_sha: str = SOURCE_SHA,
        candidate_receipt: dict[str, object] | None = None,
    ) -> dict[str, object]:
        return {
            "name": f"{category}-{profile or 'candidate'}",
            "category": category,
            "profile": profile,
            "workflow_path": WORKFLOWS[category],
            "run_id": run_id,
            "run_attempt": 1,
            "event": "workflow_dispatch" if category != "ci" else "push",
            "expected_head_sha": expected_head_sha,
            "candidate_receipt": candidate_receipt,
        }

    def run_tool(
        self, *arguments: str, expect_success: bool = True
    ) -> subprocess.CompletedProcess[str]:
        result = subprocess.run(
            ["python3", str(TOOL), *arguments],
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
        return result

    def test_complete_exact_run_set_passes(self) -> None:
        result = self.run_tool(
            "gate",
            str(self.plan_path),
            "--run-data-directory",
            str(self.runs),
        )
        receipt = json.loads(result.stdout)
        self.assertEqual(receipt["verdict"], "pass")
        self.assertEqual(receipt["candidate_source_sha"], SOURCE_SHA)
        self.assertEqual(len(receipt["gates"]), 10)

    def test_missing_profile_gate_and_duplicate_category_fail(self) -> None:
        self.plan["gates"] = [
            gate
            for gate in self.plan["gates"]
            if gate["name"] != "profile-secretsmanager-basic-lifecycle"
        ]
        write_json(self.plan_path, self.plan)
        self.run_tool("validate-plan", str(self.plan_path), expect_success=False)

        self.plan = self.make_plan()
        duplicate = dict(self.plan["gates"][0])
        duplicate["name"] = "ci-duplicate"
        duplicate["run_id"] = 999
        self.plan["gates"].append(duplicate)
        write_json(self.plan_path, self.plan)
        self.run_tool("validate-plan", str(self.plan_path), expect_success=False)

    def test_failed_wrong_sha_or_wrong_workflow_run_fails_closed(self) -> None:
        gate = self.plan["gates"][0]
        run_path = self.runs / f"{gate['run_id']}.json"
        run = json.loads(run_path.read_text(encoding="utf-8"))
        run["conclusion"] = "failure"
        write_json(run_path, run)
        self.run_tool(
            "gate",
            str(self.plan_path),
            "--run-data-directory",
            str(self.runs),
            expect_success=False,
        )

        run["conclusion"] = "success"
        run["head_sha"] = "2" * 40
        write_json(run_path, run)
        self.run_tool(
            "gate",
            str(self.plan_path),
            "--run-data-directory",
            str(self.runs),
            expect_success=False,
        )

        run["head_sha"] = SOURCE_SHA
        run["path"] = ".github/workflows/release.yml"
        write_json(run_path, run)
        self.run_tool(
            "gate",
            str(self.plan_path),
            "--run-data-directory",
            str(self.runs),
            expect_success=False,
        )


if __name__ == "__main__":
    unittest.main(verbosity=2)
