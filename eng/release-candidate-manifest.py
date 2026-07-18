#!/usr/bin/env python3
"""Generate and validate an immutable release-candidate identity manifest."""

from __future__ import annotations

import argparse
import datetime
import gzip
import hashlib
import json
import pathlib
import re
import stat
import tarfile
from typing import Any, NoReturn


SCHEMA_VERSION = 1
MAX_ARCHIVE_ENTRIES = 4096
MAX_ARCHIVE_FILE_BYTES = 512 * 1024 * 1024
MAX_ARCHIVE_TOTAL_BYTES = 1024 * 1024 * 1024
DIGEST_RE = re.compile(r"sha256:[0-9a-f]{64}")
SHA_RE = re.compile(r"[0-9a-f]{40}")
REPOSITORY_RE = re.compile(r"[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+")
IDENTIFIER_RE = re.compile(r"[A-Za-z0-9][A-Za-z0-9._:/+-]{0,255}")
CANDIDATE_RE = re.compile(
    r"v[0-9]+\.[0-9]+\.[0-9]+-rc(?:[.-]?[0-9A-Za-z]+)*"
)
RC_REF_RE = re.compile(
    r"refs/tags/v[0-9]+\.[0-9]+\.[0-9]+-rc(?:[.-]?[0-9A-Za-z]+)*"
)
PLATFORM_TARGETS = {
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
CONTAINER_PLATFORMS = ("linux/amd64", "linux/arm64")
OBSERVATION_VERDICTS = ("pass", "rollback")


def fail(message: str) -> NoReturn:
    raise SystemExit(f"release-candidate-manifest: {message}")


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
    if not isinstance(value, dict):
        fail(f"{name} must be an object")
    actual = set(value)
    if actual != keys:
        missing = sorted(keys - actual)
        unknown = sorted(actual - keys)
        details = []
        if missing:
            details.append(f"missing {missing}")
        if unknown:
            details.append(f"unknown {unknown}")
        fail(f"{name} fields are invalid: {', '.join(details)}")
    return value


def require_array(value: Any, name: str, *, nonempty: bool = True) -> list[Any]:
    if not isinstance(value, list) or (nonempty and not value):
        fail(f"{name} must be {'a non-empty' if nonempty else 'an'} array")
    return value


def require_string(value: Any, name: str) -> str:
    if not isinstance(value, str) or not value:
        fail(f"{name} must be a non-empty string")
    return value


def require_integer(value: Any, name: str, *, positive: bool = False) -> int:
    if isinstance(value, bool) or not isinstance(value, int):
        fail(f"{name} must be an integer")
    if positive and value <= 0:
        fail(f"{name} must be positive")
    if not positive and value < 0:
        fail(f"{name} must not be negative")
    return value


def require_digest(value: Any, name: str) -> str:
    text = require_string(value, name)
    if DIGEST_RE.fullmatch(text) is None:
        fail(f"{name} must be a lowercase sha256 digest")
    return text


def require_sha(value: Any, name: str) -> str:
    text = require_string(value, name)
    if SHA_RE.fullmatch(text) is None:
        fail(f"{name} must be a 40-character lowercase git SHA")
    return text


def require_identifier(value: Any, name: str) -> str:
    text = require_string(value, name)
    if IDENTIFIER_RE.fullmatch(text) is None:
        fail(f"{name} is not a safe identifier")
    return text


def require_repository(value: Any, name: str) -> str:
    text = require_string(value, name)
    if REPOSITORY_RE.fullmatch(text) is None:
        fail(f"{name} must be an owner/repository identity")
    return text


def require_ref(value: Any, name: str) -> str:
    text = require_string(value, name)
    if text != "refs/heads/main" and RC_REF_RE.fullmatch(text) is None:
        fail(f"{name} must identify protected main or a release-candidate tag")
    return text


def require_utc_timestamp(value: Any, name: str) -> datetime.datetime:
    text = require_string(value, name)
    if re.fullmatch(r"[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z", text) is None:
        fail(f"{name} must be a UTC RFC 3339 timestamp with second precision")
    try:
        return datetime.datetime.strptime(text, "%Y-%m-%dT%H:%M:%SZ").replace(
            tzinfo=datetime.timezone.utc
        )
    except ValueError as error:
        fail(f"{name} is not a valid timestamp: {error}")


def require_relative_path(value: Any, name: str) -> str:
    text = require_string(value, name)
    if "\0" in text or "\\" in text:
        fail(f"{name} is not a safe POSIX relative path")
    path = pathlib.PurePosixPath(text)
    if path.is_absolute() or text != path.as_posix():
        fail(f"{name} is not a normalized POSIX relative path")
    if not path.parts or any(part in ("", ".", "..") for part in path.parts):
        fail(f"{name} contains an unsafe path component")
    return text


def resolve_regular_file(root: pathlib.Path, relative: str, name: str) -> pathlib.Path:
    relative = require_relative_path(relative, name)
    current = root
    for part in pathlib.PurePosixPath(relative).parts:
        current = current / part
        try:
            mode = current.lstat().st_mode
        except OSError as error:
            fail(f"{name} cannot be inspected: {error}")
        if stat.S_ISLNK(mode):
            fail(f"{name} must not traverse a symbolic link: {relative}")
    try:
        mode = current.stat().st_mode
    except OSError as error:
        fail(f"{name} cannot be inspected: {error}")
    if not stat.S_ISREG(mode):
        fail(f"{name} must identify a regular file: {relative}")
    resolved_root = root.resolve()
    resolved = current.resolve()
    if resolved_root != resolved and resolved_root not in resolved.parents:
        fail(f"{name} escapes the manifest root: {relative}")
    return resolved


def sha256_file(path: pathlib.Path) -> str:
    digest = hashlib.sha256()
    try:
        with path.open("rb") as stream:
            while chunk := stream.read(1024 * 1024):
                digest.update(chunk)
    except OSError as error:
        fail(f"cannot hash {path}: {error}")
    return f"sha256:{digest.hexdigest()}"


def file_identity(root: pathlib.Path, relative: str, name: str) -> dict[str, Any]:
    path = resolve_regular_file(root, relative, name)
    return {
        "path": require_relative_path(relative, f"{name}.path"),
        "sha256": sha256_file(path),
        "size_bytes": path.stat().st_size,
    }


def validate_file_identity(
    value: Any, name: str, root: pathlib.Path
) -> dict[str, Any]:
    identity = require_object(value, name, {"path", "sha256", "size_bytes"})
    relative = require_relative_path(identity["path"], f"{name}.path")
    expected_digest = require_digest(identity["sha256"], f"{name}.sha256")
    expected_size = require_integer(identity["size_bytes"], f"{name}.size_bytes")
    path = resolve_regular_file(root, relative, f"{name}.path")
    if path.stat().st_size != expected_size:
        fail(f"{name} size does not match the referenced file")
    if sha256_file(path) != expected_digest:
        fail(f"{name} digest does not match the referenced file")
    return identity


def normalized_archive_name(name: str, is_directory: bool) -> str:
    if not name or "\0" in name or "\\" in name:
        fail(f"unsafe archive member name: {name!r}")
    raw = name[:-1] if is_directory and name.endswith("/") else name
    path = pathlib.PurePosixPath(raw)
    if path.is_absolute() or not path.parts:
        fail(f"unsafe archive member name: {name!r}")
    if any(part in ("", ".", "..") for part in path.parts):
        fail(f"traversing archive member name: {name!r}")
    normalized = path.as_posix()
    if raw != normalized:
        fail(f"non-canonical archive member name: {name!r}")
    return normalized


def validate_gzip_header(path: pathlib.Path) -> None:
    try:
        with path.open("rb") as stream:
            header = stream.read(10)
    except OSError as error:
        fail(f"cannot inspect gzip header {path}: {error}")
    if len(header) != 10 or header[0:3] != b"\x1f\x8b\x08":
        fail(f"archive is not gzip-compressed: {path}")
    if header[3] != 0:
        fail("gzip archive must not carry optional non-deterministic headers")
    if int.from_bytes(header[4:8], "little") != 0:
        fail("gzip archive timestamp must be zero")


def inspect_archive(
    root: pathlib.Path,
    relative: str,
    executable_member: str,
    expected_executable_digest: str,
    name: str,
) -> tuple[dict[str, Any], list[dict[str, Any]]]:
    path = resolve_regular_file(root, relative, f"{name}.path")
    validate_gzip_header(path)
    executable_member = require_relative_path(
        executable_member, f"{name}.executable_member"
    )
    members: list[dict[str, Any]] = []
    seen: set[str] = set()
    ordered_names: list[str] = []
    total = 0
    executable_count = 0
    try:
        with tarfile.open(path, mode="r:gz") as archive:
            if archive.pax_headers:
                fail(f"{name} must not contain global PAX headers")
            for count, member in enumerate(archive, start=1):
                if count > MAX_ARCHIVE_ENTRIES:
                    fail(f"{name} contains more than {MAX_ARCHIVE_ENTRIES} entries")
                normalized = normalized_archive_name(member.name, member.isdir())
                if normalized in seen:
                    fail(f"{name} contains duplicate member {normalized!r}")
                seen.add(normalized)
                ordered_names.append(normalized)
                if member.issym() or member.islnk():
                    fail(f"{name} contains link member {normalized!r}")
                if not (member.isdir() or member.isreg()):
                    fail(f"{name} contains special member {normalized!r}")
                if member.pax_headers:
                    fail(f"{name} contains PAX metadata for {normalized!r}")
                if member.uid != 0 or member.gid != 0:
                    fail(f"{name} member ownership must be numeric root: {normalized!r}")
                if member.uname or member.gname:
                    fail(f"{name} member owner names must be empty: {normalized!r}")
                if member.mtime != 0:
                    fail(f"{name} member timestamp must be zero: {normalized!r}")
                if member.isdir():
                    if member.mode & 0o7777 != 0o755:
                        fail(f"{name} directory mode must be 0755: {normalized!r}")
                    continue
                if member.size < 0 or member.size > MAX_ARCHIVE_FILE_BYTES:
                    fail(f"{name} member is too large: {normalized!r}")
                total += member.size
                if total > MAX_ARCHIVE_TOTAL_BYTES:
                    fail(f"{name} expands beyond {MAX_ARCHIVE_TOTAL_BYTES} bytes")
                mode = member.mode & 0o7777
                executable = normalized == executable_member
                if executable:
                    executable_count += 1
                    if mode != 0o755:
                        fail(f"{name} executable mode must be 0755")
                elif mode != 0o644:
                    fail(f"{name} non-executable mode must be 0644: {normalized!r}")
                stream = archive.extractfile(member)
                if stream is None:
                    fail(f"{name} member has no readable bytes: {normalized!r}")
                digest = hashlib.sha256()
                remaining = member.size
                with stream:
                    while remaining:
                        chunk = stream.read(min(1024 * 1024, remaining))
                        if not chunk:
                            fail(f"{name} member ended early: {normalized!r}")
                        remaining -= len(chunk)
                        digest.update(chunk)
                member_digest = f"sha256:{digest.hexdigest()}"
                if executable and member_digest != expected_executable_digest:
                    fail(f"{name} executable bytes do not match the platform executable")
                members.append(
                    {
                        "path": normalized,
                        "sha256": member_digest,
                        "size_bytes": member.size,
                        "executable": executable,
                    }
                )
    except (tarfile.TarError, OSError, EOFError, gzip.BadGzipFile) as error:
        fail(f"cannot inspect {name}: {error}")
    if ordered_names != sorted(ordered_names):
        fail(f"{name} members must be sorted by path")
    if executable_count != 1:
        fail(f"{name} must contain the exact executable member once")
    return file_identity(root, relative, name), members


def validate_archive_identity(
    value: Any,
    name: str,
    root: pathlib.Path,
    expected_executable_digest: str,
) -> dict[str, Any]:
    archive = require_object(
        value,
        name,
        {
            "path",
            "format",
            "sha256",
            "size_bytes",
            "executable_member",
            "members",
        },
    )
    if archive["format"] != "tar.gz":
        fail(f"{name}.format must be tar.gz")
    expected_members = require_array(archive["members"], f"{name}.members")
    validated_member_paths: list[str] = []
    for index, member_value in enumerate(expected_members):
        member_name = f"{name}.members[{index}]"
        member = require_object(
            member_value,
            member_name,
            {"path", "sha256", "size_bytes", "executable"},
        )
        validated_member_paths.append(
            require_relative_path(member["path"], f"{member_name}.path")
        )
        require_digest(member["sha256"], f"{member_name}.sha256")
        require_integer(member["size_bytes"], f"{member_name}.size_bytes")
        if not isinstance(member["executable"], bool):
            fail(f"{member_name}.executable must be boolean")
    if validated_member_paths != sorted(set(validated_member_paths)):
        fail(f"{name}.members paths must be unique and sorted")
    actual_identity, actual_members = inspect_archive(
        root,
        require_relative_path(archive["path"], f"{name}.path"),
        require_relative_path(
            archive["executable_member"], f"{name}.executable_member"
        ),
        expected_executable_digest,
        name,
    )
    archive_digest = require_digest(archive["sha256"], f"{name}.sha256")
    archive_size = require_integer(archive["size_bytes"], f"{name}.size_bytes")
    if archive_digest != actual_identity["sha256"]:
        fail(f"{name} digest does not match the archive")
    if archive_size != actual_identity["size_bytes"]:
        fail(f"{name} size does not match the archive")
    if archive["members"] != actual_members:
        fail(f"{name} member identities do not match the archive")
    return archive


def validate_source(value: Any, name: str) -> dict[str, Any]:
    source = require_object(value, name, {"repository", "sha", "ref"})
    require_repository(source["repository"], f"{name}.repository")
    require_sha(source["sha"], f"{name}.sha")
    require_ref(source["ref"], f"{name}.ref")
    return source


def validate_producer(
    value: Any,
    name: str,
    source: dict[str, Any],
    *,
    expected_event: str | None = None,
) -> dict[str, Any]:
    producer = require_object(
        value,
        name,
        {
            "workflow",
            "event_name",
            "run_id",
            "run_attempt",
            "attempt_url",
            "source_sha",
        },
    )
    workflow = require_relative_path(producer["workflow"], f"{name}.workflow")
    if not workflow.startswith(".github/workflows/") or not workflow.endswith(".yml"):
        fail(f"{name}.workflow must identify a repository workflow")
    event_name = require_string(producer["event_name"], f"{name}.event_name")
    if expected_event is not None and event_name != expected_event:
        fail(f"{name}.event_name must be {expected_event}")
    run_id = require_integer(producer["run_id"], f"{name}.run_id", positive=True)
    run_attempt = require_integer(
        producer["run_attempt"], f"{name}.run_attempt", positive=True
    )
    source_sha = require_sha(producer["source_sha"], f"{name}.source_sha")
    if source_sha != source["sha"]:
        fail(f"{name}.source_sha does not match the bound source SHA")
    expected_url = (
        f"https://github.com/{source['repository']}/actions/runs/"
        f"{run_id}/attempts/{run_attempt}"
    )
    if producer["attempt_url"] != expected_url:
        fail(f"{name}.attempt_url does not match its exact run identity")
    return producer


def validate_sealed_manifest(
    value: Any, candidate_source: dict[str, Any], name: str
) -> dict[str, Any]:
    manifest = require_object(
        value,
        name,
        {"schema_version", "source", "target", "runtime", "artifact", "producer"},
    )
    if require_integer(
        manifest["schema_version"], f"{name}.schema_version"
    ) != 1:
        fail(f"{name}.schema_version must be 1")
    source = require_object(
        manifest["source"], f"{name}.source", {"repository", "git_sha", "git_ref"}
    )
    require_repository(source["repository"], f"{name}.source.repository")
    require_sha(source["git_sha"], f"{name}.source.git_sha")
    require_ref(source["git_ref"], f"{name}.source.git_ref")
    if (
        source["repository"] != candidate_source["repository"]
        or source["git_sha"] != candidate_source["sha"]
    ):
        fail(f"{name} does not bind the candidate source repository and SHA")
    if manifest["target"] != PLATFORM_TARGETS["linux-x64"]:
        fail(f"{name}.target must be the exact linux-x64 target")
    runtime = require_object(
        manifest["runtime"],
        f"{name}.runtime",
        {"root", "executable", "files", "aggregate_digest"},
    )
    if runtime["root"] != "runtime":
        fail(f"{name}.runtime.root must be runtime")
    executable = require_object(
        runtime["executable"],
        f"{name}.runtime.executable",
        {"path", "name", "sha256", "size_bytes"},
    )
    if executable["path"] != "runtime/Aws2Azure.Proxy":
        fail(f"{name} has an unexpected executable path")
    if executable["name"] != "Aws2Azure.Proxy":
        fail(f"{name} has an unexpected executable name")
    executable_digest = require_digest(
        executable["sha256"], f"{name}.runtime.executable.sha256"
    )
    require_integer(
        executable["size_bytes"], f"{name}.runtime.executable.size_bytes"
    )
    files = require_array(runtime["files"], f"{name}.runtime.files")
    paths: list[str] = []
    executable_entries = 0
    hash_lines = bytearray()
    for index, file_value in enumerate(files):
        file_name = f"{name}.runtime.files[{index}]"
        runtime_file = require_object(
            file_value,
            file_name,
            {"path", "sha256", "size_bytes", "executable"},
        )
        path = require_relative_path(runtime_file["path"], f"{file_name}.path")
        if not path.startswith("runtime/") or len(pathlib.PurePosixPath(path).parts) != 2:
            fail(f"{file_name}.path must be a direct runtime child")
        digest = require_digest(runtime_file["sha256"], f"{file_name}.sha256")
        require_integer(runtime_file["size_bytes"], f"{file_name}.size_bytes")
        if not isinstance(runtime_file["executable"], bool):
            fail(f"{file_name}.executable must be boolean")
        if path == executable["path"]:
            executable_entries += 1
            if (
                runtime_file["executable"] is not True
                or digest != executable_digest
                or runtime_file["size_bytes"] != executable["size_bytes"]
            ):
                fail(f"{name} executable identity drifts from its runtime file entry")
        elif runtime_file["executable"] is not False:
            fail(f"{name} marks a non-proxy runtime file executable")
        paths.append(path)
        hash_lines.extend(
            f"{digest.removeprefix('sha256:')}  ./{path.removeprefix('runtime/')}\n".encode()
        )
    if paths != sorted(set(paths)):
        fail(f"{name}.runtime.files paths must be unique and sorted")
    if executable_entries != 1:
        fail(f"{name} must identify exactly one proxy executable")
    aggregate_digest = require_digest(
        runtime["aggregate_digest"], f"{name}.runtime.aggregate_digest"
    )
    if aggregate_digest != f"sha256:{hashlib.sha256(hash_lines).hexdigest()}":
        fail(f"{name}.runtime.aggregate_digest cannot be recomputed")
    artifact = require_object(
        manifest["artifact"],
        f"{name}.artifact",
        {"name", "archive_name", "format", "retention_days", "selection"},
    )
    retention_days = require_integer(
        artifact["retention_days"], f"{name}.artifact.retention_days"
    )
    if artifact["format"] != "tar" or retention_days != 90:
        fail(f"{name}.artifact has an unsupported format or retention")
    selection = require_object(
        artifact["selection"],
        f"{name}.artifact.selection",
        {"repository", "run_id", "run_attempt"},
    )
    require_repository(
        selection["repository"], f"{name}.artifact.selection.repository"
    )
    selection_run_id = require_integer(
        selection["run_id"], f"{name}.artifact.selection.run_id", positive=True
    )
    selection_run_attempt = require_integer(
        selection["run_attempt"],
        f"{name}.artifact.selection.run_attempt",
        positive=True,
    )
    producer = require_object(
        manifest["producer"],
        f"{name}.producer",
        {
            "event_name",
            "workflow_path",
            "workflow_ref",
            "workflow_url",
            "run_id",
            "run_attempt",
            "run_url",
            "attempt_url",
            "run_started_at",
            "produced_at",
        },
    )
    if (
        producer["event_name"] != "workflow_dispatch"
        or producer["workflow_path"] != ".github/workflows/sealed-runtime.yml"
    ):
        fail(f"{name}.producer is not the trusted sealed-runtime workflow")
    run_id = require_integer(
        producer["run_id"], f"{name}.producer.run_id", positive=True
    )
    run_attempt = require_integer(
        producer["run_attempt"], f"{name}.producer.run_attempt", positive=True
    )
    expected_run_url = (
        f"https://github.com/{source['repository']}/actions/runs/{run_id}"
    )
    if producer["run_url"] != expected_run_url:
        fail(f"{name}.producer.run_url drifts from its run identity")
    if producer["attempt_url"] != f"{expected_run_url}/attempts/{run_attempt}":
        fail(f"{name}.producer.attempt_url drifts from its attempt identity")
    expected_workflow_ref = (
        f"{source['repository']}/.github/workflows/sealed-runtime.yml@"
        f"{source['git_ref']}"
    )
    if producer["workflow_ref"] != expected_workflow_ref:
        fail(f"{name}.producer.workflow_ref does not bind the sealed source ref")
    expected_workflow_url = (
        f"https://github.com/{source['repository']}/actions/workflows/"
        "sealed-runtime.yml"
    )
    if producer["workflow_url"] != expected_workflow_url:
        fail(f"{name}.producer.workflow_url is inconsistent")
    run_started_at = require_utc_timestamp(
        producer["run_started_at"], f"{name}.producer.run_started_at"
    )
    produced_at = require_utc_timestamp(
        producer["produced_at"], f"{name}.producer.produced_at"
    )
    if run_started_at > produced_at:
        fail(f"{name}.producer timestamps are not chronological")
    if (
        selection["repository"] != source["repository"]
        or selection_run_id != run_id
        or selection_run_attempt != run_attempt
    ):
        fail(f"{name}.artifact.selection drifts from the producer identity")
    aggregate_hex = aggregate_digest.removeprefix("sha256:")
    expected_artifact = (
        f"aws2azure-sealed-linux-x64-{aggregate_hex}-run-{run_id}-"
        f"attempt-{run_attempt}"
    )
    if artifact["name"] != expected_artifact:
        fail(f"{name}.artifact.name does not bind runtime and producer identity")
    if artifact["archive_name"] != f"{expected_artifact}.tar":
        fail(f"{name}.artifact.archive_name is inconsistent")
    return manifest


def parse_sealed_runtime_input(
    value: Any,
    name: str,
    root: pathlib.Path,
    candidate_source: dict[str, Any],
    executable: dict[str, Any],
) -> dict[str, Any]:
    sealed_input = require_object(
        value, name, {"manifest_path", "artifact_id", "upload_digest"}
    )
    manifest_identity = file_identity(
        root,
        require_relative_path(sealed_input["manifest_path"], f"{name}.manifest_path"),
        f"{name}.manifest",
    )
    manifest = validate_sealed_manifest(
        load_json(resolve_regular_file(root, manifest_identity["path"], name)),
        candidate_source,
        f"{name}.manifest",
    )
    if manifest["runtime"]["executable"]["sha256"] != executable["sha256"]:
        fail(f"{name} executable does not equal the exact sealed linux-x64 bytes")
    artifact_id = require_integer(
        sealed_input["artifact_id"], f"{name}.artifact_id", positive=True
    )
    upload_digest = require_digest(
        sealed_input["upload_digest"], f"{name}.upload_digest"
    )
    return {
        "aggregate_digest": manifest["runtime"]["aggregate_digest"],
        "executable_digest": manifest["runtime"]["executable"]["sha256"],
        "manifest": manifest_identity,
        "source_ref": manifest["source"]["git_ref"],
        "producer": {
            "workflow": manifest["producer"]["workflow_path"],
            "event_name": manifest["producer"]["event_name"],
            "run_id": manifest["producer"]["run_id"],
            "run_attempt": manifest["producer"]["run_attempt"],
            "attempt_url": manifest["producer"]["attempt_url"],
        },
        "artifact": {
            "id": artifact_id,
            "name": manifest["artifact"]["name"],
            "upload_digest": upload_digest,
        },
    }


def validate_sealed_runtime_identity(
    value: Any,
    name: str,
    root: pathlib.Path,
    candidate_source: dict[str, Any],
    executable: dict[str, Any],
) -> dict[str, Any]:
    sealed = require_object(
        value,
        name,
        {
            "aggregate_digest",
            "executable_digest",
            "manifest",
            "source_ref",
            "producer",
            "artifact",
        },
    )
    require_digest(sealed["aggregate_digest"], f"{name}.aggregate_digest")
    require_digest(sealed["executable_digest"], f"{name}.executable_digest")
    require_ref(sealed["source_ref"], f"{name}.source_ref")
    manifest_identity = validate_file_identity(
        sealed["manifest"], f"{name}.manifest", root
    )
    manifest = validate_sealed_manifest(
        load_json(resolve_regular_file(root, manifest_identity["path"], name)),
        candidate_source,
        f"{name}.manifest_content",
    )
    if sealed["aggregate_digest"] != manifest["runtime"]["aggregate_digest"]:
        fail(f"{name}.aggregate_digest drifts from the sealed manifest")
    if (
        sealed["executable_digest"] != executable["sha256"]
        or sealed["executable_digest"] != manifest["runtime"]["executable"]["sha256"]
    ):
        fail(f"{name}.executable_digest drifts from exact sealed bytes")
    if sealed["source_ref"] != manifest["source"]["git_ref"]:
        fail(f"{name}.source_ref drifts from the sealed manifest")
    producer = require_object(
        sealed["producer"],
        f"{name}.producer",
        {"workflow", "event_name", "run_id", "run_attempt", "attempt_url"},
    )
    require_integer(producer["run_id"], f"{name}.producer.run_id", positive=True)
    require_integer(
        producer["run_attempt"], f"{name}.producer.run_attempt", positive=True
    )
    expected_producer = {
        "workflow": manifest["producer"]["workflow_path"],
        "event_name": manifest["producer"]["event_name"],
        "run_id": manifest["producer"]["run_id"],
        "run_attempt": manifest["producer"]["run_attempt"],
        "attempt_url": manifest["producer"]["attempt_url"],
    }
    if producer != expected_producer:
        fail(f"{name}.producer drifts from the sealed manifest")
    artifact = require_object(
        sealed["artifact"],
        f"{name}.artifact",
        {"id", "name", "upload_digest"},
    )
    require_integer(artifact["id"], f"{name}.artifact.id", positive=True)
    require_digest(artifact["upload_digest"], f"{name}.artifact.upload_digest")
    if artifact["name"] != manifest["artifact"]["name"]:
        fail(f"{name}.artifact.name drifts from the sealed manifest")
    return sealed


def build_provenance(
    value: Any,
    name: str,
    source: dict[str, Any],
    producer: dict[str, Any],
    executable: dict[str, Any],
    archive: dict[str, Any],
) -> dict[str, Any]:
    provenance = require_object(
        value,
        name,
        {"predicate_type", "bundle_digest", "producer_attempt_url", "source_sha"},
    )
    require_string(provenance["predicate_type"], f"{name}.predicate_type")
    require_digest(provenance["bundle_digest"], f"{name}.bundle_digest")
    require_string(
        provenance["producer_attempt_url"], f"{name}.producer_attempt_url"
    )
    if provenance["producer_attempt_url"] != producer["attempt_url"]:
        fail(f"{name}.producer_attempt_url drifts from the candidate producer")
    if require_sha(provenance["source_sha"], f"{name}.source_sha") != source["sha"]:
        fail(f"{name}.source_sha does not match the candidate source")
    return {
        **provenance,
        "subjects": [
            {"name": executable["path"], "digest": executable["sha256"]},
            {"name": archive["path"], "digest": archive["sha256"]},
        ],
    }


def validate_provenance(
    value: Any,
    name: str,
    source: dict[str, Any],
    producer: dict[str, Any],
    executable: dict[str, Any],
    archive: dict[str, Any],
) -> dict[str, Any]:
    provenance = require_object(
        value,
        name,
        {
            "predicate_type",
            "bundle_digest",
            "producer_attempt_url",
            "source_sha",
            "subjects",
        },
    )
    require_string(provenance["predicate_type"], f"{name}.predicate_type")
    require_digest(provenance["bundle_digest"], f"{name}.bundle_digest")
    require_string(
        provenance["producer_attempt_url"], f"{name}.producer_attempt_url"
    )
    if provenance["producer_attempt_url"] != producer["attempt_url"]:
        fail(f"{name}.producer_attempt_url drifts from the candidate producer")
    if require_sha(provenance["source_sha"], f"{name}.source_sha") != source["sha"]:
        fail(f"{name}.source_sha does not match the candidate source")
    subjects = require_array(provenance["subjects"], f"{name}.subjects")
    expected = [
        {"name": executable["path"], "digest": executable["sha256"]},
        {"name": archive["path"], "digest": archive["sha256"]},
    ]
    for index, subject_value in enumerate(subjects):
        subject = require_object(
            subject_value, f"{name}.subjects[{index}]", {"name", "digest"}
        )
        require_relative_path(subject["name"], f"{name}.subjects[{index}].name")
        require_digest(subject["digest"], f"{name}.subjects[{index}].digest")
    if subjects != expected:
        fail(f"{name}.subjects do not bind the exact executable and archive")
    return provenance


def validate_profile(value: Any, name: str) -> dict[str, Any]:
    profile = require_object(value, name, {"id", "version", "digest"})
    require_identifier(profile["id"], f"{name}.id")
    require_integer(profile["version"], f"{name}.version", positive=True)
    require_digest(profile["digest"], f"{name}.digest")
    return profile


def validate_approved_runtime(
    value: Any,
    name: str,
    source: dict[str, Any],
    sealed: dict[str, Any],
    profile: dict[str, Any],
) -> dict[str, Any]:
    approved = require_object(
        value,
        name,
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
    require_digest(
        approved["ledger_record_digest"], f"{name}.ledger_record_digest"
    )
    approved_profile = require_object(
        approved["profile"], f"{name}.profile", {"id", "version"}
    )
    require_identifier(approved_profile["id"], f"{name}.profile.id")
    require_integer(
        approved_profile["version"], f"{name}.profile.version", positive=True
    )
    if approved_profile != {"id": profile["id"], "version": profile["version"]}:
        fail(f"{name}.profile does not match the enclosing workload profile")
    if approved["status"] != "approved":
        fail(f"{name}.status must be approved")
    if approved["source_repository"] != source["repository"]:
        fail(f"{name}.source_repository drifts from the candidate source")
    if approved["source_sha"] != source["sha"]:
        fail(f"{name}.source_sha drifts from the candidate source")
    if approved["source_ref"] != sealed["source_ref"]:
        fail(f"{name}.source_ref drifts from the sealed runtime")
    if approved["aggregate_digest"] != sealed["aggregate_digest"]:
        fail(f"{name}.aggregate_digest drifts from the sealed runtime")
    if approved["executable_digest"] != sealed["executable_digest"]:
        fail(f"{name}.executable_digest drifts from the sealed runtime")
    if approved["manifest_digest"] != sealed["manifest"]["sha256"]:
        fail(f"{name}.manifest_digest drifts from the sealed runtime")
    producer = require_object(
        approved["producer"],
        f"{name}.producer",
        {"workflow", "run_id", "run_attempt", "attempt_url"},
    )
    require_integer(producer["run_id"], f"{name}.producer.run_id", positive=True)
    require_integer(
        producer["run_attempt"], f"{name}.producer.run_attempt", positive=True
    )
    expected_producer = {
        "workflow": sealed["producer"]["workflow"],
        "run_id": sealed["producer"]["run_id"],
        "run_attempt": sealed["producer"]["run_attempt"],
        "attempt_url": sealed["producer"]["attempt_url"],
    }
    if producer != expected_producer:
        fail(f"{name}.producer drifts from the sealed runtime")
    artifact = require_object(
        approved["artifact"],
        f"{name}.artifact",
        {"id", "name", "upload_digest"},
    )
    require_integer(artifact["id"], f"{name}.artifact.id", positive=True)
    require_string(artifact["name"], f"{name}.artifact.name")
    require_digest(artifact["upload_digest"], f"{name}.artifact.upload_digest")
    if artifact != sealed["artifact"]:
        fail(f"{name}.artifact drifts from the sealed runtime")
    return approved


def validate_policy(value: Any, name: str) -> dict[str, Any]:
    policy = require_object(value, name, {"identifier", "digest"})
    require_identifier(policy["identifier"], f"{name}.identifier")
    require_digest(policy["digest"], f"{name}.digest")
    return policy


def validate_observation(
    value: Any,
    name: str,
    expected_manifest_digest: str | None = None,
) -> dict[str, Any]:
    keys = {"profile", "identifier", "digest", "verdict"}
    if expected_manifest_digest is not None:
        keys.add("release_candidate_manifest_digest")
    observation = require_object(
        value, name, keys
    )
    profile = require_object(
        observation["profile"], f"{name}.profile", {"id", "version"}
    )
    require_identifier(profile["id"], f"{name}.profile.id")
    require_integer(profile["version"], f"{name}.profile.version", positive=True)
    require_identifier(observation["identifier"], f"{name}.identifier")
    require_digest(observation["digest"], f"{name}.digest")
    if observation["verdict"] not in OBSERVATION_VERDICTS:
        fail(
            f"{name}.verdict must be one of {', '.join(OBSERVATION_VERDICTS)}"
        )
    if expected_manifest_digest is not None:
        if (
            require_digest(
                observation["release_candidate_manifest_digest"],
                f"{name}.release_candidate_manifest_digest",
            )
            != expected_manifest_digest
        ):
            fail(f"{name} does not bind the release-candidate manifest identity")
    return observation


def validate_container(
    value: Any, name: str, source: dict[str, Any]
) -> dict[str, Any]:
    container = require_object(
        value, name, {"repository", "index_digest", "platforms"}
    )
    repository = require_string(container["repository"], f"{name}.repository")
    if re.fullmatch(r"ghcr\.io/[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+", repository) is None:
        fail(f"{name}.repository must be an exact GHCR repository")
    if repository != f"ghcr.io/{source['repository']}":
        fail(f"{name}.repository does not match the candidate source repository")
    require_digest(container["index_digest"], f"{name}.index_digest")
    platforms = require_array(container["platforms"], f"{name}.platforms")
    identities: list[str] = []
    for index, platform_value in enumerate(platforms):
        platform = require_object(
            platform_value,
            f"{name}.platforms[{index}]",
            {"platform", "digest"},
        )
        platform_name = require_string(
            platform["platform"], f"{name}.platforms[{index}].platform"
        )
        if platform_name not in CONTAINER_PLATFORMS:
            fail(f"{name} contains an unsupported platform: {platform_name}")
        require_digest(platform["digest"], f"{name}.platforms[{index}].digest")
        identities.append(platform_name)
    if identities != list(CONTAINER_PLATFORMS):
        fail(f"{name}.platforms must contain exact amd64 and arm64 identities")
    digests = [platform["digest"] for platform in platforms]
    if len(digests) != len(set(digests)):
        fail(f"{name}.platforms must have distinct manifest digests")
    return container


def canonical_body_digest(manifest: dict[str, Any]) -> str:
    body = {key: value for key, value in manifest.items() if key != "content_digest"}
    encoded = json.dumps(
        body, sort_keys=True, separators=(",", ":"), ensure_ascii=False
    ).encode("utf-8")
    return f"sha256:{hashlib.sha256(encoded).hexdigest()}"


def canonical_identity_digest(manifest: dict[str, Any]) -> str:
    body = {
        key: value
        for key, value in manifest.items()
        if key not in {"content_digest", "identity_digest", "observation_evidence"}
    }
    encoded = json.dumps(
        body, sort_keys=True, separators=(",", ":"), ensure_ascii=False
    ).encode("utf-8")
    return f"sha256:{hashlib.sha256(encoded).hexdigest()}"


def rendered_manifest(manifest: dict[str, Any]) -> bytes:
    return (
        json.dumps(manifest, sort_keys=True, indent=2, ensure_ascii=False) + "\n"
    ).encode("utf-8")


def generate(
    descriptor_path: pathlib.Path,
    output_path: pathlib.Path,
    identity_only: bool = False,
) -> None:
    try:
        descriptor_mode = descriptor_path.lstat().st_mode
    except OSError as error:
        fail(f"cannot inspect descriptor: {error}")
    if stat.S_ISLNK(descriptor_mode) or not stat.S_ISREG(descriptor_mode):
        fail("descriptor must be a regular non-symbolic-link file")
    descriptor_path = descriptor_path.resolve()
    root = descriptor_path.parent
    expected_descriptor_fields = {
        "schema_version",
        "candidate",
        "producer",
        "platforms",
        "container",
        "workloads",
        "compatibility_policy",
    }
    if not identity_only:
        expected_descriptor_fields.add("observation_evidence")
    descriptor = require_object(
        load_json(descriptor_path),
        "descriptor",
        expected_descriptor_fields,
    )
    if require_integer(
        descriptor["schema_version"], "descriptor.schema_version"
    ) != SCHEMA_VERSION:
        fail(f"descriptor.schema_version must be {SCHEMA_VERSION}")
    candidate = require_object(
        descriptor["candidate"], "candidate", {"identifier", "source"}
    )
    candidate_identifier = require_identifier(
        candidate["identifier"], "candidate.identifier"
    )
    if CANDIDATE_RE.fullmatch(candidate_identifier) is None:
        fail("candidate.identifier must be a semantic-version release candidate")
    source = validate_source(candidate["source"], "candidate.source")
    if source["ref"] != f"refs/tags/{candidate_identifier}":
        fail("candidate.source.ref must identify the exact candidate tag")
    producer = validate_producer(
        descriptor["producer"],
        "producer",
        source,
        expected_event="workflow_dispatch",
    )

    platform_inputs = require_array(descriptor["platforms"], "platforms")
    platforms: list[dict[str, Any]] = []
    rids: set[str] = set()
    executable_paths: set[str] = set()
    archive_paths: set[str] = set()
    sealed_runtime: dict[str, Any] | None = None
    for index, platform_value in enumerate(platform_inputs):
        platform_name = f"platforms[{index}]"
        platform = require_object(
            platform_value,
            platform_name,
            {
                "target",
                "executable_path",
                "archive",
                "provenance",
                "sealed_runtime",
            },
        )
        target = require_object(
            platform["target"],
            f"{platform_name}.target",
            {"operating_system", "architecture", "rid"},
        )
        rid = require_string(target["rid"], f"{platform_name}.target.rid")
        if rid not in PLATFORM_TARGETS or target != PLATFORM_TARGETS[rid]:
            fail(f"{platform_name}.target is not an approved exact Linux target")
        if rid in rids:
            fail(f"duplicate platform RID: {rid}")
        rids.add(rid)
        executable = file_identity(
            root,
            require_relative_path(
                platform["executable_path"], f"{platform_name}.executable_path"
            ),
            f"{platform_name}.executable",
        )
        if executable["path"] in executable_paths:
            fail(f"duplicate platform executable path: {executable['path']}")
        executable_paths.add(executable["path"])
        archive_input = require_object(
            platform["archive"],
            f"{platform_name}.archive",
            {"path", "executable_member"},
        )
        archive_identity, members = inspect_archive(
            root,
            require_relative_path(
                archive_input["path"], f"{platform_name}.archive.path"
            ),
            require_relative_path(
                archive_input["executable_member"],
                f"{platform_name}.archive.executable_member",
            ),
            executable["sha256"],
            f"{platform_name}.archive",
        )
        archive = {
            **archive_identity,
            "format": "tar.gz",
            "executable_member": archive_input["executable_member"],
            "members": members,
        }
        if archive["path"] in archive_paths:
            fail(f"duplicate platform archive path: {archive['path']}")
        archive_paths.add(archive["path"])
        generated_platform: dict[str, Any] = {
            "target": target,
            "executable": executable,
            "archive": archive,
            "provenance": build_provenance(
                platform["provenance"],
                f"{platform_name}.provenance",
                source,
                producer,
                executable,
                archive,
            ),
        }
        if rid == "linux-x64":
            if platform["sealed_runtime"] is None:
                fail("linux-x64 must carry its exact approved sealed runtime identity")
            sealed_runtime = parse_sealed_runtime_input(
                platform["sealed_runtime"],
                f"{platform_name}.sealed_runtime",
                root,
                source,
                executable,
            )
            generated_platform["sealed_runtime"] = sealed_runtime
        elif platform["sealed_runtime"] is not None:
            fail("linux-arm64 must not claim a linux-x64 sealed runtime identity")
        else:
            generated_platform["sealed_runtime"] = None
        platforms.append(generated_platform)
    if rids != set(PLATFORM_TARGETS):
        fail("platforms must contain exactly linux-x64 and linux-arm64")
    platforms.sort(key=lambda item: item["target"]["rid"])
    if sealed_runtime is None:
        fail("linux-x64 sealed runtime identity is missing")

    container = validate_container(descriptor["container"], "container", source)
    workloads_input = require_array(descriptor["workloads"], "workloads")
    workloads: list[dict[str, Any]] = []
    workload_keys: set[tuple[str, int]] = set()
    profile_digests: set[str] = set()
    ledger_digests: set[str] = set()
    for index, workload_value in enumerate(workloads_input):
        workload_name = f"workloads[{index}]"
        workload = require_object(
            workload_value, workload_name, {"profile", "approved_runtime"}
        )
        profile = validate_profile(workload["profile"], f"{workload_name}.profile")
        key = (profile["id"], profile["version"])
        if key in workload_keys:
            fail(f"duplicate workload profile identity: {key[0]} v{key[1]}")
        workload_keys.add(key)
        if profile["digest"] in profile_digests:
            fail(f"duplicate workload profile digest: {profile['digest']}")
        profile_digests.add(profile["digest"])
        approved = validate_approved_runtime(
            workload["approved_runtime"],
            f"{workload_name}.approved_runtime",
            source,
            sealed_runtime,
            profile,
        )
        if approved["ledger_record_digest"] in ledger_digests:
            fail(
                "duplicate approved-runtime ledger record digest: "
                f"{approved['ledger_record_digest']}"
            )
        ledger_digests.add(approved["ledger_record_digest"])
        workloads.append({"profile": profile, "approved_runtime": approved})
    workloads.sort(key=lambda item: (item["profile"]["id"], item["profile"]["version"]))

    policy = validate_policy(descriptor["compatibility_policy"], "compatibility_policy")
    observations: list[dict[str, Any]] = []
    if not identity_only:
        observations_input = require_array(
            descriptor["observation_evidence"], "observation_evidence"
        )
        observation_keys: set[tuple[str, int]] = set()
        observation_identifiers: set[str] = set()
        observation_digests: set[str] = set()
        for index, observation_value in enumerate(observations_input):
            observation = validate_observation(
                observation_value, f"observation_evidence[{index}]"
            )
            key = (
                observation["profile"]["id"],
                observation["profile"]["version"],
            )
            if key in observation_keys:
                fail(f"duplicate observation profile identity: {key[0]} v{key[1]}")
            observation_keys.add(key)
            if observation["identifier"] in observation_identifiers:
                fail(f"duplicate observation identifier: {observation['identifier']}")
            if observation["digest"] in observation_digests:
                fail(f"duplicate observation digest: {observation['digest']}")
            observation_identifiers.add(observation["identifier"])
            observation_digests.add(observation["digest"])
            observations.append(observation)
        if observation_keys != workload_keys:
            fail("observation evidence must cover every supported workload exactly once")
        observations.sort(
            key=lambda item: (item["profile"]["id"], item["profile"]["version"])
        )

    manifest: dict[str, Any] = {
        "schema_version": SCHEMA_VERSION,
        "candidate": candidate,
        "producer": producer,
        "platforms": platforms,
        "container": container,
        "workloads": workloads,
        "compatibility_policy": policy,
    }
    manifest["identity_digest"] = canonical_identity_digest(manifest)
    if identity_only:
        receipt = {
            "schema_version": SCHEMA_VERSION,
            "artifact_kind": "release_candidate_identity",
            **manifest,
        }
        receipt["content_digest"] = canonical_body_digest(receipt)
        output_path = output_path.resolve()
        if output_path.exists():
            fail(f"output already exists: {output_path}")
        output_path.parent.mkdir(parents=True, exist_ok=True)
        try:
            with output_path.open("xb") as stream:
                stream.write(rendered_manifest(receipt))
        except OSError as error:
            fail(f"cannot write identity receipt {output_path}: {error}")
        validate_identity(output_path)
        print(output_path)
        return

    manifest["observation_evidence"] = [
        {
            **observation,
            "release_candidate_manifest_digest": manifest["identity_digest"],
        }
        for observation in observations
    ]
    manifest["content_digest"] = canonical_body_digest(manifest)
    output_path = output_path.resolve()
    if output_path.exists():
        fail(f"output already exists: {output_path}")
    output_path.parent.mkdir(parents=True, exist_ok=True)
    try:
        with output_path.open("xb") as stream:
            stream.write(rendered_manifest(manifest))
    except OSError as error:
        fail(f"cannot write manifest {output_path}: {error}")
    validate(output_path, source["sha"], manifest["content_digest"])
    print(output_path)


def validate_identity(receipt_path: pathlib.Path) -> None:
    try:
        mode = receipt_path.lstat().st_mode
    except OSError as error:
        fail(f"cannot inspect identity receipt: {error}")
    if stat.S_ISLNK(mode) or not stat.S_ISREG(mode):
        fail("identity receipt must be a regular non-symbolic-link file")
    receipt_path = receipt_path.resolve()
    receipt = require_object(
        load_json(receipt_path),
        "identity receipt",
        {
            "schema_version",
            "artifact_kind",
            "candidate",
            "producer",
            "platforms",
            "container",
            "workloads",
            "compatibility_policy",
            "identity_digest",
            "content_digest",
        },
    )
    if (
        require_integer(receipt["schema_version"], "identity receipt schema")
        != SCHEMA_VERSION
        or receipt["artifact_kind"] != "release_candidate_identity"
    ):
        fail("identity receipt schema or artifact kind is invalid")
    identity_body = {
        key: value
        for key, value in receipt.items()
        if key not in {"artifact_kind", "content_digest", "identity_digest"}
    }
    if require_digest(
        receipt["identity_digest"], "identity receipt identity digest"
    ) != canonical_identity_digest(identity_body):
        fail("identity receipt digest does not match its canonical RC interfaces")
    if require_digest(
        receipt["content_digest"], "identity receipt content digest"
    ) != canonical_body_digest(receipt):
        fail("identity receipt content digest is invalid")
    try:
        actual_bytes = receipt_path.read_bytes()
    except OSError as error:
        fail(f"cannot read identity receipt bytes: {error}")
    if actual_bytes != rendered_manifest(receipt):
        fail("identity receipt JSON is not in canonical deterministic form")


def validate(
    manifest_path: pathlib.Path,
    expected_source_sha: str,
    expected_content_digest: str,
) -> None:
    expected_source_sha = require_sha(expected_source_sha, "expected_source_sha")
    expected_content_digest = require_digest(
        expected_content_digest, "expected_content_digest"
    )
    try:
        mode = manifest_path.lstat().st_mode
    except OSError as error:
        fail(f"cannot inspect manifest: {error}")
    if stat.S_ISLNK(mode) or not stat.S_ISREG(mode):
        fail("manifest must be a regular non-symbolic-link file")
    manifest_path = manifest_path.resolve()
    root = manifest_path.parent
    manifest = require_object(
        load_json(manifest_path),
        "manifest",
        {
            "schema_version",
            "candidate",
            "producer",
            "platforms",
            "container",
            "workloads",
            "compatibility_policy",
            "observation_evidence",
            "identity_digest",
            "content_digest",
        },
    )
    if require_integer(
        manifest["schema_version"], "manifest.schema_version"
    ) != SCHEMA_VERSION:
        fail(f"manifest.schema_version must be {SCHEMA_VERSION}")
    candidate = require_object(
        manifest["candidate"], "candidate", {"identifier", "source"}
    )
    candidate_identifier = require_identifier(
        candidate["identifier"], "candidate.identifier"
    )
    if CANDIDATE_RE.fullmatch(candidate_identifier) is None:
        fail("candidate.identifier must be a semantic-version release candidate")
    source = validate_source(candidate["source"], "candidate.source")
    if source["sha"] != expected_source_sha:
        fail("candidate source SHA does not match the trusted expected source SHA")
    if source["ref"] != f"refs/tags/{candidate_identifier}":
        fail("candidate.source.ref must identify the exact candidate tag")
    producer = validate_producer(
        manifest["producer"],
        "producer",
        source,
        expected_event="workflow_dispatch",
    )

    platforms = require_array(manifest["platforms"], "platforms")
    if len(platforms) != 2:
        fail("platforms must contain exactly linux-arm64 and linux-x64")
    rids: list[str] = []
    executable_paths: list[str] = []
    archive_paths: list[str] = []
    sealed_runtime: dict[str, Any] | None = None
    for index, platform_value in enumerate(platforms):
        platform_name = f"platforms[{index}]"
        platform = require_object(
            platform_value,
            platform_name,
            {"target", "executable", "archive", "provenance", "sealed_runtime"},
        )
        target = require_object(
            platform["target"],
            f"{platform_name}.target",
            {"operating_system", "architecture", "rid"},
        )
        rid = require_string(target["rid"], f"{platform_name}.target.rid")
        if rid not in PLATFORM_TARGETS or target != PLATFORM_TARGETS[rid]:
            fail(f"{platform_name}.target is not an approved exact Linux target")
        rids.append(rid)
        executable = validate_file_identity(
            platform["executable"], f"{platform_name}.executable", root
        )
        executable_paths.append(executable["path"])
        archive = validate_archive_identity(
            platform["archive"],
            f"{platform_name}.archive",
            root,
            executable["sha256"],
        )
        archive_paths.append(archive["path"])
        validate_provenance(
            platform["provenance"],
            f"{platform_name}.provenance",
            source,
            producer,
            executable,
            archive,
        )
        if rid == "linux-x64":
            if platform["sealed_runtime"] is None:
                fail("linux-x64 sealed runtime identity is missing")
            sealed_runtime = validate_sealed_runtime_identity(
                platform["sealed_runtime"],
                f"{platform_name}.sealed_runtime",
                root,
                source,
                executable,
            )
        elif platform["sealed_runtime"] is not None:
            fail("linux-arm64 must not carry a linux-x64 sealed runtime identity")
    if rids != sorted(PLATFORM_TARGETS):
        fail("platforms must be unique and sorted by exact RID")
    if len(executable_paths) != len(set(executable_paths)):
        fail("platform executable paths must be distinct")
    if len(archive_paths) != len(set(archive_paths)):
        fail("platform archive paths must be distinct")
    if sealed_runtime is None:
        fail("linux-x64 sealed runtime identity is missing")

    validate_container(manifest["container"], "container", source)
    workloads = require_array(manifest["workloads"], "workloads")
    workload_keys: list[tuple[str, int]] = []
    profile_digests: list[str] = []
    ledger_digests: list[str] = []
    for index, workload_value in enumerate(workloads):
        workload_name = f"workloads[{index}]"
        workload = require_object(
            workload_value, workload_name, {"profile", "approved_runtime"}
        )
        profile = validate_profile(workload["profile"], f"{workload_name}.profile")
        key = (profile["id"], profile["version"])
        workload_keys.append(key)
        profile_digests.append(profile["digest"])
        approved = validate_approved_runtime(
            workload["approved_runtime"],
            f"{workload_name}.approved_runtime",
            source,
            sealed_runtime,
            profile,
        )
        ledger_digests.append(approved["ledger_record_digest"])
    if workload_keys != sorted(set(workload_keys)):
        fail("workloads must be unique and sorted by profile identity")
    if len(profile_digests) != len(set(profile_digests)):
        fail("workload profile digests must be distinct")
    if len(ledger_digests) != len(set(ledger_digests)):
        fail("approved-runtime ledger record digests must be distinct")

    validate_policy(manifest["compatibility_policy"], "compatibility_policy")
    manifest_identity_digest = require_digest(
        manifest["identity_digest"], "identity_digest"
    )
    if manifest_identity_digest != canonical_identity_digest(manifest):
        fail("identity_digest does not match the canonical pre-observation manifest")
    observations = require_array(
        manifest["observation_evidence"], "observation_evidence"
    )
    observation_keys: list[tuple[str, int]] = []
    observation_identifiers: list[str] = []
    observation_digests: list[str] = []
    for index, observation_value in enumerate(observations):
        observation = validate_observation(
            observation_value,
            f"observation_evidence[{index}]",
            manifest_identity_digest,
        )
        observation_keys.append(
            (observation["profile"]["id"], observation["profile"]["version"])
        )
        observation_identifiers.append(observation["identifier"])
        observation_digests.append(observation["digest"])
    if observation_keys != workload_keys:
        fail(
            "observation evidence must be unique, sorted, and cover supported workloads"
        )
    if len(observation_identifiers) != len(set(observation_identifiers)):
        fail("observation identifiers must be distinct")
    if len(observation_digests) != len(set(observation_digests)):
        fail("observation digests must be distinct")

    manifest_content_digest = require_digest(
        manifest["content_digest"], "content_digest"
    )
    if manifest_content_digest != expected_content_digest:
        fail("content_digest does not match the trusted expected manifest identity")
    if manifest_content_digest != canonical_body_digest(manifest):
        fail("content_digest does not match the canonical manifest body")
    try:
        actual_bytes = manifest_path.read_bytes()
    except OSError as error:
        fail(f"cannot read manifest bytes: {error}")
    if actual_bytes != rendered_manifest(manifest):
        fail("manifest JSON is not in canonical deterministic form")
    print(
        f"Validated release candidate {candidate['identifier']} "
        f"({manifest['content_digest']})."
    )


def main() -> None:
    parser = argparse.ArgumentParser()
    subparsers = parser.add_subparsers(dest="command", required=True)
    generate_parser = subparsers.add_parser("generate")
    generate_parser.add_argument("descriptor", type=pathlib.Path)
    generate_parser.add_argument("output", type=pathlib.Path)
    identity_parser = subparsers.add_parser("identity")
    identity_parser.add_argument("descriptor", type=pathlib.Path)
    identity_parser.add_argument("output", type=pathlib.Path)
    validate_parser = subparsers.add_parser("validate")
    validate_parser.add_argument("manifest", type=pathlib.Path)
    validate_parser.add_argument("--expected-source-sha", required=True)
    validate_parser.add_argument("--expected-content-digest", required=True)
    validate_identity_parser = subparsers.add_parser("validate-identity")
    validate_identity_parser.add_argument("receipt", type=pathlib.Path)
    args = parser.parse_args()
    if args.command == "generate":
        generate(args.descriptor, args.output)
    elif args.command == "identity":
        generate(args.descriptor, args.output, identity_only=True)
    elif args.command == "validate":
        validate(
            args.manifest,
            args.expected_source_sha,
            args.expected_content_digest,
        )
    else:
        validate_identity(args.receipt)


if __name__ == "__main__":
    main()
