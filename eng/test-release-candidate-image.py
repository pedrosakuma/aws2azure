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
from collections import Counter


REPO_ROOT = pathlib.Path(__file__).resolve().parent.parent
IMAGE_TOOL = REPO_ROOT / "eng" / "release-candidate-image.py"
INPUTS_TOOL = REPO_ROOT / "eng" / "release-candidate-inputs.py"
PACKAGE_TOOL = REPO_ROOT / "eng" / "release-candidate-package.py"
WORKFLOW = REPO_ROOT / ".github" / "workflows" / "release-candidate-image.yml"
OBSERVATION_WORKFLOW = (
    REPO_ROOT / ".github" / "workflows" / "rc-observation-real-azure.yml"
)
DOCKERFILE = REPO_ROOT / "docker" / "release-candidate" / "Dockerfile"
CANDIDATE = "v1.2.3-rc.4"
REPOSITORY = "pedrosakuma/aws2azure"
SOURCE_SHA = "0123456789abcdef0123456789abcdef01234567"
ORCHESTRATION_SHA = "1123456789abcdef0123456789abcdef01234567"
CURRENT_MAIN_SHA = "2123456789abcdef0123456789abcdef01234567"
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


def identical_comparison(sha: str, *, null_head: bool = False) -> dict[str, object]:
    comparison: dict[str, object] = {
        "url": f"https://api.github.com/repos/{REPOSITORY}/compare/{sha}...{sha}",
        "html_url": f"https://github.com/{REPOSITORY}/compare/{sha}...{sha}",
        "permalink_url": (
            f"https://github.com/{REPOSITORY}/compare/pedrosakuma:{sha}"
            f"...pedrosakuma:{sha}"
        ),
        "diff_url": f"https://github.com/{REPOSITORY}/compare/{sha}...{sha}.diff",
        "patch_url": f"https://github.com/{REPOSITORY}/compare/{sha}...{sha}.patch",
        "status": "identical",
        "ahead_by": 0,
        "behind_by": 0,
        "total_commits": 0,
        "base_commit": {"sha": sha},
        "merge_base_commit": {"sha": sha},
        "commits": [],
        "files": [],
    }
    if null_head:
        comparison["head_commit"] = None
    return comparison


def ahead_comparison(source_sha: str, main_sha: str) -> dict[str, object]:
    return {
        "url": (
            f"https://api.github.com/repos/{REPOSITORY}/compare/"
            f"{source_sha}...{main_sha}"
        ),
        "status": "ahead",
        "ahead_by": 1,
        "behind_by": 0,
        "total_commits": 1,
        "base_commit": {"sha": source_sha},
        "merge_base_commit": {"sha": source_sha},
        "commits": [{"sha": main_sha}],
        "files": [],
    }


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
                    "manifest_subject_digest": sealed_manifest_digest,
                },
            },
        }

    def make_resolved_x64_identity(
        self,
        context: dict[str, object],
        ledger: dict[str, object],
    ) -> dict[str, object]:
        workload = next(
            item
            for item in context["workloads"]
            if item["profile"]["id"] == "s3-basic-object-crud"
        )
        approved = workload["approved_runtime"]
        source = context["sealed_runtime"]
        record = ledger["record"]
        producer = source["producer"]
        repository = source["source_repository"]
        run_url = f"https://github.com/{repository}/actions/runs/{producer['run_id']}"
        return {
            "schema_version": 1,
            "role": "prior",
            "profile": approved["profile"],
            "status": approved["status"],
            "eligibility": record["eligibility"],
            "ledger_record_digest": approved["ledger_record_digest"],
            "source": {
                "repository": repository,
                "sha": source["source_sha"],
                "ref": source["source_ref"],
            },
            "runtime": {
                "aggregate_digest": source["aggregate_digest"],
                "executable_digest": source["executable_digest"],
                "manifest_digest": source["manifest_digest"],
            },
            "producer": {
                "workflow": producer["workflow"],
                "event_name": "workflow_dispatch",
                "run_id": producer["run_id"],
                "run_attempt": producer["run_attempt"],
                "run_url": run_url,
                "attempt_url": producer["attempt_url"],
                "run_started_at": "2026-07-17T18:00:00+00:00",
            },
            "artifact": {
                "id": source["artifact"]["id"],
                "name": source["artifact"]["name"],
                "upload_digest": source["artifact"]["upload_digest"],
                "created_at": record["artifact"]["created_at"],
                "expires_at": record["artifact"]["expires_at"],
            },
            "attestation": {
                "predicate_type": "https://slsa.dev/provenance/v1",
                "repository": repository,
                "signer_workflow": (
                    f"{repository}/.github/workflows/sealed-runtime.yml"
                ),
                "source_sha": source["source_sha"],
                "source_ref": source["source_ref"],
                "run_invocation_url": producer["attempt_url"],
                "bundle_digest": digest_bytes(b"sealed runtime attestation"),
                "executable_subject_name": "Aws2Azure.Proxy",
                "executable_subject_digest": source["executable_digest"],
                "manifest_subject_name": "sealed-runtime-manifest.json",
                "manifest_subject_digest": source["manifest_digest"],
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
        write_json(
            sealed,
            {
                "producer": {
                    "workflow_path": ".github/workflows/sealed-runtime.yml",
                    "event_name": "workflow_dispatch",
                    "run_id": 123456,
                    "run_attempt": 2,
                    "run_url": (
                        f"https://github.com/{REPOSITORY}/actions/runs/123456"
                    ),
                    "attempt_url": (
                        f"https://github.com/{REPOSITORY}/actions/runs/123456/"
                        "attempts/2"
                    ),
                    "run_started_at": "2026-07-17T18:00:00Z",
                }
            },
        )
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
        context_value = json.loads(context.read_text(encoding="utf-8"))
        s3_ledger = json.loads(
            ledgers["s3-basic-object-crud"].read_text(encoding="utf-8")
        )
        write_json(
            bundle / "context" / "resolved-x64-identity.json",
            self.make_resolved_x64_identity(context_value, s3_ledger),
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
        self,
        identity: dict[str, object],
        kind: str,
        subjects: list[dict[str, object]] | None = None,
    ) -> dict[str, object]:
        key = "payload" if kind == "payload" else "archive_inputs"
        if subjects is None:
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

    def producer_payload_subjects(
        self, bundle: pathlib.Path
    ) -> list[dict[str, object]]:
        paths = [
            *sorted((bundle / "context").glob("*.json")),
            bundle / "sealed-runtime" / "sealed-runtime-manifest.json",
        ]
        for rid in ("linux-x64", "linux-arm64"):
            platform = bundle / "platforms" / rid
            paths.extend(
                (
                    platform / "Aws2Azure.Proxy",
                    *sorted(platform.glob("*.tar.gz")),
                    platform / "platform-manifest.json",
                    platform / "SHA256SUMS",
                )
            )
        return [
            {
                "name": path.name,
                "digest": {"sha256": digest_file(path).removeprefix("sha256:")},
            }
            for path in paths
        ]

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
        main_compare = identical_comparison(ORCHESTRATION_SHA)
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

    def test_protected_main_source_accepts_real_identical_and_strict_ahead(self) -> None:
        branch_path = self.root / "protected-main.json"
        compare_path = self.root / "protected-main-compare.json"
        branch = {
            "name": "main",
            "protected": True,
            "commit": {"sha": ORCHESTRATION_SHA},
        }
        write_json(branch_path, branch)
        arguments = (
            "validate-protected-main",
            "--source-sha",
            ORCHESTRATION_SHA,
            "--main-branch-json",
            str(branch_path),
            "--main-compare-json",
            str(compare_path),
        )

        comparison = identical_comparison(ORCHESTRATION_SHA)
        write_json(compare_path, comparison)
        self.run_tool(IMAGE_TOOL, *arguments)
        comparison["head_commit"] = None
        write_json(compare_path, comparison)
        self.run_tool(IMAGE_TOOL, *arguments)
        comparison["head_commit"] = {"sha": ORCHESTRATION_SHA}
        write_json(compare_path, comparison)
        self.run_tool(IMAGE_TOOL, *arguments)

        branch["protected"] = False
        write_json(branch_path, branch)
        self.run_tool(IMAGE_TOOL, *arguments, expect_success=False)
        branch["protected"] = True
        write_json(branch_path, branch)

        branch["commit"]["sha"] = CURRENT_MAIN_SHA
        write_json(branch_path, branch)
        comparison = ahead_comparison(ORCHESTRATION_SHA, CURRENT_MAIN_SHA)
        write_json(compare_path, comparison)
        self.run_tool(IMAGE_TOOL, *arguments)
        comparison["head_commit"] = {"sha": CURRENT_MAIN_SHA}
        comparison.pop("commits")
        write_json(compare_path, comparison)
        self.run_tool(IMAGE_TOOL, *arguments)

    def test_protected_main_source_rejects_drift_and_malformed_counts(self) -> None:
        branch_path = self.root / "protected-main.json"
        compare_path = self.root / "protected-main-compare.json"
        arguments = (
            "validate-protected-main",
            "--source-sha",
            ORCHESTRATION_SHA,
            "--main-branch-json",
            str(branch_path),
            "--main-compare-json",
            str(compare_path),
        )

        write_json(
            branch_path,
            {
                "name": "main",
                "protected": True,
                "commit": {"sha": ORCHESTRATION_SHA},
            },
        )
        identical = identical_comparison(ORCHESTRATION_SHA)
        invalid_identical = (
            lambda value: value.update(status="behind"),
            lambda value: value.update(status="diverged"),
            lambda value: value.update(status="unknown"),
            lambda value: value.pop("status"),
            lambda value: value.update(ahead_by=1),
            lambda value: value.update(ahead_by=None),
            lambda value: value.pop("ahead_by"),
            lambda value: value.update(behind_by=1),
            lambda value: value.update(behind_by=None),
            lambda value: value.pop("behind_by"),
            lambda value: value.update(total_commits=1),
            lambda value: value.update(total_commits=None),
            lambda value: value.pop("total_commits"),
            lambda value: value.pop("base_commit"),
            lambda value: value.pop("merge_base_commit"),
            lambda value: value.update(base_commit=None),
            lambda value: value.update(merge_base_commit=None),
            lambda value: value.update(base_commit={"sha": SOURCE_SHA}),
            lambda value: value.update(merge_base_commit={"sha": SOURCE_SHA}),
            lambda value: value.update(head_commit={"sha": SOURCE_SHA}),
        )
        for index, mutate in enumerate(invalid_identical):
            with self.subTest(kind="identical", mutation=index):
                value = json.loads(json.dumps(identical))
                mutate(value)
                write_json(compare_path, value)
                self.run_tool(IMAGE_TOOL, *arguments, expect_success=False)

        write_json(
            branch_path,
            {
                "name": "main",
                "protected": True,
                "commit": {"sha": CURRENT_MAIN_SHA},
            },
        )
        ahead = ahead_comparison(ORCHESTRATION_SHA, CURRENT_MAIN_SHA)
        invalid_ahead = (
            lambda value: value.update(ahead_by=0),
            lambda value: value.update(ahead_by=1.5),
            lambda value: value.pop("ahead_by"),
            lambda value: value.update(behind_by=1),
            lambda value: value.update(behind_by=None),
            lambda value: value.pop("behind_by"),
            lambda value: value.update(total_commits=2),
            lambda value: value.update(total_commits=None),
            lambda value: value.pop("total_commits"),
            lambda value: value.update(head_commit=None),
            lambda value: value.update(head_commit={"sha": SOURCE_SHA}),
            lambda value: value.pop("commits"),
            lambda value: value.update(commits=[]),
            lambda value: value.update(commits=[{"sha": SOURCE_SHA}]),
            lambda value: value.update(base_commit={"sha": SOURCE_SHA}),
            lambda value: value.update(merge_base_commit={"sha": SOURCE_SHA}),
        )
        for index, mutate in enumerate(invalid_ahead):
            with self.subTest(kind="ahead", mutation=index):
                value = json.loads(json.dumps(ahead))
                mutate(value)
                write_json(compare_path, value)
                self.run_tool(IMAGE_TOOL, *arguments, expect_success=False)

        write_json(compare_path, ahead)
        drifted_arguments = list(arguments)
        drifted_arguments[drifted_arguments.index(ORCHESTRATION_SHA)] = CURRENT_MAIN_SHA
        self.run_tool(IMAGE_TOOL, *drifted_arguments, expect_success=False)

    def test_observation_preflight_runs_identical_and_ahead_validation(self) -> None:
        workflow = OBSERVATION_WORKFLOW.read_text(encoding="utf-8")
        marker = "      - name: Validate RC and select profiles\n"
        start = workflow.index(marker)
        run_start = workflow.index("        run: |\n", start) + len("        run: |\n")
        next_step = workflow.index("\n  observe:", run_start)
        script = "\n".join(
            line[10:] if line.startswith("          ") else line
            for line in workflow[run_start:next_step].splitlines()
        )

        runtime_root = self.root / "observation-runtime"
        mock_bin = runtime_root / "bin"
        tool_root = runtime_root / "eng"
        mock_bin.mkdir(parents=True)
        tool_root.mkdir()
        shutil.copy2(IMAGE_TOOL, tool_root / IMAGE_TOOL.name)
        write_json(
            runtime_root / "main.json",
            {
                "name": "main",
                "protected": True,
                "commit": {"sha": CURRENT_MAIN_SHA},
            },
        )
        write_json(
            runtime_root / "identical.json",
            identical_comparison(CURRENT_MAIN_SHA, null_head=True),
        )
        write_json(
            runtime_root / "ahead.json",
            ahead_comparison(ORCHESTRATION_SHA, CURRENT_MAIN_SHA),
        )
        mock_gh = mock_bin / "gh"
        mock_gh.write_text(
            f"""#!/usr/bin/env bash
set -euo pipefail
endpoint="${{@: -1}}"
case "$endpoint" in
  */branches/main) cat "$MOCK_ROOT/main.json" ;;
  */compare/{CURRENT_MAIN_SHA}...{CURRENT_MAIN_SHA})
    cat "$MOCK_ROOT/identical.json"
    ;;
  */compare/{ORCHESTRATION_SHA}...{CURRENT_MAIN_SHA})
    cat "$MOCK_ROOT/ahead.json"
    ;;
  *) echo "unexpected endpoint: $endpoint" >&2; exit 1 ;;
esac
""",
            encoding="utf-8",
        )
        mock_gh.chmod(0o700)
        output = runtime_root / "github-output.txt"
        environment = {
            **os.environ,
            "PATH": f"{mock_bin}:{os.environ['PATH']}",
            "MOCK_ROOT": str(runtime_root),
            "GITHUB_REPOSITORY": REPOSITORY,
            "GITHUB_OUTPUT": str(output),
            "REQUESTED_PROFILE": "all",
            "RELEASE_CANDIDATE_ID": CANDIDATE,
            "CANDIDATE_SOURCE_SHA": SOURCE_SHA,
            "ARCHIVE_WORKFLOW_SOURCE_SHA": CURRENT_MAIN_SHA,
            "ARCHIVE_RUN_ID": "1",
            "ARCHIVE_RUN_ATTEMPT": "1",
            "ARCHIVE_ARTIFACT_ID": "1",
            "ARCHIVE_ARTIFACT_NAME": "archive",
            "ARCHIVE_ARTIFACT_DIGEST": digest_bytes(b"archive"),
            "ARCHIVE_CONTENT_DIGEST": digest_bytes(b"archive content"),
            "GHCR_WORKFLOW_SOURCE_SHA": ORCHESTRATION_SHA,
            "GHCR_RUN_ID": "2",
            "GHCR_RUN_ATTEMPT": "1",
            "GHCR_ARTIFACT_ID": "2",
            "GHCR_ARTIFACT_NAME": "ghcr",
            "GHCR_ARTIFACT_DIGEST": digest_bytes(b"ghcr"),
            "GHCR_CONTENT_DIGEST": digest_bytes(b"ghcr content"),
            "WINDOW_MINUTES": "60",
            "AZURE_LOCATION": "eastus2",
            "REF": "refs/heads/main",
            "REF_PROTECTED": "true",
        }
        result = subprocess.run(
            ["bash", "-c", script],
            cwd=runtime_root,
            env=environment,
            text=True,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            check=False,
        )
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(
            output.read_text(encoding="utf-8"),
            'profiles=["s3-basic-object-crud","secretsmanager-basic-lifecycle"]\n',
        )

        ahead = ahead_comparison(ORCHESTRATION_SHA, CURRENT_MAIN_SHA)
        ahead["head_commit"] = None
        write_json(runtime_root / "ahead.json", ahead)
        result = subprocess.run(
            ["bash", "-c", script],
            cwd=runtime_root,
            env=environment,
            text=True,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            check=False,
        )
        self.assertNotEqual(result.returncode, 0)

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

    def test_archive_consumer_resolves_paginated_ruleset_details(self) -> None:
        mock_root = self.root / "mock"
        mock_bin = self.root / "mock-bin"
        mock_root.mkdir()
        mock_bin.mkdir()
        compact_matching = {
            "id": 19148912,
            "name": "Protect release candidate tags",
            "target": "tag",
            "source_type": "Repository",
            "source": REPOSITORY,
            "enforcement": "active",
            "updated_at": "2026-07-18T18:13:18.386Z",
        }
        compact_unrelated = {
            "id": 19148913,
            "name": "Protect internal tags",
            "target": "tag",
            "source_type": "Repository",
            "source": REPOSITORY,
            "enforcement": "active",
            "updated_at": "2026-07-18T18:15:00.000Z",
        }
        write_json(
            mock_root / "ruleset-pages.json",
            [[compact_matching], [compact_unrelated]],
        )
        detail_matching = {
            **compact_matching,
            "conditions": {
                "ref_name": {
                    "include": ["refs/tags/v*-rc.*"],
                    "exclude": [],
                }
            },
            "rules": [{"type": "deletion"}, {"type": "non_fast_forward"}],
        }
        detail_unrelated = {
            **compact_unrelated,
            "conditions": {
                "ref_name": {
                    "include": ["refs/tags/internal-*"],
                    "exclude": [],
                }
            },
            "rules": [{"type": "deletion"}, {"type": "non_fast_forward"}],
        }
        write_json(mock_root / "ruleset-19148912.json", detail_matching)
        write_json(mock_root / "ruleset-19148913.json", detail_unrelated)
        write_json(
            mock_root / "tag.json",
            {"object": {"type": "commit", "sha": SOURCE_SHA}},
        )
        write_json(
            mock_root / "branch.json",
            {
                "name": "main",
                "protected": True,
                "commit": {"sha": ORCHESTRATION_SHA},
            },
        )
        write_json(mock_root / "run.json", {})
        write_json(mock_root / "artifact.json", {})
        write_json(mock_root / "compare.json", {})
        mock_gh = mock_bin / "gh"
        mock_gh.write_text(
            """#!/usr/bin/env bash
set -euo pipefail
endpoint="${@: -1}"
printf '%s\\n' "$*" >> "$MOCK_ROOT/gh.log"
case "$endpoint" in
  */actions/runs/333/attempts/2) cat "$MOCK_ROOT/run.json" ;;
  */actions/artifacts/7654321) cat "$MOCK_ROOT/artifact.json" ;;
  */rulesets\\?includes_parents=true\\&targets=tag\\&per_page=100)
    [[ "$#" == 8 && "$2" == --paginate && "$3" == --slurp ]]
    [[ "$4" == -H && "$5" == "Accept: application/vnd.github+json" ]]
    [[ "$6" == -H && "$7" == "X-GitHub-Api-Version: 2022-11-28" ]]
    [[ "$GH_TOKEN" == test-token ]]
    cat "$MOCK_ROOT/ruleset-pages.json"
    ;;
  */rulesets/19148912) cat "$MOCK_RULESET_19148912" ;;
  */rulesets/19148913) cat "$MOCK_ROOT/ruleset-19148913.json" ;;
  */git/ref/tags/v1.2.3-rc.4) cat "$MOCK_ROOT/tag.json" ;;
  */branches/main) cat "$MOCK_ROOT/branch.json" ;;
  */compare/*) cat "$MOCK_ROOT/compare.json" ;;
  *) echo "unexpected endpoint: $endpoint" >&2; exit 1 ;;
esac
""",
            encoding="utf-8",
        )
        mock_gh.chmod(0o700)

        arguments = [
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
            "archive",
            "--artifact-digest",
            digest_bytes(b"artifact"),
            "--archive-content-digest",
            digest_bytes(b"inputs"),
        ]
        environment = {
            **os.environ,
            "GH_BIN": str(mock_gh),
            "GH_TOKEN": "test-token",
            "MOCK_ROOT": str(mock_root),
            "MOCK_RULESET_19148912": str(
                mock_root / "ruleset-19148912.json"
            ),
        }
        result = subprocess.run(
            [
                *arguments,
                "--destination",
                str(self.root / "resolved"),
                "--identity-output",
                str(self.root / "resolved" / "identity.json"),
            ],
            cwd=self.root,
            env=environment,
            text=True,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            check=False,
        )
        self.assertNotEqual(result.returncode, 0)
        self.assertNotIn(
            "candidate tag is not covered by an active protection ruleset",
            result.stderr,
        )
        log = (mock_root / "gh.log").read_text(encoding="utf-8")
        self.assertIn("api --paginate --slurp", log)
        self.assertIn("/rulesets/19148912", log)
        self.assertIn("/rulesets/19148913", log)

        mismatched = {**detail_matching, "id": 19148914}
        write_json(mock_root / "ruleset-mismatched.json", mismatched)
        mismatch_result = subprocess.run(
            [
                *arguments,
                "--destination",
                str(self.root / "mismatch"),
                "--identity-output",
                str(self.root / "mismatch" / "identity.json"),
            ],
            cwd=self.root,
            env={
                **environment,
                "MOCK_RULESET_19148912": str(
                    mock_root / "ruleset-mismatched.json"
                ),
            },
            text=True,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            check=False,
        )
        self.assertNotEqual(mismatch_result.returncode, 0)
        self.assertIn(
            "detailed tag rulesets are malformed, incomplete, or inconsistent",
            mismatch_result.stderr,
        )

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
            producer_subjects = (
                self.producer_payload_subjects(bundle)
                if kind == "payload"
                else None
            )
            write_json(
                verification,
                [self.make_attestation(identity, kind, producer_subjects)],
            )
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

    def test_payload_subjects_match_real_producer_shape_and_reject_drift(self) -> None:
        bundle, selection, identity_path = self.create_bundle()
        self.validate_bundle(bundle, selection, identity_path)
        identity = json.loads(identity_path.read_text(encoding="utf-8"))
        producer_subjects = self.producer_payload_subjects(bundle)
        expected = Counter(
            (item["name"], item["digest"].removeprefix("sha256:"))
            for item in identity["attestation_subjects"]["payload"]
        )
        actual = Counter(
            (item["name"], item["digest"]["sha256"]) for item in producer_subjects
        )
        self.assertEqual(actual, expected)
        subject_name_counts = Counter(
            item["name"] for item in producer_subjects
        )
        self.assertEqual(subject_name_counts["Aws2Azure.Proxy"], 2)
        self.assertEqual(subject_name_counts["platform-manifest.json"], 2)
        self.assertEqual(subject_name_counts["SHA256SUMS"], 2)
        self.assertEqual(subject_name_counts["resolved-x64-identity.json"], 1)

        resolved_index = next(
            index
            for index, item in enumerate(producer_subjects)
            if item["name"] == "resolved-x64-identity.json"
        )
        mutations = {}
        for name in ("missing", "extra", "renamed", "duplicate", "rehashed"):
            subjects = json.loads(json.dumps(producer_subjects))
            if name == "missing":
                del subjects[resolved_index]
            elif name == "extra":
                subjects.append(
                    {
                        "name": "unexpected.json",
                        "digest": {"sha256": "a" * 64},
                    }
                )
            elif name == "renamed":
                subjects[resolved_index]["name"] = "resolved-identity.json"
            elif name == "duplicate":
                subjects.append(subjects[resolved_index])
            else:
                subjects[resolved_index]["digest"]["sha256"] = "b" * 64
            mutations[name] = subjects

        for name, subjects in mutations.items():
            with self.subTest(name=name):
                verification = self.root / f"payload-{name}.json"
                write_json(
                    verification,
                    [self.make_attestation(identity, "payload", subjects)],
                )
                self.run_tool(
                    IMAGE_TOOL,
                    "validate-attestation",
                    "--kind",
                    "payload",
                    "--identity",
                    str(identity_path),
                    "--verification",
                    str(verification),
                    expect_success=False,
                )

    def test_resolved_x64_identity_is_required_regular_and_semantically_bound(
        self,
    ) -> None:
        bundle, selection, identity_path = self.create_bundle()
        self.validate_bundle(bundle, selection, identity_path)
        resolved = bundle / "context" / "resolved-x64-identity.json"

        missing_bytes = resolved.read_bytes()
        resolved.unlink()
        self.write_complete_checksums(bundle)
        self.validate_bundle(
            bundle,
            selection,
            self.root / "missing-resolved-output.json",
            expect_success=False,
        )

        resolved.write_bytes(missing_bytes)
        link_target = bundle / "context" / "resolved-x64-identity-target.json"
        resolved.rename(link_target)
        resolved.symlink_to(link_target.name)
        self.write_complete_checksums(bundle)
        self.validate_bundle(
            bundle,
            selection,
            self.root / "linked-resolved-output.json",
            expect_success=False,
        )

        resolved.unlink()
        link_target.rename(resolved)
        drifted = json.loads(resolved.read_text(encoding="utf-8"))
        drifted["producer"]["run_started_at"] = "2026-07-17T18:00:01Z"
        write_json(resolved, drifted)
        self.write_complete_checksums(bundle)

        recomputed_identity = json.loads(identity_path.read_text(encoding="utf-8"))
        resolved_subject = next(
            item
            for item in recomputed_identity["attestation_subjects"]["payload"]
            if item["name"] == "resolved-x64-identity.json"
        )
        resolved_subject["digest"] = digest_file(resolved)
        recomputed_identity["content_digest"] = content_digest(recomputed_identity)
        recomputed_identity_path = self.root / "recomputed-identity.json"
        write_json(recomputed_identity_path, recomputed_identity)
        verification = self.root / "recomputed-payload-attestation.json"
        write_json(
            verification,
            [
                self.make_attestation(
                    recomputed_identity,
                    "payload",
                    self.producer_payload_subjects(bundle),
                )
            ],
        )
        self.run_tool(
            IMAGE_TOOL,
            "validate-attestation",
            "--kind",
            "payload",
            "--identity",
            str(recomputed_identity_path),
            "--verification",
            str(verification),
        )
        self.validate_bundle(
            bundle,
            selection,
            self.root / "semantic-drift-output.json",
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
        for consumer_path in (
            REPO_ROOT / "eng" / "resolve-release-candidate-archives.sh",
            REPO_ROOT / "eng" / "resolve-sealed-runtime.sh",
            REPO_ROOT / "eng" / "download-qualified-run-artifact.sh",
        ):
            consumer = consumer_path.read_text(encoding="utf-8")
            self.assertIn(
                '"$repo_root/eng/resolve-release-candidate-rulesets.sh"',
                consumer,
            )
            self.assertIn("--fetch-rulesets", consumer)
            self.assertNotIn(
                "rulesets?includes_parents=true&targets=tag&per_page=100",
                consumer,
            )
        ruleset_resolver = (
            REPO_ROOT / "eng" / "resolve-release-candidate-rulesets.sh"
        ).read_text(encoding="utf-8")
        self.assertIn("api --paginate --slurp", ruleset_resolver)
        self.assertIn("X-GitHub-Api-Version: 2022-11-28", ruleset_resolver)
        qualified_consumer = (
            REPO_ROOT / "eng" / "download-qualified-run-artifact.sh"
        ).read_text(encoding="utf-8")
        self.assertIn(
            'if [[ "$expected_ref" == refs/heads/main ]]; then',
            qualified_consumer,
        )
        sealed_consumer = (
            REPO_ROOT / "eng" / "resolve-sealed-runtime.sh"
        ).read_text(encoding="utf-8")
        self.assertIn(
            'if [[ "$ref" == refs/heads/main ]]; then',
            sealed_consumer,
        )

        run_blocks = re.findall(
            r"\n\s+run: \|\n((?:\s{10,}.+\n?)+)", workflow
        )
        self.assertTrue(run_blocks)
        for block in run_blocks:
            self.assertNotIn("${{ inputs.", block)


if __name__ == "__main__":
    unittest.main(verbosity=2)
