#!/usr/bin/env python3
"""Extract GitHub artifact ZIPs and sealed-runtime TARs without trusting paths."""

from __future__ import annotations

import argparse
import pathlib
import shutil
import stat
import tarfile
import zipfile


MAX_ENTRIES = 4096
MAX_FILE_BYTES = 512 * 1024 * 1024
MAX_TOTAL_BYTES = 1024 * 1024 * 1024


def fail(message: str) -> None:
    raise SystemExit(f"safe-extract: {message}")


def normalized_parts(name: str) -> tuple[str, ...]:
    if not name or "\0" in name or "\\" in name:
        fail(f"unsafe archive entry name: {name!r}")
    path = pathlib.PurePosixPath(name)
    if path.is_absolute():
        fail(f"absolute archive entry: {name!r}")
    parts = tuple(part for part in path.parts if part not in ("", "."))
    if not parts or any(part == ".." for part in parts):
        fail(f"traversing archive entry: {name!r}")
    return parts


def prepare_destination(destination: pathlib.Path) -> pathlib.Path:
    destination.mkdir(parents=True, exist_ok=True, mode=0o700)
    destination.chmod(0o700)
    if any(destination.iterdir()):
        fail(f"destination must be empty: {destination}")
    return destination.resolve()


def target_path(destination: pathlib.Path, parts: tuple[str, ...]) -> pathlib.Path:
    target = destination.joinpath(*parts).resolve()
    if target != destination and destination not in target.parents:
        fail(f"archive entry escapes destination: {'/'.join(parts)!r}")
    return target


def check_limits(count: int, size: int, total: int) -> int:
    if count > MAX_ENTRIES:
        fail(f"archive contains more than {MAX_ENTRIES} entries")
    if size < 0 or size > MAX_FILE_BYTES:
        fail(f"archive entry exceeds {MAX_FILE_BYTES} bytes")
    total += size
    if total > MAX_TOTAL_BYTES:
        fail(f"archive expands beyond {MAX_TOTAL_BYTES} bytes")
    return total


def extract_zip(source: pathlib.Path, destination: pathlib.Path) -> None:
    seen: set[tuple[str, ...]] = set()
    total = 0
    with zipfile.ZipFile(source) as archive:
        for count, info in enumerate(archive.infolist(), start=1):
            parts = normalized_parts(info.filename)
            if parts in seen:
                fail(f"duplicate ZIP entry: {info.filename!r}")
            seen.add(parts)
            total = check_limits(count, info.file_size, total)
            if info.flag_bits & 0x1:
                fail(f"encrypted ZIP entry: {info.filename!r}")

            mode = (info.external_attr >> 16) & 0xFFFF
            if stat.S_ISLNK(mode):
                fail(f"symbolic link ZIP entry: {info.filename!r}")
            if mode and not (stat.S_ISREG(mode) or stat.S_ISDIR(mode)):
                fail(f"special ZIP entry: {info.filename!r}")

            target = target_path(destination, parts)
            if info.is_dir() or stat.S_ISDIR(mode):
                target.mkdir(parents=True, exist_ok=True, mode=0o700)
                if target.is_symlink() or not target.is_dir():
                    fail(f"ZIP directory collides with a non-directory: {info.filename!r}")
                target.chmod(0o700)
                continue

            target.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
            target.parent.chmod(0o700)
            with archive.open(info) as input_stream, target.open("xb") as output_stream:
                shutil.copyfileobj(input_stream, output_stream, length=1024 * 1024)
            target.chmod(0o600)


def extract_tar(source: pathlib.Path, destination: pathlib.Path) -> None:
    seen: set[tuple[str, ...]] = set()
    total = 0
    with tarfile.open(source, mode="r:*") as archive:
        for count, member in enumerate(archive, start=1):
            if member.isdir() and member.name in (".", "./"):
                continue
            parts = normalized_parts(member.name)
            if parts in seen:
                fail(f"duplicate TAR entry: {member.name!r}")
            seen.add(parts)
            total = check_limits(count, member.size, total)
            if member.issym() or member.islnk():
                fail(f"link TAR entry: {member.name!r}")
            if not (member.isdir() or member.isreg()):
                fail(f"special TAR entry: {member.name!r}")

            target = target_path(destination, parts)
            if member.isdir():
                target.mkdir(parents=True, exist_ok=True, mode=0o700)
                if target.is_symlink() or not target.is_dir():
                    fail(f"TAR directory collides with a non-directory: {member.name!r}")
                target.chmod(0o700)
                continue

            target.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
            target.parent.chmod(0o700)
            input_stream = archive.extractfile(member)
            if input_stream is None:
                fail(f"TAR entry has no readable bytes: {member.name!r}")
            with input_stream, target.open("xb") as output_stream:
                shutil.copyfileobj(input_stream, output_stream, length=1024 * 1024)
            target.chmod(0o700 if member.mode & 0o111 else 0o600)


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("format", choices=("zip", "tar"))
    parser.add_argument("source", type=pathlib.Path)
    parser.add_argument("destination", type=pathlib.Path)
    args = parser.parse_args()

    source = args.source.resolve()
    if not source.is_file() or source.is_symlink():
        fail(f"source must be a regular file: {source}")
    destination = prepare_destination(args.destination)
    if args.format == "zip":
        extract_zip(source, destination)
    else:
        extract_tar(source, destination)


if __name__ == "__main__":
    main()
