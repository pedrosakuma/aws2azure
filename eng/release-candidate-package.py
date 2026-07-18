#!/usr/bin/env python3
"""Create and validate deterministic release-candidate platform archives."""

from __future__ import annotations

import argparse
import gzip
import hashlib
import io
import json
import pathlib
import re
import shutil
import stat
import tarfile
from typing import Any, NoReturn


SCHEMA_VERSION = 1
CANDIDATE_RE = re.compile(
    r"v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)-rc\.([1-9][0-9]*)"
)
SHA_RE = re.compile(r"[0-9a-f]{40}")
DIGEST_RE = re.compile(r"sha256:[0-9a-f]{64}")
TARGETS = {
    "linux-x64": {"operating_system": "linux", "architecture": "x64", "rid": "linux-x64"},
    "linux-arm64": {
        "operating_system": "linux",
        "architecture": "arm64",
        "rid": "linux-arm64",
    },
}


def fail(message: str) -> NoReturn:
    raise SystemExit(f"release-candidate-package: {message}")


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


def file_identity(path: pathlib.Path) -> dict[str, Any]:
    regular_file(path, str(path))
    return {
        "path": path.name,
        "sha256": sha256_file(path),
        "size_bytes": path.stat().st_size,
    }


def require_candidate(value: Any) -> str:
    if not isinstance(value, str) or CANDIDATE_RE.fullmatch(value) is None:
        fail("candidate must be strict vMAJOR.MINOR.PATCH-rc.NUMBER SemVer")
    return value


def require_sha(value: Any, name: str) -> str:
    if not isinstance(value, str) or SHA_RE.fullmatch(value) is None:
        fail(f"{name} must be a lowercase 40-character SHA")
    return value


def require_digest(value: Any, name: str) -> str:
    if not isinstance(value, str) or DIGEST_RE.fullmatch(value) is None:
        fail(f"{name} must be a lowercase SHA-256 digest")
    return value


def require_source(repository: Any, sha: Any, ref: Any, candidate: str) -> None:
    if not isinstance(repository, str) or re.fullmatch(
        r"[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+", repository
    ) is None:
        fail("source repository is invalid")
    require_sha(sha, "source SHA")
    if ref != f"refs/tags/{candidate}":
        fail("source ref must be the exact candidate tag")


def tar_entry(name: str, data: bytes, mode: int) -> tuple[tarfile.TarInfo, bytes]:
    info = tarfile.TarInfo(name)
    info.uid = 0
    info.gid = 0
    info.uname = ""
    info.gname = ""
    info.mtime = 0
    info.mode = mode
    info.size = len(data)
    return info, data


def archive_members(path: pathlib.Path) -> list[dict[str, Any]]:
    try:
        with path.open("rb") as stream:
            header = stream.read(10)
        if len(header) != 10 or header[:3] != b"\x1f\x8b\x08":
            fail("archive is not gzip")
        if header[3] != 0 or int.from_bytes(header[4:8], "little") != 0:
            fail("gzip metadata is not deterministic")
        members: list[dict[str, Any]] = []
        names: list[str] = []
        with tarfile.open(path, "r:gz") as archive:
            if archive.pax_headers:
                fail("archive contains global PAX headers")
            for member in archive:
                if not member.isreg():
                    fail(f"archive contains non-regular member {member.name}")
                if (
                    member.uid != 0
                    or member.gid != 0
                    or member.uname
                    or member.gname
                    or member.mtime != 0
                    or member.pax_headers
                ):
                    fail(f"archive metadata is not deterministic for {member.name}")
                if pathlib.PurePosixPath(member.name).as_posix() != member.name:
                    fail(f"archive member path is not canonical: {member.name}")
                if any(part in ("", ".", "..") for part in pathlib.PurePosixPath(member.name).parts):
                    fail(f"archive member path is unsafe: {member.name}")
                expected_mode = 0o755 if member.name.endswith("/aws2azure") else 0o644
                if member.mode & 0o7777 != expected_mode:
                    fail(f"archive member mode is invalid: {member.name}")
                extracted = archive.extractfile(member)
                if extracted is None:
                    fail(f"cannot read archive member {member.name}")
                data = extracted.read()
                if len(data) != member.size:
                    fail(f"archive member ended early: {member.name}")
                names.append(member.name)
                members.append(
                    {
                        "path": member.name,
                        "sha256": f"sha256:{hashlib.sha256(data).hexdigest()}",
                        "size_bytes": member.size,
                        "executable": member.name.endswith("/aws2azure"),
                    }
                )
        if names != sorted(names) or len(names) != len(set(names)):
            fail("archive members must be unique and sorted")
        return members
    except (OSError, tarfile.TarError, gzip.BadGzipFile, EOFError) as error:
        fail(f"cannot inspect archive: {error}")


def package(args: argparse.Namespace) -> None:
    candidate = require_candidate(args.candidate)
    require_source(args.repository, args.source_sha, args.source_ref, candidate)
    if args.rid not in TARGETS:
        fail("RID must be linux-x64 or linux-arm64")
    executable = regular_file(args.executable.resolve(), "executable")
    license_path = regular_file(args.license.resolve(), "license")
    config_path = regular_file(args.config.resolve(), "example config")
    output = args.output.resolve()
    if output.exists():
        fail(f"output already exists: {output}")
    output.mkdir(parents=True)

    packaged_executable = output / "Aws2Azure.Proxy"
    shutil.copyfile(executable, packaged_executable)
    packaged_executable.chmod(0o755)
    executable_identity = file_identity(packaged_executable)
    root = f"aws2azure-{candidate}-{args.rid}"
    readme = (
        f"aws2azure {candidate} release candidate\n"
        f"Target: {args.rid}\n"
        f"Source: https://github.com/{args.repository}/tree/{args.source_sha}\n"
        "This immutable candidate is not a v1 release. Verify SHA256SUMS and provenance.\n"
    ).encode("utf-8")
    entries = [
        tar_entry(f"{root}/LICENSE", license_path.read_bytes(), 0o644),
        tar_entry(f"{root}/README.md", readme, 0o644),
        tar_entry(f"{root}/aws2azure", packaged_executable.read_bytes(), 0o755),
        tar_entry(f"{root}/config.example.json", config_path.read_bytes(), 0o644),
    ]
    entries.sort(key=lambda item: item[0].name)

    temporary_archive = output / "archive.tar.gz"
    with temporary_archive.open("xb") as raw:
        with gzip.GzipFile(
            filename="", mode="wb", fileobj=raw, compresslevel=9, mtime=0
        ) as compressed:
            with tarfile.open(
                fileobj=compressed, mode="w", format=tarfile.GNU_FORMAT
            ) as archive:
                for info, data in entries:
                    archive.addfile(info, io.BytesIO(data))
    archive_digest = sha256_file(temporary_archive)
    archive_name = (
        f"aws2azure-{candidate}-{args.rid}-"
        f"{archive_digest.removeprefix('sha256:')}.tar.gz"
    )
    archive_path = output / archive_name
    temporary_archive.rename(archive_path)
    members = archive_members(archive_path)

    checksums = (
        f"{archive_digest.removeprefix('sha256:')}  {archive_name}\n"
        f"{executable_identity['sha256'].removeprefix('sha256:')}  Aws2Azure.Proxy\n"
    ).encode("ascii")
    checksum_path = output / "SHA256SUMS"
    checksum_path.write_bytes(checksums)
    checksum_path.chmod(0o644)

    manifest: dict[str, Any] = {
        "schema_version": SCHEMA_VERSION,
        "artifact_kind": "release_candidate_platform",
        "candidate": {
            "identifier": candidate,
            "source": {
                "repository": args.repository,
                "sha": args.source_sha,
                "ref": args.source_ref,
            },
        },
        "target": TARGETS[args.rid],
        "executable": executable_identity,
        "archive": {
            "path": archive_name,
            "format": "tar.gz",
            "sha256": archive_digest,
            "size_bytes": archive_path.stat().st_size,
            "executable_member": f"{root}/aws2azure",
            "members": members,
        },
        "checksums": file_identity(checksum_path),
    }
    manifest["content_digest"] = content_digest(manifest)
    manifest_path = output / "platform-manifest.json"
    manifest_path.write_bytes(canonical_bytes(manifest))
    manifest_path.chmod(0o644)
    validate(manifest_path)
    print(manifest_path)


def require_keys(value: Any, name: str, keys: set[str]) -> dict[str, Any]:
    if not isinstance(value, dict) or set(value) != keys:
        fail(f"{name} fields are invalid")
    return value


def validate(manifest_path: pathlib.Path) -> None:
    regular_file(manifest_path, "platform manifest")
    root = manifest_path.resolve().parent
    manifest = require_keys(
        load_json(manifest_path),
        "manifest",
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
    if manifest["schema_version"] != 1 or manifest["artifact_kind"] != "release_candidate_platform":
        fail("manifest schema or artifact kind is invalid")
    candidate_object = require_keys(
        manifest["candidate"], "candidate", {"identifier", "source"}
    )
    candidate = require_candidate(candidate_object["identifier"])
    source = require_keys(
        candidate_object["source"], "candidate.source", {"repository", "sha", "ref"}
    )
    require_source(
        source["repository"], source["sha"], source["ref"], candidate
    )
    target = require_keys(
        manifest["target"], "target", {"operating_system", "architecture", "rid"}
    )
    rid = target["rid"]
    if rid not in TARGETS or target != TARGETS[rid]:
        fail("target is invalid")

    executable = require_keys(
        manifest["executable"], "executable", {"path", "sha256", "size_bytes"}
    )
    if executable["path"] != "Aws2Azure.Proxy":
        fail("executable path is invalid")
    require_digest(executable["sha256"], "executable digest")
    executable_path = regular_file(root / executable["path"], "packaged executable")
    if (
        executable_path.stat().st_size != executable["size_bytes"]
        or sha256_file(executable_path) != executable["sha256"]
    ):
        fail("packaged executable identity does not match")

    archive = require_keys(
        manifest["archive"],
        "archive",
        {
            "path",
            "format",
            "sha256",
            "size_bytes",
            "executable_member",
            "members",
        },
    )
    require_digest(archive["sha256"], "archive digest")
    if (
        archive["format"] != "tar.gz"
        or not isinstance(archive["path"], str)
        or pathlib.Path(archive["path"]).name != archive["path"]
        or archive["path"]
        != f"aws2azure-{candidate}-{rid}-{archive['sha256'].removeprefix('sha256:')}.tar.gz"
    ):
        fail("archive identity is invalid")
    archive_path = regular_file(root / archive["path"], "archive")
    if (
        archive_path.stat().st_size != archive["size_bytes"]
        or sha256_file(archive_path) != archive["sha256"]
        or archive_members(archive_path) != archive["members"]
    ):
        fail("archive identity does not match")
    executable_members = [
        member
        for member in archive["members"]
        if isinstance(member, dict) and member.get("executable") is True
    ]
    if (
        len(executable_members) != 1
        or executable_members[0].get("path") != archive["executable_member"]
        or executable_members[0].get("sha256") != executable["sha256"]
        or executable_members[0].get("size_bytes") != executable["size_bytes"]
    ):
        fail("archive does not contain the exact executable once")

    checksums = require_keys(
        manifest["checksums"], "checksums", {"path", "sha256", "size_bytes"}
    )
    if checksums["path"] != "SHA256SUMS":
        fail("checksum path is invalid")
    require_digest(checksums["sha256"], "checksum digest")
    checksum_path = regular_file(root / checksums["path"], "checksums")
    expected_checksums = (
        f"{archive['sha256'].removeprefix('sha256:')}  {archive['path']}\n"
        f"{executable['sha256'].removeprefix('sha256:')}  {executable['path']}\n"
    ).encode("ascii")
    if (
        checksum_path.read_bytes() != expected_checksums
        or checksum_path.stat().st_size != checksums["size_bytes"]
        or sha256_file(checksum_path) != checksums["sha256"]
    ):
        fail("checksum manifest does not match exact platform files")
    require_digest(manifest["content_digest"], "content digest")
    if manifest["content_digest"] != content_digest(manifest):
        fail("content digest does not match")
    if manifest_path.read_bytes() != canonical_bytes(manifest):
        fail("platform manifest is not canonical JSON")
    print(f"Validated {candidate} {rid} ({manifest['content_digest']}).")


def main() -> None:
    parser = argparse.ArgumentParser()
    subparsers = parser.add_subparsers(dest="command", required=True)
    package_parser = subparsers.add_parser("package")
    package_parser.add_argument("--candidate", required=True)
    package_parser.add_argument("--repository", required=True)
    package_parser.add_argument("--source-sha", required=True)
    package_parser.add_argument("--source-ref", required=True)
    package_parser.add_argument("--rid", required=True)
    package_parser.add_argument("--executable", type=pathlib.Path, required=True)
    package_parser.add_argument("--license", type=pathlib.Path, required=True)
    package_parser.add_argument("--config", type=pathlib.Path, required=True)
    package_parser.add_argument("--output", type=pathlib.Path, required=True)
    validate_parser = subparsers.add_parser("validate")
    validate_parser.add_argument("manifest", type=pathlib.Path)
    args = parser.parse_args()
    if args.command == "package":
        package(args)
    else:
        validate(args.manifest)


if __name__ == "__main__":
    main()
