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


SCHEMA_VERSION = 1
SHA_RE = re.compile(r"[0-9a-f]{40}")
REPOSITORY_RE = re.compile(r"[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+")
REQUIRED_SINGLETONS = {
    "ci",
    "aot",
    "conformance",
    "perf",
    "footprint",
    "observation",
}
PROFILE_CATEGORIES = {"real-azure", "profile"}
SUPPORTED_PROFILES = {
    "s3-basic-object-crud",
    "secretsmanager-basic-lifecycle",
}
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
            if profile not in SUPPORTED_PROFILES:
                fail(f"{name} must identify a supported profile")
            profile_coverage[category].add(profile)
        elif profile is not None:
            fail(f"{name} must not declare a profile")
        if category in REQUIRED_SINGLETONS:
            singleton_counts[category] += 1
        candidate_receipt = gate["candidate_receipt"]
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

    missing_singletons = sorted(
        category for category, count in singleton_counts.items() if count != 1
    )
    if missing_singletons:
        fail(
            "singleton gate categories must appear exactly once: "
            + ", ".join(missing_singletons)
        )
    for category, profiles in profile_coverage.items():
        if profiles != SUPPORTED_PROFILES:
            fail(f"{category} gates must cover both supported profiles")
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
    run_data_directory: pathlib.Path | None,
) -> None:
    expected = gate["candidate_receipt"]
    if expected is None:
        return
    artifact_id = expected["artifact_id"]
    if run_data_directory is not None:
        metadata = load_json(run_data_directory / f"artifact-{artifact_id}.json")
        receipt = load_json(run_data_directory / expected["receipt_name"])
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
