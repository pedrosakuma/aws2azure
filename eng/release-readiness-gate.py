#!/usr/bin/env python3
"""Validate immutable release gates without dispatching or mutating workflows."""

from __future__ import annotations

import argparse
import hashlib
import json
import pathlib
import re
import subprocess
import tempfile
import zipfile
from typing import Any, NoReturn

from release_profile_coverage import required_profiles

SCHEMA_VERSION = 1
SHA_RE = re.compile(r"[0-9a-f]{40}")
REPOSITORY_RE = re.compile(r"[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+")
REQUIRED_SINGLETONS = {
    "ci",
    "aot",
    "conformance",
    "perf",
    "footprint",
}
PROFILE_CATEGORIES = {"real-azure", "profile", "observation"}
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


def fail(message: str) -> NoReturn:
    raise SystemExit(f"release-readiness-gate: {message}")


def duplicate_object(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
    result: dict[str, Any] = {}
    for key, value in pairs:
        if key in result:
            fail(f"duplicate JSON field: {key}")
        result[key] = value
    return result


def load_json(path: pathlib.Path) -> Any:
    try:
        with path.open("r", encoding="utf-8") as stream:
            return json.load(stream, object_pairs_hook=duplicate_object)
    except (OSError, UnicodeError, json.JSONDecodeError) as error:
        fail(f"cannot read JSON {path}: {error}")


def require_object(value: Any, name: str, keys: set[str]) -> dict[str, Any]:
    if not isinstance(value, dict) or set(value) != keys:
        fail(f"{name} must contain exact fields: {', '.join(sorted(keys))}")
    return value


def require_string(value: Any, name: str) -> str:
    if not isinstance(value, str) or not value:
        fail(f"{name} must be a non-empty string")
    return value


def require_positive_integer(value: Any, name: str) -> int:
    if isinstance(value, bool) or not isinstance(value, int) or value <= 0:
        fail(f"{name} must be a positive integer")
    return value


def canonical_digest(value: dict[str, Any]) -> str:
    encoded = json.dumps(
        value, sort_keys=True, separators=(",", ":"), ensure_ascii=False
    ).encode("utf-8")
    return f"sha256:{hashlib.sha256(encoded).hexdigest()}"


def validate_plan(path: pathlib.Path) -> dict[str, Any]:
    supported_profiles = required_profiles()
    plan = require_object(
        load_json(path),
        "plan",
        {
            "schema_version",
            "repository",
            "candidate_source_sha",
            "candidate_identity_digest",
            "gates",
        },
    )
    if plan["schema_version"] != SCHEMA_VERSION:
        fail(f"schema_version must be {SCHEMA_VERSION}")
    repository = require_string(plan["repository"], "repository")
    if REPOSITORY_RE.fullmatch(repository) is None:
        fail("repository must be an owner/repository identity")
    source_sha = require_string(plan["candidate_source_sha"], "candidate_source_sha")
    if SHA_RE.fullmatch(source_sha) is None:
        fail("candidate_source_sha must be a lowercase 40-character SHA")
    identity_digest = require_string(
        plan["candidate_identity_digest"], "candidate_identity_digest"
    )
    if re.fullmatch(r"sha256:[0-9a-f]{64}", identity_digest) is None:
        fail("candidate_identity_digest must be a lowercase sha256 digest")
    if not isinstance(plan["gates"], list) or not plan["gates"]:
        fail("gates must be a non-empty array")

    singleton_counts = {category: 0 for category in REQUIRED_SINGLETONS}
    profile_coverage = {category: set() for category in PROFILE_CATEGORIES}
    seen_names: set[str] = set()
    for index, value in enumerate(plan["gates"]):
        gate = require_object(
            value,
            f"gates[{index}]",
            {
                "name",
                "category",
                "profile",
                "workflow_path",
                "run_id",
                "run_attempt",
                "event",
                "expected_head_sha",
                "candidate_receipt",
            },
        )
        name = require_string(gate["name"], f"gates[{index}].name")
        if name in seen_names:
            fail(f"duplicate gate name: {name}")
        seen_names.add(name)
        category = require_string(gate["category"], f"gates[{index}].category")
        if category not in WORKFLOWS:
            fail(f"unsupported gate category: {category}")
        if gate["workflow_path"] != WORKFLOWS[category]:
            fail(f"{name} uses the wrong workflow for category {category}")
        require_positive_integer(gate["run_id"], f"gates[{index}].run_id")
        require_positive_integer(gate["run_attempt"], f"gates[{index}].run_attempt")
        require_string(gate["event"], f"gates[{index}].event")
        expected_head_sha = require_string(
            gate["expected_head_sha"], f"gates[{index}].expected_head_sha"
        )
        if SHA_RE.fullmatch(expected_head_sha) is None:
            fail(f"{name} expected_head_sha is invalid")
        profile = gate["profile"]
        if category in PROFILE_CATEGORIES:
            if profile not in supported_profiles:
                fail(f"{name} must identify a supported profile")
            if profile in profile_coverage[category]:
                fail(f"duplicate {category} gate for {profile}")
            profile_coverage[category].add(profile)
        elif profile is not None:
            fail(f"{name} must not declare a profile")
        if category in REQUIRED_SINGLETONS:
            singleton_counts[category] += 1
        candidate_receipt = gate["candidate_receipt"]
        if category == "observation" and candidate_receipt is None:
            fail(f"{name} requires its immutable profile observation selection receipt")
        if candidate_receipt is not None:
            receipt = require_object(
                candidate_receipt,
                f"gates[{index}].candidate_receipt",
                {
                    "artifact_id",
                    "artifact_name",
                    "artifact_upload_digest",
                    "receipt_name",
                },
            )
            require_positive_integer(
                receipt["artifact_id"],
                f"gates[{index}].candidate_receipt.artifact_id",
            )
            require_string(
                receipt["artifact_name"],
                f"gates[{index}].candidate_receipt.artifact_name",
            )
            digest = require_string(
                receipt["artifact_upload_digest"],
                f"gates[{index}].candidate_receipt.artifact_upload_digest",
            )
            if re.fullmatch(r"sha256:[0-9a-f]{64}", digest) is None:
                fail(f"{name} candidate receipt upload digest is invalid")
            receipt_name = require_string(
                receipt["receipt_name"],
                f"gates[{index}].candidate_receipt.receipt_name",
            )
            if pathlib.PurePosixPath(receipt_name).name != receipt_name:
                fail(f"{name} candidate receipt name must be a basename")
            if category == "observation":
                expected_name = (
                    f"real-azure-rc-observation-selection-{profile}"
                    f"-run-{gate['run_id']}-attempt-{gate['run_attempt']}"
                )
                if receipt["artifact_name"] != expected_name or receipt_name != "observation-upload-identity.json":
                    fail(f"{name} must select the exact profile/run/attempt observation receipt")

    missing_singletons = sorted(
        category for category, count in singleton_counts.items() if count != 1
    )
    if missing_singletons:
        fail(
            "singleton gate categories must appear exactly once: "
            + ", ".join(missing_singletons)
        )
    for category, profiles in profile_coverage.items():
        if profiles != supported_profiles:
            fail(f"{category} gates must cover every required release profile: " + ", ".join(sorted(supported_profiles)))
    return plan


def load_run(
    repository: str,
    run_id: int,
    run_data_directory: pathlib.Path | None,
) -> dict[str, Any]:
    if run_data_directory is not None:
        return require_object(
            load_json(run_data_directory / f"{run_id}.json"),
            f"run {run_id}",
            {
                "id",
                "run_attempt",
                "event",
                "status",
                "conclusion",
                "head_sha",
                "path",
                "html_url",
                "repository",
            },
        )
    result = subprocess.run(
        [
            "gh",
            "api",
            f"repos/{repository}/actions/runs/{run_id}",
        ],
        text=True,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        check=False,
    )
    if result.returncode != 0:
        fail(f"cannot resolve workflow run {run_id}: {result.stderr.strip()}")
    try:
        payload = json.loads(result.stdout, object_pairs_hook=duplicate_object)
    except json.JSONDecodeError as error:
        fail(f"workflow run {run_id} returned invalid JSON: {error}")
    payload["repository"] = payload.get("repository", {}).get("full_name")
    return payload


def validate_candidate_receipt(
    repository: str,
    gate: dict[str, Any],
    candidate_source_sha: str,
    candidate_identity_digest: str,
    run_data_directory: pathlib.Path | None,
) -> None:
    expected = gate["candidate_receipt"]
    if expected is None:
        return
    artifact_id = expected["artifact_id"]
    if run_data_directory is not None:
        metadata = load_json(run_data_directory / f"artifact-{artifact_id}.json")
        receipt = load_json(run_data_directory / f"artifact-{artifact_id}" / expected["receipt_name"])
    else:
        metadata_result = subprocess.run(
            [
                "gh",
                "api",
                f"repos/{repository}/actions/artifacts/{artifact_id}",
            ],
            text=True,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            check=False,
        )
        if metadata_result.returncode != 0:
            fail(
                f"cannot resolve candidate receipt artifact {artifact_id}: "
                f"{metadata_result.stderr.strip()}"
            )
        metadata = json.loads(
            metadata_result.stdout, object_pairs_hook=duplicate_object
        )
        with tempfile.TemporaryDirectory() as temporary:
            archive_path = pathlib.Path(temporary) / "artifact.zip"
            archive_result = subprocess.run(
                [
                    "gh",
                    "api",
                    f"repos/{repository}/actions/artifacts/{artifact_id}/zip",
                ],
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
                check=False,
            )
            if archive_result.returncode != 0:
                fail(
                    f"cannot download candidate receipt artifact {artifact_id}: "
                    f"{archive_result.stderr.decode(errors='replace').strip()}"
                )
            archive_path.write_bytes(archive_result.stdout)
            if "sha256:" + hashlib.sha256(archive_result.stdout).hexdigest() != expected["artifact_upload_digest"]:
                fail(f"candidate receipt artifact {artifact_id} ZIP digest mismatch")
            try:
                with zipfile.ZipFile(archive_path) as archive:
                    names = archive.namelist()
                    if names != [expected["receipt_name"]]:
                        fail(
                            f"candidate receipt artifact {artifact_id} must contain "
                            f"only {expected['receipt_name']}"
                        )
                    receipt = json.loads(
                        archive.read(expected["receipt_name"]),
                        object_pairs_hook=duplicate_object,
                    )
            except (OSError, zipfile.BadZipFile, UnicodeError, json.JSONDecodeError) as error:
                fail(f"candidate receipt artifact {artifact_id} is invalid: {error}")
    workflow_run = metadata.get("workflow_run", {})
    required_metadata = {
        "id": artifact_id,
        "name": expected["artifact_name"],
        "digest": expected["artifact_upload_digest"],
        "expired": False,
    }
    for key, value in required_metadata.items():
        if metadata.get(key) != value:
            fail(
                f"candidate receipt artifact {artifact_id} has {key}="
                f"{metadata.get(key)!r}, expected {value!r}"
            )
    if workflow_run.get("id") != gate["run_id"]:
        fail(f"candidate receipt artifact {artifact_id} belongs to another run")
    if gate["category"] == "observation":
        validate_observation_receipt(receipt, repository, gate, candidate_identity_digest)
        evidence = receipt["artifact"]
        if run_data_directory is not None:
            evidence_metadata = load_json(run_data_directory / f"artifact-{evidence['id']}.json")
        else:
            response = subprocess.run(
                ["gh", "api", f"repos/{repository}/actions/artifacts/{evidence['id']}"],
                text=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE, check=False,
            )
            if response.returncode != 0:
                fail(f"cannot resolve observation evidence artifact {evidence['id']}: {response.stderr.strip()}")
            evidence_metadata = json.loads(response.stdout, object_pairs_hook=duplicate_object)
        if (evidence_metadata.get("id") != evidence["id"]
                or evidence_metadata.get("name") != evidence["name"]
                or evidence_metadata.get("digest") != evidence["upload_digest"]
                or evidence_metadata.get("expired") is not False
                or evidence_metadata.get("workflow_run", {}).get("id") != gate["run_id"]):
            fail("observation evidence artifact is absent, expired or does not match its selection")
        return
    receipt = require_object(
        receipt,
        f"gate {gate['name']} candidate receipt",
        {
            "schema_version",
            "artifact_kind",
            "repository",
            "workflow_path",
            "run_id",
            "run_attempt",
            "candidate_source_sha",
            "orchestration_source_sha",
            "verdict",
        },
    )
    required_receipt = {
        "schema_version": SCHEMA_VERSION,
        "artifact_kind": "release_candidate_gate",
        "repository": repository,
        "workflow_path": gate["workflow_path"],
        "run_id": gate["run_id"],
        "run_attempt": gate["run_attempt"],
        "candidate_source_sha": candidate_source_sha,
        "orchestration_source_sha": gate["expected_head_sha"],
        "verdict": "pass",
    }
    if receipt != required_receipt:
        fail(f"gate {gate['name']} candidate receipt does not match the trusted run")


def validate_observation_receipt(
    value: Any, repository: str, gate: dict[str, Any], candidate_identity_digest: str,
) -> None:
    receipt = require_object(value, "observation selection", {
        "schema_version", "profile_id", "release_candidate_id",
        "release_candidate_identity_digest", "evidence_digest", "verdict", "producer",
        "artifact", "archive_inputs", "ghcr_inputs", "manifest_observation",
    })
    if (type(receipt["schema_version"]) is not int or receipt["schema_version"] != 1
            or receipt["profile_id"] != gate["profile"]
            or receipt["release_candidate_identity_digest"] != candidate_identity_digest
            or receipt["verdict"] != "pass"):
        fail(f"gate {gate['name']} observation profile, candidate identity or verdict mismatch")
    require_string(receipt["release_candidate_id"], "release_candidate_id")
    evidence_digest = require_string(receipt["evidence_digest"], "evidence_digest")
    if re.fullmatch(r"sha256:[0-9a-f]{64}", evidence_digest) is None:
        fail("invalid observation evidence digest")
    producer = require_object(receipt["producer"], "observation producer", {
        "repository", "workflow_path", "run_id", "run_attempt", "run_url", "source_sha", "source_ref",
    })
    require_positive_integer(producer["run_id"], "observation run id")
    require_positive_integer(producer["run_attempt"], "observation run attempt")
    required_producer = {
        "repository": repository, "workflow_path": WORKFLOWS["observation"],
        "run_id": gate["run_id"], "run_attempt": gate["run_attempt"],
        "run_url": f"https://github.com/{repository}/actions/runs/{gate['run_id']}",
        "source_sha": gate["expected_head_sha"], "source_ref": "refs/heads/main",
    }
    if producer != required_producer:
        fail(f"gate {gate['name']} observation producer mismatch")
    artifact = require_object(receipt["artifact"], "observation evidence artifact", {"id", "name", "upload_digest"})
    require_positive_integer(artifact["id"], "observation evidence artifact id")
    expected_name = f"real-azure-rc-observation-{gate['profile']}-run-{gate['run_id']}-attempt-{gate['run_attempt']}"
    if artifact["name"] != expected_name or re.fullmatch(
            r"sha256:[0-9a-f]{64}", require_string(artifact["upload_digest"], "observation upload digest")) is None:
        fail("observation evidence artifact identity mismatch")
    observation = require_object(receipt["manifest_observation"], "manifest observation", {
        "profile", "identifier", "digest", "verdict",
    })
    profile = require_object(observation["profile"], "manifest observation profile", {"id", "version"})
    require_positive_integer(profile["version"], "manifest observation profile version")
    expected_observation = {
        "profile": {"id": gate["profile"], "version": 1},
        "identifier": (f"github-actions:{repository}:run-{gate['run_id']}:attempt-{gate['run_attempt']}"
                       f":artifact-{artifact['id']}:{artifact['upload_digest']}"),
        "digest": evidence_digest, "verdict": "pass",
    }
    if observation != expected_observation:
        fail("manifest observation does not bind the exact passing profile evidence")
    # Full archive/GHCR/evidence binding and freshness remain mandatory in promotion's
    # validate-rc-observation call; this gate verifies the producer's selection receipt.
    for key in ("archive_inputs", "ghcr_inputs"):
        require_object(receipt[key], key, {"content_digest", "producer", "artifact"} |
                       ({"index_digest"} if key == "ghcr_inputs" else set()))


def gate(plan_path: pathlib.Path, run_data_directory: pathlib.Path | None) -> None:
    plan = validate_plan(plan_path)
    results: list[dict[str, Any]] = []
    for expected in plan["gates"]:
        run = load_run(plan["repository"], expected["run_id"], run_data_directory)
        actual = {
            "id": run.get("id"),
            "run_attempt": run.get("run_attempt"),
            "event": run.get("event"),
            "status": run.get("status"),
            "conclusion": run.get("conclusion"),
            "head_sha": run.get("head_sha"),
            "path": run.get("path"),
            "html_url": run.get("html_url"),
            "repository": run.get("repository"),
        }
        required = {
            "id": expected["run_id"],
            "run_attempt": expected["run_attempt"],
            "event": expected["event"],
            "status": "completed",
            "conclusion": "success",
            "head_sha": expected["expected_head_sha"],
            "path": expected["workflow_path"],
            "repository": plan["repository"],
        }
        for key, value in required.items():
            if actual.get(key) != value:
                fail(
                    f"gate {expected['name']} run {expected['run_id']} has "
                    f"{key}={actual.get(key)!r}, expected {value!r}"
                )
        validate_candidate_receipt(
            plan["repository"],
            expected,
            plan["candidate_source_sha"],
            plan["candidate_identity_digest"],
            run_data_directory,
        )
        results.append(
            {
                "name": expected["name"],
                "category": expected["category"],
                "profile": expected["profile"],
                "run_id": expected["run_id"],
                "run_attempt": expected["run_attempt"],
                "url": require_string(
                    actual["html_url"], f"gate {expected['name']} html_url"
                ),
            }
        )
    receipt = {
        "schema_version": SCHEMA_VERSION,
        "artifact_kind": "release_readiness_gate",
        "repository": plan["repository"],
        "candidate_source_sha": plan["candidate_source_sha"],
        "candidate_identity_digest": plan["candidate_identity_digest"],
        "plan_digest": canonical_digest(plan),
        "verdict": "pass",
        "gates": results,
    }
    print(json.dumps(receipt, sort_keys=True, indent=2, ensure_ascii=False))


def main() -> None:
    parser = argparse.ArgumentParser()
    subparsers = parser.add_subparsers(dest="command", required=True)
    validate_parser = subparsers.add_parser("validate-plan")
    validate_parser.add_argument("plan", type=pathlib.Path)
    gate_parser = subparsers.add_parser("gate")
    gate_parser.add_argument("plan", type=pathlib.Path)
    gate_parser.add_argument("--run-data-directory", type=pathlib.Path)
    args = parser.parse_args()
    if args.command == "validate-plan":
        validate_plan(args.plan)
        print(args.plan)
    else:
        gate(args.plan, args.run_data_directory)


if __name__ == "__main__":
    main()
