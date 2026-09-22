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
import textwrap
import unittest

from release_profile_coverage import (
    approved_ledger_filename, required_profiles, resolved_identity_filename,
)

REPO_ROOT = pathlib.Path(__file__).resolve().parent.parent
PACKAGE_TOOL = REPO_ROOT / "eng" / "release-candidate-package.py"
INPUTS_TOOL = REPO_ROOT / "eng" / "release-candidate-inputs.py"
WORKFLOW = REPO_ROOT / ".github" / "workflows" / "release-candidate.yml"
STABLE_WORKFLOW = REPO_ROOT / ".github" / "workflows" / "release.yml"
CONTAINER_WORKFLOW = REPO_ROOT / ".github" / "workflows" / "container.yml"
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
        for profile in sorted(required_profiles()):
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
                    "created_at": "2026-07-17T18:01:00+00:00",
                    "expires_at": "2099-07-17T18:01:00+00:00",
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

    def profile_arguments(self, profiles: list[str] | None = None) -> list[str]:
        return [
            value for profile in (sorted(self.ledgers) if profiles is None else profiles)
            for value in ("--profile-input", profile, str(self.ledgers[profile]),
                          str(REPO_ROOT / f"docs/workloads/{profile}.yaml"))
        ]

    def create_context(self, *, expect_success: bool = True,
                       profiles: list[str] | None = None) -> subprocess.CompletedProcess[str]:
        return self.run_tool(
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
            *self.profile_arguments(profiles),
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

    def test_context_binds_four_ledgers_profiles_and_policy(self) -> None:
        self.create_context()
        self.run_tool(INPUTS_TOOL, "validate-context", str(self.context))
        context = json.loads(self.context.read_text(encoding="utf-8"))
        self.assertEqual(
            [item["profile"]["id"] for item in context["workloads"]],
            ["dynamodb-basic-crud", "s3-basic-object-crud",
             "secretsmanager-basic-lifecycle", "sqs-standard-messaging"],
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
            *self.profile_arguments(),
            "--policy",
            str(REPO_ROOT / "docs/versioning-and-compatibility.md"),
            "--output",
            str(self.context),
            expect_success=False,
        )

    def test_each_required_profile_is_unique_approved_unexpired_and_exact(self) -> None:
        profiles = sorted(self.ledgers)
        mutations = {
            ("profile", "id"): "unadvertised-profile",
            ("profile", "version"): 2,
            ("eligibility", "promotion_eligible"): False,
            ("runtime", "source_sha"): "e" * 40,
            ("runtime", "aggregate_digest"): digest_bytes(b"other build"),
            ("runtime", "executable_digest"): digest_bytes(b"other executable"),
            ("producer", "run_id"): 999,
            ("producer", "run_attempt"): 3,
            ("artifact", "id"): 999,
            ("artifact", "upload_digest"): digest_bytes(b"other upload"),
            ("artifact", "expires_at"): "2000-01-01T00:00:00Z",
            ("artifact", "created_at"): "2026-07-17T18:01:00",
            ("attestation", "manifest_subject_digest"): digest_bytes(b"other manifest"),
        }
        for profile in profiles:
            with self.subTest(profile=profile, case="missing"):
                result = self.create_context(
                    profiles=[item for item in profiles if item != profile], expect_success=False)
                self.assertIn("uniquely cover", result.stderr)
            with self.subTest(profile=profile, case="duplicate"):
                result = self.create_context(profiles=profiles + [profile], expect_success=False)
                self.assertIn("uniquely cover", result.stderr)
            for (section, key), value in mutations.items():
                with self.subTest(profile=profile, field=f"{section}.{key}"):
                    ledger = self.make_ledger(profile)
                    ledger["record"][section][key] = value
                    if key == "expires_at":
                        ledger["record"]["artifact"]["created_at"] = "1999-01-01T00:00:00Z"
                    write_json(self.ledgers[profile], ledger)
                    result = self.create_context(expect_success=False)
                    if key == "expires_at":
                        self.assertIn("expired", result.stderr)
                    self.assertFalse(self.context.exists())
            write_json(self.ledgers[profile], self.make_ledger(profile))
        self.create_context()

    def test_context_revalidation_rejects_rehashed_profile_coverage_drift(self) -> None:
        self.create_context()
        original = self.context.read_bytes()
        for profile in sorted(self.ledgers):
            for mutation in ("missing", "duplicate", "substituted", "version"):
                with self.subTest(profile=profile, mutation=mutation):
                    context = json.loads(original)
                    workload = next(item for item in context["workloads"] if item["profile"]["id"] == profile)
                    if mutation == "missing":
                        context["workloads"].remove(workload)
                    elif mutation == "duplicate":
                        context["workloads"].append(workload)
                    elif mutation == "substituted":
                        workload["profile"]["id"] = "unadvertised-profile"
                    else:
                        workload["profile"]["version"] = 2
                        workload["approved_runtime"]["profile"]["version"] = 2
                    body = {key: value for key, value in context.items() if key != "content_digest"}
                    context["content_digest"] = digest_bytes(
                        json.dumps(body, sort_keys=True, separators=(",", ":"), ensure_ascii=False).encode())
                    write_json(self.context, context)
                    result = self.run_tool(INPUTS_TOOL, "validate-context", str(self.context), expect_success=False)
                    self.assertNotIn("digest does not match", result.stderr)

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

    def test_actual_workflow_steps_export_and_resolve_every_profile_fail_closed(self) -> None:
        workspace = self.root / "workflow"
        orchestration = workspace / "orchestration"
        tools = orchestration / "eng"
        tools.mkdir(parents=True)
        (workspace / "artifacts/trust").mkdir(parents=True)
        for name in ("release-candidate-inputs.py", "release_profile_coverage.py"):
            shutil.copyfile(REPO_ROOT / "eng" / name, tools / name)
        for relative in (
            "docs/site/workload-ga.json", "docs/versioning-and-compatibility.md",
            *(f"docs/workloads/{profile}.yaml" for profile in sorted(self.ledgers)),
        ):
            destination = orchestration / relative
            destination.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(REPO_ROOT / relative, destination)
        bin_dir = workspace / "bin"
        bin_dir.mkdir()
        dotnet = bin_dir / "dotnet"
        dotnet.write_text(textwrap.dedent("""\
            #!/usr/bin/env python3
            import os, pathlib, shutil, sys
            args = sys.argv
            profile = args[args.index("--profile") + 1]
            if profile == os.environ.get("FAIL_EXPORT_PROFILE"):
                raise SystemExit("offline exporter failure")
            shutil.copyfile(pathlib.Path(os.environ["LEDGER_INPUT"]) / (profile + "-ledger.json"),
                            args[args.index("--output") + 1])
            """))
        dotnet.chmod(0o755)
        resolver = tools / "resolve-sealed-runtime.sh"
        resolver.write_text(textwrap.dedent("""\
            #!/usr/bin/env python3
            import json, os, pathlib, shutil, sys
            args = dict(zip(sys.argv[1::2], sys.argv[2::2]))
            with open(os.environ["RESOLVER_LOG"], "a") as log:
                log.write(json.dumps(args) + "\\n")
            if args["--profile"] == os.environ.get("FAIL_RESOLVE_PROFILE"):
                raise SystemExit("offline resolver failure")
            bundle = pathlib.Path(args["--destination"]) / "bundle"
            bundle.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(os.environ["SEALED_INPUT"], bundle / "sealed-runtime-manifest.json")
            pathlib.Path(args["--identity-output"]).write_text(json.dumps(args))
            """))
        resolver.chmod(0o755)
        env = {
            **os.environ, "PATH": f"{bin_dir}:{os.environ['PATH']}",
            "LEDGER_INPUT": str(self.root / "input"),
            "RESOLVER_LOG": str(workspace / "resolver.jsonl"),
            "SEALED_INPUT": str(self.sealed_manifest),
            "GITHUB_WORKSPACE": str(workspace), "GITHUB_ENV": str(workspace / "github-env"),
            "GITHUB_REPOSITORY": REPOSITORY, "CANDIDATE": CANDIDATE,
            "CANDIDATE_SHA": SOURCE_SHA, "ORCHESTRATION_SHA": ORCHESTRATION_SHA,
        }
        workflow = WORKFLOW.read_text()

        def run_step(name: str, overrides: dict[str, str] | None = None):
            section = workflow.split(f"      - name: {name}\n", 1)[1].split("\n      - ", 1)[0]
            script = textwrap.dedent(section.split("        run: |\n", 1)[1])
            return subprocess.run(
                ["bash", "-c", script], cwd=workspace, env={**env, **(overrides or {})},
                text=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE, check=False)

        export_step = "Export and bind all required GA runtime ledgers"
        resolve_step = "Resolve exact approved sealed linux-x64 bytes"
        for name in (export_step, resolve_step):
            result = run_step(name)
            self.assertEqual(result.returncode, 0, result.stderr)
        calls = [json.loads(line) for line in (workspace / "resolver.jsonl").read_text().splitlines()]
        self.assertEqual([call["--profile"] for call in calls], sorted(required_profiles()))
        for call in calls:
            profile = call["--profile"]
            self.assertEqual(call["--profile-version"], "1")
            self.assertEqual(call["--artifact-id"], "987654")
            self.assertEqual(call["--run-id"], "123456")
            self.assertEqual(call["--run-attempt"], "2")
            self.assertEqual(call["--expected-sha"], SOURCE_SHA)
            self.assertEqual(call["--expected-ref"], "refs/heads/main")
            self.assertEqual(call["--ledger-json"], f"artifacts/rc-x64/context/{approved_ledger_filename(profile)}")
            self.assertEqual(call["--identity-output"], f"artifacts/rc-x64/context/{resolved_identity_filename(profile)}")
            self.assertTrue((workspace / call["--identity-output"]).is_file())
        result = run_step(resolve_step, {"FAIL_RESOLVE_PROFILE": "dynamodb-basic-crud"})
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("offline resolver failure", result.stderr)
        self.assertEqual(len((workspace / "resolver.jsonl").read_text().splitlines()), 5)
        context = workspace / "artifacts/rc-x64/context/release-candidate-context.json"
        context.unlink()
        result = run_step(export_step, {"FAIL_EXPORT_PROFILE": "sqs-standard-messaging"})
        self.assertNotEqual(result.returncode, 0)
        self.assertFalse(context.exists())
        certification = orchestration / "docs/site/workload-ga.json"
        original_rows = json.loads(certification.read_bytes())
        for mutation in ("new-ga", "missing-required", "new-version"):
            with self.subTest(certification=mutation):
                rows = json.loads(json.dumps(original_rows))
                if mutation == "new-ga":
                    rows.append({"schema_version": 1, "profile_id": "new-ga-profile",
                                 "profile_version": 1, "verdict": "ga"})
                elif mutation == "missing-required":
                    rows = [row for row in rows if row["profile_id"] != "dynamodb-basic-crud"]
                else:
                    next(row for row in rows if row["profile_id"] == "sqs-standard-messaging")["profile_version"] = 2
                write_json(certification, rows)
                result = run_step(export_step)
                self.assertNotEqual(result.returncode, 0)
                self.assertIn("release-profile-coverage:", result.stderr)
                self.assertFalse(context.exists())

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
        self.assertIn("python3 orchestration/eng/release_profile_coverage.py", text)
        self.assertEqual(text.count("done < artifacts/trust/required-profiles.tsv"), 2)
        self.assertIn('--profile-input "$profile"', text)
        self.assertIn('--profile "$profile"', text)
        self.assertIn('--profile-version "$profile_version"', text)
        self.assertIn('--ledger-json "artifacts/rc-x64/context/$ledger"', text)
        self.assertIn('--identity-output "artifacts/rc-x64/context/$identity"', text)
        self.assertNotIn("--s3-ledger", text)
        self.assertIn("archive_hex", text)
        self.assertIn("digest_hex", text)
        self.assertNotIn("packages: write", text)
        self.assertNotIn("contents: write", text)
        self.assertNotIn("gh release", text)
        self.assertNotIn("release: published", text)
        self.assertIn("#588", text)
        self.assertIn("#582", text)
        stable_text = STABLE_WORKFLOW.read_text(encoding="utf-8")
        self.assertIn("'v0.*.*'", stable_text)
        self.assertIn("'!v0.*.*-rc.*'", stable_text)
        self.assertIn("only accepts stable v0.MINOR.PATCH tags", stable_text)
        self.assertNotIn("'v*.*.*'", stable_text)
        self.assertIn("build:\n    needs: guard", stable_text)
        self.assertIn('"refs/tags/$REF"', stable_text)
        self.assertIn("github.ref_name == inputs.tag", stable_text)
        self.assertNotIn(
            'REF="${{ github.event.inputs.tag || github.ref_name }}"', stable_text
        )
        self.assertNotIn(
            'TAG="${{ github.event.inputs.tag || github.ref_name }}"', stable_text
        )
        self.assertEqual(stable_text.count("RELEASE_TAG:"), 3)
        container_text = CONTAINER_WORKFLOW.read_text(encoding="utf-8")
        self.assertNotIn("release:\n    types: [published]", container_text)


if __name__ == "__main__":
    unittest.main(verbosity=2)
