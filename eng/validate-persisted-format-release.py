#!/usr/bin/env python3
"""Validate release notes against the inventory in the candidate checkout."""

from __future__ import annotations

import argparse
import hashlib
import json
import pathlib
import re
from typing import Any, NoReturn


INVENTORY = pathlib.PurePosixPath(
    "docs/compatibility/dynamodb-persisted-formats-v1.json"
)
DIGEST_RE = re.compile(r"[0-9a-f]{64}")


def fail(message: str) -> NoReturn:
    raise SystemExit(f"persisted-format-release: {message}")


def normalized_relative(value: str, name: str) -> pathlib.PurePosixPath:
    parsed = pathlib.PurePosixPath(value)
    if (
        parsed.is_absolute()
        or value != parsed.as_posix()
        or any(part in ("", ".", "..") for part in parsed.parts)
    ):
        fail(f"{name} must be a normalized candidate-relative path")
    return parsed


def load_json(path: pathlib.Path) -> dict[str, Any]:
    try:
        value = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, UnicodeError, json.JSONDecodeError) as error:
        fail(f"cannot read inventory {path}: {error}")
    if not isinstance(value, dict):
        fail("inventory must be a JSON object")
    return value


def validate_fixture_hashes(candidate_root: pathlib.Path, inventory: dict[str, Any]) -> None:
    formats = inventory.get("formats")
    if not isinstance(formats, list) or not formats:
        fail("inventory formats must be a non-empty array")
    for index, value in enumerate(formats):
        if not isinstance(value, dict):
            fail(f"formats[{index}] must be an object")
        for key in ("v1_fixture", "current_fixture"):
            if key not in value:
                continue
            relative = normalized_relative(str(value[key]), f"formats[{index}].{key}")
            expected = value.get(f"{key}_sha256")
            if not isinstance(expected, str) or DIGEST_RE.fullmatch(expected) is None:
                fail(f"formats[{index}].{key}_sha256 must be a lowercase sha256")
            fixture_path = candidate_root / relative
            try:
                actual = hashlib.sha256(fixture_path.read_bytes()).hexdigest()
            except OSError as error:
                fail(f"cannot read frozen fixture {relative}: {error}")
            if actual != expected:
                fail(f"frozen fixture digest mismatch: {relative}")


def require_declaration(notes: str, label: str) -> str:
    match = re.search(rf"^- {re.escape(label)}: (.+)$", notes, flags=re.MULTILINE)
    if match is None:
        fail(f"release notes must declare {label}")
    value = match.group(1).strip()
    if not value or value in {"TBD", "TODO"} or " / " in value:
        fail(f"release notes contain a placeholder for {label}")
    return value


def validate(candidate_root: pathlib.Path, release_notes: str) -> None:
    notes_relative = normalized_relative(release_notes, "release_notes")
    inventory_path = candidate_root / INVENTORY
    notes_path = candidate_root / notes_relative
    inventory = load_json(inventory_path)
    validate_fixture_hashes(candidate_root, inventory)

    try:
        inventory_bytes = inventory_path.read_bytes()
        notes = notes_path.read_text(encoding="utf-8")
    except (OSError, UnicodeError) as error:
        fail(f"cannot read candidate release inputs: {error}")

    inventory_version = inventory.get("inventory_version")
    if not isinstance(inventory_version, int) or inventory_version <= 0:
        fail("inventory_version must be a positive integer")
    digest = hashlib.sha256(inventory_bytes).hexdigest()
    if "## Persisted-format compatibility" not in notes:
        fail("release notes must contain a Persisted-format compatibility section")
    identity = (
        "DynamoDB persisted-format contract: "
        f"inventory `v{inventory_version}`, `sha256:{digest}`."
    )
    if identity not in notes:
        fail("release notes do not pin the candidate inventory digest")

    require_declaration(notes, "Changes from previous supported release")
    require_declaration(notes, "Adjacent-runtime validation")
    require_declaration(notes, "Historical incompatible-state export/import")


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--candidate-root", required=True, type=pathlib.Path)
    parser.add_argument("--release-notes", required=True)
    args = parser.parse_args()
    validate(args.candidate_root.resolve(), args.release_notes)
    print(args.candidate_root / args.release_notes)


if __name__ == "__main__":
    main()
