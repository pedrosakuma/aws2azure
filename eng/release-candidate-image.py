#!/usr/bin/env python3
"""Validate and bind immutable RC archives to exact GHCR image identities."""

from __future__ import annotations

import argparse
import datetime
import hashlib
import importlib.util
import json
import os
import pathlib
import re
import shutil
import stat
import struct
from collections import Counter
from typing import Any, NoReturn


SCHEMA_VERSION = 1
RC_WORKFLOW = ".github/workflows/release-candidate.yml"
IMAGE_WORKFLOW = ".github/workflows/release-candidate-image.yml"
PREDICATE_TYPE = "https://slsa.dev/provenance/v1"
MATERIALS_PREDICATE_TYPE = (
    "https://aws2azure.dev/attestations/release-candidate-image-materials/v1"
)
BASE_NAME = "mcr.microsoft.com/dotnet/runtime-deps:10.0-noble-chiseled"
BASE_INDEX_DIGEST = (
    "sha256:bc6ba0158e93277ca1a5bc0881d13b08e6ca7f6d98db623592095eaf7fd7a816"
)
PLATFORMS = {
    "linux/amd64": {
        "rid": "linux-x64",
        "architecture": "amd64",
        "elf_machine": 62,
        "base_digest": (
            "sha256:481d8747c961286738b6ce814c89de840bbc018330283bc54e8d29484ee88b16"
        ),
    },
    "linux/arm64": {
        "rid": "linux-arm64",
        "architecture": "arm64",
        "elf_machine": 183,
        "base_digest": (
            "sha256:84fc5eb352e49b24564ff085ece8de373ed14d019a1d0e9f6d1103ea00c43454"
        ),
    },
}
RID_TO_PLATFORM = {value["rid"]: key for key, value in PLATFORMS.items()}
CANDIDATE_RE = re.compile(
    r"v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)-rc\.([1-9][0-9]*)"
)
SHA_RE = re.compile(r"[0-9a-f]{40}")
DIGEST_RE = re.compile(r"sha256:[0-9a-f]{64}")
REPOSITORY_RE = re.compile(r"[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+")
TAG_RE = re.compile(r"[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}")
CHECKSUM_RE = re.compile(r"([0-9a-f]{64})  \./([^\n]+)")


def fail(message: str) -> NoReturn:
    raise SystemExit(f"release-candidate-image: {message}")


def duplicate_object(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
    result: dict[str, Any] = {}
    for key, value in pairs:
        if key in result:
            fail(f"duplicate JSON field: {key}")
        result[key] = value
    return result


def load_json(path: pathlib.Path) -> Any:
    regular_file(path, str(path))
    try:
        with path.open("r", encoding="utf-8") as stream:
            return json.load(stream, object_pairs_hook=duplicate_object)
    except (OSError, UnicodeError, json.JSONDecodeError) as error:
        fail(f"cannot read JSON {path}: {error}")


def canonical_bytes(value: dict[str, Any]) -> bytes:
    return (
        json.dumps(value, sort_keys=True, indent=2, ensure_ascii=False) + "\n"
    ).encode("utf-8")


def content_digest(value: dict[str, Any]) -> str:
    body = {key: item for key, item in value.items() if key != "content_digest"}
    encoded = json.dumps(
        body, sort_keys=True, separators=(",", ":"), ensure_ascii=False
    ).encode("utf-8")
    return f"sha256:{hashlib.sha256(encoded).hexdigest()}"


def write_new_json(path: pathlib.Path, value: dict[str, Any]) -> None:
    if path.exists() or path.is_symlink():
        fail(f"output already exists: {path}")
    path.parent.mkdir(parents=True, exist_ok=True)
    try:
        with path.open("xb") as stream:
            stream.write(canonical_bytes(value))
    except OSError as error:
        fail(f"cannot write {path}: {error}")


def require_object(value: Any, name: str, keys: set[str] | None = None) -> dict[str, Any]:
    if not isinstance(value, dict):
        fail(f"{name} must be an object")
    if keys is not None and set(value) != keys:
        fail(f"{name} fields are invalid")
    return value


def require_array(value: Any, name: str) -> list[Any]:
    if not isinstance(value, list) or not value:
        fail(f"{name} must be a non-empty array")
    return value


def require_string(value: Any, name: str) -> str:
    if not isinstance(value, str) or not value:
        fail(f"{name} must be a non-empty string")
    return value


def require_integer(value: Any, name: str) -> int:
    if isinstance(value, bool) or not isinstance(value, int) or value <= 0:
        fail(f"{name} must be a positive integer")
    return value


def require_candidate(value: Any, name: str = "candidate") -> str:
    text = require_string(value, name)
    if CANDIDATE_RE.fullmatch(text) is None:
        fail(f"{name} must be strict vMAJOR.MINOR.PATCH-rc.NUMBER SemVer")
    return text


def require_sha(value: Any, name: str) -> str:
    text = require_string(value, name)
    if SHA_RE.fullmatch(text) is None:
        fail(f"{name} must be a lowercase 40-character git SHA")
    return text


def require_digest(value: Any, name: str) -> str:
    text = require_string(value, name)
    if DIGEST_RE.fullmatch(text) is None:
        fail(f"{name} must be a lowercase SHA-256 digest")
    return text


def require_repository(value: Any, name: str) -> str:
    text = require_string(value, name)
    if REPOSITORY_RE.fullmatch(text) is None:
        fail(f"{name} must be an owner/repository identity")
    return text


def require_tag(value: Any, name: str) -> str:
    text = require_string(value, name)
    if TAG_RE.fullmatch(text) is None:
        fail(f"{name} is not a safe OCI tag")
    return text


def regular_file(path: pathlib.Path, name: str) -> pathlib.Path:
    try:
        mode = path.lstat().st_mode
    except OSError as error:
        fail(f"cannot inspect {name}: {error}")
    if stat.S_ISLNK(mode) or not stat.S_ISREG(mode):
        fail(f"{name} must be a regular non-symbolic-link file")
    return path


def sha256_file(path: pathlib.Path) -> str:
    digest = hashlib.sha256()
    try:
        with path.open("rb") as stream:
            while chunk := stream.read(1024 * 1024):
                digest.update(chunk)
    except OSError as error:
        fail(f"cannot hash {path}: {error}")
    return f"sha256:{digest.hexdigest()}"


def normalized_relative(value: Any, name: str) -> str:
    text = require_string(value, name)
    if "\0" in text or "\\" in text:
        fail(f"{name} is not a safe relative path")
    path = pathlib.PurePosixPath(text)
    if (
        path.is_absolute()
        or path.as_posix() != text
        or not path.parts
        or any(part in ("", ".", "..") for part in path.parts)
    ):
        fail(f"{name} is not a normalized safe relative path")
    return text


def resolve_file(root: pathlib.Path, relative: Any, name: str) -> pathlib.Path:
    relative_text = normalized_relative(relative, name)
    current = root
    for part in pathlib.PurePosixPath(relative_text).parts:
        current = current / part
        try:
            mode = current.lstat().st_mode
        except OSError as error:
            fail(f"cannot inspect {name}: {error}")
        if stat.S_ISLNK(mode):
            fail(f"{name} must not traverse a symbolic link")
    regular_file(current, name)
    resolved_root = root.resolve()
    resolved = current.resolve()
    if resolved_root != resolved and resolved_root not in resolved.parents:
        fail(f"{name} escapes its root")
    return resolved


def load_tool(path: pathlib.Path, module_name: str) -> Any:
    spec = importlib.util.spec_from_file_location(module_name, path)
    if spec is None or spec.loader is None:
        fail(f"cannot load trusted helper {path}")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def parse_timestamp(value: Any, name: str) -> datetime.datetime:
    text = require_string(value, name)
    try:
        timestamp = datetime.datetime.fromisoformat(text.replace("Z", "+00:00"))
    except ValueError as error:
        fail(f"{name} is invalid: {error}")
    if timestamp.tzinfo is None:
        fail(f"{name} must include a timezone")
    return timestamp


def validate_protected_main_source(
    source_sha: str,
    main_branch_path: pathlib.Path,
    main_compare_path: pathlib.Path,
) -> None:
    main_branch = require_object(load_json(main_branch_path), "main branch")
    main_commit = require_object(main_branch.get("commit"), "main branch commit")
    main_sha = require_sha(main_commit.get("sha"), "main branch commit SHA")
    if main_branch.get("name") != "main" or main_branch.get("protected") is not True:
        fail("workflow source is not anchored to protected main")
    main_compare = require_object(load_json(main_compare_path), "main comparison")
    base_commit = require_object(main_compare.get("base_commit"), "comparison base commit")
    merge_base = require_object(
        main_compare.get("merge_base_commit"), "comparison merge-base commit"
    )
    if base_commit.get("sha") != source_sha or merge_base.get("sha") != source_sha:
        fail("workflow source is not protected main history")

    status = main_compare.get("status")
    ahead_by = main_compare.get("ahead_by")
    behind_by = main_compare.get("behind_by")
    total_commits = main_compare.get("total_commits")
    head_commit = main_compare.get("head_commit")
    if "head_commit" in main_compare:
        exact_head = (
            isinstance(head_commit, dict) and head_commit.get("sha") == main_sha
        )
    else:
        commits = main_compare.get("commits")
        exact_head = (
            isinstance(commits, list)
            and bool(commits)
            and isinstance(commits[-1], dict)
            and commits[-1].get("sha") == main_sha
        )
    counts_are_integers = all(
        isinstance(value, int) and not isinstance(value, bool)
        for value in (ahead_by, behind_by, total_commits)
    )
    if status == "identical":
        valid = (
            source_sha == main_sha
            and counts_are_integers
            and ahead_by == 0
            and behind_by == 0
            and total_commits == 0
            and (
                head_commit is None
                or (
                    isinstance(head_commit, dict)
                    and head_commit.get("sha") == main_sha
                )
            )
        )
    elif status == "ahead":
        valid = (
            source_sha != main_sha
            and counts_are_integers
            and ahead_by > 0
            and behind_by == 0
            and total_commits == ahead_by
            and exact_head
        )
    else:
        valid = False
    if not valid:
        fail("workflow source is not protected main history")


def validate_protected_main(args: argparse.Namespace) -> None:
    source_sha = require_sha(args.source_sha, "workflow source SHA")
    validate_protected_main_source(
        source_sha,
        args.main_branch_json.resolve(),
        args.main_compare_json.resolve(),
    )


def validate_selection(args: argparse.Namespace) -> None:
    repository = require_repository(args.repository, "repository")
    candidate = require_candidate(args.candidate)
    source_sha = require_sha(args.source_sha, "source SHA")
    workflow_source_sha = require_sha(
        args.workflow_source_sha, "workflow source SHA"
    )
    run_id = require_integer(args.run_id, "run id")
    run_attempt = require_integer(args.run_attempt, "run attempt")
    artifact_id = require_integer(args.artifact_id, "artifact id")
    artifact_name = require_string(args.artifact_name, "artifact name")
    upload_digest = require_digest(args.artifact_digest, "artifact digest")
    archive_content_digest = require_digest(
        args.archive_content_digest, "archive-input content digest"
    )
    validate_protected_main_source(
        workflow_source_sha,
        args.main_branch_json.resolve(),
        args.main_compare_json.resolve(),
    )
    expected_name = (
        f"aws2azure-rc-archives-{candidate}-"
        f"{archive_content_digest.removeprefix('sha256:')}-run-{run_id}-"
        f"attempt-{run_attempt}"
    )
    if artifact_name != expected_name:
        fail("artifact name does not bind candidate, content digest, run, and attempt")

    run = require_object(load_json(args.run_json), "run")
    if (
        run.get("id") != run_id
        or run.get("run_attempt") != run_attempt
        or run.get("event") != "workflow_dispatch"
        or run.get("status") != "completed"
        or run.get("conclusion") != "success"
        or run.get("path") != RC_WORKFLOW
        or run.get("head_sha") != workflow_source_sha
        or run.get("head_branch") != "main"
        or (run.get("repository") or {}).get("full_name") != repository
        or (run.get("head_repository") or {}).get("full_name") != repository
    ):
        fail("selected run is not the exact successful same-repository RC producer")

    artifact = require_object(load_json(args.artifact_json), "artifact")
    workflow_run = require_object(artifact.get("workflow_run"), "artifact.workflow_run")
    if (
        artifact.get("id") != artifact_id
        or artifact.get("name") != artifact_name
        or artifact.get("digest") != upload_digest
        or artifact.get("expired") is not False
        or workflow_run.get("id") != run_id
        or workflow_run.get("head_sha") != workflow_source_sha
    ):
        fail("selected artifact does not match its exact API identity")
    if parse_timestamp(artifact.get("expires_at"), "artifact.expires_at") <= datetime.datetime.now(
        datetime.timezone.utc
    ):
        fail("selected artifact has expired")

    selection: dict[str, Any] = {
        "schema_version": SCHEMA_VERSION,
        "artifact_kind": "release_candidate_archive_selection",
        "candidate": {
            "identifier": candidate,
            "source": {
                "repository": repository,
                "sha": source_sha,
                "ref": f"refs/tags/{candidate}",
            },
        },
        "producer": {
            "workflow": RC_WORKFLOW,
            "event_name": "workflow_dispatch",
            "run_id": run_id,
            "run_attempt": run_attempt,
            "attempt_url": (
                f"https://github.com/{repository}/actions/runs/{run_id}/attempts/"
                f"{run_attempt}"
            ),
            "run_started_at": require_string(
                run.get("run_started_at"), "run.run_started_at"
            ),
            "source_sha": workflow_source_sha,
            "source_ref": "refs/heads/main",
        },
        "artifact": {
            "id": artifact_id,
            "name": artifact_name,
            "upload_digest": upload_digest,
            "created_at": require_string(artifact.get("created_at"), "artifact.created_at"),
            "expires_at": require_string(artifact.get("expires_at"), "artifact.expires_at"),
        },
        "archive_input_content_digest": archive_content_digest,
    }
    selection["content_digest"] = content_digest(selection)
    write_new_json(args.output, selection)


def validate_selection_file(path: pathlib.Path) -> dict[str, Any]:
    selection = require_object(
        load_json(path),
        "selection",
        {
            "schema_version",
            "artifact_kind",
            "candidate",
            "producer",
            "artifact",
            "archive_input_content_digest",
            "content_digest",
        },
    )
    if (
        selection["schema_version"] != SCHEMA_VERSION
        or selection["artifact_kind"] != "release_candidate_archive_selection"
    ):
        fail("selection schema or artifact kind is invalid")
    candidate = require_object(
        selection["candidate"], "selection.candidate", {"identifier", "source"}
    )
    identifier = require_candidate(candidate["identifier"])
    source = require_object(
        candidate["source"],
        "selection.candidate.source",
        {"repository", "sha", "ref"},
    )
    repository = require_repository(source["repository"], "selection source repository")
    require_sha(source["sha"], "selection source SHA")
    if source["ref"] != f"refs/tags/{identifier}":
        fail("selection source ref does not match the candidate")
    producer = require_object(
        selection["producer"],
        "selection.producer",
        {
            "workflow",
            "event_name",
            "run_id",
            "run_attempt",
            "attempt_url",
            "run_started_at",
            "source_sha",
            "source_ref",
        },
    )
    run_id = require_integer(producer["run_id"], "selection producer run id")
    run_attempt = require_integer(
        producer["run_attempt"], "selection producer run attempt"
    )
    expected_attempt = (
        f"https://github.com/{repository}/actions/runs/{run_id}/attempts/{run_attempt}"
    )
    require_sha(producer["source_sha"], "selection producer source SHA")
    if (
        producer["workflow"] != RC_WORKFLOW
        or producer["event_name"] != "workflow_dispatch"
        or producer["attempt_url"] != expected_attempt
        or producer["source_ref"] != "refs/heads/main"
    ):
        fail("selection producer identity is invalid")
    parse_timestamp(producer["run_started_at"], "selection producer start time")
    artifact = require_object(
        selection["artifact"],
        "selection.artifact",
        {"id", "name", "upload_digest", "created_at", "expires_at"},
    )
    require_integer(artifact["id"], "selection artifact id")
    require_string(artifact["name"], "selection artifact name")
    require_digest(artifact["upload_digest"], "selection artifact upload digest")
    parse_timestamp(artifact["created_at"], "selection artifact created time")
    parse_timestamp(artifact["expires_at"], "selection artifact expiry time")
    archive_digest = require_digest(
        selection["archive_input_content_digest"], "selection archive-input digest"
    )
    expected_name = (
        f"aws2azure-rc-archives-{identifier}-{archive_digest.removeprefix('sha256:')}"
        f"-run-{run_id}-attempt-{run_attempt}"
    )
    if artifact["name"] != expected_name:
        fail("selection artifact name is not canonical")
    require_digest(selection["content_digest"], "selection content digest")
    if selection["content_digest"] != content_digest(selection):
        fail("selection content digest does not match")
    if path.read_bytes() != canonical_bytes(selection):
        fail("selection is not canonical JSON")
    return selection


def validate_elf(path: pathlib.Path, rid: str) -> None:
    if rid not in RID_TO_PLATFORM:
        fail(f"unsupported RID for ELF validation: {rid}")
    try:
        header = path.read_bytes()[:64]
    except OSError as error:
        fail(f"cannot read executable header: {error}")
    if len(header) < 20 or header[:4] != b"\x7fELF":
        fail(f"{rid} executable is not ELF")
    if header[4] != 2 or header[5] != 1:
        fail(f"{rid} executable must be 64-bit little-endian ELF")
    machine = struct.unpack_from("<H", header, 18)[0]
    expected = PLATFORMS[RID_TO_PLATFORM[rid]]["elf_machine"]
    if machine != expected:
        fail(f"{rid} executable ELF architecture does not match its platform")


def all_regular_files(root: pathlib.Path) -> dict[str, pathlib.Path]:
    result: dict[str, pathlib.Path] = {}
    for path in sorted(root.rglob("*")):
        try:
            mode = path.lstat().st_mode
        except OSError as error:
            fail(f"cannot inspect bundle entry {path}: {error}")
        if stat.S_ISLNK(mode):
            fail(f"bundle contains a symbolic link: {path}")
        if stat.S_ISDIR(mode):
            continue
        if not stat.S_ISREG(mode):
            fail(f"bundle contains a special file: {path}")
        relative = path.relative_to(root).as_posix()
        result[relative] = path
    return result


def validate_complete_checksums(root: pathlib.Path) -> dict[str, pathlib.Path]:
    checksum_path = resolve_file(root, "SHA256SUMS.all", "complete checksums")
    try:
        lines = checksum_path.read_text(encoding="ascii").splitlines()
    except (OSError, UnicodeError) as error:
        fail(f"cannot read complete checksums: {error}")
    declared: dict[str, str] = {}
    ordered: list[str] = []
    for line in lines:
        match = CHECKSUM_RE.fullmatch(line)
        if match is None:
            fail("SHA256SUMS.all contains a non-canonical line")
        digest_hex, relative = match.groups()
        normalized_relative(relative, "checksum path")
        if relative == "SHA256SUMS.all" or relative in declared:
            fail("SHA256SUMS.all contains an invalid or duplicate path")
        declared[relative] = f"sha256:{digest_hex}"
        ordered.append(relative)
    if ordered != sorted(ordered):
        fail("SHA256SUMS.all paths must be sorted")
    files = all_regular_files(root)
    actual_paths = set(files) - {"SHA256SUMS.all"}
    if set(declared) != actual_paths:
        fail("SHA256SUMS.all must cover every other bundle file exactly once")
    for relative, expected in declared.items():
        if sha256_file(files[relative]) != expected:
            fail(f"bundle checksum mismatch: {relative}")
    return files


def subject(path: pathlib.Path) -> dict[str, str]:
    return {"name": path.name, "digest": sha256_file(path)}


def validate_resolved_x64_identity(
    path: pathlib.Path,
    context: dict[str, Any],
    approved_ledger: dict[str, Any],
    sealed_manifest: dict[str, Any],
) -> None:
    identity = require_object(
        load_json(path),
        "resolved linux-x64 identity",
        {
            "schema_version",
            "role",
            "profile",
            "status",
            "eligibility",
            "ledger_record_digest",
            "source",
            "runtime",
            "producer",
            "artifact",
            "attestation",
        },
    )
    s3_workload = next(
        (
            item
            for item in context["workloads"]
            if item["profile"]["id"] == "s3-basic-object-crud"
        ),
        None,
    )
    if s3_workload is None:
        fail("release-candidate context omits the approved S3 runtime")
    approved = s3_workload["approved_runtime"]
    ledger = require_object(approved_ledger, "S3 approved-runtime ledger")
    record = require_object(ledger.get("record"), "S3 approved-runtime record")
    ledger_artifact = require_object(
        record.get("artifact"), "S3 approved-runtime artifact"
    )
    ledger_attestation = require_object(
        record.get("attestation"), "S3 approved-runtime attestation"
    )
    if (
        ledger.get("schema_version") != SCHEMA_VERSION
        or ledger.get("ledger_record_digest") != approved["ledger_record_digest"]
        or record.get("schema_version") != SCHEMA_VERSION
        or record.get("profile") != approved["profile"]
        or record.get("status") != approved["status"]
        or record.get("eligibility")
        != {"rollback_baseline_eligible": True, "promotion_eligible": True}
    ):
        fail("S3 approved-runtime ledger drifts from the selected context")

    source = context["sealed_runtime"]
    expected_profile = {
        "id": s3_workload["profile"]["id"],
        "version": s3_workload["profile"]["version"],
    }
    if (
        identity["schema_version"] != SCHEMA_VERSION
        or identity["role"] != "prior"
        or identity["profile"] != expected_profile
        or identity["status"] != approved["status"]
        or identity["eligibility"] != record["eligibility"]
        or identity["ledger_record_digest"] != approved["ledger_record_digest"]
        or identity["source"]
        != {
            "repository": source["source_repository"],
            "sha": source["source_sha"],
            "ref": source["source_ref"],
        }
        or identity["runtime"]
        != {
            "aggregate_digest": source["aggregate_digest"],
            "executable_digest": source["executable_digest"],
            "manifest_digest": source["manifest_digest"],
        }
    ):
        fail("resolved linux-x64 identity drifts from the selected approved runtime")

    producer = require_object(
        identity["producer"],
        "resolved linux-x64 producer",
        {
            "workflow",
            "event_name",
            "run_id",
            "run_attempt",
            "run_url",
            "attempt_url",
            "run_started_at",
        },
    )
    selected_producer = source["producer"]
    sealed_producer = require_object(
        sealed_manifest.get("producer"), "sealed runtime manifest producer"
    )
    run_url = (
        f"https://github.com/{source['source_repository']}/actions/runs/"
        f"{selected_producer['run_id']}"
    )
    if producer != {
        "workflow": selected_producer["workflow"],
        "event_name": "workflow_dispatch",
        "run_id": selected_producer["run_id"],
        "run_attempt": selected_producer["run_attempt"],
        "run_url": run_url,
        "attempt_url": selected_producer["attempt_url"],
        "run_started_at": producer["run_started_at"],
    }:
        fail("resolved linux-x64 producer drifts from the selected approved runtime")
    if any(
        sealed_producer.get(key) != expected
        for key, expected in (
            ("workflow_path", producer["workflow"]),
            ("event_name", producer["event_name"]),
            ("run_id", producer["run_id"]),
            ("run_attempt", producer["run_attempt"]),
            ("run_url", producer["run_url"]),
            ("attempt_url", producer["attempt_url"]),
        )
    ):
        fail("sealed runtime manifest producer drifts from the selected approved runtime")
    run_started_at = parse_timestamp(
        producer["run_started_at"], "resolved linux-x64 run_started_at"
    )
    if run_started_at != parse_timestamp(
        sealed_producer.get("run_started_at"),
        "sealed runtime manifest producer.run_started_at",
    ):
        fail("resolved linux-x64 start time drifts from the sealed runtime manifest")

    artifact = require_object(
        identity["artifact"],
        "resolved linux-x64 artifact",
        {"id", "name", "upload_digest", "created_at", "expires_at"},
    )
    if any(
        artifact[key] != source["artifact"][key]
        for key in ("id", "name", "upload_digest")
    ):
        fail("resolved linux-x64 artifact drifts from the selected approved runtime")
    created_at = parse_timestamp(
        artifact["created_at"], "resolved linux-x64 artifact.created_at"
    )
    expires_at = parse_timestamp(
        artifact["expires_at"], "resolved linux-x64 artifact.expires_at"
    )
    ledger_created_at = parse_timestamp(
        ledger_artifact.get("created_at"), "S3 approved-runtime artifact.created_at"
    )
    ledger_expires_at = parse_timestamp(
        ledger_artifact.get("expires_at"), "S3 approved-runtime artifact.expires_at"
    )
    if (
        created_at != ledger_created_at
        or expires_at != ledger_expires_at
        or expires_at <= created_at
    ):
        fail("resolved linux-x64 artifact timestamps drift from its approved ledger")

    attestation = require_object(
        identity["attestation"],
        "resolved linux-x64 attestation",
        {
            "predicate_type",
            "repository",
            "signer_workflow",
            "source_sha",
            "source_ref",
            "run_invocation_url",
            "bundle_digest",
            "executable_subject_name",
            "executable_subject_digest",
            "manifest_subject_name",
            "manifest_subject_digest",
        },
    )
    attestation_bundle_digest = require_digest(
        attestation["bundle_digest"], "resolved linux-x64 attestation bundle digest"
    )
    if attestation != {
        "predicate_type": PREDICATE_TYPE,
        "repository": source["source_repository"],
        "signer_workflow": (
            f"{source['source_repository']}/{selected_producer['workflow']}"
        ),
        "source_sha": source["source_sha"],
        "source_ref": source["source_ref"],
        "run_invocation_url": selected_producer["attempt_url"],
        "bundle_digest": attestation_bundle_digest,
        "executable_subject_name": "Aws2Azure.Proxy",
        "executable_subject_digest": source["executable_digest"],
        "manifest_subject_name": "sealed-runtime-manifest.json",
        "manifest_subject_digest": source["manifest_digest"],
    }:
        fail("resolved linux-x64 attestation drifts from the selected approved runtime")
    if any(
        ledger_attestation.get(key) != expected
        for key, expected in (
            ("predicate_type", attestation["predicate_type"]),
            ("repository", attestation["repository"]),
            ("signer_workflow", attestation["signer_workflow"]),
            ("source_sha", attestation["source_sha"]),
            ("source_ref", attestation["source_ref"]),
            ("subject_name", attestation["executable_subject_name"]),
            ("subject_digest", attestation["executable_subject_digest"]),
            ("manifest_subject_name", attestation["manifest_subject_name"]),
            ("manifest_subject_digest", attestation["manifest_subject_digest"]),
        )
    ):
        fail("resolved linux-x64 attestation drifts from its approved ledger")
    if path.read_bytes() != canonical_bytes(identity):
        fail("resolved linux-x64 identity is not canonical JSON")


def validate_bundle(args: argparse.Namespace) -> None:
    root = args.bundle.resolve()
    if not root.is_dir() or root.is_symlink():
        fail("bundle must be a regular directory")
    selection = validate_selection_file(args.selection.resolve())
    files = validate_complete_checksums(root)

    repo_root = pathlib.Path(__file__).resolve().parent.parent
    inputs_tool = load_tool(
        repo_root / "eng" / "release-candidate-inputs.py", "rc_archive_inputs"
    )
    package_tool = load_tool(
        repo_root / "eng" / "release-candidate-package.py", "rc_platform_package"
    )
    archive_inputs_path = resolve_file(
        root,
        "release-candidate-archive-inputs.json",
        "release-candidate archive inputs",
    )
    archive_inputs = inputs_tool.validate_inputs(archive_inputs_path)
    if (
        archive_inputs["candidate"] != selection["candidate"]
        or archive_inputs["producer"]["workflow"] != selection["producer"]["workflow"]
        or archive_inputs["producer"]["run_id"] != selection["producer"]["run_id"]
        or archive_inputs["producer"]["run_attempt"]
        != selection["producer"]["run_attempt"]
        or archive_inputs["producer"]["attempt_url"]
        != selection["producer"]["attempt_url"]
        or archive_inputs["producer"]["source_sha"]
        != selection["producer"]["source_sha"]
        or archive_inputs["producer"]["source_ref"]
        != selection["producer"]["source_ref"]
        or archive_inputs["content_digest"]
        != selection["archive_input_content_digest"]
    ):
        fail("downloaded archive inputs do not match the selected exact identity")

    context_path = resolve_file(
        root,
        "context/release-candidate-context.json",
        "release-candidate context",
    )
    context = inputs_tool.validate_context(context_path)
    if any(
        context[key] != archive_inputs[key]
        for key in (
            "candidate",
            "orchestration_source",
            "approved_ledger_source",
            "workloads",
            "compatibility_policy",
        )
    ):
        fail("release-candidate context drifts from the archive inputs")
    s3_ledger_path = resolve_file(
        root,
        "context/s3-approved-runtime.json",
        "S3 approved-runtime ledger",
    )
    sealed_manifest_path = resolve_file(
        root,
        "sealed-runtime/sealed-runtime-manifest.json",
        "sealed runtime manifest",
    )
    resolved_x64_identity_path = resolve_file(
        root,
        "context/resolved-x64-identity.json",
        "resolved linux-x64 identity",
    )
    validate_resolved_x64_identity(
        resolved_x64_identity_path,
        context,
        load_json(s3_ledger_path),
        require_object(load_json(sealed_manifest_path), "sealed runtime manifest"),
    )

    platform_identities: list[dict[str, Any]] = []
    payload_paths: list[pathlib.Path] = [
        context_path,
        s3_ledger_path,
        resolve_file(
            root,
            "context/secretsmanager-approved-runtime.json",
            "Secrets Manager approved-runtime ledger",
        ),
        resolved_x64_identity_path,
        sealed_manifest_path,
    ]
    for rid in ("linux-x64", "linux-arm64"):
        manifest_relative = f"platforms/{rid}/platform-manifest.json"
        manifest_path = resolve_file(root, manifest_relative, f"{rid} platform manifest")
        package_tool.validate(manifest_path)
        manifest = load_json(manifest_path)
        if (
            manifest["candidate"] != selection["candidate"]
            or manifest["target"]["rid"] != rid
        ):
            fail(f"{rid} platform manifest does not bind the selected candidate")
        archive_platform = next(
            (
                item
                for item in archive_inputs["platforms"]
                if item["target"]["rid"] == rid
            ),
            None,
        )
        if archive_platform is None:
            fail(f"archive inputs omit {rid}")
        expected_executable_relative = f"platforms/{rid}/{manifest['executable']['path']}"
        expected_archive_relative = f"platforms/{rid}/{manifest['archive']['path']}"
        if (
            archive_platform["executable_path"] != expected_executable_relative
            or archive_platform["archive"]["path"] != expected_archive_relative
            or archive_platform["archive"]["executable_member"]
            != manifest["archive"]["executable_member"]
        ):
            fail(f"{rid} archive-input paths drift from its platform manifest")
        executable = resolve_file(
            root, expected_executable_relative, f"{rid} executable"
        )
        archive = resolve_file(root, expected_archive_relative, f"{rid} archive")
        checksums = resolve_file(
            root, f"platforms/{rid}/SHA256SUMS", f"{rid} checksums"
        )
        validate_elf(executable, rid)
        payload_paths.extend((executable, archive, manifest_path, checksums))
        platform = RID_TO_PLATFORM[rid]
        platform_identities.append(
            {
                "platform": platform,
                "rid": rid,
                "executable": {
                    "path": expected_executable_relative,
                    "sha256": manifest["executable"]["sha256"],
                    "size_bytes": manifest["executable"]["size_bytes"],
                },
                "archive": {
                    "path": expected_archive_relative,
                    "sha256": manifest["archive"]["sha256"],
                    "size_bytes": manifest["archive"]["size_bytes"],
                },
                "platform_manifest": {
                    "path": manifest_relative,
                    "sha256": sha256_file(manifest_path),
                },
                "base_image": {
                    "name": BASE_NAME,
                    "digest": PLATFORMS[platform]["base_digest"],
                },
            }
        )
    platform_identities.sort(key=lambda item: item["platform"])

    provenance_path = resolve_file(
        root,
        "provenance/archive-payload-provenance.json",
        "archive payload provenance",
    )
    provenance_digest = sha256_file(provenance_path)
    for platform in archive_inputs["platforms"]:
        if platform["provenance"]["bundle_digest"] != provenance_digest:
            fail("archive payload provenance bundle digest does not match its bytes")

    payload_subjects = [subject(path) for path in payload_paths]
    input_subjects = [
        subject(archive_inputs_path),
        subject(resolve_file(root, "SHA256SUMS.all", "complete checksums")),
    ]
    identity: dict[str, Any] = {
        "schema_version": SCHEMA_VERSION,
        "artifact_kind": "resolved_release_candidate_archives",
        "candidate": selection["candidate"],
        "producer": selection["producer"],
        "artifact": selection["artifact"],
        "archive_input": {
            "path": "release-candidate-archive-inputs.json",
            "content_digest": archive_inputs["content_digest"],
            "sha256": sha256_file(archive_inputs_path),
        },
        "provenance": {
            "path": "provenance/archive-payload-provenance.json",
            "sha256": provenance_digest,
            "predicate_type": PREDICATE_TYPE,
        },
        "platforms": platform_identities,
        "attestation_subjects": {
            "payload": payload_subjects,
            "archive_inputs": input_subjects,
        },
    }
    identity["content_digest"] = content_digest(identity)
    write_new_json(args.output, identity)


def validate_identity(path: pathlib.Path) -> dict[str, Any]:
    identity = require_object(load_json(path), "resolved identity")
    if (
        identity.get("schema_version") != SCHEMA_VERSION
        or identity.get("artifact_kind") != "resolved_release_candidate_archives"
    ):
        fail("resolved archive identity schema is invalid")
    require_digest(identity.get("content_digest"), "resolved identity content digest")
    if identity["content_digest"] != content_digest(identity):
        fail("resolved identity content digest does not match")
    if path.read_bytes() != canonical_bytes(identity):
        fail("resolved identity is not canonical JSON")
    return identity


def normalized_subjects(value: Any, name: str) -> Counter[tuple[str, str]]:
    subjects = require_array(value, name)
    result: Counter[tuple[str, str]] = Counter()
    for index, item_value in enumerate(subjects):
        item = require_object(item_value, f"{name}[{index}]")
        subject_name = require_string(item.get("name"), f"{name}[{index}].name")
        digest_object = require_object(item.get("digest"), f"{name}[{index}].digest")
        digest_hex = require_string(
            digest_object.get("sha256"), f"{name}[{index}].digest.sha256"
        )
        require_digest(f"sha256:{digest_hex}", f"{name}[{index}].digest.sha256")
        result[(subject_name, f"sha256:{digest_hex}")] += 1
    return result


def validate_attestation(args: argparse.Namespace) -> None:
    identity = validate_identity(args.identity.resolve())
    repository = identity["candidate"]["source"]["repository"]
    source_sha = identity["producer"]["source_sha"]
    source_ref = identity["producer"]["source_ref"]
    attempt_url = identity["producer"]["attempt_url"]
    expected_values = identity["attestation_subjects"][
        "payload" if args.kind == "payload" else "archive_inputs"
    ]
    expected = Counter(
        (item["name"], item["digest"]) for item in expected_values
    )
    verification = require_array(load_json(args.verification), "verification results")
    matching = 0
    for item_value in verification:
        item = require_object(item_value, "verification result")
        result = require_object(item.get("verificationResult"), "verificationResult")
        statement = require_object(result.get("statement"), "statement")
        signature = require_object(result.get("signature"), "signature")
        certificate = require_object(signature.get("certificate"), "certificate")
        predicate = require_object(statement.get("predicate"), "statement.predicate")
        run_details = require_object(
            predicate.get("runDetails"), "statement.predicate.runDetails"
        )
        metadata = require_object(
            run_details.get("metadata"), "statement.predicate.runDetails.metadata"
        )
        if (
            statement.get("predicateType") != PREDICATE_TYPE
            or certificate.get("githubWorkflowTrigger") != "workflow_dispatch"
            or certificate.get("githubWorkflowRepository") != repository
            or certificate.get("githubWorkflowRef") != source_ref
            or certificate.get("githubWorkflowSHA") != source_sha
            or certificate.get("sourceRepositoryDigest") != source_sha
            or certificate.get("sourceRepositoryRef") != source_ref
            or certificate.get("runInvocationURI") != attempt_url
            or metadata.get("invocationId") != attempt_url
        ):
            continue
        if normalized_subjects(statement.get("subject"), "statement.subject") == expected:
            matching += 1
    if matching != 1:
        fail(
            f"{args.kind} provenance must contain exactly one attestation for the "
            "selected producer attempt and exact subjects"
        )


def validate_dockerfile(path: pathlib.Path) -> None:
    regular_file(path, "release-candidate Dockerfile")
    text = path.read_text(encoding="utf-8")
    required = (
        f"ARG RUNTIME_BASE={BASE_NAME}@{BASE_INDEX_DIGEST}",
        "FROM ${RUNTIME_BASE}",
        "COPY --chmod=0755 Aws2Azure.Proxy /app/Aws2Azure.Proxy",
        'ENTRYPOINT ["/app/Aws2Azure.Proxy"]',
        'CMD ["/app/Aws2Azure.Proxy", "--health-check"]',
        "USER $APP_UID",
    )
    for value in required:
        if value not in text:
            fail(f"release-candidate Dockerfile is missing invariant: {value}")
    forbidden = (
        "dotnet publish",
        "dotnet build",
        "dotnet restore",
        "COPY src/",
        "COPY . .",
        "COPY --from=",
        "/sdk:",
    )
    for value in forbidden:
        if value in text:
            fail(f"release-candidate Dockerfile may not rebuild source: {value}")
    from_lines = [
        line.strip() for line in text.splitlines() if line.lstrip().startswith("FROM ")
    ]
    if from_lines != ["FROM ${RUNTIME_BASE}"]:
        fail("release-candidate Dockerfile must have exactly one runtime-only stage")


def prepare_context(args: argparse.Namespace) -> None:
    identity = validate_identity(args.identity.resolve())
    rid = require_string(args.rid, "RID")
    if rid not in RID_TO_PLATFORM:
        fail("RID must be linux-x64 or linux-arm64")
    platform = next(
        (item for item in identity["platforms"] if item["rid"] == rid), None
    )
    if platform is None:
        fail(f"resolved archive identity omits {rid}")
    bundle = args.bundle.resolve()
    executable = resolve_file(
        bundle, platform["executable"]["path"], f"{rid} executable"
    )
    if (
        sha256_file(executable) != platform["executable"]["sha256"]
        or executable.stat().st_size != platform["executable"]["size_bytes"]
    ):
        fail(f"{rid} executable no longer matches the resolved identity")
    validate_elf(executable, rid)
    validate_dockerfile(args.dockerfile.resolve())
    output = args.output.resolve()
    if output.exists() or output.is_symlink():
        fail(f"context output already exists: {output}")
    output.mkdir(parents=True)
    shutil.copyfile(args.dockerfile.resolve(), output / "Dockerfile")
    shutil.copyfile(executable, output / "Aws2Azure.Proxy")
    (output / "Dockerfile").chmod(0o644)
    (output / "Aws2Azure.Proxy").chmod(0o755)
    os.utime(output / "Dockerfile", (0, 0))
    os.utime(output / "Aws2Azure.Proxy", (0, 0))
    if sorted(path.name for path in output.iterdir()) != [
        "Aws2Azure.Proxy",
        "Dockerfile",
    ]:
        fail("release-candidate build context must contain exactly two files")
    print(
        json.dumps(
            {
                "platform": RID_TO_PLATFORM[rid],
                "rid": rid,
                "executable_digest": platform["executable"]["sha256"],
                "base_digest": platform["base_image"]["digest"],
            },
            sort_keys=True,
        )
    )


def validate_executable(args: argparse.Namespace) -> None:
    identity = validate_identity(args.identity.resolve())
    platform_name = require_string(args.platform, "platform")
    if platform_name not in PLATFORMS:
        fail("platform must be linux/amd64 or linux/arm64")
    platform = next(
        (item for item in identity["platforms"] if item["platform"] == platform_name),
        None,
    )
    if platform is None:
        fail("resolved identity omits requested executable platform")
    executable = regular_file(args.executable.resolve(), "image executable")
    if (
        sha256_file(executable) != platform["executable"]["sha256"]
        or executable.stat().st_size != platform["executable"]["size_bytes"]
    ):
        fail("image executable does not match the exact released executable")
    validate_elf(executable, platform["rid"])


def required_labels(
    identity: dict[str, Any], platform: dict[str, Any]
) -> dict[str, str]:
    return {
        "org.opencontainers.image.source": (
            f"https://github.com/{identity['candidate']['source']['repository']}"
        ),
        "org.opencontainers.image.description": (
            "AWS to Azure transparent protocol proxy"
        ),
        "org.opencontainers.image.licenses": "MIT",
        "org.opencontainers.image.version": identity["candidate"]["identifier"],
        "org.opencontainers.image.revision": identity["candidate"]["source"]["sha"],
        "org.opencontainers.image.base.name": BASE_NAME,
        "org.opencontainers.image.base.digest": platform["base_image"]["digest"],
        "io.aws2azure.release-candidate.archive-content-digest": (
            identity["archive_input"]["content_digest"]
        ),
    }


def validate_image_inspect(
    value: Any, identity: dict[str, Any], platform_identity: dict[str, Any]
) -> None:
    inspect_values = require_array(value, "docker image inspect")
    if len(inspect_values) != 1:
        fail("docker image inspect must contain exactly one image")
    image = require_object(inspect_values[0], "docker image")
    platform = platform_identity["platform"]
    expected_architecture = PLATFORMS[platform]["architecture"]
    if image.get("Os") != "linux" or image.get("Architecture") != expected_architecture:
        fail("local image architecture does not match its RC executable")
    config = require_object(image.get("Config"), "docker image Config")
    if config.get("User") != "1654":
        fail("release-candidate image must run as chiseled APP_UID 1654")
    if config.get("WorkingDir") != "/app":
        fail("release-candidate image working directory must be /app")
    if config.get("Entrypoint") != ["/app/Aws2Azure.Proxy"]:
        fail("release-candidate image entrypoint drifted")
    environment = config.get("Env")
    if not isinstance(environment, list) or not all(
        isinstance(item, str) for item in environment
    ):
        fail("release-candidate image environment is invalid")
    if "ASPNETCORE_URLS=http://+:8080" not in environment:
        fail("release-candidate image must listen on sidecar port 8080")
    if any(item.split("=", 1)[0].startswith("AWS2AZURE_") for item in environment):
        fail("release-candidate image must not bake AWS2AZURE runtime overrides")
    if set((config.get("ExposedPorts") or {}).keys()) != {"8080/tcp"}:
        fail("release-candidate image must expose only port 8080")
    health = require_object(config.get("Healthcheck"), "docker image Healthcheck")
    if (
        health.get("Test") != ["CMD", "/app/Aws2Azure.Proxy", "--health-check"]
        or health.get("Interval") != 30_000_000_000
        or health.get("Timeout") != 3_000_000_000
        or health.get("StartPeriod") != 5_000_000_000
        or health.get("Retries") != 3
    ):
        fail("release-candidate image health probe drifted")
    labels = require_object(config.get("Labels"), "docker image labels")
    for key, expected in required_labels(identity, platform_identity).items():
        if labels.get(key) != expected:
            fail(f"release-candidate image label drifted: {key}")


def validate_single_manifest(path: pathlib.Path, digest: str) -> dict[str, Any]:
    raw = regular_file(path, "platform manifest").read_bytes()
    if f"sha256:{hashlib.sha256(raw).hexdigest()}" != digest:
        fail("platform image digest does not match registry manifest bytes")
    manifest = require_object(load_json(path), "platform image manifest")
    if manifest.get("schemaVersion") != 2 or "manifests" in manifest:
        fail("platform image reference must resolve to one image manifest")
    require_string(manifest.get("mediaType"), "platform image mediaType")
    require_object(manifest.get("config"), "platform image config")
    layers = manifest.get("layers")
    if not isinstance(layers, list) or not layers:
        fail("platform image must contain at least one layer")
    return manifest


def validate_base_layers(args: argparse.Namespace) -> None:
    platform_name = require_string(args.platform, "platform")
    if platform_name not in PLATFORMS:
        fail("platform must be linux/amd64 or linux/arm64")
    expected_application_layers = require_integer(
        args.expected_application_layers, "expected application layers"
    )
    expected_digest = PLATFORMS[platform_name]["base_digest"]
    base_raw = regular_file(args.base_manifest.resolve(), "base manifest").read_bytes()
    if f"sha256:{hashlib.sha256(base_raw).hexdigest()}" != expected_digest:
        fail("base manifest bytes do not match the pinned production digest")
    base = require_object(load_json(args.base_manifest.resolve()), "base manifest")
    image = require_object(load_json(args.image_manifest.resolve()), "image manifest")
    base_layers = base.get("layers")
    image_layers = image.get("layers")
    if (
        not isinstance(base_layers, list)
        or not base_layers
        or not isinstance(image_layers, list)
        or len(image_layers) != len(base_layers) + expected_application_layers
        or image_layers[: len(base_layers)] != base_layers
    ):
        fail("image layers are not based on the exact pinned production manifest")


def validate_image_config(args: argparse.Namespace) -> None:
    identity = validate_identity(args.identity.resolve())
    platform_name = require_string(args.platform, "platform")
    if platform_name not in PLATFORMS:
        fail("platform must be linux/amd64 or linux/arm64")
    platform = next(
        (item for item in identity["platforms"] if item["platform"] == platform_name),
        None,
    )
    if platform is None:
        fail("resolved identity omits requested image platform")
    validate_image_inspect(load_json(args.inspect.resolve()), identity, platform)


def compare_image_config(args: argparse.Namespace) -> None:
    expected_values = require_array(
        load_json(args.expected.resolve()), "expected docker image inspect"
    )
    actual_values = require_array(
        load_json(args.actual.resolve()), "actual docker image inspect"
    )
    if len(expected_values) != 1 or len(actual_values) != 1:
        fail("image-config comparison requires one expected and one actual image")
    expected = require_object(expected_values[0], "expected docker image")
    actual = require_object(actual_values[0], "actual docker image")
    if (
        expected.get("Os") != actual.get("Os")
        or expected.get("Architecture") != actual.get("Architecture")
        or expected.get("Config") != actual.get("Config")
        or expected.get("RootFS") != actual.get("RootFS")
    ):
        fail(
            "registry image config or root filesystem does not match the locally "
            "built and smoked image"
        )


def write_platform_result(args: argparse.Namespace) -> None:
    identity = validate_identity(args.identity.resolve())
    platform_name = require_string(args.platform, "platform")
    if platform_name not in PLATFORMS:
        fail("platform must be linux/amd64 or linux/arm64")
    platform = next(
        (item for item in identity["platforms"] if item["platform"] == platform_name),
        None,
    )
    if platform is None:
        fail("resolved identity omits requested image platform")
    digest = require_digest(args.image_digest, "platform image digest")
    tag = require_tag(args.tag, "platform image tag")
    expected_tag = (
        f"{identity['candidate']['identifier']}-{PLATFORMS[platform_name]['architecture']}-"
        f"{platform['executable']['sha256'].removeprefix('sha256:')}"
    )
    if tag != expected_tag:
        fail("platform image tag must include the exact executable digest")
    base_digest = require_digest(args.base_digest, "base image digest")
    if base_digest != platform["base_image"]["digest"]:
        fail("platform base image digest does not match the pinned production base")
    validate_single_manifest(args.manifest.resolve(), digest)
    inspect = load_json(args.inspect.resolve())
    validate_image_inspect(inspect, identity, platform)
    result: dict[str, Any] = {
        "schema_version": SCHEMA_VERSION,
        "artifact_kind": "release_candidate_platform_image",
        "candidate": identity["candidate"],
        "archive_input_content_digest": identity["archive_input"]["content_digest"],
        "repository": require_string(args.repository, "image repository"),
        "platform": platform_name,
        "rid": platform["rid"],
        "digest": digest,
        "tag": tag,
        "executable": platform["executable"],
        "archive": platform["archive"],
        "base_image": platform["base_image"],
    }
    expected_repository = (
        f"ghcr.io/{identity['candidate']['source']['repository'].lower()}"
    )
    if result["repository"] != expected_repository:
        fail("platform image repository does not match the candidate repository")
    result["content_digest"] = content_digest(result)
    write_new_json(args.output, result)


def validate_platform_result(path: pathlib.Path, identity: dict[str, Any]) -> dict[str, Any]:
    result = require_object(load_json(path), "platform result")
    if (
        result.get("schema_version") != SCHEMA_VERSION
        or result.get("artifact_kind") != "release_candidate_platform_image"
        or result.get("candidate") != identity["candidate"]
        or result.get("archive_input_content_digest")
        != identity["archive_input"]["content_digest"]
    ):
        fail("platform result identity is invalid")
    platform_name = require_string(result.get("platform"), "platform result platform")
    if platform_name not in PLATFORMS:
        fail("platform result has an unsupported platform")
    expected = next(
        item for item in identity["platforms"] if item["platform"] == platform_name
    )
    if (
        result.get("rid") != expected["rid"]
        or result.get("executable") != expected["executable"]
        or result.get("archive") != expected["archive"]
        or result.get("base_image") != expected["base_image"]
    ):
        fail("platform result materials drift from the archive identity")
    require_digest(result.get("digest"), "platform result digest")
    require_tag(result.get("tag"), "platform result tag")
    require_digest(result.get("content_digest"), "platform result content digest")
    if result["content_digest"] != content_digest(result):
        fail("platform result content digest does not match")
    if path.read_bytes() != canonical_bytes(result):
        fail("platform result is not canonical JSON")
    return result


def validate_index(
    path: pathlib.Path, digest: str, results: dict[str, dict[str, Any]]
) -> None:
    raw = regular_file(path, "OCI index").read_bytes()
    if f"sha256:{hashlib.sha256(raw).hexdigest()}" != digest:
        fail("OCI index digest does not match registry bytes")
    index = require_object(load_json(path), "OCI index")
    if index.get("schemaVersion") != 2:
        fail("OCI index schemaVersion must be 2")
    manifests = require_array(index.get("manifests"), "OCI index manifests")
    if len(manifests) != 2:
        fail("OCI index must contain exactly two platform manifests")
    observed: dict[str, str] = {}
    for item_value in manifests:
        item = require_object(item_value, "OCI index manifest")
        platform = require_object(item.get("platform"), "OCI index platform")
        if set(platform) != {"architecture", "os"} or platform.get("os") != "linux":
            fail("OCI index platform identity must contain only Linux architecture")
        platform_name = f"linux/{platform.get('architecture')}"
        if platform_name not in PLATFORMS or platform_name in observed:
            fail("OCI index contains an unsupported or duplicate platform")
        item_digest = require_digest(item.get("digest"), "OCI index manifest digest")
        if item_digest != results[platform_name]["digest"]:
            fail("OCI index platform digest does not match the attested platform image")
        observed[platform_name] = item_digest
    if set(observed) != set(PLATFORMS):
        fail("OCI index must contain exactly amd64 and arm64")


def validate_index_artifact(args: argparse.Namespace) -> None:
    identity = validate_identity(args.identity.resolve())
    results: dict[str, dict[str, Any]] = {}
    for path in sorted(args.results.resolve().glob("*.json")):
        result = validate_platform_result(path, identity)
        platform = result["platform"]
        if platform in results:
            fail(f"duplicate platform result: {platform}")
        results[platform] = result
    if set(results) != set(PLATFORMS):
        fail("platform results must contain exactly linux/amd64 and linux/arm64")
    validate_index(
        args.index.resolve(),
        require_digest(args.index_digest, "OCI index digest"),
        results,
    )


def assemble(args: argparse.Namespace) -> None:
    identity = validate_identity(args.identity.resolve())
    results: dict[str, dict[str, Any]] = {}
    for path in sorted(args.results.resolve().glob("*.json")):
        result = validate_platform_result(path, identity)
        platform = result["platform"]
        if platform in results:
            fail(f"duplicate platform result: {platform}")
        results[platform] = result
    if set(results) != set(PLATFORMS):
        fail("platform results must contain exactly linux/amd64 and linux/arm64")
    repository = require_string(args.repository, "image repository")
    expected_repository = (
        f"ghcr.io/{identity['candidate']['source']['repository'].lower()}"
    )
    if repository != expected_repository or any(
        result["repository"] != repository for result in results.values()
    ):
        fail("image repository does not match every platform result")
    index_digest = require_digest(args.index_digest, "OCI index digest")
    validate_index(args.index.resolve(), index_digest, results)

    candidate = identity["candidate"]["identifier"]
    candidate_tag = require_tag(args.candidate_tag, "candidate tag")
    immutable_tag = require_tag(args.immutable_tag, "immutable tag")
    expected_immutable = (
        f"{candidate}-{identity['archive_input']['content_digest'].removeprefix('sha256:')}"
    )
    if candidate_tag != candidate or immutable_tag != expected_immutable:
        fail("RC image tags do not bind the exact candidate and archive-input digest")
    if candidate_tag in ("latest", "main") or CANDIDATE_RE.fullmatch(candidate_tag) is None:
        fail("RC image path may not create stable SemVer, branch, or latest tags")
    workflow_sha = require_sha(args.workflow_sha, "image workflow source SHA")
    run_id = require_integer(args.run_id, "image workflow run id")
    run_attempt = require_integer(args.run_attempt, "image workflow run attempt")
    attempt_url = (
        f"https://github.com/{identity['candidate']['source']['repository']}/actions/"
        f"runs/{run_id}/attempts/{run_attempt}"
    )
    container = {
        "repository": repository,
        "index_digest": index_digest,
        "platforms": [
            {
                "platform": platform,
                "digest": results[platform]["digest"],
            }
            for platform in ("linux/amd64", "linux/arm64")
        ],
    }
    output: dict[str, Any] = {
        "schema_version": SCHEMA_VERSION,
        "artifact_kind": "release_candidate_ghcr_inputs",
        "candidate": identity["candidate"],
        "archive_input": {
            "producer": identity["producer"],
            "artifact": identity["artifact"],
            "content_digest": identity["archive_input"]["content_digest"],
            "manifest_sha256": identity["archive_input"]["sha256"],
        },
        "producer": {
            "workflow": IMAGE_WORKFLOW,
            "event_name": "workflow_dispatch",
            "run_id": run_id,
            "run_attempt": run_attempt,
            "attempt_url": attempt_url,
            "workflow_source_sha": workflow_sha,
        },
        "container": container,
        "tags": {
            "candidate": candidate_tag,
            "immutable": immutable_tag,
        },
        "platform_materials": [
            {
                "platform": platform,
                "image_digest": results[platform]["digest"],
                "image_tag": results[platform]["tag"],
                "executable_digest": results[platform]["executable"]["sha256"],
                "archive_digest": results[platform]["archive"]["sha256"],
                "base_image": results[platform]["base_image"],
            }
            for platform in ("linux/amd64", "linux/arm64")
        ],
    }
    output["content_digest"] = content_digest(output)
    write_new_json(args.output, output)
    validate_ghcr_input(args.output)

    materials: dict[str, Any] = {
        "schema_version": SCHEMA_VERSION,
        "predicate_type": MATERIALS_PREDICATE_TYPE,
        "candidate": output["candidate"],
        "archive_input": output["archive_input"],
        "container": output["container"],
        "tags": output["tags"],
        "materials": [
            {
                "uri": (
                    f"https://github.com/{identity['candidate']['source']['repository']}"
                    f"/actions/runs/{identity['producer']['run_id']}/artifacts/"
                    f"{identity['artifact']['id']}"
                ),
                "digest": identity["artifact"]["upload_digest"],
            },
            *[
                {
                    "uri": (
                        f"{BASE_NAME}@{results[platform]['base_image']['digest']}"
                    ),
                    "digest": results[platform]["base_image"]["digest"],
                }
                for platform in ("linux/amd64", "linux/arm64")
            ],
            *[
                {
                    "uri": (
                        f"{repository}@{results[platform]['digest']}"
                    ),
                    "digest": results[platform]["digest"],
                }
                for platform in ("linux/amd64", "linux/arm64")
            ],
        ],
    }
    write_new_json(args.materials_output, materials)


def validate_ghcr_input(path: pathlib.Path) -> dict[str, Any]:
    value = require_object(
        load_json(path),
        "GHCR input",
        {
            "schema_version",
            "artifact_kind",
            "candidate",
            "archive_input",
            "producer",
            "container",
            "tags",
            "platform_materials",
            "content_digest",
        },
    )
    if (
        value["schema_version"] != SCHEMA_VERSION
        or value["artifact_kind"] != "release_candidate_ghcr_inputs"
    ):
        fail("GHCR input schema or artifact kind is invalid")
    candidate = require_object(
        value["candidate"], "GHCR input candidate", {"identifier", "source"}
    )
    identifier = require_candidate(candidate["identifier"], "GHCR input candidate id")
    source = require_object(
        candidate["source"],
        "GHCR input candidate source",
        {"repository", "sha", "ref"},
    )
    repository = require_repository(source["repository"], "GHCR input source repository")
    require_sha(source["sha"], "GHCR input source SHA")
    if source["ref"] != f"refs/tags/{identifier}":
        fail("GHCR input source ref does not match its candidate")
    archive_input = require_object(
        value["archive_input"],
        "GHCR input archive identity",
        {"producer", "artifact", "content_digest", "manifest_sha256"},
    )
    archive_content_digest = require_digest(
        archive_input["content_digest"], "GHCR input archive content digest"
    )
    require_digest(
        archive_input["manifest_sha256"], "GHCR input archive manifest digest"
    )
    producer = require_object(
        value["producer"],
        "GHCR input producer",
        {
            "workflow",
            "event_name",
            "run_id",
            "run_attempt",
            "attempt_url",
            "workflow_source_sha",
        },
    )
    image_run_id = require_integer(producer["run_id"], "GHCR input producer run id")
    image_run_attempt = require_integer(
        producer["run_attempt"], "GHCR input producer run attempt"
    )
    require_sha(producer["workflow_source_sha"], "GHCR input workflow source SHA")
    expected_image_attempt = (
        f"https://github.com/{repository}/actions/runs/{image_run_id}/attempts/"
        f"{image_run_attempt}"
    )
    if (
        producer["workflow"] != IMAGE_WORKFLOW
        or producer["event_name"] != "workflow_dispatch"
        or producer["attempt_url"] != expected_image_attempt
    ):
        fail("GHCR input producer identity is invalid")
    container = require_object(
        value["container"],
        "GHCR input container",
        {"repository", "index_digest", "platforms"},
    )
    expected_repository = f"ghcr.io/{repository.lower()}"
    if container["repository"] != expected_repository:
        fail("GHCR input container repository does not match its source")
    require_digest(container["index_digest"], "GHCR input index digest")
    container_platforms = require_array(
        container["platforms"], "GHCR input container platforms"
    )
    if [
        require_object(item, "GHCR input container platform", {"platform", "digest"})[
            "platform"
        ]
        for item in container_platforms
    ] != ["linux/amd64", "linux/arm64"]:
        fail("GHCR input container must contain exact sorted amd64 and arm64")
    container_digests: dict[str, str] = {}
    for item in container_platforms:
        container_digests[item["platform"]] = require_digest(
            item["digest"], f"GHCR input {item['platform']} digest"
        )
    if len(set(container_digests.values())) != 2:
        fail("GHCR input platform image digests must be distinct")
    tags = require_object(
        value["tags"], "GHCR input tags", {"candidate", "immutable"}
    )
    candidate_tag = require_tag(tags["candidate"], "GHCR candidate tag")
    immutable_tag = require_tag(tags["immutable"], "GHCR immutable tag")
    if (
        candidate_tag != identifier
        or immutable_tag
        != f"{identifier}-{archive_content_digest.removeprefix('sha256:')}"
    ):
        fail("GHCR input tags are not exact immutable RC tags")
    materials = require_array(
        value["platform_materials"], "GHCR input platform materials"
    )
    if [item.get("platform") for item in materials if isinstance(item, dict)] != [
        "linux/amd64",
        "linux/arm64",
    ]:
        fail("GHCR input platform materials must contain exact sorted platforms")
    for item_value in materials:
        item = require_object(
            item_value,
            "GHCR input platform material",
            {
                "platform",
                "image_digest",
                "image_tag",
                "executable_digest",
                "archive_digest",
                "base_image",
            },
        )
        platform = item["platform"]
        if item["image_digest"] != container_digests[platform]:
            fail("GHCR input platform material image digest drifts from the index")
        require_digest(item["executable_digest"], "GHCR input executable digest")
        require_digest(item["archive_digest"], "GHCR input archive digest")
        base = require_object(
            item["base_image"],
            "GHCR input base image",
            {"name", "digest"},
        )
        if base != {"name": BASE_NAME, "digest": PLATFORMS[platform]["base_digest"]}:
            fail("GHCR input base image is not the pinned production manifest")
        expected_tag = (
            f"{identifier}-{PLATFORMS[platform]['architecture']}-"
            f"{item['executable_digest'].removeprefix('sha256:')}"
        )
        if item["image_tag"] != expected_tag:
            fail("GHCR input platform tag does not bind its executable digest")
    archive_producer = require_object(
        archive_input["producer"],
        "GHCR archive producer",
        {
            "workflow",
            "event_name",
            "run_id",
            "run_attempt",
            "attempt_url",
            "run_started_at",
            "source_sha",
            "source_ref",
        },
    )
    archive_run_id = require_integer(
        archive_producer["run_id"], "GHCR archive producer run id"
    )
    archive_run_attempt = require_integer(
        archive_producer["run_attempt"], "GHCR archive producer run attempt"
    )
    archive_attempt = (
        f"https://github.com/{repository}/actions/runs/{archive_run_id}/attempts/"
        f"{archive_run_attempt}"
    )
    require_sha(
        archive_producer["source_sha"], "GHCR archive producer source SHA"
    )
    if (
        archive_producer["workflow"] != RC_WORKFLOW
        or archive_producer["event_name"] != "workflow_dispatch"
        or archive_producer["attempt_url"] != archive_attempt
        or archive_producer["source_ref"] != "refs/heads/main"
    ):
        fail("GHCR archive producer identity is invalid")
    parse_timestamp(archive_producer["run_started_at"], "GHCR archive start time")
    archive_artifact = require_object(
        archive_input["artifact"],
        "GHCR archive artifact",
        {"id", "name", "upload_digest", "created_at", "expires_at"},
    )
    require_integer(archive_artifact["id"], "GHCR archive artifact id")
    require_digest(
        archive_artifact["upload_digest"], "GHCR archive artifact upload digest"
    )
    parse_timestamp(archive_artifact["created_at"], "GHCR archive artifact created time")
    parse_timestamp(archive_artifact["expires_at"], "GHCR archive artifact expiry time")
    expected_artifact_name = (
        f"aws2azure-rc-archives-{identifier}-"
        f"{archive_content_digest.removeprefix('sha256:')}-run-{archive_run_id}-"
        f"attempt-{archive_run_attempt}"
    )
    if archive_artifact["name"] != expected_artifact_name:
        fail("GHCR archive artifact name is not canonical")
    require_digest(value["content_digest"], "GHCR input content digest")
    if value["content_digest"] != content_digest(value):
        fail("GHCR input content digest does not match")
    if path.read_bytes() != canonical_bytes(value):
        fail("GHCR input is not canonical JSON")
    return value


def main() -> None:
    parser = argparse.ArgumentParser()
    subparsers = parser.add_subparsers(dest="command", required=True)

    protected_main = subparsers.add_parser("validate-protected-main")
    protected_main.add_argument("--source-sha", required=True)
    protected_main.add_argument("--main-branch-json", type=pathlib.Path, required=True)
    protected_main.add_argument("--main-compare-json", type=pathlib.Path, required=True)

    selection = subparsers.add_parser("validate-selection")
    selection.add_argument("--repository", required=True)
    selection.add_argument("--candidate", required=True)
    selection.add_argument("--source-sha", required=True)
    selection.add_argument("--workflow-source-sha", required=True)
    selection.add_argument("--run-id", type=int, required=True)
    selection.add_argument("--run-attempt", type=int, required=True)
    selection.add_argument("--artifact-id", type=int, required=True)
    selection.add_argument("--artifact-name", required=True)
    selection.add_argument("--artifact-digest", required=True)
    selection.add_argument("--archive-content-digest", required=True)
    selection.add_argument("--run-json", type=pathlib.Path, required=True)
    selection.add_argument("--artifact-json", type=pathlib.Path, required=True)
    selection.add_argument("--main-branch-json", type=pathlib.Path, required=True)
    selection.add_argument("--main-compare-json", type=pathlib.Path, required=True)
    selection.add_argument("--output", type=pathlib.Path, required=True)

    bundle = subparsers.add_parser("validate-bundle")
    bundle.add_argument("--bundle", type=pathlib.Path, required=True)
    bundle.add_argument("--selection", type=pathlib.Path, required=True)
    bundle.add_argument("--output", type=pathlib.Path, required=True)

    attestation = subparsers.add_parser("validate-attestation")
    attestation.add_argument("--kind", choices=("payload", "archive-inputs"), required=True)
    attestation.add_argument("--identity", type=pathlib.Path, required=True)
    attestation.add_argument("--verification", type=pathlib.Path, required=True)

    context = subparsers.add_parser("prepare-context")
    context.add_argument("--bundle", type=pathlib.Path, required=True)
    context.add_argument("--identity", type=pathlib.Path, required=True)
    context.add_argument("--rid", required=True)
    context.add_argument("--dockerfile", type=pathlib.Path, required=True)
    context.add_argument("--output", type=pathlib.Path, required=True)

    executable = subparsers.add_parser("validate-executable")
    executable.add_argument("--identity", type=pathlib.Path, required=True)
    executable.add_argument("--platform", required=True)
    executable.add_argument("--executable", type=pathlib.Path, required=True)

    base_layers = subparsers.add_parser("validate-base-layers")
    base_layers.add_argument("--platform", required=True)
    base_layers.add_argument("--base-manifest", type=pathlib.Path, required=True)
    base_layers.add_argument("--image-manifest", type=pathlib.Path, required=True)
    base_layers.add_argument(
        "--expected-application-layers", type=int, required=True
    )

    platform_result = subparsers.add_parser("write-platform-result")
    platform_result.add_argument("--identity", type=pathlib.Path, required=True)
    platform_result.add_argument("--platform", required=True)
    platform_result.add_argument("--repository", required=True)
    platform_result.add_argument("--tag", required=True)
    platform_result.add_argument("--image-digest", required=True)
    platform_result.add_argument("--base-digest", required=True)
    platform_result.add_argument("--manifest", type=pathlib.Path, required=True)
    platform_result.add_argument("--inspect", type=pathlib.Path, required=True)
    platform_result.add_argument("--output", type=pathlib.Path, required=True)

    image_config = subparsers.add_parser("validate-image-config")
    image_config.add_argument("--identity", type=pathlib.Path, required=True)
    image_config.add_argument("--platform", required=True)
    image_config.add_argument("--inspect", type=pathlib.Path, required=True)

    compare_config = subparsers.add_parser("compare-image-config")
    compare_config.add_argument("--expected", type=pathlib.Path, required=True)
    compare_config.add_argument("--actual", type=pathlib.Path, required=True)

    assemble_parser = subparsers.add_parser("assemble")
    assemble_parser.add_argument("--identity", type=pathlib.Path, required=True)
    assemble_parser.add_argument("--results", type=pathlib.Path, required=True)
    assemble_parser.add_argument("--repository", required=True)
    assemble_parser.add_argument("--candidate-tag", required=True)
    assemble_parser.add_argument("--immutable-tag", required=True)
    assemble_parser.add_argument("--index", type=pathlib.Path, required=True)
    assemble_parser.add_argument("--index-digest", required=True)
    assemble_parser.add_argument("--workflow-sha", required=True)
    assemble_parser.add_argument("--run-id", type=int, required=True)
    assemble_parser.add_argument("--run-attempt", type=int, required=True)
    assemble_parser.add_argument("--output", type=pathlib.Path, required=True)
    assemble_parser.add_argument("--materials-output", type=pathlib.Path, required=True)

    validate_ghcr = subparsers.add_parser("validate-ghcr-input")
    validate_ghcr.add_argument("manifest", type=pathlib.Path)

    validate_index_parser = subparsers.add_parser("validate-index")
    validate_index_parser.add_argument("--identity", type=pathlib.Path, required=True)
    validate_index_parser.add_argument("--results", type=pathlib.Path, required=True)
    validate_index_parser.add_argument("--index", type=pathlib.Path, required=True)
    validate_index_parser.add_argument("--index-digest", required=True)

    args = parser.parse_args()
    if args.command == "validate-protected-main":
        validate_protected_main(args)
    elif args.command == "validate-selection":
        validate_selection(args)
    elif args.command == "validate-bundle":
        validate_bundle(args)
    elif args.command == "validate-attestation":
        validate_attestation(args)
    elif args.command == "prepare-context":
        prepare_context(args)
    elif args.command == "validate-executable":
        validate_executable(args)
    elif args.command == "validate-base-layers":
        validate_base_layers(args)
    elif args.command == "validate-image-config":
        validate_image_config(args)
    elif args.command == "compare-image-config":
        compare_image_config(args)
    elif args.command == "write-platform-result":
        write_platform_result(args)
    elif args.command == "validate-ghcr-input":
        validate_ghcr_input(args.manifest)
    elif args.command == "validate-index":
        validate_index_artifact(args)
    else:
        assemble(args)


if __name__ == "__main__":
    main()
