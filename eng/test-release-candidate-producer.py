#!/usr/bin/env python3
"""Tests for trusted immutable release-candidate archive production."""

from __future__ import annotations

import hashlib
import json
import os
import pathlib
import re
import shutil
import subprocess
import unittest


REPO_ROOT = pathlib.Path(__file__).resolve().parent.parent
PACKAGE_TOOL = REPO_ROOT / "eng" / "release-candidate-package.py"
INPUTS_TOOL = REPO_ROOT / "eng" / "release-candidate-inputs.py"
WORKFLOW = REPO_ROOT / ".github" / "workflows" / "release-candidate.yml"
STABLE_WORKFLOW = REPO_ROOT / ".github" / "workflows" / "release.yml"
CANDIDATE = "v1.2.3-rc.4"
REPOSITORY = "pedrosakuma/aws2azure"
SOURCE_SHA = "0123456789abcdef0123456789abcdef01234567"
ORCHESTRATION_SHA = "1123456789abcdef0123456789abcdef01234567"
APPROVAL_SHA = ORCHESTRATION_SHA
SOURCE_REF = f"refs/tags/{CANDIDATE}"


def digest_bytes(value: bytes) -> str:
    return f"sha256:{hashlib.sha256(value).hexdigest()}"


def write_json(path: pathlib.Path, value: object) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(
        json.dumps(value, sort_keys=True, indent=2, ensure_ascii=False) + "\n",
        encoding="utf-8",
    )


class ReleaseCandidateProducerTests(unittest.TestCase):
    def setUp(self) -> None:
        self.root = (
            REPO_ROOT
            / "artifacts"
            / f"test-release-candidate-producer-{os.getpid()}-{self._testMethodName}"
        )
        shutil.rmtree(self.root, ignore_errors=True)
        self.root.mkdir(parents=True)
        self.executable = self.root / "input" / "Aws2Azure.Proxy"
        self.executable.parent.mkdir()
        self.executable.write_bytes(b"fake native executable bytes\n")
        self.executable.chmod(0o755)
        self.sealed_manifest = self.root / "input" / "sealed-runtime-manifest.json"
        self.sealed_manifest.write_text('{"sealed":"runtime"}\n', encoding="utf-8")
        self.manifest_digest = digest_bytes(self.sealed_manifest.read_bytes())
        self.ledgers: dict[str, pathlib.Path] = {}
        for profile in ("s3-basic-object-crud", "secretsmanager-basic-lifecycle"):
            path = self.root / "input" / f"{profile}-ledger.json"
            write_json(path, self.make_ledger(profile))
            self.ledgers[profile] = path
        self.context = self.root / "context.json"

    def tearDown(self) -> None:
        shutil.rmtree(self.root, ignore_errors=True)
        try:
            (REPO_ROOT / "artifacts").rmdir()
        except OSError:
            pass

    def make_ledger(self, profile: str) -> dict[str, object]:
        executable_digest = digest_bytes(self.executable.read_bytes())
        run_id = 123456
        run_attempt = 2
        artifact_name = (
            "aws2azure-sealed-linux-x64-"
            f"{'a' * 64}-run-{run_id}-attempt-{run_attempt}"
        )
        return {
            "schema_version": 1,
            "ledger_record_digest": digest_bytes(f"ledger:{profile}".encode()),
            "record": {
                "schema_version": 1,
                "profile": {"id": profile, "version": 1},
                "status": "approved",
                "eligibility": {
                    "rollback_baseline_eligible": True,
                    "promotion_eligible": True,
                },
                "runtime": {
                    "target": {
                        "operating_system": "linux",
                        "architecture": "x64",
                        "rid": "linux-x64",
                    },
                    "source_repository": REPOSITORY,
                    "source_sha": SOURCE_SHA,
                    "aggregate_digest": digest_bytes(b"aggregate"),
                    "executable_digest": executable_digest,
                },
                "producer": {
                    "workflow": ".github/workflows/sealed-runtime.yml",
                    "run_id": run_id,
                    "run_attempt": run_attempt,
                    "run_url": f"https://github.com/{REPOSITORY}/actions/runs/{run_id}",
                },
                "artifact": {
                    "id": 987654,
                    "name": artifact_name,
                    "upload_digest": digest_bytes(b"artifact zip"),
                },
                "attestation": {
                    "predicate_type": "https://slsa.dev/provenance/v1",
                    "repository": REPOSITORY,
                    "signer_workflow": (
                        f"{REPOSITORY}/.github/workflows/sealed-runtime.yml"
                    ),
                    "source_sha": SOURCE_SHA,
                    "source_ref": "refs/heads/main",
                    "subject_name": "Aws2Azure.Proxy",
                    "subject_digest": executable_digest,
                    "manifest_subject_name": "sealed-runtime-manifest.json",
                    "manifest_subject_digest": self.manifest_digest,
                },
            },
        }

    def run_tool(
        self, tool: pathlib.Path, *arguments: str, expect_success: bool = True
    ) -> subprocess.CompletedProcess[str]:
        result = subprocess.run(
            ["python3", str(tool), *arguments],
            cwd=REPO_ROOT,
            text=True,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            check=False,
        )
        if expect_success and result.returncode != 0:
            self.fail(f"tool failed:\nstdout={result.stdout}\nstderr={result.stderr}")
        if not expect_success and result.returncode == 0:
            self.fail(f"tool unexpectedly passed:\nstdout={result.stdout}")
        return result

    def create_context(self, *, expect_success: bool = True) -> None:
        self.run_tool(
            INPUTS_TOOL,
            "create-context",
            "--candidate",
            CANDIDATE,
            "--repository",
            REPOSITORY,
            "--source-sha",
            SOURCE_SHA,
            "--source-ref",
            SOURCE_REF,
            "--orchestration-sha",
            ORCHESTRATION_SHA,
            "--approval-sha",
            APPROVAL_SHA,
            "--s3-ledger",
            str(self.ledgers["s3-basic-object-crud"]),
            "--s3-profile",
            str(REPO_ROOT / "docs/workloads/s3-basic-object-crud.yaml"),
            "--secrets-ledger",
            str(self.ledgers["secretsmanager-basic-lifecycle"]),
            "--secrets-profile",
            str(REPO_ROOT / "docs/workloads/secretsmanager-basic-lifecycle.yaml"),
            "--policy",
            str(REPO_ROOT / "docs/versioning-and-compatibility.md"),
            "--output",
            str(self.context),
            expect_success=expect_success,
        )

    def package(
        self, rid: str, output: pathlib.Path, *, source_sha: str = SOURCE_SHA
    ) -> pathlib.Path:
        self.run_tool(
            PACKAGE_TOOL,
            "package",
            "--candidate",
            CANDIDATE,
            "--repository",
            REPOSITORY,
            "--source-sha",
            source_sha,
            "--source-ref",
            SOURCE_REF,
            "--rid",
            rid,
            "--executable",
            str(self.executable),
            "--license",
            str(REPO_ROOT / "LICENSE"),
            "--config",
            str(REPO_ROOT / "docker/config.json"),
            "--output",
            str(output),
        )
        return output / "platform-manifest.json"

    def test_context_binds_both_ledgers_profiles_and_policy(self) -> None:
        self.create_context()
        self.run_tool(INPUTS_TOOL, "validate-context", str(self.context))
        context = json.loads(self.context.read_text(encoding="utf-8"))
        self.assertEqual(
            [item["profile"]["id"] for item in context["workloads"]],
            ["s3-basic-object-crud", "secretsmanager-basic-lifecycle"],
        )
        self.assertNotEqual(
            context["workloads"][0]["approved_runtime"]["ledger_record_digest"],
            context["workloads"][1]["approved_runtime"]["ledger_record_digest"],
        )
        self.assertEqual(
            context["compatibility_policy"]["digest"],
            digest_bytes(
                (REPO_ROOT / "docs/versioning-and-compatibility.md").read_bytes()
            ),
        )
        self.assertEqual(context["approved_ledger_source"]["sha"], APPROVAL_SHA)
        self.assertEqual(
            context["orchestration_source"],
            context["approved_ledger_source"],
        )
        self.assertNotEqual(
            context["orchestration_source"]["sha"],
            context["candidate"]["source"]["sha"],
        )

    def test_context_rejects_runtime_source_and_ledger_identity_drift(self) -> None:
        secrets_path = self.ledgers["secretsmanager-basic-lifecycle"]
        secrets = json.loads(secrets_path.read_text(encoding="utf-8"))
        secrets["record"]["runtime"]["executable_digest"] = digest_bytes(b"different")
        secrets["record"]["attestation"]["subject_digest"] = digest_bytes(b"different")
        write_json(secrets_path, secrets)
        self.create_context(expect_success=False)

        write_json(secrets_path, self.make_ledger("secretsmanager-basic-lifecycle"))
        s3_path = self.ledgers["s3-basic-object-crud"]
        s3 = json.loads(s3_path.read_text(encoding="utf-8"))
        s3["record"]["runtime"]["source_sha"] = "1" * 40
        s3["record"]["attestation"]["source_sha"] = "1" * 40
        write_json(s3_path, s3)
        self.create_context(expect_success=False)

        write_json(s3_path, self.make_ledger("s3-basic-object-crud"))
        secrets = self.make_ledger("secretsmanager-basic-lifecycle")
        secrets["ledger_record_digest"] = self.make_ledger("s3-basic-object-crud")[
            "ledger_record_digest"
        ]
        write_json(secrets_path, secrets)
        self.create_context(expect_success=False)

        write_json(secrets_path, self.make_ledger("secretsmanager-basic-lifecycle"))
        s3 = self.make_ledger("s3-basic-object-crud")
        s3["record"]["attestation"]["source_ref"] = "refs/tags/v1.2.3-rc.3"
        write_json(s3_path, s3)
        self.create_context(expect_success=False)

    def test_context_rejects_candidate_tag_and_ledger_runtime_mismatch(self) -> None:
        self.run_tool(
            INPUTS_TOOL,
            "create-context",
            "--candidate",
            CANDIDATE,
            "--repository",
            REPOSITORY,
            "--source-sha",
            "2" * 40,
            "--source-ref",
            SOURCE_REF,
            "--orchestration-sha",
            ORCHESTRATION_SHA,
            "--approval-sha",
            APPROVAL_SHA,
            "--s3-ledger",
            str(self.ledgers["s3-basic-object-crud"]),
            "--s3-profile",
            str(REPO_ROOT / "docs/workloads/s3-basic-object-crud.yaml"),
            "--secrets-ledger",
            str(self.ledgers["secretsmanager-basic-lifecycle"]),
            "--secrets-profile",
            str(REPO_ROOT / "docs/workloads/secretsmanager-basic-lifecycle.yaml"),
            "--policy",
            str(REPO_ROOT / "docs/versioning-and-compatibility.md"),
            "--output",
            str(self.context),
            expect_success=False,
        )

    def test_archive_inputs_reject_mixed_platform_candidate_sources(self) -> None:
        self.create_context()
        bundle = self.root / "mixed"
        sealed = bundle / "sealed-runtime" / "sealed-runtime-manifest.json"
        sealed.parent.mkdir(parents=True)
        shutil.copyfile(self.sealed_manifest, sealed)
        x64 = self.package("linux-x64", bundle / "platforms/linux-x64")
        arm64 = self.package(
            "linux-arm64",
            bundle / "platforms/linux-arm64",
            source_sha="2" * 40,
        )
        self.run_tool(
            INPUTS_TOOL,
            "assemble",
            "--context",
            str(self.context),
            "--x64-manifest",
            str(x64),
            "--arm64-manifest",
            str(arm64),
            "--sealed-manifest",
            str(sealed),
            "--bundle-digest",
            digest_bytes(b"bundle"),
            "--run-id",
            "333",
            "--run-attempt",
            "1",
            "--attempt-url",
            f"https://github.com/{REPOSITORY}/actions/runs/333/attempts/1",
            "--output",
            str(bundle / "release-candidate-archive-inputs.json"),
            expect_success=False,
        )

    def test_package_is_deterministic_strict_and_tamper_evident(self) -> None:
        first = self.root / "package-one"
        second = self.root / "package-two"
        first_manifest = self.package("linux-x64", first)
        second_manifest = self.package("linux-x64", second)
        first_value = json.loads(first_manifest.read_text(encoding="utf-8"))
        second_value = json.loads(second_manifest.read_text(encoding="utf-8"))
        self.assertEqual(first_value, second_value)
        self.assertEqual(
            (first / first_value["archive"]["path"]).read_bytes(),
            (second / second_value["archive"]["path"]).read_bytes(),
        )
        self.assertRegex(
            first_value["archive"]["path"], r"[0-9a-f]{64}\.tar\.gz$"
        )
        (first / "Aws2Azure.Proxy").write_bytes(b"tampered")
        self.run_tool(
            PACKAGE_TOOL, "validate", str(first_manifest), expect_success=False
        )

    def test_package_rejects_noncanonical_candidate_and_overwrite(self) -> None:
        output = self.root / "bad-package"
        result = self.run_tool(
            PACKAGE_TOOL,
            "package",
            "--candidate",
            "v1.2.3-rc.04",
            "--repository",
            REPOSITORY,
            "--source-sha",
            SOURCE_SHA,
            "--source-ref",
            "refs/tags/v1.2.3-rc.04",
            "--rid",
            "linux-x64",
            "--executable",
            str(self.executable),
            "--license",
            str(REPO_ROOT / "LICENSE"),
            "--config",
            str(REPO_ROOT / "docker/config.json"),
            "--output",
            str(output),
            expect_success=False,
        )
        self.assertIn("strict", result.stderr)
        self.package("linux-x64", output)
        self.run_tool(
            PACKAGE_TOOL,
            "package",
            "--candidate",
            CANDIDATE,
            "--repository",
            REPOSITORY,
            "--source-sha",
            SOURCE_SHA,
            "--source-ref",
            SOURCE_REF,
            "--rid",
            "linux-x64",
            "--executable",
            str(self.executable),
            "--license",
            str(REPO_ROOT / "LICENSE"),
            "--config",
            str(REPO_ROOT / "docker/config.json"),
            "--output",
            str(output),
            expect_success=False,
        )

    def test_archive_inputs_use_canonical_fields_and_only_real_pending_interfaces(
        self,
    ) -> None:
        self.create_context()
        bundle = self.root / "bundle"
        (bundle / "context").mkdir(parents=True)
        (bundle / "sealed-runtime").mkdir()
        shutil.copyfile(
            self.context, bundle / "context" / "release-candidate-context.json"
        )
        shutil.copyfile(
            self.sealed_manifest,
            bundle / "sealed-runtime" / "sealed-runtime-manifest.json",
        )
        x64_manifest = self.package(
            "linux-x64", bundle / "platforms" / "linux-x64"
        )
        arm64_manifest = self.package(
            "linux-arm64", bundle / "platforms" / "linux-arm64"
        )
        output = bundle / "release-candidate-archive-inputs.json"
        self.run_tool(
            INPUTS_TOOL,
            "assemble",
            "--context",
            str(bundle / "context" / "release-candidate-context.json"),
            "--x64-manifest",
            str(x64_manifest),
            "--arm64-manifest",
            str(arm64_manifest),
            "--sealed-manifest",
            str(bundle / "sealed-runtime" / "sealed-runtime-manifest.json"),
            "--bundle-digest",
            digest_bytes(b"attestation bundle"),
            "--run-id",
            "333",
            "--run-attempt",
            "2",
            "--attempt-url",
            f"https://github.com/{REPOSITORY}/actions/runs/333/attempts/2",
            "--output",
            str(output),
        )
        self.run_tool(INPUTS_TOOL, "validate", str(output))
        inputs = json.loads(output.read_text(encoding="utf-8"))
        self.assertEqual(
            set(inputs) - {"artifact_kind", "pending_interfaces", "content_digest"},
            {
                "schema_version",
                "candidate",
                "orchestration_source",
                "producer",
                "approved_ledger_source",
                "platforms",
                "workloads",
                "compatibility_policy",
            },
        )
        self.assertEqual(inputs["pending_interfaces"]["container"]["issue"], 588)
        self.assertEqual(
            inputs["pending_interfaces"]["observation_evidence"]["issue"], 582
        )
        self.assertEqual(
            inputs["platforms"][1]["sealed_runtime"]["manifest_path"],
            "sealed-runtime/sealed-runtime-manifest.json",
        )

        inputs["pending_interfaces"]["container"]["status"] = "complete"
        write_json(output, inputs)
        self.run_tool(INPUTS_TOOL, "validate", str(output), expect_success=False)

        self.run_tool(
            INPUTS_TOOL,
            "assemble",
            "--context",
            str(bundle / "context" / "release-candidate-context.json"),
            "--x64-manifest",
            str(x64_manifest),
            "--arm64-manifest",
            str(arm64_manifest),
            "--sealed-manifest",
            str(bundle / "sealed-runtime" / "sealed-runtime-manifest.json"),
            "--bundle-digest",
            digest_bytes(b"second attestation bundle"),
            "--run-id",
            "334",
            "--run-attempt",
            "1",
            "--attempt-url",
            f"https://github.com/{REPOSITORY}/actions/runs/334/attempts/1",
            "--output",
            str(bundle / "traversal-inputs.json"),
        )
        traversal_path = bundle / "traversal-inputs.json"
        traversal = json.loads(traversal_path.read_text(encoding="utf-8"))
        traversal["platforms"][0]["executable_path"] = "../../outside"
        write_json(traversal_path, traversal)
        self.run_tool(
            INPUTS_TOOL, "validate", str(traversal_path), expect_success=False
        )

    def test_workflow_enforces_trust_architecture_and_nonpublication_invariants(
        self,
    ) -> None:
        text = WORKFLOW.read_text(encoding="utf-8")
        remote_uses = re.findall(r"uses:\s+([^./\s][^@\s]+)@([^\s#]+)", text)
        self.assertTrue(remote_uses)
        for action, reference in remote_uses:
            with self.subTest(action=action):
                self.assertRegex(reference, r"^[0-9a-f]{40}$")
        self.assertEqual(text.count("dotnet publish "), 1)
        self.assertIn("-r linux-arm64", text)
        self.assertNotIn("-r linux-x64", text)
        self.assertIn("runs-on: ubuntu-24.04-arm", text)
        self.assertIn("--rid linux-arm64", text)
        self.assertIn("native arm64 runner", text)
        self.assertEqual(text.count("ref: ${{ github.sha }}"), 3)
        self.assertEqual(text.count("path: orchestration"), 3)
        self.assertEqual(text.count("path: candidate-source"), 2)
        self.assertIn("ref: ${{ steps.trust.outputs.candidate_sha }}", text)
        self.assertIn("ref: ${{ needs.linux-x64.outputs.candidate_sha }}", text)
        self.assertIn("working-directory: candidate-source", text)
        self.assertIn("uses: ./orchestration/.github/actions/dotnet-setup", text)
        self.assertNotIn("uses: ./candidate-source/", text)
        self.assertIn("approved-ledger-compare.json", text)
        self.assertIn("gh api --paginate --slurp", text)
        self.assertIn("compact-tag-rulesets.json", text)
        self.assertIn(
            "orchestration/eng/resolve-release-candidate-rulesets.sh",
            text,
        )
        self.assertIn("dispatch ref must be protected main, never the candidate tag", (
            REPO_ROOT / "eng" / "validate-release-candidate-ref.sh"
        ).read_text(encoding="utf-8"))
        self.assertIn(
            "--orchestration-sha \"$ORCHESTRATION_SHA\"",
            text,
        )
        self.assertIn("--source-sha \"$CANDIDATE_SHA\"", text)
        self.assertIn(
            "orchestration/eng/validate-release-candidate-checkouts.sh",
            text,
        )
        self.assertNotIn("approved-ledgers", text)
        self.assertIn("persist-credentials: false", text)
        self.assertIn("overwrite: false", text)
        self.assertIn("actions/attest-build-provenance@", text)
        self.assertIn("actions/download-artifact@", text)
        self.assertIn("context_hex", text)
        self.assertIn("archive_hex", text)
        self.assertIn("digest_hex", text)
        self.assertNotIn("packages: write", text)
        self.assertNotIn("contents: write", text)
        self.assertNotIn("gh release", text)
        self.assertNotIn("release: published", text)
        self.assertIn("#588", text)
        self.assertIn("#582", text)
        stable_text = STABLE_WORKFLOW.read_text(encoding="utf-8")
        self.assertIn("'!v*.*.*-rc.*'", stable_text)
        self.assertIn("only accepts stable vMAJOR.MINOR.PATCH tags", stable_text)
        self.assertIn('"refs/tags/$REF"', stable_text)
        self.assertIn("github.ref_name == inputs.tag", stable_text)
        self.assertNotIn(
            'REF="${{ github.event.inputs.tag || github.ref_name }}"', stable_text
        )
        self.assertNotIn(
            'TAG="${{ github.event.inputs.tag || github.ref_name }}"', stable_text
        )
        self.assertEqual(stable_text.count("RELEASE_TAG:"), 2)


if __name__ == "__main__":
    unittest.main(verbosity=2)
