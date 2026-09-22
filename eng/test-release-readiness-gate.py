#!/usr/bin/env python3
"""Tests for the read-only immutable release-readiness gate."""

from __future__ import annotations

import json
import copy
import importlib.util
import os
import pathlib
import shutil
import subprocess
import unittest
from unittest.mock import patch

import release_profile_coverage


REPO_ROOT = pathlib.Path(__file__).resolve().parent.parent
TOOL = REPO_ROOT / "eng" / "release-readiness-gate.py"
SOURCE_SHA = "c885a4b7bfbc35390a32b98139495c19dfb7da0b"
IDENTITY_DIGEST = "sha256:" + "b" * 64
REPOSITORY = "pedrosakuma/aws2azure"
PROFILES = ("dynamodb-basic-crud", "s3-basic-object-crud", "secretsmanager-basic-lifecycle", "sqs-standard-messaging")
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
                if gate["category"] == "observation":
                    evidence = self.observation_receipt(gate)["artifact"]
                    write_json(self.runs / f"artifact-{evidence['id']}.json", {
                        "id": evidence["id"], "name": evidence["name"], "digest": evidence["upload_digest"],
                        "expired": False, "workflow_run": {"id": gate["run_id"]},
                    })
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
                    self.runs / f"artifact-{receipt['artifact_id']}" / receipt["receipt_name"],
                    self.observation_receipt(gate) if gate["category"] == "observation" else {
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
        for category in ("real-azure", "profile", "observation"):
            for profile in PROFILES:
                next_run += 1
                receipt = None if category != "observation" else {
                    "artifact_id": 1000 + next_run,
                    "artifact_name": f"real-azure-rc-observation-selection-{profile}-run-{next_run}-attempt-1",
                    "artifact_upload_digest": "sha256:" + "9" * 64,
                    "receipt_name": "observation-upload-identity.json",
                }
                gates.append(self.gate(category, next_run, profile, candidate_receipt=receipt))
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

    def observation_receipt(self, gate):
        profile, run = gate["profile"], gate["run_id"]
        artifact = {
            "id": 2000 + run,
            "name": f"real-azure-rc-observation-{profile}-run-{run}-attempt-1",
            "upload_digest": "sha256:" + "8" * 64,
        }
        return {
            "schema_version": 1, "profile_id": profile, "release_candidate_id": "v1.2.0-rc.1",
            "release_candidate_identity_digest": IDENTITY_DIGEST,
            "evidence_digest": "sha256:" + "7" * 64, "verdict": "pass",
            "producer": {
                "repository": REPOSITORY, "workflow_path": WORKFLOWS["observation"],
                "run_id": run, "run_attempt": 1,
                "run_url": f"https://github.com/{REPOSITORY}/actions/runs/{run}",
                "source_sha": gate["expected_head_sha"], "source_ref": "refs/heads/main",
            },
            "artifact": artifact,
            "archive_inputs": {"content_digest": "sha256:" + "6" * 64, "producer": {}, "artifact": {}},
            "ghcr_inputs": {"content_digest": "sha256:" + "5" * 64, "producer": {}, "artifact": {},
                            "index_digest": "sha256:" + "4" * 64},
            "manifest_observation": {
                "profile": {"id": profile, "version": 1},
                "identifier": f"github-actions:{REPOSITORY}:run-{run}:attempt-1:artifact-{artifact['id']}:{artifact['upload_digest']}",
                "digest": "sha256:" + "7" * 64, "verdict": "pass",
            },
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
        self.assertEqual(len(receipt["gates"]), 17)

    def test_each_profile_requires_exactly_one_observation_and_qualification(self):
        for category in ("real-azure", "profile", "observation"):
            for profile in PROFILES:
                with self.subTest(category=category, profile=profile):
                    plan = copy.deepcopy(self.plan)
                    selected = next(g for g in plan["gates"] if g["category"] == category and g["profile"] == profile)
                    plan["gates"].remove(selected)
                    write_json(self.plan_path, plan)
                    self.run_tool("validate-plan", str(self.plan_path), expect_success=False)
                    plan["gates"].append(selected)
                    plan["gates"].append({**selected, "name": selected["name"] + "-duplicate"})
                    write_json(self.plan_path, plan)
                    self.run_tool("validate-plan", str(self.plan_path), expect_success=False)

    def test_old_two_profile_and_unscoped_observation_plans_are_rejected(self):
        plan = copy.deepcopy(self.plan)
        plan["gates"] = [g for g in plan["gates"] if g["profile"] not in ("dynamodb-basic-crud", "sqs-standard-messaging")]
        write_json(self.plan_path, plan)
        self.run_tool("validate-plan", str(self.plan_path), expect_success=False)
        plan = copy.deepcopy(self.plan)
        observation = next(g for g in plan["gates"] if g["category"] == "observation")
        observation["profile"] = None
        write_json(self.plan_path, plan)
        self.run_tool("validate-plan", str(self.plan_path), expect_success=False)

    def test_observation_selection_cannot_be_missing_or_for_another_profile_attempt(self):
        for field, value in (("candidate_receipt", None), ("profile", "sns-standard-publish-event-grid")):
            plan = copy.deepcopy(self.plan)
            selected = next(g for g in plan["gates"] if g["category"] == "observation")
            selected[field] = value
            write_json(self.plan_path, plan)
            self.run_tool("validate-plan", str(self.plan_path), expect_success=False)
        for field, value in (("artifact_name", "old-attempt"), ("receipt_name", "generic-pass.json")):
            plan = copy.deepcopy(self.plan)
            selected = next(g for g in plan["gates"] if g["category"] == "observation")
            selected["candidate_receipt"][field] = value
            write_json(self.plan_path, plan)
            self.run_tool("validate-plan", str(self.plan_path), expect_success=False)

    def test_observation_receipts_fail_closed_for_each_profile(self):
        for selected in (g for g in self.plan["gates"] if g["category"] == "observation"):
            expected = selected["candidate_receipt"]
            receipt_path = self.runs / f"artifact-{expected['artifact_id']}" / expected["receipt_name"]
            original = self.observation_receipt(selected)
            for path, value in (
                (("verdict",), "rollback"), (("profile_id",), "other"),
                (("release_candidate_identity_digest",), "sha256:" + "0" * 64),
                (("schema_version",), True), (("producer", "run_attempt"), 2),
                (("producer", "run_attempt"), True),
                (("producer", "source_sha"), "0" * 40),
                (("producer", "repository"), "other/repo"),
                (("producer", "source_ref"), "refs/heads/untrusted"),
                (("artifact", "name"), "other-profile-evidence"),
                (("manifest_observation", "profile"), {"id": "other", "version": 1}),
                (("manifest_observation", "profile"), {"id": selected["profile"], "version": True}),
                (("manifest_observation", "digest"), "sha256:" + "0" * 64),
                (("manifest_observation", "verdict"), "rollback"),
            ):
                with self.subTest(profile=selected["profile"], field=path):
                    value_copy = copy.deepcopy(original)
                    target = value_copy
                    for key in path[:-1]:
                        target = target[key]
                    target[path[-1]] = value
                    write_json(receipt_path, value_copy)
                    self.run_tool("gate", str(self.plan_path), "--run-data-directory", str(self.runs), expect_success=False)
            receipt_path.unlink()
            self.run_tool("gate", str(self.plan_path), "--run-data-directory", str(self.runs), expect_success=False)
            write_json(receipt_path, original)

    def test_each_profile_rejects_failed_runs_and_absent_expired_or_substituted_evidence(self):
        for selected in (g for g in self.plan["gates"] if g["category"] == "observation"):
            run_path = self.runs / f"{selected['run_id']}.json"
            original_run = json.loads(run_path.read_text())
            for conclusion in ("failure", "cancelled", "skipped", None):
                with self.subTest(profile=selected["profile"], conclusion=conclusion):
                    write_json(run_path, {**original_run, "conclusion": conclusion})
                    self.run_tool("gate", str(self.plan_path), "--run-data-directory", str(self.runs), expect_success=False)
            write_json(run_path, original_run)
            evidence = self.observation_receipt(selected)["artifact"]
            path = self.runs / f"artifact-{evidence['id']}.json"
            original = json.loads(path.read_text())
            for field, value in (("expired", True), ("digest", "sha256:" + "0" * 64),
                                 ("workflow_run", {"id": 999}), ("name", "other-profile")):
                with self.subTest(profile=selected["profile"], field=field):
                    write_json(path, {**original, field: value})
                    self.run_tool("gate", str(self.plan_path), "--run-data-directory", str(self.runs), expect_success=False)
            path.unlink()
            self.run_tool("gate", str(self.plan_path), "--run-data-directory", str(self.runs), expect_success=False)
            write_json(path, original)

    def test_certification_malformed_duplicate_missing_and_unknown_verdict_fail(self):
        original = json.loads(release_profile_coverage.CERTIFICATION.read_text())
        report = self.root / "certification.json"
        cases = [[], {}, original + [original[0]], original[1:],
                 [{**original[0], "verdict": "unsupported"}] + original[1:],
                 [{**original[0], "profile_version": 2}] + original[1:]]
        with patch.object(release_profile_coverage, "CERTIFICATION", report):
            for value in cases:
                write_json(report, value)
                with self.assertRaises(SystemExit):
                    release_profile_coverage.required_profiles()
            report.write_text('[{"schema_version":1,"schema_version":1}]')
            with self.assertRaisesRegex(SystemExit, "duplicate"):
                release_profile_coverage.required_profiles()
            report.unlink()
            with self.assertRaises(SystemExit):
                release_profile_coverage.required_profiles()

    def test_new_ga_profile_blocks_until_explicitly_supported_and_downgrade_does_not_shrink_coverage(self):
        rows = json.loads(release_profile_coverage.CERTIFICATION.read_text())
        test_report = self.root / "certification.json"
        for row in rows:
            row["verdict"] = "candidate"
        write_json(test_report, rows)
        with patch.object(release_profile_coverage, "CERTIFICATION", test_report):
            self.assertEqual(release_profile_coverage.required_profiles(), frozenset(PROFILES))
            rows.append({"schema_version": 1, "profile_id": "future-ga", "profile_version": 1, "verdict": "ga"})
            write_json(test_report, rows)
            with self.assertRaisesRegex(SystemExit, "new GA profiles"):
                release_profile_coverage.required_profiles()
            write_json(test_report, rows[:-2])
            with self.assertRaises(SystemExit):
                release_profile_coverage.required_profiles()

    def test_observation_archive_bytes_are_verified_not_just_metadata_digest(self):
        spec = importlib.util.spec_from_file_location("readiness_gate", TOOL)
        module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)
        selected = next(g for g in self.plan["gates"] if g["category"] == "observation")
        artifact_id = selected["candidate_receipt"]["artifact_id"]
        metadata = (self.runs / f"artifact-{artifact_id}.json").read_text()
        responses = [
            subprocess.CompletedProcess([], 0, stdout=metadata, stderr=""),
            subprocess.CompletedProcess([], 0, stdout=b"substituted ZIP", stderr=b""),
        ]
        with patch.object(module.subprocess, "run", side_effect=responses):
            with self.assertRaisesRegex(SystemExit, "ZIP digest mismatch"):
                module.validate_candidate_receipt(REPOSITORY, selected, SOURCE_SHA, IDENTITY_DIGEST, None)

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
