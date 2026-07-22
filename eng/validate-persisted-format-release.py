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
    match = re.search(
        rf"^- {re.escape(label)}: (.*(?:\n  .*)*)$",
        notes,
        flags=re.MULTILINE,
    )
    if match is None:
        fail(f"release notes must declare {label}")
    value = " ".join(line.strip() for line in match.group(1).splitlines()).strip()
    if (
        not value
        or value in {"TBD", "TODO"}
        or " / " in value
        or "<" in value
        or ">" in value
        or "evidence URL, or None with justification" in value
        or "describe every changed format" in value
        or "Not required / required" in value
    ):
        fail(f"release notes contain a placeholder for {label}")
    return value


def stored_procedure_identities(
    inventory: dict[str, Any], name: str
) -> dict[str, str]:
    values = inventory.get("stored_procedures")
    if not isinstance(values, list):
        fail(f"{name} stored_procedures must be an array")
    identities: dict[str, str] = {}
    for index, value in enumerate(values):
        if not isinstance(value, dict):
            fail(f"{name} stored_procedures[{index}] must be an object")
        sproc_id = value.get("id")
        body_hash = value.get("body_sha256")
        if not isinstance(sproc_id, str) or not sproc_id:
            fail(f"{name} stored_procedures[{index}].id must be a string")
        if not isinstance(body_hash, str) or DIGEST_RE.fullmatch(body_hash) is None:
            fail(
                f"{name} stored_procedures[{index}].body_sha256 "
                "must be a lowercase sha256"
            )
        if sproc_id in identities:
            fail(f"{name} repeats stored-procedure id {sproc_id}")
        identities[sproc_id] = body_hash
    return identities


def validate_stored_procedure_immutability(
    candidate: dict[str, Any], baseline_identities: dict[str, str]
) -> None:
    candidate_identities = stored_procedure_identities(candidate, "candidate inventory")
    for sproc_id, baseline_hash in baseline_identities.items():
        candidate_hash = candidate_identities.get(sproc_id)
        if candidate_hash is not None and candidate_hash != baseline_hash:
            fail(
                f"stored-procedure body changed without a new id: {sproc_id}"
            )


def raw_string_literal(value: str, indentation: str, field_name: str) -> str:
    lines = value.split("\n")
    normalized: list[str] = []
    for line in lines:
        if line and not line.startswith(indentation):
            fail(f"baseline raw string {field_name} has invalid indentation")
        normalized.append(line[len(indentation):] if line else "")
    return "\n".join(normalized)


def string_field_value(
    source: str,
    field_name: str,
    constants: dict[str, str],
) -> str:
    start_match = re.search(rf"\b{re.escape(field_name)}\s*=\s*", source)
    if start_match is None:
        fail(f"baseline source does not declare string {field_name}")
    end_match = re.search(r'\n[ \t]*""";', source[start_match.end():])
    if end_match is None:
        fail(f"baseline source does not terminate string {field_name}")
    expression = source[start_match.end():start_match.end() + end_match.end() - 1]
    token_re = re.compile(
        r'"""\n(.*?)\n([ \t]*)"""|([A-Za-z_][A-Za-z0-9_]*)',
        flags=re.DOTALL,
    )
    parts: list[str] = []
    position = 0
    for match in token_re.finditer(expression):
        separator = expression[position:match.start()]
        if separator.strip(" \t\r\n+"):
            fail(f"baseline string {field_name} contains an unsupported expression")
        if match.group(3) is not None:
            constant = constants.get(match.group(3))
            if constant is None:
                fail(
                    f"baseline string {field_name} references unknown "
                    f"constant {match.group(3)}"
                )
            parts.append(constant)
        else:
            parts.append(
                raw_string_literal(match.group(1), match.group(2), field_name)
            )
        position = match.end()
    if expression[position:].strip(" \t\r\n+;"):
        fail(f"baseline string {field_name} has an unsupported suffix")
    if not parts:
        fail(f"baseline source does not define value for {field_name}")
    return "".join(parts)


def baseline_stored_procedure_identities(
    baseline_root: pathlib.Path,
) -> dict[str, str]:
    inventory_path = baseline_root / INVENTORY
    if inventory_path.exists():
        return stored_procedure_identities(
            load_json(inventory_path), "baseline inventory"
        )

    manager_path = (
        baseline_root
        / "src/Aws2Azure.Modules.DynamoDb/Internal/SprocManager.cs"
    )
    sources_path = (
        baseline_root
        / "src/Aws2Azure.Modules.DynamoDb/Internal/SprocManager.Sources.cs"
    )
    try:
        manager = manager_path.read_text(encoding="utf-8")
        sources = sources_path.read_text(encoding="utf-8")
    except (OSError, UnicodeError) as error:
        fail(
            "baseline has neither a persisted-format inventory nor readable "
            f"stored-procedure sources: {error}"
        )

    constants: dict[str, str] = {}
    if re.search(r"\bConditionEvaluatorJs\s*=", sources):
        constants["ConditionEvaluatorJs"] = string_field_value(
            sources, "ConditionEvaluatorJs", {}
        )
    identities: dict[str, str] = {}
    declarations = (
        ("SprocId", "SprocBody"),
        ("TransactSprocId", "TransactSprocBody"),
    )
    for id_name, body_name in declarations:
        id_match = re.search(
            rf"\b{id_name}\s*=\s*\"([^\"]+)\";",
            manager,
        )
        if id_match is None:
            fail(f"baseline source does not declare literal {id_name}")
        sproc_id = id_match.group(1)
        body_hash = hashlib.sha256(
            string_field_value(
                sources,
                body_name,
                constants,
            ).encode("utf-8")
        ).hexdigest()
        identities[sproc_id] = body_hash
    return identities


def validate(
    candidate_root: pathlib.Path,
    release_notes: str,
    baseline_root: pathlib.Path | None,
) -> None:
    notes_relative = normalized_relative(release_notes, "release_notes")
    inventory_path = candidate_root / INVENTORY
    notes_path = candidate_root / notes_relative
    inventory = load_json(inventory_path)
    validate_fixture_hashes(candidate_root, inventory)
    stored_procedure_identities(inventory, "candidate inventory")
    if baseline_root is not None:
        validate_stored_procedure_immutability(
            inventory,
            baseline_stored_procedure_identities(baseline_root),
        )

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
    parser.add_argument("--baseline-root", type=pathlib.Path)
    parser.add_argument("--release-notes", required=True)
    args = parser.parse_args()
    validate(
        args.candidate_root.resolve(),
        args.release_notes,
        args.baseline_root.resolve() if args.baseline_root is not None else None,
    )
    print(args.candidate_root / args.release_notes)


if __name__ == "__main__":
    main()
