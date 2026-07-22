#!/usr/bin/env python3
"""Tests for candidate-bound persisted-format release-note validation."""

from __future__ import annotations

import hashlib
import json
import os
import pathlib
import shutil
import subprocess
import unittest


REPO_ROOT = pathlib.Path(__file__).resolve().parent.parent
TOOL = REPO_ROOT / "eng" / "validate-persisted-format-release.py"
INVENTORY = REPO_ROOT / "docs/compatibility/dynamodb-persisted-formats-v1.json"


class PersistedFormatReleaseTests(unittest.TestCase):
    def setUp(self) -> None:
        self.root = (
            REPO_ROOT
            / "artifacts"
            / f"test-persisted-release-{os.getpid()}-{self._testMethodName}"
        )
        shutil.rmtree(self.root, ignore_errors=True)
        self.root.mkdir(parents=True)
        self.notes = self.root / "notes.md"
        inventory_relative = pathlib.Path(
            "docs/compatibility/dynamodb-persisted-formats-v1.json"
        )
        candidate_inventory = self.root / inventory_relative
        candidate_inventory.parent.mkdir(parents=True)
        shutil.copy2(INVENTORY, candidate_inventory)
        inventory = json.loads(INVENTORY.read_text(encoding="utf-8"))
        for format_entry in inventory["formats"]:
            for key in ("v1_fixture", "current_fixture"):
                if key not in format_entry:
                    continue
                relative = pathlib.Path(format_entry[key])
                destination = self.root / relative
                destination.parent.mkdir(parents=True, exist_ok=True)
                shutil.copy2(REPO_ROOT / relative, destination)

    def tearDown(self) -> None:
        shutil.rmtree(self.root, ignore_errors=True)
        try:
            (REPO_ROOT / "artifacts").rmdir()
        except OSError:
            pass

    def write_notes(self, digest: str, changes: str = "None.") -> None:
        self.notes.write_text(
            "## Persisted-format compatibility\n\n"
            f"- DynamoDB persisted-format contract: inventory `v1`, `sha256:{digest}`.\n"
            f"- Changes from previous supported release: {changes}\n"
            "- Adjacent-runtime validation: evidence://candidate-prior.\n"
            "- Historical incompatible-state export/import: Not required.\n",
            encoding="utf-8",
        )

    def run_tool(self, *, expect_success: bool) -> None:
        result = subprocess.run(
            [
                "python3",
                str(TOOL),
                "--candidate-root",
                str(self.root),
                "--release-notes",
                self.notes.relative_to(self.root).as_posix(),
            ],
            cwd=REPO_ROOT,
            text=True,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            check=False,
        )
        if expect_success and result.returncode != 0:
            self.fail(f"tool failed: {result.stderr}")
        if not expect_success and result.returncode == 0:
            self.fail("tool unexpectedly passed")

    def test_exact_candidate_inventory_and_complete_declarations_pass(self) -> None:
        digest = hashlib.sha256(
            (self.root / "docs/compatibility/dynamodb-persisted-formats-v1.json")
            .read_bytes()
        ).hexdigest()
        self.write_notes(digest)
        self.run_tool(expect_success=True)

    def test_wrong_inventory_digest_fails(self) -> None:
        self.write_notes("0" * 64)
        self.run_tool(expect_success=False)

    def test_template_placeholder_fails(self) -> None:
        digest = hashlib.sha256(
            (self.root / "docs/compatibility/dynamodb-persisted-formats-v1.json")
            .read_bytes()
        ).hexdigest()
        self.write_notes(digest, "None / describe every changed format,")
        self.run_tool(expect_success=False)


if __name__ == "__main__":
    unittest.main(verbosity=2)
