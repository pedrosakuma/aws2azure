#!/usr/bin/env python3
"""Tests for RC observation cleanup workflow guardrails."""

from __future__ import annotations

import json
import os
import shutil
import stat
import subprocess
import unittest
from pathlib import Path


REPO_ROOT = Path(__file__).resolve().parents[1]
SCRIPT = REPO_ROOT / ".github" / "scripts" / "cleanup-rc-observation-resource-groups.sh"
RC_WORKFLOW = REPO_ROOT / ".github" / "workflows" / "rc-observation-real-azure.yml"
REAPER_WORKFLOW = REPO_ROOT / ".github" / "workflows" / "real-azure-reaper.yml"


class RcObservationCleanupTests(unittest.TestCase):
    def setUp(self) -> None:
        self.root = (
            REPO_ROOT
            / ".artifacts"
            / f"test-rc-observation-cleanup-{os.getpid()}-{self._testMethodName}"
        )
        if self.root.exists():
            shutil.rmtree(self.root)
        (self.root / "bin").mkdir(parents=True)
        self.tags_path = self.root / "tags.json"
        self.cleanup_log = self.root / "cleanup.log"
        self.cleanup_script = self.root / "cleanup.sh"
        self.cleanup_script.write_text(
            "#!/usr/bin/env bash\n"
            "set -euo pipefail\n"
            "printf '%s\\n' \"$@\" > \"$CLEANUP_LOG\"\n",
            encoding="utf-8",
        )
        self.cleanup_script.chmod(
            self.cleanup_script.stat().st_mode | stat.S_IXUSR
        )
        fake_az = self.root / "bin" / "az"
        fake_az.write_text(
            "#!/usr/bin/env bash\n"
            "set -euo pipefail\n"
            "if [ \"$1 $2\" = 'group exists' ]; then\n"
            "  name=''\n"
            "  while [ $# -gt 0 ]; do\n"
            "    case \"$1\" in --name) name=\"$2\"; shift 2 ;; *) shift ;; esac\n"
            "  done\n"
            "  if grep -Fxq \"$name\" \"$AZ_FAKE_ABSENT_FILE\" 2>/dev/null; then\n"
            "    echo false\n"
            "  else\n"
            "    echo true\n"
            "  fi\n"
            "elif [ \"$1 $2\" = 'group show' ]; then\n"
            "  name=''\n"
            "  while [ $# -gt 0 ]; do\n"
            "    case \"$1\" in --name) name=\"$2\"; shift 2 ;; *) shift ;; esac\n"
            "  done\n"
            "  jq -er --arg name \"$name\" '.[$name]' \"$AZ_FAKE_TAGS_FILE\"\n"
            "else\n"
            "  echo \"unexpected az invocation: $*\" >&2\n"
            "  exit 99\n"
            "fi\n",
            encoding="utf-8",
        )
        fake_az.chmod(fake_az.stat().st_mode | stat.S_IXUSR)

    def tearDown(self) -> None:
        shutil.rmtree(self.root, ignore_errors=True)

    def run_script(
        self,
        groups: object,
        tags: dict[str, dict[str, str]] | None = None,
        absent: list[str] | None = None,
        *,
        expect_success: bool = True,
    ) -> subprocess.CompletedProcess[str]:
        self.tags_path.write_text(json.dumps(tags or {}), encoding="utf-8")
        absent_path = self.root / "absent.txt"
        absent_path.write_text("\n".join(absent or []), encoding="utf-8")
        env = os.environ.copy()
        env.update(
            {
                "PATH": f"{self.root / 'bin'}:{env['PATH']}",
                "AZ_FAKE_TAGS_FILE": str(self.tags_path),
                "AZ_FAKE_ABSENT_FILE": str(absent_path),
                "CLEANUP_LOG": str(self.cleanup_log),
            }
        )
        payload = groups if isinstance(groups, str) else json.dumps(groups)
        result = subprocess.run(
            ["bash", str(SCRIPT), payload, str(self.cleanup_script)],
            cwd=REPO_ROOT,
            env=env,
            text=True,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            check=False,
        )
        if expect_success:
            self.assertEqual(result.returncode, 0, result.stderr + result.stdout)
        else:
            self.assertNotEqual(result.returncode, 0, result.stdout)
            self.assertFalse(self.cleanup_log.exists())
        return result

    def test_exact_observation_group_is_verified_before_cleanup(self) -> None:
        group = "aws2azure-rc-observe-s3-basic-object-crud-29667841798-1"
        self.run_script(
            [group],
            {
                group: {
                    "purpose": "aws2azure-rc-observation",
                    "profile": "s3-basic-object-crud",
                    "run-id": "29667841798",
                    "run-attempt": "1",
                    "rc": "v1.0.0-rc.1",
                }
            },
        )
        self.assertEqual(self.cleanup_log.read_text(encoding="utf-8"), group + "\n")

    def test_legacy_leaked_group_without_run_attempt_tag_is_allowed(self) -> None:
        group = "aws2azure-rc-observe-secretsmanager-basic-lifecycle-29667841798-1"
        self.run_script(
            [group],
            {
                group: {
                    "purpose": "aws2azure-rc-observation",
                    "profile": "secretsmanager-basic-lifecycle",
                    "run-id": "29667841798",
                    "rc": "v1.0.0-rc.1",
                }
            },
        )
        self.assertEqual(self.cleanup_log.read_text(encoding="utf-8"), group + "\n")

    def test_absent_exact_group_is_success_without_cleanup_invocation(self) -> None:
        group = "aws2azure-rc-observe-secretsmanager-basic-lifecycle-29667841798-1"
        result = self.run_script([group], absent=[group])
        self.assertIn("already absent", result.stdout)
        self.assertFalse(self.cleanup_log.exists())

    def test_arbitrary_names_and_unstructured_input_are_rejected(self) -> None:
        self.run_script(
            ["production-shared-rg"],
            {"production-shared-rg": {"purpose": "aws2azure-rc-observation"}},
            expect_success=False,
        )
        self.run_script('{"resourceGroups":["aws2azure-rc-observe-s3-basic-object-crud-1-1"]}', expect_success=False)

    def test_tags_must_match_observation_purpose_profile_and_run_identity(self) -> None:
        group = "aws2azure-rc-observe-s3-basic-object-crud-29667841798-1"
        for tags in (
            {
                "purpose": "aws2azure-nightly",
                "profile": "s3-basic-object-crud",
                "run-id": "29667841798",
            },
            {
                "purpose": "aws2azure-rc-observation",
                "profile": "secretsmanager-basic-lifecycle",
                "run-id": "29667841798",
            },
            {
                "purpose": "aws2azure-rc-observation",
                "profile": "s3-basic-object-crud",
                "run-id": "1",
            },
            {
                "purpose": "aws2azure-rc-observation",
                "profile": "s3-basic-object-crud",
                "run-id": "29667841798",
                "run-attempt": "2",
            },
        ):
            with self.subTest(tags=tags):
                self.run_script([group], {group: tags}, expect_success=False)

    def test_workflows_pin_cleanup_login_and_preserve_safe_ordering(self) -> None:
        workflow = RC_WORKFLOW.read_text(encoding="utf-8")
        self.assertIn("run-attempt=\"$GITHUB_RUN_ATTEMPT\"", workflow)
        credential_delete = workflow.index(
            "- name: Remove runtime bytes and projected credentials"
        )
        refresh_login = workflow.index(
            "- name: Refresh Azure login for cleanup (OIDC)"
        )
        cleanup = workflow.index("- name: Deallocate ephemeral Azure resources")
        self.assertLess(credential_delete, refresh_login)
        self.assertLess(refresh_login, cleanup)
        between = workflow[refresh_login:cleanup]
        self.assertIn("if: always()", between)
        self.assertIn(
            "uses: azure/login@a457da9ea143d694b1b9c7c869ebb04ebe844ef5",
            between,
        )
        self.assertNotIn("${{ inputs.", between)
        self.assertEqual(
            workflow[refresh_login:cleanup].count("uses: azure/login@"), 1
        )

    def test_reaper_supports_exact_manual_and_stale_observation_cleanup(self) -> None:
        workflow = REAPER_WORKFLOW.read_text(encoding="utf-8")
        self.assertIn("rc_observation_resource_groups_json:", workflow)
        self.assertIn(
            "cleanup-rc-observation-resource-groups.sh",
            workflow,
        )
        self.assertIn("for purpose in aws2azure-nightly aws2azure-rc-observation", workflow)
        self.assertIn("cleanup_failed=0", workflow)
        self.assertIn('exit "$cleanup_failed"', workflow)
        self.assertIn("MAX_AGE_HOURS: '6'", workflow)
        self.assertIn('inputs.rc_observation_resource_groups_json != \'[]\'', workflow)


if __name__ == "__main__":
    unittest.main(verbosity=2)
