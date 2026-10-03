#!/usr/bin/env python3
"""Offline tests of the paid workflows' actual conditions and selection scripts."""

from __future__ import annotations

import ast
import itertools
import json
import os
from pathlib import Path
import re
import subprocess
import tempfile
import textwrap
import unittest


ROOT = Path(__file__).resolve().parents[1]
WORKFLOWS = ROOT / ".github/workflows"
PAID = {
    "integration-real-azure": ("real-azure", "run-real-azure"),
    "perf-real-azure": ("perf-real-azure", "run-perf-real-azure"),
    "workload-load-real-azure": ("select", "run-workload-load"),
}
PROFILES = [
    "secretsmanager-basic-lifecycle",
    "s3-basic-object-crud",
    "dynamodb-basic-crud",
    "dynamodb-single-partition-transactions",
    "sqs-standard-messaging",
    "kinesis-single-consumer-per-shard",
]


def workflow(name: str) -> str:
    return (WORKFLOWS / f"{name}.yml").read_text(encoding="utf-8")


def selected(name: str, event: str, action: str = "", label: str = "",
             labels: tuple[str, ...] = ()) -> bool:
    job, _ = PAID[name]
    source = workflow(name).split(f"\n  {job}:\n", 1)[1]
    expression = source.split("    if: >-\n", 1)[1].split("    runs-on:", 1)[0]
    values = {
        "github.event_name": event,
        "github.event.action": action,
        "github.event.label.name": label,
        "github.event.pull_request.labels.*.name": labels,
    }
    for index, (key, value) in enumerate(values.items()):
        expression = expression.replace(key, f"value{index}")
    context = {f"value{index}": value for index, value in enumerate(values.values())}
    expression = " ".join(expression.split()).replace("&&", " and ").replace("||", " or ")

    # Interpret only the boolean/string/contains subset used by these job guards.
    # Unknown syntax fails the test instead of executing code or assuming a result.
    def evaluate(node: ast.AST):
        if isinstance(node, ast.Constant) and isinstance(node.value, str):
            return node.value
        if isinstance(node, ast.Name):
            return context[node.id]
        if isinstance(node, ast.BoolOp) and isinstance(node.op, (ast.And, ast.Or)):
            results = [evaluate(value) for value in node.values]
            return all(results) if isinstance(node.op, ast.And) else any(results)
        if isinstance(node, ast.Compare) and len(node.ops) == 1:
            left, right = evaluate(node.left), evaluate(node.comparators[0])
            if isinstance(node.ops[0], ast.Eq):
                return left.casefold() == right.casefold()
            if isinstance(node.ops[0], ast.NotEq):
                return left.casefold() != right.casefold()
        if (isinstance(node, ast.Call) and isinstance(node.func, ast.Name)
                and node.func.id == "contains" and len(node.args) == 2 and not node.keywords):
            items, item = (evaluate(arg) for arg in node.args)
            return item.casefold() in [entry.casefold() for entry in items]
        raise AssertionError(f"Unsupported workflow expression: {ast.dump(node)}")

    result = evaluate(ast.parse(expression.strip(), mode="eval").body)
    assert isinstance(result, bool)
    return result


def step_script(name: str, step: str) -> str:
    source = workflow(name).split(f"      - name: {step}\n", 1)[1]
    script = source.split("        run: |\n", 1)[1]
    script = re.split(r"\n(?= {0,9}\S)", script, maxsplit=1)[0]
    return textwrap.dedent(script)


def run_step(name: str, step: str, settings: dict[str, str]):
    with tempfile.TemporaryDirectory(prefix="paid-workflow-test-") as directory:
        output, environment = Path(directory) / "output", Path(directory) / "env"
        result = subprocess.run(
            ["bash", "-c", step_script(name, step)], cwd=ROOT,
            env={"PATH": os.environ["PATH"], **settings,
                 "GITHUB_OUTPUT": str(output), "GITHUB_ENV": str(environment)},
            text=True, capture_output=True, check=False,
        )
        outputs = dict(line.split("=", 1) for line in output.read_text().splitlines()) if output.exists() else {}
        return result, outputs, environment.read_text() if environment.exists() else ""


class PaidWorkflowAuthorizationTests(unittest.TestCase):
    def test_existing_paid_label_trigger_set_and_events_are_bounded(self):
        paid_label_workflows = {
            path.stem for path in WORKFLOWS.glob("*real-azure.yml")
            if "pull_request:" in path.read_text()
        }
        self.assertEqual(set(PAID), paid_label_workflows)
        for name in PAID:
            self.assertIn("types: [labeled, synchronize, reopened]", workflow(name))
            self.assertNotIn("pull_request_target:", workflow(name))

    def test_required_integration_and_perf_labels_never_select_load(self):
        labels = ("run-real-azure", "run-perf", "run-integration", "run-footprint")
        for action, label in [("labeled", label) for label in labels] + [
            ("synchronize", ""), ("reopened", "")
        ]:
            with self.subTest(action=action, label=label):
                self.assertFalse(selected("workload-load-real-azure", "pull_request", action, label, labels))

    def test_each_paid_scope_accepts_only_its_own_label_event_and_current_opt_in(self):
        labels = tuple(label for _, label in PAID.values()) + ("run-perf", "run-integration", "run-footprint", "bug")
        for present in itertools.product((False, True), repeat=len(labels)):
            current = tuple(label for label, included in zip(labels, present) if included)
            for name, (_, own_label) in PAID.items():
                for triggering_label in labels:
                    with self.subTest(workflow=name, current=current, triggering=triggering_label):
                        self.assertEqual(
                            own_label in current and triggering_label == own_label,
                            selected(name, "pull_request", "labeled", triggering_label, current),
                        )

    def test_label_batch_selects_each_authorized_scope_once_not_once_per_label(self):
        labels = tuple(label for _, label in PAID.values()) + ("run-perf", "run-footprint")
        for name in PAID:
            self.assertEqual(1, sum(
                selected(name, "pull_request", "labeled", label, labels) for label in labels
            ))

    def test_synchronize_and_reopened_keep_each_explicit_opt_in(self):
        for name, (_, own_label) in PAID.items():
            for action in ("synchronize", "reopened"):
                with self.subTest(workflow=name, action=action):
                    self.assertTrue(selected(name, "pull_request", action, labels=(own_label,)))
                    self.assertFalse(selected(name, "pull_request", action))
                    self.assertFalse(selected(name, "pull_request", action, labels=("run-perf",)))

    def test_deliberate_own_label_readdition_is_not_suppressed(self):
        for name, (_, own_label) in PAID.items():
            for _ in range(2):
                self.assertTrue(selected(name, "pull_request", "labeled", own_label, (own_label,)))

    def test_schedule_and_manual_do_not_need_pr_labels_and_push_does_not_select(self):
        for name, (_, own_label) in PAID.items():
            self.assertTrue(selected(name, "schedule"))
            self.assertTrue(selected(name, "workflow_dispatch"))
            self.assertFalse(selected(name, "push", labels=(own_label,)))
        for name, cron in (
            ("integration-real-azure", "0 5 * * *"),
            ("perf-real-azure", "30 6 * * 0"),
            ("workload-load-real-azure", "15 6 * * *"),
        ):
            self.assertIn(f"- cron: '{cron}'", workflow(name))

    def test_load_profile_selection_is_unchanged(self):
        cases = [("schedule", "", PROFILES[:1]), ("pull_request", "", PROFILES),
                 ("workflow_dispatch", "all", PROFILES)]
        cases += [("workflow_dispatch", profile, [profile]) for profile in PROFILES]
        for event, requested, expected in cases:
            with self.subTest(event=event, requested=requested):
                result, outputs, _ = run_step(
                    "workload-load-real-azure", "Select workload profiles",
                    {"EVENT_NAME": event, "REQUESTED_PROFILE": requested},
                )
                self.assertEqual(0, result.returncode, result.stderr)
                self.assertEqual(expected, json.loads(outputs["profiles"]))
        result, outputs, _ = run_step(
            "workload-load-real-azure", "Select workload profiles",
            {"EVENT_NAME": "workflow_dispatch", "REQUESTED_PROFILE": "unsupported"},
        )
        self.assertNotEqual(0, result.returncode)
        self.assertNotIn("profiles", outputs)
        self.assertIn("::error::Unsupported workload profile", result.stdout)
        self.assertIn("needs: select", workflow("workload-load-real-azure"))
        self.assertIn("profile: ${{ fromJSON(needs.select.outputs.profiles) }}", workflow("workload-load-real-azure"))

    def test_forks_without_secrets_keep_existing_provisioning_guards(self):
        for name in ("integration-real-azure", "perf-real-azure"):
            result, outputs, _ = run_step(
                name, "Check provisioning credentials",
                {"AZURE_CLIENT_ID": "", "AZURE_CLIENT_OBJECT_ID": ""},
            )
            self.assertEqual(0, result.returncode, result.stderr)
            self.assertEqual("false", outputs["enabled"])
            self.assertIn(
                "- name: Azure login (OIDC)\n        if: steps.gate.outputs.enabled == 'true'",
                workflow(name),
            )
        result, _, _ = run_step(
            "workload-load-real-azure", "Require Azure OIDC provisioning",
            {"AZURE_CLIENT_ID": "", "AZURE_TENANT_ID": "", "AZURE_SUBSCRIPTION_ID": ""},
        )
        self.assertNotEqual(0, result.returncode)
        self.assertIn("::error::Azure OIDC secrets are required", result.stdout)

    def test_load_source_validation_and_sealed_modes_remain_separate(self):
        settings = {
            "EVENT_NAME": "workflow_dispatch", "REF": "refs/heads/main", "REF_PROTECTED": "true",
            "REQUALIFICATION_MODE": "promote", "CANDIDATE_RUN_ID": "123",
            "CANDIDATE_RUN_ATTEMPT": "1", "CANDIDATE_ARTIFACT_ID": "", "PROFILE": PROFILES[0],
        }
        cases = [
            ({}, True, "rollback"),
            ({"REQUALIFICATION_MODE": "reaffirm", "CANDIDATE_RUN_ID": ""}, True, "rollback"),
            ({"EVENT_NAME": "pull_request", "REF": "refs/pull/1/merge", "REF_PROTECTED": "false"}, False, "source_validation"),
            ({"EVENT_NAME": "schedule"}, False, "source_validation"),
        ]
        for overrides, qualifying, mode in cases:
            with self.subTest(overrides=overrides):
                result, outputs, environment = run_step(
                    "workload-load-real-azure", "Select qualifying or source-validation mode",
                    {**settings, **overrides},
                )
                self.assertEqual(0, result.returncode, result.stderr)
                self.assertEqual(str(qualifying).lower(), outputs["qualifying"])
                self.assertEqual(overrides.get("REQUALIFICATION_MODE", "promote"), outputs["requalification_mode"])
                self.assertIn(f"AWS2AZURE_SEALED_RUNTIME_MODE={mode}\n", environment)
        for overrides in (
            {"CANDIDATE_RUN_ID": ""},
            {"REF_PROTECTED": "false"},
            {"REF": "refs/heads/unprotected"},
            {"REQUALIFICATION_MODE": "reaffirm"},
        ):
            result, outputs, _ = run_step(
                "workload-load-real-azure", "Select qualifying or source-validation mode",
                {**settings, **overrides},
            )
            self.assertNotEqual(0, result.returncode)
            self.assertIn("::error::", result.stdout)
            self.assertNotIn("qualifying", outputs)


if __name__ == "__main__":
    unittest.main(verbosity=2)
