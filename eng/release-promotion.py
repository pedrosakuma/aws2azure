#!/usr/bin/env python3
"""Validate the immutable inputs for a no-rebuild stable promotion."""

from __future__ import annotations

import argparse
import datetime as dt
import hashlib
import json
import pathlib
import re
import subprocess
from typing import Any, NoReturn

from release_profile_coverage import required_profiles

SCHEMA_VERSION = 2
DECISION_POLICY = "release-evidence-reuse-v1"
DIGEST_RE = re.compile(r"sha256:[0-9a-f]{64}")
SHA_RE = re.compile(r"[0-9a-f]{40}")
STABLE_TAG_RE = re.compile(r"v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)")
CANDIDATE_RE = re.compile(
    r"v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)-rc\.[1-9][0-9]*"
)
REPOSITORY_RE = re.compile(r"[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+")


def fail(message: str) -> NoReturn:
    raise SystemExit(f"release-promotion: {message}")


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


def require_integer(value: Any, name: str) -> int:
    if isinstance(value, bool) or not isinstance(value, int) or value <= 0:
        fail(f"{name} must be a positive integer")
    return value


def require_digest(value: Any, name: str) -> str:
    text = require_string(value, name)
    if DIGEST_RE.fullmatch(text) is None:
        fail(f"{name} must be a lowercase sha256 digest")
    return text


def validate_artifact(value: Any, name: str) -> dict[str, Any]:
    artifact = require_object(
        value,
        name,
        {"id", "name", "upload_digest"},
    )
    require_integer(artifact["id"], f"{name}.id")
    require_string(artifact["name"], f"{name}.name")
    require_digest(artifact["upload_digest"], f"{name}.upload_digest")
    return artifact


def validate_producer(value: Any, name: str, workflow_path: str) -> dict[str, Any]:
    producer = require_object(
        value,
        name,
        {
            "workflow_path",
            "run_id",
            "run_attempt",
            "source_sha",
        },
    )
    if producer["workflow_path"] != workflow_path:
        fail(f"{name}.workflow_path must be {workflow_path}")
    require_integer(producer["run_id"], f"{name}.run_id")
    require_integer(producer["run_attempt"], f"{name}.run_attempt")
    source_sha = require_string(producer["source_sha"], f"{name}.source_sha")
    if SHA_RE.fullmatch(source_sha) is None:
        fail(f"{name}.source_sha must be a lowercase 40-character SHA")
    return producer


def validate_plan(path: pathlib.Path) -> dict[str, Any]:
    required = required_profiles()
    raw = load_json(path)
    version = raw.get("schema_version") if isinstance(raw, dict) else None
    fields = {
        "schema_version", "repository", "stable_tag", "candidate", "archive",
        "ghcr", "observations", "readiness_plan", "release_notes",
    }
    if version == SCHEMA_VERSION:
        fields.add("evidence_decision")
    plan = require_object(
        raw,
        "plan",
        fields,
    )
    if type(version) is not int or version not in (1, SCHEMA_VERSION):
        fail(f"schema_version must be 1 (historical) or {SCHEMA_VERSION}")
    repository = require_string(plan["repository"], "repository")
    if REPOSITORY_RE.fullmatch(repository) is None:
        fail("repository must be an owner/repository identity")
    stable_tag = require_string(plan["stable_tag"], "stable_tag")
    if STABLE_TAG_RE.fullmatch(stable_tag) is None:
        fail("stable_tag must be an exact stable semantic version")
    candidate = require_object(
        plan["candidate"],
        "candidate",
        {"identifier", "source_sha", "identity_digest"},
    )
    candidate_id = require_string(candidate["identifier"], "candidate.identifier")
    match = CANDIDATE_RE.fullmatch(candidate_id)
    if match is None:
        fail("candidate.identifier must be an rc.N semantic version")
    if stable_tag != f"v{match.group(1)}.{match.group(2)}.{match.group(3)}":
        fail("stable_tag must be the stable form of candidate.identifier")
    source_sha = require_string(candidate["source_sha"], "candidate.source_sha")
    if SHA_RE.fullmatch(source_sha) is None:
        fail("candidate.source_sha must be a lowercase 40-character SHA")
    require_digest(candidate["identity_digest"], "candidate.identity_digest")

    archive = require_object(
        plan["archive"],
        "archive",
        {"producer", "artifact", "content_digest"},
    )
    validate_producer(
        archive["producer"],
        "archive.producer",
        ".github/workflows/release-candidate.yml",
    )
    validate_artifact(archive["artifact"], "archive.artifact")
    require_digest(archive["content_digest"], "archive.content_digest")

    ghcr = require_object(
        plan["ghcr"],
        "ghcr",
        {"producer", "artifact", "content_digest", "index_digest"},
    )
    validate_producer(
        ghcr["producer"],
        "ghcr.producer",
        ".github/workflows/release-candidate-image.yml",
    )
    validate_artifact(ghcr["artifact"], "ghcr.artifact")
    require_digest(ghcr["content_digest"], "ghcr.content_digest")
    require_digest(ghcr["index_digest"], "ghcr.index_digest")

    observations = plan["observations"]
    if not isinstance(observations, list) or len(observations) != len(required):
        fail("observations must contain every required release profile: " + ", ".join(sorted(required)))
    profiles: set[str] = set()
    evidence_digests: set[str] = set()
    for index, value in enumerate(observations):
        observation = require_object(
            value,
            f"observations[{index}]",
            {
                "profile",
                "producer",
                "selection_artifact",
                "evidence_artifact",
                "evidence_digest",
            },
        )
        profile = require_string(observation["profile"], f"observations[{index}].profile")
        if profile not in required or profile in profiles:
            fail("observations must uniquely cover every required release profile")
        profiles.add(profile)
        validate_producer(
            observation["producer"],
            f"observations[{index}].producer",
            ".github/workflows/rc-observation-real-azure.yml",
        )
        validate_artifact(
            observation["selection_artifact"],
            f"observations[{index}].selection_artifact",
        )
        validate_artifact(
            observation["evidence_artifact"],
            f"observations[{index}].evidence_artifact",
        )
        evidence_digest = require_digest(
            observation["evidence_digest"],
            f"observations[{index}].evidence_digest",
        )
        if evidence_digest in evidence_digests:
            fail("observation evidence digests must be distinct")
        evidence_digests.add(evidence_digest)
    if profiles != required:
        fail("observations must cover every required release profile")

    for key in ("readiness_plan", "release_notes", *(["evidence_decision"] if version == 2 else [])):
        relative = require_string(plan[key], key)
        parsed = pathlib.PurePosixPath(relative)
        if (
            parsed.is_absolute()
            or relative != parsed.as_posix()
            or any(part in ("", ".", "..") for part in parsed.parts)
        ):
            fail(f"{key} must be a normalized repository-relative path")
    return plan


def repository_path(root: pathlib.Path, relative: str) -> pathlib.Path:
    parsed = pathlib.PurePosixPath(require_string(relative, "repository path"))
    if parsed.is_absolute() or parsed.as_posix() != relative or ".." in parsed.parts or "\\" in relative:
        fail("expected a normalized repository-relative path")
    path = root / relative
    if not path.is_file() or path.resolve() != path.absolute() or not path.resolve().is_relative_to(root.resolve()):
        fail(f"expected a regular repository file: {relative}")
    return path


def digest_bytes(content: bytes) -> str:
    return "sha256:" + hashlib.sha256(content).hexdigest()


def review_inputs_digest(root: pathlib.Path, plan_path: pathlib.Path, plan: dict[str, Any]) -> str:
    """Bind review to release inputs and tracked implementation/policy, not generated output."""
    result = subprocess.run(
        ["git", "ls-files", "-z", "--cached", "--others", "--exclude-standard"], cwd=root, check=True, stdout=subprocess.PIPE,
    )
    prefixes = ("src/", "tests/", "tools/", "eng/", ".github/", "deploy/",
                "docs/gaps/", "docs/workloads/", "docker/")
    files = {
        name for name in result.stdout.decode("utf-8").split("\0") if name
        and (name.startswith(prefixes) or "/" not in name)
    }
    files.update((plan_path.resolve().relative_to(root.resolve()).as_posix(),
                  plan["readiness_plan"], plan["release_notes"]))
    # The decision is a statement about the inputs, not an input to its own hash.
    files.discard(plan["evidence_decision"])
    rows = [
        [name, digest_bytes(repository_path(root, name).read_bytes())]
        for name in sorted(files)
    ]
    return digest_bytes(json.dumps(rows, separators=(",", ":"), ensure_ascii=True).encode())


def timestamp(value: Any, name: str) -> dt.datetime:
    text = require_string(value, name)
    try:
        parsed = dt.datetime.fromisoformat(text.replace("Z", "+00:00"))
        if parsed.tzinfo is None:
            raise ValueError("timezone required")
        return parsed.astimezone(dt.timezone.utc)
    except ValueError:
        fail(f"{name} must be a timestamp with a timezone")


def review_section(value: Any, name: str, accepted: str) -> None:
    section = require_object(value, name, {"status", "rationale", "references"})
    if section["status"] != accepted:
        fail(f"{name} is unresolved or invalidates historical evidence")
    require_string(section["rationale"], name + ".rationale").strip() or fail("empty review rationale")
    if not isinstance(section["references"], list) or not section["references"]:
        fail(f"{name} requires review references")
    for reference in section["references"]:
        if not require_string(reference, name + ".reference").startswith("https://"):
            fail(f"{name} reference must be HTTPS")


def validate_decision(root: pathlib.Path, plan_path: pathlib.Path, plan: dict[str, Any],
                      now: dt.datetime) -> dict[str, Any]:
    if plan["schema_version"] != 2:
        fail("new promotions require schema_version 2 and an explicit evidence decision")
    decision = require_object(
        load_json(repository_path(root, plan["evidence_decision"])), "evidence decision",
        {"schema_version", "policy", "status", "candidate_identity_digest", "reviewed_inputs_digest",
         "reviewed_at_utc", "owner", "review_url", "changes", "incidents", "profiles"},
    )
    if type(decision["schema_version"]) is not int or decision["schema_version"] != 1 or decision["policy"] != DECISION_POLICY:
        fail("unsupported evidence decision policy")
    if decision["status"] != "approved":
        fail("release evidence decision is pending or blocked; no automatic renewal or promotion")
    if decision["candidate_identity_digest"] != plan["candidate"]["identity_digest"]:
        fail("evidence decision belongs to another candidate")
    if decision["reviewed_inputs_digest"] != review_inputs_digest(root, plan_path, plan):
        fail("reviewed release inputs changed; a new impact/incident decision is required")
    reviewed = timestamp(decision["reviewed_at_utc"], "reviewed_at_utc")
    if reviewed > now:
        fail("release review is dated in the future")
    require_string(decision["owner"], "owner").strip() or fail("empty decision owner")
    review_url = require_string(decision["review_url"], "review_url")
    if re.fullmatch(r"https://github\.com/" + re.escape(plan["repository"]) + r"/(?:pull|issues)/[1-9][0-9]*(?:#issuecomment-[0-9]+)?", review_url) is None:
        fail("review_url must identify a review in this repository")
    review_section(decision["changes"], "changes", "no_invalidating_changes")
    review_section(decision["incidents"], "incidents", "no_unresolved_incidents")
    profiles = decision["profiles"]
    if not isinstance(profiles, list) or len(profiles) != len(required_profiles()):
        fail("decision must cover all required release profiles")
    seen: set[str] = set()
    for value in profiles:
        profile = require_object(value, "profile decision", {"profile", "decision", "rationale", "checks"})
        name = require_string(profile["profile"], "profile")
        if name in seen or name not in required_profiles():
            fail("duplicate or unexpected profile decision")
        seen.add(name)
        require_string(profile["rationale"], name + ".rationale").strip() or fail("empty profile rationale")
        checks = profile["checks"]
        if not isinstance(checks, list):
            fail("checks must be an array")
        if profile["decision"] == "reuse":
            if checks:
                fail("reuse cannot hide pending checks")
        elif profile["decision"] == "checks_required":
            if not checks:
                fail("checks_required must identify checks")
            check_ids: set[str] = set()
            for item in checks:
                check = require_object(item, "current-health check",
                    {"id", "purpose", "rationale", "status", "not_before_utc", "valid_until_utc",
                     "producer", "artifact"})
                check_id = require_string(check["id"], "check id")
                if check_id in check_ids:
                    fail("duplicate health check")
                check_ids.add(check_id)
                for field in ("purpose", "rationale"):
                    require_string(check[field], field).strip() or fail("empty check rationale")
                if check["status"] != "pass":
                    fail("required current-health check is pending or failed")
                start = timestamp(check["not_before_utc"], "not_before_utc")
                end = timestamp(check["valid_until_utc"], "valid_until_utc")
                if not start <= reviewed <= now <= end:
                    fail("required check is outside its explicitly reviewed validity window")
                producer = check["producer"]
                if not isinstance(producer, dict):
                    fail("missing check producer")
                workflow = require_string(producer.get("workflow_path"), "check workflow")
                if re.fullmatch(r"\.github/workflows/[A-Za-z0-9_-]+\.yml", workflow) is None:
                    fail("check workflow must be an explicit repository workflow")
                validate_producer(producer, "check producer", workflow)
                validate_artifact(check["artifact"], "check artifact")
        else:
            fail(f"profile {name} decision is pending or requires requalification")
    return decision


def api_json(repository: str, resource: str) -> dict[str, Any]:
    result = subprocess.run(["gh", "api", f"repos/{repository}/actions/{resource}"],
                            check=False, text=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=60)
    if result.returncode:
        fail(f"cannot verify {resource}: {result.stderr.strip()}")
    try:
        return json.loads(result.stdout, object_pairs_hook=duplicate_object)
    except json.JSONDecodeError as error:
        fail(f"invalid API result for {resource}: {error}")


def verify_live_source(repository: str, producer: dict[str, Any], artifact: dict[str, Any],
                       *, event: str = "workflow_dispatch", head_ref: str = "refs/heads/main") -> dict[str, Any]:
    run = api_json(repository, f"runs/{producer['run_id']}")
    expected = {
        "id": producer["run_id"], "run_attempt": producer["run_attempt"], "path": producer["workflow_path"],
        "head_sha": producer["source_sha"], "event": event, "status": "completed", "conclusion": "success",
        "head_branch": head_ref.removeprefix("refs/heads/").removeprefix("refs/tags/"),
    }
    if any(run.get(k) != v for k, v in expected.items()) or run.get("repository", {}).get("full_name") != repository:
        fail("source run is no longer the exact successful reviewed producer")
    metadata = api_json(repository, f"artifacts/{artifact['id']}")
    expected_artifact = {"id": artifact["id"], "name": artifact["name"],
                         "digest": artifact["upload_digest"], "expired": False}
    if any(metadata.get(k) != v for k, v in expected_artifact.items()) or metadata.get("workflow_run", {}).get("id") != producer["run_id"]:
        fail("source artifact is unavailable, expired or differs from the reviewed evidence")
    return run


def gate_history(root: pathlib.Path, plan_path: pathlib.Path, plan: dict[str, Any],
                 history_path: pathlib.Path, now: dt.datetime) -> dict[str, Any]:
    decision = validate_decision(root, plan_path, plan, now)
    history = load_json(history_path)
    if (history.get("schema_version") != 1 or history.get("policy") != DECISION_POLICY
            or history.get("candidate_identity_digest") != plan["candidate"]["identity_digest"]
            or history.get("valid_when_issued") is not True
            or history.get("release_eligible") is not False or history.get("current_health_verified") is not False
            or timestamp(history.get("evaluated_at_utc"), "history evaluation") > now):
        fail("missing or mismatched historical validation; health checks cannot replace it")
    profiles = history.get("profiles", [])
    if len(profiles) != len(required_profiles()) or {p["profile"] for p in profiles} != required_profiles():
        fail("historical validation must cover every required profile exactly once")
    for item in profiles:
        observation = next(p for p in plan["observations"] if p["profile"] == item["profile"])
        if item["observation_digest"] != observation["evidence_digest"]:
            fail("historical observation differs from the immutable promotion selection")
        if max(timestamp(item["qualification_issued_at_utc"], "qualification issuance"),
               timestamp(item["observation_issued_at_utc"], "observation issuance")) > timestamp(decision["reviewed_at_utc"], "review time"):
            fail("release review predates the evidence it accepts")
    sources = history.get("source_artifacts")
    if not isinstance(sources, list) or not sources or any(source is None for source in sources):
        fail("historical qualification source provenance is missing")
    if {source.get("profile_id") for source in sources} != required_profiles():
        fail("historical sources must cover every required profile")
    seen_sources = set()
    for source in sources:
        if source["repository"] != plan["repository"] or source["profile_id"] not in required_profiles():
            fail("historical source repository/profile mismatch")
        key = (source["run_id"], source["run_attempt"], source["artifact"]["id"])
        if key in seen_sources:
            continue
        seen_sources.add(key)
        producer = {"run_id": source["run_id"], "run_attempt": source["run_attempt"],
                    "workflow_path": source["workflow_path"], "source_sha": source["head_sha"]}
        verify_live_source(plan["repository"], producer, source["artifact"],
                           event=source["event_name"], head_ref=source["head_ref"])
    for profile in decision["profiles"]:
        for check in profile["checks"]:
            run = verify_live_source(plan["repository"], check["producer"], check["artifact"])
            if not (timestamp(check["not_before_utc"], "check lower bound")
                    <= timestamp(run.get("run_started_at"), "check run start")
                    <= timestamp(run.get("updated_at"), "check run completion")
                    <= timestamp(decision["reviewed_at_utc"], "review time")):
                fail("check was not completed within its reviewed evidence window")
    return {
        "schema_version": 1, "policy": DECISION_POLICY, "release_eligible": True,
        "candidate_identity_digest": plan["candidate"]["identity_digest"],
        "evaluated_at_utc": now.isoformat(), "reviewed_inputs_digest": decision["reviewed_inputs_digest"],
        "decision_digest": digest_bytes(repository_path(root, plan["evidence_decision"]).read_bytes()),
        "historical_report_digest": digest_bytes(history_path.read_bytes()),
        "health_basis": {p["profile"]: p["decision"] for p in decision["profiles"]},
        "canonical_evidence_renewed": False,
    }


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("plan", type=pathlib.Path)
    parser.add_argument("--require-decision", action="store_true")
    parser.add_argument("--review-inputs-digest", action="store_true")
    parser.add_argument("--gate-history", type=pathlib.Path)
    args = parser.parse_args()
    plan = validate_plan(args.plan)
    root = pathlib.Path(__file__).resolve().parent.parent
    now = dt.datetime.now(dt.timezone.utc)
    if args.review_inputs_digest:
        if plan["schema_version"] != 2:
            fail("review inputs require a schema-v2 plan")
        print(review_inputs_digest(root, args.plan, plan))
    elif args.gate_history:
        print(json.dumps(gate_history(root, args.plan, plan, args.gate_history, now), indent=2, sort_keys=True))
    else:
        if args.require_decision:
            validate_decision(root, args.plan, plan, now)
        print(args.plan)


if __name__ == "__main__":
    main()
