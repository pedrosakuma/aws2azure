#!/usr/bin/env python3
"""Offline checks for accepted Azure deletion versus failed cleanup."""

from __future__ import annotations

import json
import os
import subprocess
import tempfile
import unittest
from pathlib import Path


REPO_ROOT = Path(__file__).resolve().parents[1]
SCRIPT = REPO_ROOT / ".github/scripts/cleanup-real-azure-resource-groups.sh"

FAKE_AZ = """#!/usr/bin/env python3
import json
import os
import sys
from pathlib import Path

root = Path(os.environ["CLEANUP_TEST_ROOT"])
args = sys.argv[1:]
with (root / "calls.jsonl").open("a") as log:
    log.write(json.dumps(args) + "\\n")
config = json.loads((root / "config.json").read_text())
state_file = root / "state.json"
state = json.loads(state_file.read_text()) if state_file.exists() else {}
group = next((args[args.index(flag) + 1] for flag in ("--name", "--resource-group")
              if flag in args), "")
settings = config.get(group, {})

if args[:2] == ["account", "show"]:
    print("test-subscription")
elif args[:2] == ["group", "exists"]:
    index = state.get(group, 0)
    results = settings.get("exists", ["true"])
    result = results[min(index, len(results) - 1)]
    state[group] = index + 1
    state_file.write_text(json.dumps(state))
    if result == "error":
        print("AuthorizationFailed", file=sys.stderr)
        sys.exit(1)
    print(result)
elif args[:2] == ["group", "delete"]:
    if settings.get("delete_error"):
        print("AuthorizationFailed", file=sys.stderr)
        sys.exit(1)
elif args[:2] == ["keyvault", "list"]:
    if settings.get("vault_error"):
        print("test-vault")
elif args[:2] == ["keyvault", "delete"]:
    print("Vault deletion denied", file=sys.stderr)
    sys.exit(1)
elif args[:3] == ["storage", "account", "list"]:
    if settings.get("protected_storage"):
        print("test-storage")
elif args[:4] == ["storage", "account", "keys", "list"]:
    print("test-key")
elif args[:3] == ["storage", "account", "generate-sas"]:
    print("test-sas")
elif args[:3] == ["storage", "container", "list"]:
    pass
elif args[:3] == ["storage", "account", "delete"]:
    print("AccountProtectedFromDeletion", file=sys.stderr)
    sys.exit(1)
else:
    print("Unexpected fake Azure invocation: " + repr(args), file=sys.stderr)
    sys.exit(99)
"""


class RealAzureCleanupTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temp = tempfile.TemporaryDirectory(prefix="aws2azure-cleanup-test-")
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        bin_dir = self.root / "bin"
        bin_dir.mkdir()
        scripts = {
            "az": FAKE_AZ,
            "date": """#!/usr/bin/env python3
import os
import sys
from pathlib import Path
if sys.argv[1:] == ["-u", "+%s"]:
    clock = Path(os.environ["CLEANUP_TEST_ROOT"]) / "clock"
    now = int(clock.read_text()) if clock.exists() else 0
    print(now)
    clock.write_text(str(now + 600))
elif sys.argv[1:] == ["-u", "-d", "+1 hour", "+%Y-%m-%dT%H:%MZ"]:
    print("2026-09-25T16:00Z")
else:
    sys.exit(99)
""",
            "sleep": "#!/usr/bin/env bash\n[ \"$*\" = 15 ]\n",
        }
        for name, content in scripts.items():
            path = bin_dir / name
            path.write_text(content, encoding="utf-8")
            path.chmod(0o755)
        self.env = {
            **os.environ,
            "PATH": f"{bin_dir}:{os.environ['PATH']}",
            "CLEANUP_TEST_ROOT": str(self.root),
            "GITHUB_STEP_SUMMARY": str(self.root / "summary.md"),
        }

    def run_cleanup(
        self, groups: dict[str, dict], *, allow_pending: bool = True
    ) -> subprocess.CompletedProcess[str]:
        for name in ("state.json", "clock", "calls.jsonl", "summary.md"):
            (self.root / name).unlink(missing_ok=True)
        (self.root / "config.json").write_text(json.dumps(groups), encoding="utf-8")
        args = ["bash", str(SCRIPT)]
        if allow_pending:
            args.append("--allow-pending-deletion")
        return subprocess.run(
            [*args, *groups],
            cwd=REPO_ROOT,
            env=self.env,
            capture_output=True,
            text=True,
            timeout=15,
            check=False,
        )

    def calls(self) -> list[list[str]]:
        return [
            json.loads(line)
            for line in (self.root / "calls.jsonl").read_text().splitlines()
        ]

    def test_absent_group_never_requests_deletion(self) -> None:
        result = self.run_cleanup({"rg-absent": {"exists": ["false"]}})
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn("already absent", result.stdout)
        self.assertFalse(any(call[:2] == ["group", "delete"] for call in self.calls()))

    def test_confirmed_deletion_has_no_pending_warning(self) -> None:
        result = self.run_cleanup({"rg-deleted": {"exists": ["true", "false"]}})
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn("Deleted resource group rg-deleted.", result.stdout)
        self.assertNotIn("::warning::", result.stdout)
        self.assertFalse((self.root / "summary.md").exists())

    def test_accepted_pending_deletion_warns_and_identifies_group_in_summary(self) -> None:
        result = self.run_cleanup({"rg-pending": {}})
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn("::warning::Azure accepted deletion of resource group rg-pending", result.stdout)
        self.assertIn("1200s", result.stdout)
        self.assertNotIn("Deleted resource group", result.stdout)
        self.assertIn(
            ["group", "delete", "--name", "rg-pending", "--yes", "--no-wait"],
            self.calls(),
        )
        summary = (self.root / "summary.md").read_text()
        self.assertIn("**Cleanup pending:**", summary)
        self.assertIn("`rg-pending`", summary)
        self.assertIn("six-hour real-azure-reaper", summary)

    def test_reaper_default_still_requires_confirmed_deletion(self) -> None:
        result = self.run_cleanup({"rg-pending": {}}, allow_pending=False)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("::error::Azure did not confirm deletion", result.stdout)
        self.assertNotIn("::warning::", result.stdout)

    def test_rejected_deletion_is_never_downgraded(self) -> None:
        result = self.run_cleanup({"rg-denied": {"delete_error": True}})
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("AuthorizationFailed", result.stderr)
        self.assertNotIn("::warning::", result.stdout)
        self.assertFalse((self.root / "summary.md").exists())

    def test_initial_and_post_acceptance_probe_errors_remain_blocking(self) -> None:
        for results in (["error"], ["true", "error"], ["unexpected"], ["true", "unexpected"]):
            with self.subTest(results=results):
                result = self.run_cleanup({"rg-unverified": {"exists": results}})
                self.assertNotEqual(result.returncode, 0)
                self.assertIn("::error::", result.stdout)
                self.assertNotIn("::warning::", result.stdout)
                self.assertFalse((self.root / "summary.md").exists())

    def test_protected_storage_and_vault_errors_prevent_group_deletion(self) -> None:
        for failure in ("protected_storage", "vault_error"):
            with self.subTest(failure=failure):
                result = self.run_cleanup({"rg-protected": {failure: True}})
                self.assertNotEqual(result.returncode, 0)
                self.assertFalse(any(call[:2] == ["group", "delete"] for call in self.calls()))
                self.assertNotIn("::warning::Azure accepted", result.stdout)

    def test_pending_group_does_not_hide_another_groups_verification_error(self) -> None:
        result = self.run_cleanup({
            "rg-pending": {},
            "rg-error": {"exists": ["true", "error"]},
        })
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("::warning::Azure accepted deletion of resource group rg-pending", result.stdout)
        self.assertIn("::error::Could not verify deletion of resource group rg-error", result.stdout)
        summary = (self.root / "summary.md").read_text()
        self.assertIn("rg-pending", summary)
        self.assertNotIn("rg-error", summary)

    def test_summary_only_lists_groups_still_pending(self) -> None:
        result = self.run_cleanup({
            "rg-deleted": {"exists": ["true", "false"]},
            "rg-pending": {},
        })
        self.assertEqual(result.returncode, 0, result.stderr)
        summary = (self.root / "summary.md").read_text()
        self.assertIn("rg-pending", summary)
        self.assertNotIn("rg-deleted", summary)

    def test_allow_pending_without_groups_is_usage_error(self) -> None:
        result = self.run_cleanup({})
        self.assertEqual(result.returncode, 2)
        self.assertIn("usage:", result.stderr)

    def test_only_foreground_workflows_opt_in_and_reaper_keeps_six_hour_schedule(self) -> None:
        for name, expected_calls in (
            ("integration-real-azure.yml", 1),
            ("workload-load-real-azure.yml", 1),
            ("rc-observation-real-azure.yml", 2),
        ):
            with self.subTest(workflow=name):
                workflow = (REPO_ROOT / ".github/workflows" / name).read_text()
                cleanup_steps = [
                    step for step in workflow.split("      - name:")
                    if "cleanup-real-azure-resource-groups.sh" in step
                ]
                self.assertEqual(len(cleanup_steps), expected_calls)
                for step in cleanup_steps:
                    self.assertIn("--allow-pending-deletion", step)
                    self.assertNotIn("continue-on-error:", step)
        reaper = (REPO_ROOT / ".github/workflows/real-azure-reaper.yml").read_text()
        self.assertIn("cron: '30 */6 * * *'", reaper)
        self.assertIn("MAX_AGE_HOURS: '6'", reaper)
        self.assertNotIn("--allow-pending-deletion", reaper)
        wrapper = (REPO_ROOT / ".github/scripts/cleanup-rc-observation-resource-groups.sh").read_text()
        self.assertNotIn("--allow-pending-deletion", wrapper)


if __name__ == "__main__":
    unittest.main(verbosity=2)
