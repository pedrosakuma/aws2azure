#!/usr/bin/env python3
"""Tests for immutable stable-promotion plan validation."""

from __future__ import annotations

import json
import copy
import datetime as dt
import importlib.util
import os
import pathlib
import shutil
import subprocess
import unittest
from unittest import mock


REPO_ROOT = pathlib.Path(__file__).resolve().parent.parent
TOOL = REPO_ROOT / "eng" / "release-promotion.py"
DIGEST = "sha256:" + "a" * 64
SHA = "1" * 40
SPEC = importlib.util.spec_from_file_location("promotion", TOOL)
promotion = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(promotion)


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
            ("s3-basic-object-crud", "secretsmanager-basic-lifecycle", "dynamodb-basic-crud", "sqs-standard-messaging")
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

    def test_omitting_any_required_observation_fails(self) -> None:
        for index in range(4):
            with self.subTest(index=index):
                plan = self.make_plan()
                del plan["observations"][index]
                write_json(self.plan_path, plan)
                self.run_tool(expect_success=False)

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


class EvidenceDecisionTests(unittest.TestCase):
    def setUp(self) -> None:
        self.root = REPO_ROOT / "artifacts" / f"test-evidence-decision-{os.getpid()}-{self._testMethodName}"
        self.root.mkdir(parents=True)
        subprocess.run(["git", "init", "--quiet", str(self.root)], check=True)
        self.plan = ReleasePromotionTests.make_plan(self)
        self.plan["schema_version"] = 2
        self.plan["evidence_decision"] = "docs/releases/decision.json"
        self.plan_path = self.root / "docs/releases/plan.json"
        write_json(self.plan_path, self.plan)
        for field in ("readiness_plan", "release_notes"):
            path = self.root / self.plan[field]
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text("{}\n")
        (self.root / "src").mkdir()
        (self.root / "src/runtime.cs").write_text("unchanged runtime")
        subprocess.run(["git", "add", "."], cwd=self.root, check=True)
        self.now = dt.datetime(2026, 10, 2, 12, tzinfo=dt.timezone.utc)
        self.decision_path = self.root / self.plan["evidence_decision"]
        self.decision = {
            "schema_version": 1, "policy": promotion.DECISION_POLICY, "status": "approved",
            "candidate_identity_digest": DIGEST,
            "reviewed_inputs_digest": promotion.review_inputs_digest(self.root, self.plan_path, self.plan),
            "reviewed_at_utc": "2026-10-02T11:00:00Z", "owner": "release-owner",
            "review_url": "https://github.com/pedrosakuma/aws2azure/pull/123",
            "changes": {"status": "no_invalidating_changes", "rationale": "Exact runtime and contract reviewed.",
                        "references": ["https://github.com/pedrosakuma/aws2azure/issues/1062"]},
            "incidents": {"status": "no_unresolved_incidents", "rationale": "Known incidents reviewed.",
                         "references": ["https://github.com/pedrosakuma/aws2azure/issues/1062"]},
            "profiles": [
                {"profile": o["profile"], "decision": "reuse", "rationale": "Historical coverage remains applicable.",
                 "checks": []} for o in self.plan["observations"]
            ],
        }
        self.history = {
            "schema_version": 1, "policy": promotion.DECISION_POLICY, "valid_when_issued": True,
            "release_eligible": False, "current_health_verified": False,
            "candidate_identity_digest": DIGEST, "evaluated_at_utc": self.now.isoformat(),
            "profiles": [
                {"profile": o["profile"], "observation_digest": o["evidence_digest"],
                 "qualification_issued_at_utc": "2026-09-20T12:00:00Z",
                 "observation_issued_at_utc": "2026-09-21T12:00:00Z"}
                for o in self.plan["observations"]
            ],
            "source_artifacts": [{
                "repository": self.plan["repository"], "profile_id": observation["profile"],
                "run_id": 100, "run_attempt": 1, "workflow_path": ".github/workflows/integration-real-azure.yml",
                "head_sha": SHA, "head_ref": "refs/heads/main", "event_name": "workflow_dispatch",
                "artifact": self.artifact("qualification-source", 200),
            } for observation in self.plan["observations"]],
        }
        self.history_path = self.root / "artifacts/history.json"

    producer = ReleasePromotionTests.producer
    artifact = ReleasePromotionTests.artifact

    def tearDown(self) -> None:
        shutil.rmtree(self.root)

    def validate(self):
        write_json(self.decision_path, self.decision)
        return promotion.validate_decision(self.root, self.plan_path, self.plan, self.now)

    def gate(self):
        write_json(self.decision_path, self.decision)
        write_json(self.history_path, self.history)
        return promotion.gate_history(self.root, self.plan_path, self.plan, self.history_path, self.now)

    def add_check(self):
        check = {
            "id": "external-compatibility", "purpose": "Check external API compatibility, not a new long observation.",
            "rationale": "Explicit review window for this incident.",
            "status": "pass", "not_before_utc": "2026-10-02T08:00:00Z", "valid_until_utc": "2026-10-02T13:00:00Z",
            "producer": self.producer(".github/workflows/integration-real-azure.yml", 900),
            "artifact": self.artifact("health-evidence", 901),
        }
        self.decision["profiles"][0]["decision"] = "checks_required"
        self.decision["profiles"][0]["checks"] = [check]
        return check

    def test_pending_rc5_and_legacy_plans_cannot_promote(self):
        for value in ("pending", "blocked"):
            with self.subTest(status=value):
                self.decision["status"] = value
                with self.assertRaises(SystemExit):
                    self.validate()
        self.plan["schema_version"] = 1
        with self.assertRaises(SystemExit):
            self.validate()
        rc5 = REPO_ROOT / "docs/releases/v1.1.1-promotion.json"
        plan = promotion.validate_plan(rc5)
        with self.assertRaisesRegex(SystemExit, "pending"):
            promotion.validate_decision(REPO_ROOT, rc5, plan, self.now)

    def test_historical_age_alone_does_not_trigger_renewal(self):
        with mock.patch.object(promotion, "verify_live_source") as verify:
            result = self.gate()
        self.assertTrue(result["release_eligible"])
        self.assertFalse(result["canonical_evidence_renewed"])
        self.assertEqual(self.history["profiles"][0]["observation_issued_at_utc"], "2026-09-21T12:00:00Z")
        verify.assert_called_once()

    def test_review_inputs_drift_invalidates_approval(self):
        for name in ("src/runtime.cs", self.plan["readiness_plan"], self.plan["release_notes"]):
            path = self.root / name
            original = path.read_text()
            path.write_text(original + "changed")
            with self.subTest(path=name), self.assertRaisesRegex(SystemExit, "inputs changed"):
                self.validate()
            path.write_text(original)
        added = self.root / "tools/new-validator.py"
        added.parent.mkdir()
        added.write_text("new implementation")
        with self.assertRaisesRegex(SystemExit, "inputs changed"):
            self.validate()
        added.unlink()
        self.plan["ghcr"]["index_digest"] = "sha256:" + "b" * 64
        write_json(self.plan_path, self.plan)
        with self.assertRaisesRegex(SystemExit, "inputs changed"):
            self.validate()

    def test_incomplete_impact_incident_review_and_owner_block(self):
        original = copy.deepcopy(self.decision)
        for section, value in (("changes", "invalidating_changes"), ("incidents", "unresolved_incidents")):
            self.decision = copy.deepcopy(original)
            self.decision[section]["status"] = value
            with self.subTest(section=section), self.assertRaises(SystemExit):
                self.validate()
        for field, value in (("owner", " "), ("review_url", "https://example.com/"),
                             ("reviewed_at_utc", "2026-10-03T00:00:00Z"),
                             ("candidate_identity_digest", "sha256:" + "b" * 64)):
            self.decision = copy.deepcopy(original)
            self.decision[field] = value
            with self.subTest(field=field), self.assertRaises(SystemExit):
                self.validate()

    def test_missing_duplicate_or_pending_profile_blocks(self):
        original = copy.deepcopy(self.decision)
        for mutation in ("missing", "duplicate", "pending", "missing-check", "hidden-check"):
            self.decision = copy.deepcopy(original)
            if mutation == "missing":
                self.decision["profiles"].pop()
            elif mutation == "duplicate":
                self.decision["profiles"][1] = self.decision["profiles"][0]
            elif mutation == "pending":
                self.decision["profiles"][0]["decision"] = "pending"
            elif mutation == "missing-check":
                self.decision["profiles"][0]["decision"] = "checks_required"
            else:
                self.decision["profiles"][0]["checks"] = [{}]
            with self.subTest(mutation=mutation), self.assertRaises(SystemExit):
                self.validate()

    def test_required_check_pending_failed_expired_or_future_blocks(self):
        check = self.add_check()
        original = copy.deepcopy(check)
        for field, value in (("status", "pending"), ("status", "failure"),
                             ("valid_until_utc", "2026-10-02T11:59:59Z"),
                             ("not_before_utc", "2026-10-02T12:30:00Z"),
                             ("purpose", " ")):
            check.clear()
            check.update(original)
            check[field] = value
            with self.subTest(field=field, value=value), self.assertRaises(SystemExit):
                self.validate()

    def test_accepted_health_check_is_reverified_but_does_not_replace_history(self):
        self.add_check()
        with mock.patch.object(promotion, "verify_live_source",
                               return_value={"run_started_at": "2026-10-02T09:00:00Z", "updated_at": "2026-10-02T10:00:00Z"}) as verify:
            self.assertTrue(self.gate()["release_eligible"])
            self.assertEqual(2, verify.call_count)
            self.history["valid_when_issued"] = False
            with self.assertRaises(SystemExit):
                self.gate()

    def test_unavailable_or_failed_source_cannot_be_waived_by_review(self):
        with mock.patch.object(promotion, "verify_live_source", side_effect=SystemExit("artifact unavailable")):
            with self.assertRaisesRegex(SystemExit, "artifact unavailable"):
                self.gate()

    def test_history_identity_coverage_and_time_must_match_review(self):
        original = copy.deepcopy(self.history)
        for mutation in ("candidate", "missing-profile", "wrong-evidence", "post-review", "missing-source", "missing-source-profile"):
            self.history = copy.deepcopy(original)
            if mutation == "candidate":
                self.history["candidate_identity_digest"] = "sha256:" + "b" * 64
            elif mutation == "missing-profile":
                self.history["profiles"].pop()
            elif mutation == "wrong-evidence":
                self.history["profiles"][0]["observation_digest"] = "sha256:" + "b" * 64
            elif mutation == "post-review":
                self.history["profiles"][0]["qualification_issued_at_utc"] = "2026-10-02T11:30:00Z"
            elif mutation == "missing-source":
                self.history["source_artifacts"] = []
            else:
                self.history["source_artifacts"].pop()
            with self.subTest(mutation=mutation), self.assertRaises(SystemExit):
                self.gate()

    def test_live_source_requires_exact_successful_run_and_unexpired_artifact(self):
        producer = self.producer(".github/workflows/integration-real-azure.yml", 100)
        artifact = self.artifact("evidence", 200)
        run = {
            "id": 100, "run_attempt": 1, "path": producer["workflow_path"], "head_sha": SHA,
            "event": "workflow_dispatch", "status": "completed", "conclusion": "success",
            "head_branch": "main", "repository": {"full_name": self.plan["repository"]},
        }
        metadata = {"id": 200, "name": "evidence", "digest": DIGEST, "expired": False, "workflow_run": {"id": 100}}
        with mock.patch.object(promotion, "api_json", side_effect=[run, metadata]):
            promotion.verify_live_source(self.plan["repository"], producer, artifact)
        for field, value in (("run_attempt", 2), ("conclusion", "failure"), ("head_sha", "2"*40),
                             ("head_branch", "unreviewed"), ("event", "pull_request")):
            changed = {**run, field: value}
            with self.subTest(field=field), mock.patch.object(promotion, "api_json", return_value=changed), self.assertRaises(SystemExit):
                promotion.verify_live_source(self.plan["repository"], producer, artifact)
        for field, value in (("expired", True), ("digest", "sha256:" + "b"*64), ("workflow_run", {"id": 999})):
            with self.subTest(field=field), mock.patch.object(promotion, "api_json", side_effect=[run, {**metadata, field: value}]), self.assertRaises(SystemExit):
                promotion.verify_live_source(self.plan["repository"], producer, artifact)


if __name__ == "__main__":
    unittest.main(verbosity=2)
