#!/usr/bin/env python3
"""Tests for immutable GHCR RC images built from released archives."""

from __future__ import annotations

import hashlib
import json
import os
import pathlib
import re
import shutil
import struct
import subprocess
import unittest


REPO_ROOT = pathlib.Path(__file__).resolve().parent.parent
IMAGE_TOOL = REPO_ROOT / "eng" / "release-candidate-image.py"
INPUTS_TOOL = REPO_ROOT / "eng" / "release-candidate-inputs.py"
PACKAGE_TOOL = REPO_ROOT / "eng" / "release-candidate-package.py"
WORKFLOW = REPO_ROOT / ".github" / "workflows" / "release-candidate-image.yml"
DOCKERFILE = REPO_ROOT / "docker" / "release-candidate" / "Dockerfile"
CANDIDATE = "v1.2.3-rc.4"
REPOSITORY = "pedrosakuma/aws2azure"
SOURCE_SHA = "0123456789abcdef0123456789abcdef01234567"
ORCHESTRATION_SHA = "1123456789abcdef0123456789abcdef01234567"
APPROVAL_SHA = ORCHESTRATION_SHA
RUN_ID = 333
RUN_ATTEMPT = 2
BASES = {
    "linux/amd64": (
        "sha256:481d8747c961286738b6ce814c89de840bbc018330283bc54e8d29484ee88b16"
    ),
    "linux/arm64": (
        "sha256:84fc5eb352e49b24564ff085ece8de373ed14d019a1d0e9f6d1103ea00c43454"
    ),
}


def digest_bytes(value: bytes) -> str:
    return f"sha256:{hashlib.sha256(value).hexdigest()}"


def digest_file(path: pathlib.Path) -> str:
    return digest_bytes(path.read_bytes())


def canonical_bytes(value: object) -> bytes:
    return (
        json.dumps(value, sort_keys=True, indent=2, ensure_ascii=False) + "\n"
    ).encode("utf-8")


def content_digest(value: dict[str, object]) -> str:
    body = {key: item for key, item in value.items() if key != "content_digest"}
    return digest_bytes(
        json.dumps(
            body, sort_keys=True, separators=(",", ":"), ensure_ascii=False
        ).encode("utf-8")
    )


def write_json(path: pathlib.Path, value: object) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(canonical_bytes(value))


def elf(machine: int, marker: bytes) -> bytes:
    header = bytearray(64)
    header[:4] = b"\x7fELF"
    header[4] = 2
    header[5] = 1
    header[6] = 1
    struct.pack_into("<H", header, 16, 3)
    struct.pack_into("<H", header, 18, machine)
    struct.pack_into("<I", header, 20, 1)
    return bytes(header) + marker


class ReleaseCandidateImageTests(unittest.TestCase):
    def setUp(self) -> None:
        self.root = (
            REPO_ROOT
            / "artifacts"
            / f"test-release-candidate-image-{os.getpid()}-{self._testMethodName}"
        )
        shutil.rmtree(self.root, ignore_errors=True)
        self.root.mkdir(parents=True)

    def tearDown(self) -> None:
        shutil.rmtree(self.root, ignore_errors=True)
        try:
            (REPO_ROOT / "artifacts").rmdir()
        except OSError:
            pass

    def run_tool(
        self,
        tool: pathlib.Path,
        *arguments: str,
        expect_success: bool = True,
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

    def make_ledger(
        self,
        profile: str,
        executable_digest: str,
        sealed_manifest_digest: str,
    ) -> dict[str, object]:
        run_id = 123456
        run_attempt = 2
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
                    "run_url": (
                        f"https://github.com/{REPOSITORY}/actions/runs/{run_id}"
                    ),
                },
                "artifact": {
                    "id": 987654,
                    "name": (
                        "aws2azure-sealed-linux-x64-"
                        f"{'a' * 64}-run-{run_id}-attempt-{run_attempt}"
                    ),
                    "upload_digest": digest_bytes(b"sealed artifact"),
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
                    "manifest_subject_digest": sealed_manifest_digest,
                },
            },
        }

    def write_complete_checksums(self, bundle: pathlib.Path) -> None:
        checksum = bundle / "SHA256SUMS.all"
        if checksum.exists():
            checksum.unlink()
        files = sorted(
            path
            for path in bundle.rglob("*")
            if path.is_file() and not path.is_symlink()
        )
        checksum.write_text(
            "".join(
                f"{digest_file(path).removeprefix('sha256:')}  "
                f"./{path.relative_to(bundle).as_posix()}\n"
                for path in files
            ),
            encoding="ascii",
        )

    def create_bundle(
        self, *, swap_architectures: bool = False
    ) -> tuple[pathlib.Path, pathlib.Path, pathlib.Path]:
        bundle = self.root / "bundle"
        input_dir = self.root / "input"
        input_dir.mkdir(parents=True)
        x64 = input_dir / "x64"
        arm64 = input_dir / "arm64"
        x64.write_bytes(elf(62, b"x64 release candidate"))
        arm64.write_bytes(elf(183, b"arm64 release candidate"))
        x64.chmod(0o755)
        arm64.chmod(0o755)
        packaged_x64 = arm64 if swap_architectures else x64
        packaged_arm64 = x64 if swap_architectures else arm64

        sealed = input_dir / "sealed-runtime-manifest.json"
        sealed.write_text('{"sealed":"runtime"}\n', encoding="utf-8")
        sealed_digest = digest_file(sealed)
        executable_digest = digest_file(packaged_x64)
        ledgers: dict[str, pathlib.Path] = {}
        for profile in ("s3-basic-object-crud", "secretsmanager-basic-lifecycle"):
            ledger = input_dir / f"{profile}.json"
            write_json(
                ledger,
                self.make_ledger(profile, executable_digest, sealed_digest),
            )
            ledgers[profile] = ledger

        context = bundle / "context" / "release-candidate-context.json"
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
            f"refs/tags/{CANDIDATE}",
            "--orchestration-sha",
            ORCHESTRATION_SHA,
            "--approval-sha",
            APPROVAL_SHA,
            "--s3-ledger",
            str(ledgers["s3-basic-object-crud"]),
            "--s3-profile",
            str(REPO_ROOT / "docs/workloads/s3-basic-object-crud.yaml"),
            "--secrets-ledger",
            str(ledgers["secretsmanager-basic-lifecycle"]),
            "--secrets-profile",
            str(REPO_ROOT / "docs/workloads/secretsmanager-basic-lifecycle.yaml"),
            "--policy",
            str(REPO_ROOT / "docs/versioning-and-compatibility.md"),
            "--output",
            str(context),
        )
        shutil.copyfile(
            ledgers["s3-basic-object-crud"],
            bundle / "context" / "s3-approved-runtime.json",
        )
        shutil.copyfile(
            ledgers["secretsmanager-basic-lifecycle"],
            bundle / "context" / "secretsmanager-approved-runtime.json",
        )
        sealed_output = bundle / "sealed-runtime" / "sealed-runtime-manifest.json"
        sealed_output.parent.mkdir()
        shutil.copyfile(sealed, sealed_output)

        manifests: dict[str, pathlib.Path] = {}
        for rid, executable in (
            ("linux-x64", packaged_x64),
            ("linux-arm64", packaged_arm64),
        ):
            output = bundle / "platforms" / rid
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
                f"refs/tags/{CANDIDATE}",
                "--rid",
                rid,
                "--executable",
                str(executable),
                "--license",
                str(REPO_ROOT / "LICENSE"),
                "--config",
                str(REPO_ROOT / "docker/config.json"),
                "--output",
                str(output),
            )
            manifests[rid] = output / "platform-manifest.json"

        provenance = bundle / "provenance" / "archive-payload-provenance.json"
        provenance.parent.mkdir()
        provenance.write_text('{"trusted":"bundle"}\n', encoding="utf-8")
        archive_inputs = bundle / "release-candidate-archive-inputs.json"
        self.run_tool(
            INPUTS_TOOL,
            "assemble",
            "--context",
            str(context),
            "--x64-manifest",
            str(manifests["linux-x64"]),
            "--arm64-manifest",
            str(manifests["linux-arm64"]),
            "--sealed-manifest",
            str(sealed_output),
            "--bundle-digest",
            digest_file(provenance),
            "--run-id",
            str(RUN_ID),
            "--run-attempt",
            str(RUN_ATTEMPT),
            "--attempt-url",
            f"https://github.com/{REPOSITORY}/actions/runs/{RUN_ID}/attempts/{RUN_ATTEMPT}",
            "--output",
            str(archive_inputs),
        )
        self.write_complete_checksums(bundle)
        archive_content_digest = json.loads(
            archive_inputs.read_text(encoding="utf-8")
        )["content_digest"]
        artifact_name = (
            f"aws2azure-rc-archives-{CANDIDATE}-"
            f"{archive_content_digest.removeprefix('sha256:')}-run-{RUN_ID}-"
            f"attempt-{RUN_ATTEMPT}"
        )
        selection: dict[str, object] = {
            "schema_version": 1,
            "artifact_kind": "release_candidate_archive_selection",
            "candidate": {
                "identifier": CANDIDATE,
                "source": {
                    "repository": REPOSITORY,
                    "sha": SOURCE_SHA,
                    "ref": f"refs/tags/{CANDIDATE}",
                },
            },
            "producer": {
                "workflow": ".github/workflows/release-candidate.yml",
                "event_name": "workflow_dispatch",
                "run_id": RUN_ID,
                "run_attempt": RUN_ATTEMPT,
                "attempt_url": (
                    f"https://github.com/{REPOSITORY}/actions/runs/{RUN_ID}/"
                    f"attempts/{RUN_ATTEMPT}"
                ),
                "run_started_at": "2026-07-18T10:00:00Z",
                "source_sha": ORCHESTRATION_SHA,
                "source_ref": "refs/heads/main",
            },
            "artifact": {
                "id": 7654321,
                "name": artifact_name,
                "upload_digest": digest_bytes(b"github artifact zip"),
                "created_at": "2026-07-18T10:10:00Z",
                "expires_at": "2099-07-18T10:10:00Z",
            },
            "archive_input_content_digest": archive_content_digest,
        }
        selection["content_digest"] = content_digest(selection)
        selection_path = self.root / "selection.json"
        write_json(selection_path, selection)
        identity_path = self.root / "resolved-identity.json"
        return bundle, selection_path, identity_path

    def validate_bundle(
        self, bundle: pathlib.Path, selection: pathlib.Path, output: pathlib.Path,
        *, expect_success: bool = True
    ) -> subprocess.CompletedProcess[str]:
        return self.run_tool(
            IMAGE_TOOL,
            "validate-bundle",
            "--bundle",
            str(bundle),
            "--selection",
            str(selection),
            "--output",
            str(output),
            expect_success=expect_success,
        )

    def make_attestation(
        self, identity: dict[str, object], kind: str
    ) -> dict[str, object]:
        key = "payload" if kind == "payload" else "archive_inputs"
        subjects = [
            {
                "name": item["name"],
                "digest": {
                    "sha256": item["digest"].removeprefix("sha256:")
                },
            }
            for item in identity["attestation_subjects"][key]
        ]
        source = identity["candidate"]["source"]
        producer = identity["producer"]
        attempt_url = identity["producer"]["attempt_url"]
        return {
            "verificationResult": {
                "signature": {
                    "certificate": {
                        "githubWorkflowTrigger": "workflow_dispatch",
                        "githubWorkflowRepository": source["repository"],
                        "githubWorkflowRef": producer["source_ref"],
                        "githubWorkflowSHA": producer["source_sha"],
                        "sourceRepositoryDigest": producer["source_sha"],
                        "sourceRepositoryRef": producer["source_ref"],
                        "runInvocationURI": attempt_url,
                    }
                },
                "statement": {
                    "predicateType": "https://slsa.dev/provenance/v1",
                    "subject": subjects,
                    "predicate": {
                        "runDetails": {
                            "metadata": {"invocationId": attempt_url}
                        }
                    },
                },
            }
        }

    def image_inspect(
        self, identity: dict[str, object], platform: str
    ) -> list[dict[str, object]]:
        platform_identity = next(
            item for item in identity["platforms"] if item["platform"] == platform
        )
        architecture = platform.removeprefix("linux/")
        labels = {
            "org.opencontainers.image.source": f"https://github.com/{REPOSITORY}",
            "org.opencontainers.image.description": (
                "AWS to Azure transparent protocol proxy"
            ),
            "org.opencontainers.image.licenses": "MIT",
            "org.opencontainers.image.version": CANDIDATE,
            "org.opencontainers.image.revision": SOURCE_SHA,
            "org.opencontainers.image.base.name": (
                "mcr.microsoft.com/dotnet/runtime-deps:10.0-noble-chiseled"
            ),
            "org.opencontainers.image.base.digest": BASES[platform],
            "io.aws2azure.release-candidate.archive-content-digest": (
                identity["archive_input"]["content_digest"]
            ),
        }
        return [
            {
                "Os": "linux",
                "Architecture": architecture,
                "Config": {
                    "User": "1654",
                    "WorkingDir": "/app",
                    "Entrypoint": ["/app/Aws2Azure.Proxy"],
                    "Env": ["ASPNETCORE_URLS=http://+:8080"],
                    "ExposedPorts": {"8080/tcp": {}},
                    "Healthcheck": {
                        "Test": [
                            "CMD",
                            "/app/Aws2Azure.Proxy",
                            "--health-check",
                        ],
                        "Interval": 30_000_000_000,
                        "Timeout": 3_000_000_000,
                        "StartPeriod": 5_000_000_000,
                        "Retries": 3,
                    },
                    "Labels": labels,
                },
                "RootFS": {
                    "Type": "layers",
                    "Layers": [
                        digest_bytes(f"{platform}-base-layer".encode()),
                        digest_bytes(f"{platform}-binary-layer".encode()),
                    ],
                },
                "Marker": platform_identity["executable"]["sha256"],
            }
        ]

    def create_platform_results(
        self, identity_path: pathlib.Path
    ) -> tuple[pathlib.Path, dict[str, str]]:
        identity = json.loads(identity_path.read_text(encoding="utf-8"))
        results = self.root / "results"
        results.mkdir()
        digests: dict[str, str] = {}
        for platform in ("linux/amd64", "linux/arm64"):
            arch = platform.removeprefix("linux/")
            manifest = self.root / f"{arch}-manifest.json"
            manifest.write_text(
                json.dumps(
                    {
                        "schemaVersion": 2,
                        "mediaType": (
                            "application/vnd.oci.image.manifest.v1+json"
                        ),
                        "config": {
                            "mediaType": (
                                "application/vnd.oci.image.config.v1+json"
                            ),
                            "digest": digest_bytes(f"{arch}-config".encode()),
                            "size": 10,
                        },
                        "layers": [
                            {
                                "mediaType": (
                                    "application/vnd.oci.image.layer.v1.tar+gzip"
                                ),
                                "digest": digest_bytes(f"{arch}-layer".encode()),
                                "size": 20,
                            }
                        ],
                    },
                    separators=(",", ":"),
                ),
                encoding="utf-8",
            )
            digest = digest_file(manifest)
            digests[platform] = digest
            inspect = self.root / f"{arch}-inspect.json"
            write_json(inspect, self.image_inspect(identity, platform))
            executable_digest = next(
                item["executable"]["sha256"]
                for item in identity["platforms"]
                if item["platform"] == platform
            )
            tag = f"{CANDIDATE}-{arch}-{executable_digest.removeprefix('sha256:')}"
            self.run_tool(
                IMAGE_TOOL,
                "write-platform-result",
                "--identity",
                str(identity_path),
                "--platform",
                platform,
                "--repository",
                f"ghcr.io/{REPOSITORY}",
                "--tag",
                tag,
                "--image-digest",
                digest,
                "--base-digest",
                BASES[platform],
                "--manifest",
                str(manifest),
                "--inspect",
                str(inspect),
                "--output",
                str(results / f"linux-{arch}.json"),
            )
        return results, digests

    def write_index(
        self, path: pathlib.Path, digests: dict[str, str], platforms: list[str]
    ) -> str:
        value = {
            "schemaVersion": 2,
            "mediaType": "application/vnd.oci.image.index.v1+json",
            "manifests": [
                {
                    "mediaType": "application/vnd.oci.image.manifest.v1+json",
                    "digest": digests.get(platform, digest_bytes(platform.encode())),
                    "size": 100,
                    "platform": {
                        "architecture": platform.removeprefix("linux/"),
                        "os": "linux",
                    },
                }
                for platform in platforms
            ],
        }
        path.write_text(json.dumps(value, separators=(",", ":")), encoding="utf-8")
        return digest_file(path)

    def test_selection_binds_exact_run_attempt_name_and_digest(self) -> None:
        archive_digest = digest_bytes(b"archive inputs")
        artifact_name = (
            f"aws2azure-rc-archives-{CANDIDATE}-"
            f"{archive_digest.removeprefix('sha256:')}-run-{RUN_ID}-"
            f"attempt-{RUN_ATTEMPT}"
        )
        run = {
            "id": RUN_ID,
            "run_attempt": RUN_ATTEMPT,
            "event": "workflow_dispatch",
            "status": "completed",
            "conclusion": "success",
            "path": ".github/workflows/release-candidate.yml",
            "head_sha": ORCHESTRATION_SHA,
            "head_branch": "main",
            "run_started_at": "2026-07-18T10:00:00Z",
            "repository": {"full_name": REPOSITORY},
            "head_repository": {"full_name": REPOSITORY},
        }
        artifact = {
            "id": 7654321,
            "name": artifact_name,
            "digest": digest_bytes(b"artifact zip"),
            "expired": False,
            "created_at": "2026-07-18T10:10:00Z",
            "expires_at": "2099-07-18T10:10:00Z",
            "workflow_run": {"id": RUN_ID, "head_sha": ORCHESTRATION_SHA},
        }
        run_path = self.root / "run.json"
        artifact_path = self.root / "artifact.json"
        main_branch_path = self.root / "main-branch.json"
        main_compare_path = self.root / "main-compare.json"
        write_json(run_path, run)
        write_json(artifact_path, artifact)
        main_branch = {
            "name": "main",
            "protected": True,
            "commit": {"sha": ORCHESTRATION_SHA},
        }
        main_compare = {
            "status": "identical",
            "base_commit": {"sha": ORCHESTRATION_SHA},
            "merge_base_commit": {"sha": ORCHESTRATION_SHA},
            "head_commit": {"sha": ORCHESTRATION_SHA},
        }
        write_json(main_branch_path, main_branch)
        write_json(main_compare_path, main_compare)
        output = self.root / "selection.json"
        arguments = (
            "validate-selection",
            "--repository",
            REPOSITORY,
            "--candidate",
            CANDIDATE,
            "--source-sha",
            SOURCE_SHA,
            "--workflow-source-sha",
            ORCHESTRATION_SHA,
            "--run-id",
            str(RUN_ID),
            "--run-attempt",
            str(RUN_ATTEMPT),
            "--artifact-id",
            "7654321",
            "--artifact-name",
            artifact_name,
            "--artifact-digest",
            artifact["digest"],
            "--archive-content-digest",
            archive_digest,
            "--run-json",
            str(run_path),
            "--artifact-json",
            str(artifact_path),
            "--main-branch-json",
            str(main_branch_path),
            "--main-compare-json",
            str(main_compare_path),
            "--output",
            str(output),
        )
        self.run_tool(IMAGE_TOOL, *arguments)
        selection = json.loads(output.read_text(encoding="utf-8"))
        self.assertEqual(selection["producer"]["run_attempt"], RUN_ATTEMPT)
        self.assertEqual(selection["artifact"]["upload_digest"], artifact["digest"])
        self.assertEqual(selection["candidate"]["source"]["sha"], SOURCE_SHA)
        self.assertEqual(selection["producer"]["source_sha"], ORCHESTRATION_SHA)
        self.assertEqual(selection["producer"]["source_ref"], "refs/heads/main")

        output.unlink()
        run["head_sha"] = SOURCE_SHA
        run["head_branch"] = None
        write_json(run_path, run)
        self.run_tool(IMAGE_TOOL, *arguments, expect_success=False)

        run["head_sha"] = ORCHESTRATION_SHA
        run["head_branch"] = "main"
        run["run_attempt"] = 1
        write_json(run_path, run)
        self.run_tool(IMAGE_TOOL, *arguments, expect_success=False)

        run["run_attempt"] = RUN_ATTEMPT
        write_json(run_path, run)
        malicious = artifact_name + "$(touch injected)"
        bad_arguments = list(arguments)
        bad_arguments[bad_arguments.index(artifact_name)] = malicious
        self.run_tool(IMAGE_TOOL, *bad_arguments, expect_success=False)
        self.assertFalse((self.root / "injected").exists())

        main_branch["protected"] = False
        write_json(main_branch_path, main_branch)
        self.run_tool(IMAGE_TOOL, *arguments, expect_success=False)
        main_branch["protected"] = True
        write_json(main_branch_path, main_branch)

        main_compare["status"] = "diverged"
        main_compare["merge_base_commit"]["sha"] = SOURCE_SHA
        write_json(main_compare_path, main_compare)
        self.run_tool(IMAGE_TOOL, *arguments, expect_success=False)

    def test_protected_main_source_rejects_unprotected_and_diverged(self) -> None:
        branch_path = self.root / "protected-main.json"
        compare_path = self.root / "protected-main-compare.json"
        branch = {
            "name": "main",
            "protected": True,
            "commit": {"sha": ORCHESTRATION_SHA},
        }
        comparison = {
            "status": "identical",
            "base_commit": {"sha": ORCHESTRATION_SHA},
            "merge_base_commit": {"sha": ORCHESTRATION_SHA},
            "head_commit": {"sha": ORCHESTRATION_SHA},
        }
        write_json(branch_path, branch)
        write_json(compare_path, comparison)
        arguments = (
            "validate-protected-main",
            "--source-sha",
            ORCHESTRATION_SHA,
            "--main-branch-json",
            str(branch_path),
            "--main-compare-json",
            str(compare_path),
        )
        self.run_tool(IMAGE_TOOL, *arguments)

        branch["protected"] = False
        write_json(branch_path, branch)
        self.run_tool(IMAGE_TOOL, *arguments, expect_success=False)
        branch["protected"] = True
        write_json(branch_path, branch)

        comparison["status"] = "diverged"
        comparison["merge_base_commit"]["sha"] = SOURCE_SHA
        write_json(compare_path, comparison)
        self.run_tool(IMAGE_TOOL, *arguments, expect_success=False)

    def test_shell_consumer_rejects_injection_before_network_or_file_effects(self) -> None:
        injected = self.root / "injected"
        destination = self.root / "destination"
        result = subprocess.run(
            [
                "bash",
                str(REPO_ROOT / "eng" / "resolve-release-candidate-archives.sh"),
                "--repository",
                REPOSITORY,
                "--candidate",
                CANDIDATE,
                "--source-sha",
                SOURCE_SHA,
                "--workflow-source-sha",
                ORCHESTRATION_SHA,
                "--run-id",
                str(RUN_ID),
                "--run-attempt",
                str(RUN_ATTEMPT),
                "--artifact-id",
                "7654321",
                "--artifact-name",
                f"safe$(touch {injected})",
                "--artifact-digest",
                digest_bytes(b"artifact"),
                "--archive-content-digest",
                digest_bytes(b"inputs"),
                "--destination",
                str(destination),
                "--identity-output",
                str(destination / "identity.json"),
            ],
            cwd=REPO_ROOT,
            env={**os.environ, "GH_BIN": "/bin/false"},
            text=True,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            check=False,
        )
        self.assertNotEqual(result.returncode, 0)
        self.assertFalse(injected.exists())
        self.assertFalse(destination.exists())

    def test_bundle_validates_archives_checksums_provenance_and_elf_identity(self) -> None:
        bundle, selection, identity = self.create_bundle()
        self.validate_bundle(bundle, selection, identity)
        value = json.loads(identity.read_text(encoding="utf-8"))
        self.assertEqual(
            [item["platform"] for item in value["platforms"]],
            ["linux/amd64", "linux/arm64"],
        )
        context = self.root / "context"
        self.run_tool(
            IMAGE_TOOL,
            "prepare-context",
            "--bundle",
            str(bundle),
            "--identity",
            str(identity),
            "--rid",
            "linux-x64",
            "--dockerfile",
            str(DOCKERFILE),
            "--output",
            str(context),
        )
        self.assertEqual(
            sorted(path.name for path in context.iterdir()),
            ["Aws2Azure.Proxy", "Dockerfile"],
        )

        archive_manifest = json.loads(
            (bundle / "platforms/linux-arm64/platform-manifest.json").read_text(
                encoding="utf-8"
            )
        )
        archive = bundle / "platforms/linux-arm64" / archive_manifest["archive"]["path"]
        archive.write_bytes(archive.read_bytes() + b"tamper")
        self.write_complete_checksums(bundle)
        tampered_identity = self.root / "tampered-identity.json"
        self.validate_bundle(
            bundle, selection, tampered_identity, expect_success=False
        )

    def test_architecture_swap_and_provenance_tamper_fail(self) -> None:
        bundle, selection, identity = self.create_bundle(swap_architectures=True)
        self.validate_bundle(bundle, selection, identity, expect_success=False)

        shutil.rmtree(self.root)
        self.root.mkdir()
        bundle, selection, identity = self.create_bundle()
        provenance = bundle / "provenance/archive-payload-provenance.json"
        provenance.write_text('{"tampered":true}\n', encoding="utf-8")
        self.write_complete_checksums(bundle)
        self.validate_bundle(bundle, selection, identity, expect_success=False)

    def test_attestation_policy_binds_exact_attempt_and_subject_set(self) -> None:
        bundle, selection, identity_path = self.create_bundle()
        self.validate_bundle(bundle, selection, identity_path)
        identity = json.loads(identity_path.read_text(encoding="utf-8"))
        for kind in ("payload", "archive-inputs"):
            verification = self.root / f"{kind}.json"
            write_json(verification, [self.make_attestation(identity, kind)])
            self.run_tool(
                IMAGE_TOOL,
                "validate-attestation",
                "--kind",
                kind,
                "--identity",
                str(identity_path),
                "--verification",
                str(verification),
            )
            value = json.loads(verification.read_text(encoding="utf-8"))
            value[0]["verificationResult"]["signature"]["certificate"][
                "runInvocationURI"
            ] = f"https://github.com/{REPOSITORY}/actions/runs/{RUN_ID}/attempts/1"
            write_json(verification, value)
            self.run_tool(
                IMAGE_TOOL,
                "validate-attestation",
                "--kind",
                kind,
                "--identity",
                str(identity_path),
                "--verification",
                str(verification),
                expect_success=False,
            )

    def test_registry_image_config_rejects_baked_overrides_and_drift(self) -> None:
        bundle, selection, identity_path = self.create_bundle()
        self.validate_bundle(bundle, selection, identity_path)
        identity = json.loads(identity_path.read_text(encoding="utf-8"))
        expected_path = self.root / "expected-inspect.json"
        actual_path = self.root / "actual-inspect.json"
        expected = self.image_inspect(identity, "linux/amd64")
        write_json(expected_path, expected)
        write_json(actual_path, expected)
        self.run_tool(
            IMAGE_TOOL,
            "compare-image-config",
            "--expected",
            str(expected_path),
            "--actual",
            str(actual_path),
        )

        actual = json.loads(actual_path.read_text(encoding="utf-8"))
        actual[0]["Config"]["Env"].append("AWS2AZURE_INSECURE_TLS=1")
        write_json(actual_path, actual)
        self.run_tool(
            IMAGE_TOOL,
            "validate-image-config",
            "--identity",
            str(identity_path),
            "--platform",
            "linux/amd64",
            "--inspect",
            str(actual_path),
            expect_success=False,
        )
        actual = json.loads(expected_path.read_text(encoding="utf-8"))
        actual[0]["RootFS"]["Layers"][-1] = digest_bytes(b"unexpected extra content")
        write_json(actual_path, actual)
        self.run_tool(
            IMAGE_TOOL,
            "compare-image-config",
            "--expected",
            str(expected_path),
            "--actual",
            str(actual_path),
            expect_success=False,
        )
        self.run_tool(
            IMAGE_TOOL,
            "compare-image-config",
            "--expected",
            str(expected_path),
            "--actual",
            str(actual_path),
            expect_success=False,
        )

    def test_platform_images_and_exact_two_platform_index_assemble(self) -> None:
        bundle, selection, identity_path = self.create_bundle()
        self.validate_bundle(bundle, selection, identity_path)
        results, digests = self.create_platform_results(identity_path)
        index = self.root / "index.json"
        index_digest = self.write_index(
            index, digests, ["linux/amd64", "linux/arm64"]
        )
        output = self.root / "ghcr-inputs.json"
        materials = self.root / "materials.json"
        archive_digest = json.loads(
            identity_path.read_text(encoding="utf-8")
        )["archive_input"]["content_digest"]
        self.run_tool(
            IMAGE_TOOL,
            "assemble",
            "--identity",
            str(identity_path),
            "--results",
            str(results),
            "--repository",
            f"ghcr.io/{REPOSITORY}",
            "--candidate-tag",
            CANDIDATE,
            "--immutable-tag",
            f"{CANDIDATE}-{archive_digest.removeprefix('sha256:')}",
            "--index",
            str(index),
            "--index-digest",
            index_digest,
            "--workflow-sha",
            "2" * 40,
            "--run-id",
            "999",
            "--run-attempt",
            "1",
            "--output",
            str(output),
            "--materials-output",
            str(materials),
        )
        value = json.loads(output.read_text(encoding="utf-8"))
        self.run_tool(
            IMAGE_TOOL, "validate-ghcr-input", str(output)
        )
        self.assertEqual(
            value["container"],
            {
                "repository": f"ghcr.io/{REPOSITORY}",
                "index_digest": index_digest,
                "platforms": [
                    {"platform": "linux/amd64", "digest": digests["linux/amd64"]},
                    {"platform": "linux/arm64", "digest": digests["linux/arm64"]},
                ],
            },
        )
        self.assertNotIn("latest", value["tags"].values())
        value["tags"]["candidate"] = "latest"
        value["content_digest"] = content_digest(value)
        write_json(output, value)
        self.run_tool(
            IMAGE_TOOL,
            "validate-ghcr-input",
            str(output),
            expect_success=False,
        )

        for label, platforms in (
            ("missing", ["linux/amd64"]),
            ("extra", ["linux/amd64", "linux/arm64", "linux/s390x"]),
        ):
            bad_index = self.root / f"{label}-index.json"
            bad_digest = self.write_index(bad_index, digests, platforms)
            self.run_tool(
                IMAGE_TOOL,
                "assemble",
                "--identity",
                str(identity_path),
                "--results",
                str(results),
                "--repository",
                f"ghcr.io/{REPOSITORY}",
                "--candidate-tag",
                CANDIDATE,
                "--immutable-tag",
                f"{CANDIDATE}-{archive_digest.removeprefix('sha256:')}",
                "--index",
                str(bad_index),
                "--index-digest",
                bad_digest,
                "--workflow-sha",
                "2" * 40,
                "--run-id",
                "999",
                "--run-attempt",
                "1",
                "--output",
                str(self.root / f"{label}-output.json"),
                "--materials-output",
                str(self.root / f"{label}-materials.json"),
                expect_success=False,
            )

    def test_workflow_and_dockerfile_invariants(self) -> None:
        workflow = WORKFLOW.read_text(encoding="utf-8")
        dockerfile = DOCKERFILE.read_text(encoding="utf-8")
        remote_uses = re.findall(r"uses:\s+([^./\s][^@\s]+)@([^\s#]+)", workflow)
        self.assertTrue(remote_uses)
        for action, reference in remote_uses:
            with self.subTest(action=action):
                self.assertRegex(reference, r"^[0-9a-f]{40}$")
        self.assertRegex(
            dockerfile,
            r"ARG RUNTIME_BASE=mcr\.microsoft\.com/.+@sha256:[0-9a-f]{64}",
        )
        self.assertEqual(
            len(re.findall(r"base_digest: sha256:[0-9a-f]{64}", workflow)), 2
        )
        combined = workflow + "\n" + dockerfile
        for forbidden in (
            "dotnet publish",
            "dotnet build",
            "dotnet restore",
            "COPY . .",
            "COPY src/",
            "docker/metadata-action@",
            "type=semver",
            "type=ref",
            ":latest",
            "setup-qemu",
        ):
            self.assertNotIn(forbidden, combined)
        self.assertIn("runs-on: ${{ matrix.runner }}", workflow)
        self.assertIn("archive_workflow_source_sha:", workflow)
        self.assertIn("runner: ubuntu-24.04-arm", workflow)
        self.assertIn("Require native architecture", workflow)
        self.assertIn("overwrite: false", workflow)
        self.assertIn("actions/attest-build-provenance@", workflow)
        self.assertIn("actions/attest@", workflow)
        self.assertIn("push-to-registry: true", workflow)
        self.assertIn("imagetools create", workflow)
        self.assertIn("No stable SemVer", workflow)
        self.assertEqual(
            workflow.count("Authorization: Bearer " + "$token")
            + workflow.count('-H "$authorization"'),
            5,
        )
        self.assertIn("Select an existing immutable platform digest", workflow)
        self.assertIn("Validate exact registry image by digest", workflow)
        self.assertIn('digest_ref="$repository@$digest"', workflow)
        self.assertIn("Create or recover exact amd64 and arm64 index", workflow)
        self.assertEqual(workflow.count("docker run -d --name"), 2)
        self.assertNotIn("${!", (
            REPO_ROOT / "eng" / "resolve-release-candidate-archives.sh"
        ).read_text(encoding="utf-8"))
        resolver = (
            REPO_ROOT / "eng" / "resolve-release-candidate-archives.sh"
        ).read_text(encoding="utf-8")
        self.assertIn("/branches/main", resolver)
        self.assertIn(
            "/compare/$workflow_source_sha...$main_sha",
            resolver,
        )
        self.assertIn('--main-branch-json "$main_branch_json"', resolver)
        self.assertIn('--main-compare-json "$main_compare_json"', resolver)

        run_blocks = re.findall(
            r"\n\s+run: \|\n((?:\s{10,}.+\n?)+)", workflow
        )
        self.assertTrue(run_blocks)
        for block in run_blocks:
            self.assertNotIn("${{ inputs.", block)


if __name__ == "__main__":
    unittest.main(verbosity=2)
