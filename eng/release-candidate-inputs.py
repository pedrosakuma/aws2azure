#!/usr/bin/env python3
"""Bind approved runtime ledgers and produced archives into canonical RC inputs."""

from __future__ import annotations

import argparse
import hashlib
import json
import pathlib
import re
import stat
from typing import Any, NoReturn


SCHEMA_VERSION = 1
CANDIDATE_RE = re.compile(
    r"v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)-rc\.([1-9][0-9]*)"
)
SHA_RE = re.compile(r"[0-9a-f]{40}")
DIGEST_RE = re.compile(r"sha256:[0-9a-f]{64}")
REPOSITORY_RE = re.compile(r"[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+")
PROFILES = ("s3-basic-object-crud", "secretsmanager-basic-lifecycle")
POLICY_IDENTIFIER = "aws2azure-compatibility-policy-v1"
SEALED_WORKFLOW = ".github/workflows/sealed-runtime.yml"
RC_WORKFLOW = ".github/workflows/release-candidate.yml"
TARGETS = {
    "linux-arm64": {
        "operating_system": "linux",
        "architecture": "arm64",
        "rid": "linux-arm64",
    },
    "linux-x64": {
        "operating_system": "linux",
        "architecture": "x64",
        "rid": "linux-x64",
    },
}


def fail(message: str) -> NoReturn:
    raise SystemExit(f"release-candidate-inputs: {message}")


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
    with regular_file(path, str(path)).open("rb") as stream:
        while chunk := stream.read(1024 * 1024):
            digest.update(chunk)
    return f"sha256:{digest.hexdigest()}"


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


def require_object(value: Any, name: str) -> dict[str, Any]:
    if not isinstance(value, dict):
        fail(f"{name} must be an object")
    return value


def require_keys(value: Any, name: str, keys: set[str]) -> dict[str, Any]:
    result = require_object(value, name)
    if set(result) != keys:
        fail(f"{name} fields are invalid")
    return result


def require_integer(value: Any, name: str) -> int:
    if isinstance(value, bool) or not isinstance(value, int) or value <= 0:
        fail(f"{name} must be a positive integer")
    return value


def require_digest(value: Any, name: str) -> str:
    if not isinstance(value, str) or DIGEST_RE.fullmatch(value) is None:
        fail(f"{name} must be a lowercase SHA-256 digest")
    return value


def require_sha(value: Any, name: str) -> str:
    if not isinstance(value, str) or SHA_RE.fullmatch(value) is None:
        fail(f"{name} must be a lowercase 40-character SHA")
    return value


def require_candidate(value: Any) -> str:
    if not isinstance(value, str) or CANDIDATE_RE.fullmatch(value) is None:
        fail("candidate must be strict vMAJOR.MINOR.PATCH-rc.NUMBER SemVer")
    return value


def top_level_yaml_identity(path: pathlib.Path) -> dict[str, Any]:
    values: dict[str, str] = {}
    try:
        for line in regular_file(path, str(path)).read_text(encoding="utf-8").splitlines():
            if not line or line[0].isspace() or line.lstrip().startswith("#"):
                continue
            match = re.fullmatch(r"([a-z_]+):[ \t]*(.*)", line)
            if match and match.group(1) in {"schema_version", "id", "version"}:
                if match.group(1) in values:
                    fail(f"{path} contains duplicate top-level {match.group(1)}")
                values[match.group(1)] = match.group(2).strip()
    except (OSError, UnicodeError) as error:
        fail(f"cannot read profile {path}: {error}")
    if set(values) != {"schema_version", "id", "version"}:
        fail(f"{path} does not expose the required profile identity")
    try:
        schema_version = int(values["schema_version"])
        version = int(values["version"])
    except ValueError:
        fail(f"{path} profile versions must be integers")
    if schema_version != 1 or version <= 0:
        fail(f"{path} profile identity is invalid")
    return {"id": values["id"], "version": version}


def approved_runtime(
    ledger_path: pathlib.Path,
    profile_path: pathlib.Path,
    expected_profile: str,
    candidate: str,
    repository: str,
    source_sha: str,
) -> tuple[dict[str, Any], dict[str, Any]]:
    export = require_object(load_json(ledger_path), f"{expected_profile} ledger")
    if export.get("schema_version") != 1:
        fail(f"{expected_profile} ledger export schema must be 1")
    ledger_digest = require_digest(
        export.get("ledger_record_digest"), f"{expected_profile} ledger digest"
    )
    record = require_object(export.get("record"), f"{expected_profile} record")
    profile = require_object(record.get("profile"), f"{expected_profile} profile")
    profile_id = profile.get("id")
    profile_version = require_integer(
        profile.get("version"), f"{expected_profile} profile version"
    )
    if profile_id != expected_profile:
        fail(f"ledger profile is not {expected_profile}")
    source_profile = top_level_yaml_identity(profile_path)
    if source_profile != {"id": profile_id, "version": profile_version}:
        fail(f"{expected_profile} profile file identity drifts from its ledger")
    if (
        record.get("schema_version") != 1
        or record.get("status") != "approved"
        or require_object(record.get("eligibility"), "eligibility").get(
            "promotion_eligible"
        )
        is not True
    ):
        fail(f"{expected_profile} ledger is not promotion-approved")
    runtime = require_object(record.get("runtime"), f"{expected_profile} runtime")
    if runtime.get("target") != {
        "operating_system": "linux",
        "architecture": "x64",
        "rid": "linux-x64",
    }:
        fail(f"{expected_profile} approved runtime is not exact linux-x64")
    if (
        runtime.get("source_repository") != repository
        or runtime.get("source_sha") != source_sha
    ):
        fail(f"{expected_profile} approved runtime source drifts from the producer")
    aggregate_digest = require_digest(
        runtime.get("aggregate_digest"), f"{expected_profile} aggregate digest"
    )
    executable_digest = require_digest(
        runtime.get("executable_digest"), f"{expected_profile} executable digest"
    )
    producer = require_object(record.get("producer"), f"{expected_profile} producer")
    run_id = require_integer(producer.get("run_id"), f"{expected_profile} run id")
    run_attempt = require_integer(
        producer.get("run_attempt"), f"{expected_profile} run attempt"
    )
    run_url = f"https://github.com/{repository}/actions/runs/{run_id}"
    if producer.get("workflow") != SEALED_WORKFLOW or producer.get("run_url") != run_url:
        fail(f"{expected_profile} producer identity is invalid")
    attempt_url = f"{run_url}/attempts/{run_attempt}"
    artifact = require_object(record.get("artifact"), f"{expected_profile} artifact")
    artifact_id = require_integer(
        artifact.get("id"), f"{expected_profile} artifact id"
    )
    artifact_name = artifact.get("name")
    if not isinstance(artifact_name, str) or not artifact_name:
        fail(f"{expected_profile} artifact name is invalid")
    upload_digest = require_digest(
        artifact.get("upload_digest"), f"{expected_profile} upload digest"
    )
    attestation = require_object(
        record.get("attestation"), f"{expected_profile} attestation"
    )
    source_ref = attestation.get("source_ref")
    if source_ref not in ("refs/heads/main", f"refs/tags/{candidate}"):
        fail(
            f"{expected_profile} sealed source ref must be protected main or "
            "the exact candidate tag"
        )
    manifest_digest = require_digest(
        attestation.get("manifest_subject_digest"),
        f"{expected_profile} manifest digest",
    )
    if (
        attestation.get("predicate_type") != "https://slsa.dev/provenance/v1"
        or attestation.get("repository") != repository
        or attestation.get("signer_workflow")
        != f"{repository}/{SEALED_WORKFLOW}"
        or attestation.get("source_sha") != source_sha
        or attestation.get("subject_name") != "Aws2Azure.Proxy"
        or attestation.get("subject_digest") != executable_digest
        or attestation.get("manifest_subject_name")
        != "sealed-runtime-manifest.json"
    ):
        fail(f"{expected_profile} sealed attestation identity is invalid")
    common = {
        "source_repository": repository,
        "source_sha": source_sha,
        "source_ref": source_ref,
        "aggregate_digest": aggregate_digest,
        "executable_digest": executable_digest,
        "manifest_digest": manifest_digest,
        "producer": {
            "workflow": SEALED_WORKFLOW,
            "run_id": run_id,
            "run_attempt": run_attempt,
            "attempt_url": attempt_url,
        },
        "artifact": {
            "id": artifact_id,
            "name": artifact_name,
            "upload_digest": upload_digest,
        },
    }
    canonical = {
        "ledger_record_digest": ledger_digest,
        "profile": {"id": profile_id, "version": profile_version},
        "status": "approved",
        **common,
    }
    workload = {
        "profile": {
            "id": profile_id,
            "version": profile_version,
            "digest": sha256_file(profile_path),
        },
        "approved_runtime": canonical,
    }
    return common, workload


def create_context(args: argparse.Namespace) -> None:
    candidate = require_candidate(args.candidate)
    if REPOSITORY_RE.fullmatch(args.repository) is None:
        fail("repository must be OWNER/REPO")
    source_sha = require_sha(args.source_sha, "source SHA")
    orchestration_sha = require_sha(args.orchestration_sha, "orchestration SHA")
    approval_sha = require_sha(args.approval_sha, "approved-ledger SHA")
    if orchestration_sha != approval_sha:
        fail("orchestration and approved-ledger SHAs must identify one trusted main commit")
    if args.source_ref != f"refs/tags/{candidate}":
        fail("source ref must be the exact candidate tag")
    common_identities: list[dict[str, Any]] = []
    workloads: list[dict[str, Any]] = []
    for profile_id, ledger_path, profile_path in (
        (PROFILES[0], args.s3_ledger, args.s3_profile),
        (PROFILES[1], args.secrets_ledger, args.secrets_profile),
    ):
        common, workload = approved_runtime(
            ledger_path.resolve(),
            profile_path.resolve(),
            profile_id,
            candidate,
            args.repository,
            source_sha,
        )
        common_identities.append(common)
        workloads.append(workload)
    if common_identities[0] != common_identities[1]:
        fail("both GA profile ledgers must approve the exact same sealed runtime")
    if (
        workloads[0]["approved_runtime"]["ledger_record_digest"]
        == workloads[1]["approved_runtime"]["ledger_record_digest"]
    ):
        fail("GA profile ledger identities must remain distinct")
    workloads.sort(key=lambda item: item["profile"]["id"])
    policy_path = regular_file(args.policy.resolve(), "compatibility policy")
    context: dict[str, Any] = {
        "schema_version": SCHEMA_VERSION,
        "artifact_kind": "release_candidate_context",
        "candidate": {
            "identifier": candidate,
            "source": {
                "repository": args.repository,
                "sha": source_sha,
                "ref": args.source_ref,
            },
        },
        "orchestration_source": {
            "repository": args.repository,
            "sha": orchestration_sha,
            "ref": "refs/heads/main",
        },
        "approved_ledger_source": {
            "repository": args.repository,
            "sha": approval_sha,
            "ref": "refs/heads/main",
        },
        "sealed_runtime": common_identities[0],
        "workloads": workloads,
        "compatibility_policy": {
            "identifier": POLICY_IDENTIFIER,
            "digest": sha256_file(policy_path),
        },
    }
    context["content_digest"] = content_digest(context)
    output = args.output.resolve()
    if output.exists():
        fail(f"output already exists: {output}")
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_bytes(canonical_bytes(context))
    validate_context(output)
    print(output)


def validate_context(path: pathlib.Path) -> dict[str, Any]:
    context = require_object(load_json(path), "context")
    expected_keys = {
        "schema_version",
        "artifact_kind",
        "candidate",
        "orchestration_source",
        "approved_ledger_source",
        "sealed_runtime",
        "workloads",
        "compatibility_policy",
        "content_digest",
    }
    if set(context) != expected_keys:
        fail("context fields are invalid")
    if (
        context["schema_version"] != 1
        or context["artifact_kind"] != "release_candidate_context"
    ):
        fail("context schema or artifact kind is invalid")
    candidate = require_keys(context["candidate"], "candidate", {"identifier", "source"})
    identifier = require_candidate(candidate.get("identifier"))
    source = require_keys(
        candidate.get("source"), "candidate source", {"repository", "sha", "ref"}
    )
    if (
        REPOSITORY_RE.fullmatch(str(source.get("repository"))) is None
        or source.get("ref") != f"refs/tags/{identifier}"
    ):
        fail("context candidate source is invalid")
    require_sha(source.get("sha"), "context source SHA")
    orchestration_source = require_keys(
        context["orchestration_source"],
        "orchestration source",
        {"repository", "sha", "ref"},
    )
    if (
        orchestration_source["repository"] != source["repository"]
        or orchestration_source["ref"] != "refs/heads/main"
    ):
        fail("orchestration source must be the same repository's protected main")
    require_sha(orchestration_source["sha"], "orchestration source SHA")
    approved_source = require_keys(
        context["approved_ledger_source"],
        "approved ledger source",
        {"repository", "sha", "ref"},
    )
    if (
        approved_source != orchestration_source
    ):
        fail("approved ledger source must equal the exact trusted orchestration source")
    sealed = require_keys(
        context["sealed_runtime"],
        "sealed runtime",
        {
            "source_repository",
            "source_sha",
            "source_ref",
            "aggregate_digest",
            "executable_digest",
            "manifest_digest",
            "producer",
            "artifact",
        },
    )
    if (
        sealed["source_repository"] != source["repository"]
        or sealed["source_sha"] != source["sha"]
    ):
        fail("sealed runtime source drifts from the candidate")
    sealed_ref = sealed["source_ref"]
    if sealed_ref not in ("refs/heads/main", f"refs/tags/{identifier}"):
        fail("sealed runtime source ref must be protected main or the exact candidate tag")
    for field in ("aggregate_digest", "executable_digest", "manifest_digest"):
        require_digest(sealed.get(field), f"sealed runtime {field}")
    sealed_producer = require_keys(
        sealed["producer"],
        "sealed runtime producer",
        {"workflow", "run_id", "run_attempt", "attempt_url"},
    )
    sealed_run_id = require_integer(sealed_producer["run_id"], "sealed run id")
    sealed_run_attempt = require_integer(
        sealed_producer["run_attempt"], "sealed run attempt"
    )
    if (
        sealed_producer["workflow"] != SEALED_WORKFLOW
        or sealed_producer["attempt_url"]
        != f"https://github.com/{source['repository']}/actions/runs/{sealed_run_id}/attempts/{sealed_run_attempt}"
    ):
        fail("sealed runtime producer identity is invalid")
    sealed_artifact = require_keys(
        sealed["artifact"],
        "sealed runtime artifact",
        {"id", "name", "upload_digest"},
    )
    require_integer(sealed_artifact["id"], "sealed artifact id")
    if not isinstance(sealed_artifact["name"], str) or not sealed_artifact["name"]:
        fail("sealed artifact name is invalid")
    require_digest(sealed_artifact["upload_digest"], "sealed upload digest")
    workloads = context["workloads"]
    if (
        not isinstance(workloads, list)
        or [item.get("profile", {}).get("id") for item in workloads] != sorted(PROFILES)
    ):
        fail("context workloads must contain both GA profiles in sorted order")
    ledger_digests: list[str] = []
    for workload in workloads:
        workload = require_keys(
            workload, "workload", {"profile", "approved_runtime"}
        )
        profile = require_keys(
            workload["profile"], "workload profile", {"id", "version", "digest"}
        )
        require_integer(profile["version"], "workload profile version")
        require_digest(profile["digest"], "workload profile digest")
        approved = require_keys(
            workload.get("approved_runtime"),
            "approved runtime",
            {
                "ledger_record_digest",
                "profile",
                "status",
                "source_repository",
                "source_sha",
                "source_ref",
                "aggregate_digest",
                "executable_digest",
                "manifest_digest",
                "producer",
                "artifact",
            },
        )
        ledger_digests.append(
            require_digest(approved.get("ledger_record_digest"), "ledger digest")
        )
        if (
            approved["status"] != "approved"
            or approved["profile"]
            != {"id": profile["id"], "version": profile["version"]}
        ):
            fail("approved runtime profile or status is invalid")
        if any(approved.get(key) != sealed.get(key) for key in sealed):
            fail("approved runtime drifts from the shared sealed runtime")
    if len(set(ledger_digests)) != 2:
        fail("approved ledger identities must be distinct")
    policy = require_object(context["compatibility_policy"], "compatibility policy")
    if policy.get("identifier") != POLICY_IDENTIFIER:
        fail("compatibility policy identifier is invalid")
    require_digest(policy.get("digest"), "compatibility policy digest")
    require_digest(context["content_digest"], "context content digest")
    if context["content_digest"] != content_digest(context):
        fail("context content digest does not match")
    if path.read_bytes() != canonical_bytes(context):
        fail("context is not canonical JSON")
    return context


def relative_file(root: pathlib.Path, path: pathlib.Path, name: str) -> str:
    resolved = regular_file(path.resolve(), name)
    try:
        return resolved.relative_to(root).as_posix()
    except ValueError:
        fail(f"{name} must be below the archive-input root")


def resolve_relative_file(root: pathlib.Path, relative: Any, name: str) -> pathlib.Path:
    if not isinstance(relative, str) or "\\" in relative:
        fail(f"{name} path is invalid")
    pure = pathlib.PurePosixPath(relative)
    if (
        pure.is_absolute()
        or pure.as_posix() != relative
        or not pure.parts
        or any(part in ("", ".", "..") for part in pure.parts)
    ):
        fail(f"{name} path is not a normalized safe relative path")
    path = regular_file(root.joinpath(*pure.parts), name).resolve()
    if root != path and root not in path.parents:
        fail(f"{name} escapes the archive-input root")
    return path


def validate_platform_manifest(path: pathlib.Path, context: dict[str, Any]) -> dict[str, Any]:
    manifest = require_keys(
        load_json(path),
        "platform manifest",
        {
            "schema_version",
            "artifact_kind",
            "candidate",
            "target",
            "executable",
            "archive",
            "checksums",
            "content_digest",
        },
    )
    if (
        manifest["schema_version"] != 1
        or manifest["artifact_kind"] != "release_candidate_platform"
        or manifest["candidate"] != context["candidate"]
    ):
        fail("platform manifest identity is invalid")
    target = require_object(manifest["target"], "platform target")
    rid = target.get("rid")
    if rid not in TARGETS or target != TARGETS[rid]:
        fail("platform target is invalid")
    require_digest(manifest["content_digest"], "platform content digest")
    if manifest["content_digest"] != content_digest(manifest):
        fail("platform content digest does not match")
    if path.read_bytes() != canonical_bytes(manifest):
        fail("platform manifest is not canonical JSON")
    root = path.resolve().parent
    executable = require_object(manifest["executable"], "platform executable")
    executable_path = resolve_relative_file(
        root, executable.get("path"), "platform executable"
    )
    if (
        sha256_file(executable_path)
        != require_digest(executable.get("sha256"), "platform executable digest")
        or executable_path.stat().st_size != executable.get("size_bytes")
    ):
        fail("platform executable identity does not match")
    archive = require_object(manifest["archive"], "platform archive")
    archive_path = resolve_relative_file(root, archive.get("path"), "platform archive")
    if (
        sha256_file(archive_path)
        != require_digest(archive.get("sha256"), "platform archive digest")
        or archive_path.stat().st_size != archive.get("size_bytes")
    ):
        fail("platform archive identity does not match")
    checksums = require_object(manifest["checksums"], "platform checksums")
    checksum_path = resolve_relative_file(
        root, checksums.get("path"), "platform checksums"
    )
    if (
        sha256_file(checksum_path)
        != require_digest(checksums.get("sha256"), "platform checksum digest")
        or checksum_path.stat().st_size != checksums.get("size_bytes")
    ):
        fail("platform checksum identity does not match")
    return manifest


def assemble(args: argparse.Namespace) -> None:
    output = args.output.resolve()
    if output.exists():
        fail(f"output already exists: {output}")
    root = output.parent
    context = validate_context(args.context.resolve())
    source = context["candidate"]["source"]
    orchestration_source = context["orchestration_source"]
    producer = {
        "workflow": RC_WORKFLOW,
        "event_name": "workflow_dispatch",
        "run_id": require_integer(args.run_id, "run id"),
        "run_attempt": require_integer(args.run_attempt, "run attempt"),
        "attempt_url": args.attempt_url,
        "source_sha": orchestration_source["sha"],
        "source_ref": orchestration_source["ref"],
    }
    expected_attempt = (
        f"https://github.com/{source['repository']}/actions/runs/"
        f"{producer['run_id']}/attempts/{producer['run_attempt']}"
    )
    if producer["attempt_url"] != expected_attempt:
        fail("producer attempt URL is invalid")
    bundle_digest = require_digest(args.bundle_digest, "provenance bundle digest")
    sealed_manifest_path = relative_file(
        root, args.sealed_manifest, "sealed runtime manifest"
    )
    if sha256_file(args.sealed_manifest.resolve()) != context["sealed_runtime"][
        "manifest_digest"
    ]:
        fail("sealed runtime manifest bytes drift from both approved ledgers")

    platforms: list[dict[str, Any]] = []
    for manifest_path in (args.arm64_manifest, args.x64_manifest):
        package_manifest = validate_platform_manifest(manifest_path.resolve(), context)
        target = require_object(package_manifest.get("target"), "platform target")
        rid = target.get("rid")
        platform_root = manifest_path.resolve().parent
        executable_path = relative_file(
            root, platform_root / package_manifest["executable"]["path"], f"{rid} executable"
        )
        archive_path = relative_file(
            root, platform_root / package_manifest["archive"]["path"], f"{rid} archive"
        )
        platform: dict[str, Any] = {
            "target": target,
            "executable_path": executable_path,
            "archive": {
                "path": archive_path,
                "executable_member": package_manifest["archive"]["executable_member"],
            },
            "provenance": {
                "predicate_type": "https://slsa.dev/provenance/v1",
                "bundle_digest": bundle_digest,
                "producer_attempt_url": producer["attempt_url"],
                "producer_source_sha": orchestration_source["sha"],
                "candidate_source_sha": source["sha"],
            },
            "sealed_runtime": None,
        }
        if rid == "linux-x64":
            if (
                package_manifest["executable"]["sha256"]
                != context["sealed_runtime"]["executable_digest"]
            ):
                fail("linux-x64 package does not contain exact approved sealed bytes")
            platform["sealed_runtime"] = {
                "manifest_path": sealed_manifest_path,
                "artifact_id": context["sealed_runtime"]["artifact"]["id"],
                "upload_digest": context["sealed_runtime"]["artifact"]["upload_digest"],
            }
        platforms.append(platform)
    platforms.sort(key=lambda item: item["target"]["rid"])
    if [item["target"]["rid"] for item in platforms] != ["linux-arm64", "linux-x64"]:
        fail("platform inputs must contain exact linux-arm64 and linux-x64")

    evidence: dict[str, Any] = {
        "schema_version": SCHEMA_VERSION,
        "artifact_kind": "release_candidate_archive_inputs",
        "candidate": context["candidate"],
        "orchestration_source": orchestration_source,
        "producer": producer,
        "approved_ledger_source": context["approved_ledger_source"],
        "platforms": platforms,
        "workloads": context["workloads"],
        "compatibility_policy": context["compatibility_policy"],
        "pending_interfaces": {
            "container": {
                "status": "pending",
                "issue": 588,
                "reason": "GHCR identities must be produced from these exact released binaries.",
            },
            "observation_evidence": {
                "status": "pending",
                "issue": 582,
                "reason": "Per-profile real-Azure observation evidence is not produced by this workflow.",
            },
        },
    }
    evidence["content_digest"] = content_digest(evidence)
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_bytes(canonical_bytes(evidence))
    validate_inputs(output)
    print(output)


def validate_inputs(path: pathlib.Path) -> dict[str, Any]:
    evidence = require_object(load_json(path), "archive inputs")
    expected_keys = {
        "schema_version",
        "artifact_kind",
        "candidate",
        "orchestration_source",
        "producer",
        "approved_ledger_source",
        "platforms",
        "workloads",
        "compatibility_policy",
        "pending_interfaces",
        "content_digest",
    }
    if set(evidence) != expected_keys:
        fail("archive-input fields are invalid")
    if (
        evidence["schema_version"] != 1
        or evidence["artifact_kind"] != "release_candidate_archive_inputs"
    ):
        fail("archive-input schema or artifact kind is invalid")
    candidate = require_keys(evidence["candidate"], "candidate", {"identifier", "source"})
    identifier = require_candidate(candidate.get("identifier"))
    source = require_keys(
        candidate.get("source"), "source", {"repository", "sha", "ref"}
    )
    require_sha(source.get("sha"), "source SHA")
    if (
        REPOSITORY_RE.fullmatch(str(source.get("repository"))) is None
        or source.get("ref") != f"refs/tags/{identifier}"
    ):
        fail("source ref does not bind the exact candidate")
    orchestration_source = require_keys(
        evidence["orchestration_source"],
        "orchestration source",
        {"repository", "sha", "ref"},
    )
    if (
        orchestration_source["repository"] != source["repository"]
        or orchestration_source["ref"] != "refs/heads/main"
    ):
        fail("orchestration source must be exact protected main history")
    require_sha(orchestration_source["sha"], "orchestration source SHA")
    producer = require_keys(
        evidence["producer"],
        "producer",
        {
            "workflow",
            "event_name",
            "run_id",
            "run_attempt",
            "attempt_url",
            "source_sha",
            "source_ref",
        },
    )
    if (
        producer.get("workflow") != RC_WORKFLOW
        or producer.get("event_name") != "workflow_dispatch"
        or producer.get("source_sha") != orchestration_source["sha"]
        or producer.get("source_ref") != orchestration_source["ref"]
    ):
        fail("producer identity is invalid")
    run_id = require_integer(producer.get("run_id"), "producer run id")
    run_attempt = require_integer(
        producer.get("run_attempt"), "producer run attempt"
    )
    if producer.get("attempt_url") != (
        f"https://github.com/{source['repository']}/actions/runs/{run_id}/attempts/"
        f"{run_attempt}"
    ):
        fail("producer attempt URL drifts from its exact identity")
    approved_source = require_keys(
        evidence["approved_ledger_source"],
        "approved ledger source",
        {"repository", "sha", "ref"},
    )
    if (
        approved_source != orchestration_source
    ):
        fail("approved ledger source must equal the exact orchestration source")
    platforms = evidence["platforms"]
    if (
        not isinstance(platforms, list)
        or [item.get("target", {}).get("rid") for item in platforms]
        != ["linux-arm64", "linux-x64"]
    ):
        fail("archive inputs must contain exact sorted platforms")
    root = path.resolve().parent
    approved_common: dict[str, Any] | None = None
    for platform in platforms:
        platform = require_keys(
            platform,
            "platform",
            {"target", "executable_path", "archive", "provenance", "sealed_runtime"},
        )
        target = require_object(platform["target"], "platform target")
        rid = target.get("rid")
        if rid not in TARGETS or target != TARGETS[rid]:
            fail("platform target is invalid")
        executable = resolve_relative_file(
            root, platform["executable_path"], f"{rid} executable"
        )
        archive_input = require_keys(
            platform["archive"], f"{rid} archive", {"path", "executable_member"}
        )
        archive = resolve_relative_file(root, archive_input["path"], f"{rid} archive")
        provenance = require_keys(
            platform["provenance"],
            f"{rid} provenance",
            {
                "predicate_type",
                "bundle_digest",
                "producer_attempt_url",
                "producer_source_sha",
                "candidate_source_sha",
            },
        )
        require_digest(provenance.get("bundle_digest"), f"{rid} bundle digest")
        if (
            provenance.get("predicate_type") != "https://slsa.dev/provenance/v1"
            or provenance.get("producer_attempt_url") != producer["attempt_url"]
            or provenance.get("producer_source_sha") != orchestration_source["sha"]
            or provenance.get("candidate_source_sha") != source["sha"]
        ):
            fail(f"{rid} provenance identity is invalid")
        if executable.stat().st_size <= 0 or archive.stat().st_size <= 0:
            fail(f"{rid} files must not be empty")
        if rid == "linux-x64":
            sealed = require_keys(
                platform.get("sealed_runtime"),
                "sealed runtime",
                {"manifest_path", "artifact_id", "upload_digest"},
            )
            sealed_manifest = resolve_relative_file(
                root, sealed["manifest_path"], "sealed runtime manifest"
            )
            require_integer(sealed.get("artifact_id"), "sealed artifact id")
            require_digest(sealed.get("upload_digest"), "sealed upload digest")
        elif platform.get("sealed_runtime") is not None:
            fail("linux-arm64 must not claim the linux-x64 sealed runtime")
    workloads = evidence["workloads"]
    if (
        not isinstance(workloads, list)
        or [item.get("profile", {}).get("id") for item in workloads] != sorted(PROFILES)
    ):
        fail("archive inputs must bind both exact GA profiles")
    ledger_digests: list[str] = []
    for workload in workloads:
        workload = require_keys(
            workload, "workload", {"profile", "approved_runtime"}
        )
        profile = require_keys(
            workload["profile"], "profile", {"id", "version", "digest"}
        )
        require_integer(profile["version"], "profile version")
        require_digest(profile["digest"], "profile digest")
        approved = require_keys(
            workload["approved_runtime"],
            "approved runtime",
            {
                "ledger_record_digest",
                "profile",
                "status",
                "source_repository",
                "source_sha",
                "source_ref",
                "aggregate_digest",
                "executable_digest",
                "manifest_digest",
                "producer",
                "artifact",
            },
        )
        ledger_digests.append(
            require_digest(approved["ledger_record_digest"], "ledger digest")
        )
        if (
            approved["status"] != "approved"
            or approved["profile"]
            != {"id": profile["id"], "version": profile["version"]}
            or approved["source_repository"] != source["repository"]
            or approved["source_sha"] != source["sha"]
        ):
            fail("approved runtime source, profile, or status is invalid")
        common = {
            key: approved[key]
            for key in (
                "source_repository",
                "source_sha",
                "source_ref",
                "aggregate_digest",
                "executable_digest",
                "manifest_digest",
                "producer",
                "artifact",
            )
        }
        if approved_common is None:
            approved_common = common
        elif approved_common != common:
            fail("GA profile approved runtime identities disagree")
    if len(set(ledger_digests)) != 2 or approved_common is None:
        fail("approved runtime ledger identities must be distinct")
    x64_platform = next(
        item for item in platforms if item["target"]["rid"] == "linux-x64"
    )
    x64_executable = resolve_relative_file(
        root, x64_platform["executable_path"], "linux-x64 executable"
    )
    if sha256_file(x64_executable) != approved_common["executable_digest"]:
        fail("linux-x64 bytes drift from the approved runtime")
    if sha256_file(sealed_manifest) != approved_common["manifest_digest"]:
        fail("sealed runtime manifest drifts from the approved ledgers")
    if (
        x64_platform["sealed_runtime"]["artifact_id"]
        != approved_common["artifact"]["id"]
        or x64_platform["sealed_runtime"]["upload_digest"]
        != approved_common["artifact"]["upload_digest"]
    ):
        fail("sealed artifact identity drifts from the approved ledgers")
    policy = require_object(evidence["compatibility_policy"], "compatibility policy")
    if policy.get("identifier") != POLICY_IDENTIFIER:
        fail("compatibility policy identifier is invalid")
    require_digest(policy.get("digest"), "compatibility policy digest")
    pending = require_object(evidence["pending_interfaces"], "pending interfaces")
    if (
        pending.get("container", {}).get("status") != "pending"
        or pending.get("container", {}).get("issue") != 588
        or pending.get("observation_evidence", {}).get("status") != "pending"
        or pending.get("observation_evidence", {}).get("issue") != 582
    ):
        fail("only the explicit #588 and #582 interfaces may remain pending")
    require_digest(evidence["content_digest"], "archive-input content digest")
    if evidence["content_digest"] != content_digest(evidence):
        fail("archive-input content digest does not match")
    if path.read_bytes() != canonical_bytes(evidence):
        fail("archive-input manifest is not canonical JSON")
    return evidence


def main() -> None:
    parser = argparse.ArgumentParser()
    subparsers = parser.add_subparsers(dest="command", required=True)
    context_parser = subparsers.add_parser("create-context")
    context_parser.add_argument("--candidate", required=True)
    context_parser.add_argument("--repository", required=True)
    context_parser.add_argument("--source-sha", required=True)
    context_parser.add_argument("--source-ref", required=True)
    context_parser.add_argument("--orchestration-sha", required=True)
    context_parser.add_argument("--approval-sha", required=True)
    context_parser.add_argument("--s3-ledger", type=pathlib.Path, required=True)
    context_parser.add_argument("--s3-profile", type=pathlib.Path, required=True)
    context_parser.add_argument("--secrets-ledger", type=pathlib.Path, required=True)
    context_parser.add_argument("--secrets-profile", type=pathlib.Path, required=True)
    context_parser.add_argument("--policy", type=pathlib.Path, required=True)
    context_parser.add_argument("--output", type=pathlib.Path, required=True)
    validate_context_parser = subparsers.add_parser("validate-context")
    validate_context_parser.add_argument("context", type=pathlib.Path)
    assemble_parser = subparsers.add_parser("assemble")
    assemble_parser.add_argument("--context", type=pathlib.Path, required=True)
    assemble_parser.add_argument("--x64-manifest", type=pathlib.Path, required=True)
    assemble_parser.add_argument("--arm64-manifest", type=pathlib.Path, required=True)
    assemble_parser.add_argument("--sealed-manifest", type=pathlib.Path, required=True)
    assemble_parser.add_argument("--bundle-digest", required=True)
    assemble_parser.add_argument("--run-id", type=int, required=True)
    assemble_parser.add_argument("--run-attempt", type=int, required=True)
    assemble_parser.add_argument("--attempt-url", required=True)
    assemble_parser.add_argument("--output", type=pathlib.Path, required=True)
    validate_parser = subparsers.add_parser("validate")
    validate_parser.add_argument("inputs", type=pathlib.Path)
    args = parser.parse_args()
    if args.command == "create-context":
        create_context(args)
    elif args.command == "validate-context":
        validate_context(args.context)
    elif args.command == "assemble":
        assemble(args)
    else:
        validate_inputs(args.inputs)


if __name__ == "__main__":
    main()
