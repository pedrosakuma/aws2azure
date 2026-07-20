#!/usr/bin/env python3
"""Validate the immutable inputs for a no-rebuild stable promotion."""

from __future__ import annotations

import argparse
import json
import pathlib
import re
from typing import Any, NoReturn


SCHEMA_VERSION = 1
DIGEST_RE = re.compile(r"sha256:[0-9a-f]{64}")
SHA_RE = re.compile(r"[0-9a-f]{40}")
STABLE_TAG_RE = re.compile(r"v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)")
CANDIDATE_RE = re.compile(
    r"v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)-rc\.[1-9][0-9]*"
)
REPOSITORY_RE = re.compile(r"[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+")
PROFILES = {
    "s3-basic-object-crud",
    "secretsmanager-basic-lifecycle",
}


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
    plan = require_object(
        load_json(path),
        "plan",
        {
            "schema_version",
            "repository",
            "stable_tag",
            "candidate",
            "archive",
            "ghcr",
            "observations",
            "readiness_plan",
            "release_notes",
        },
    )
    if plan["schema_version"] != SCHEMA_VERSION:
        fail(f"schema_version must be {SCHEMA_VERSION}")
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
    if not isinstance(observations, list) or len(observations) != len(PROFILES):
        fail("observations must contain exactly the two supported profiles")
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
        if profile not in PROFILES or profile in profiles:
            fail("observations must uniquely cover the two supported profiles")
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
    if profiles != PROFILES:
        fail("observations must cover both supported profiles")

    for key in ("readiness_plan", "release_notes"):
        relative = require_string(plan[key], key)
        parsed = pathlib.PurePosixPath(relative)
        if (
            parsed.is_absolute()
            or relative != parsed.as_posix()
            or any(part in ("", ".", "..") for part in parsed.parts)
        ):
            fail(f"{key} must be a normalized repository-relative path")
    return plan


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("plan", type=pathlib.Path)
    args = parser.parse_args()
    validate_plan(args.plan)
    print(args.plan)


if __name__ == "__main__":
    main()
