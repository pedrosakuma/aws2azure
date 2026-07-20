#!/usr/bin/env python3
"""Tests for the immutable release-candidate manifest primitives."""

from __future__ import annotations

import gzip
import hashlib
import io
import json
import os
import pathlib
import shutil
import subprocess
import tarfile
import unittest


REPO_ROOT = pathlib.Path(__file__).resolve().parent.parent
TOOL = REPO_ROOT / "eng" / "release-candidate-manifest.py"
SOURCE_SHA = "c885a4b7bfbc35390a32b98139495c19dfb7da0b"
ORCHESTRATION_SHA = "1123456789abcdef0123456789abcdef01234567"
SOURCE_REPOSITORY = "pedrosakuma/aws2azure"
SEALED_RUN_ID = 29629752701
SEALED_RUN_ATTEMPT = 1
SEALED_ATTEMPT_URL = (
    "https://github.com/pedrosakuma/aws2azure/actions/runs/"
    "29629752701/attempts/1"
)


def digest_bytes(value: bytes) -> str:
    return f"sha256:{hashlib.sha256(value).hexdigest()}"


def digest_file(path: pathlib.Path) -> str:
    return digest_bytes(path.read_bytes())


def canonical_body_digest(manifest: dict[str, object]) -> str:
    body = {key: value for key, value in manifest.items() if key != "content_digest"}
    return digest_bytes(
        json.dumps(
            body, sort_keys=True, separators=(",", ":"), ensure_ascii=False
        ).encode("utf-8")
    )


def canonical_identity_digest(manifest: dict[str, object]) -> str:
    body = {
        key: value
        for key, value in manifest.items()
        if key not in {"content_digest", "identity_digest", "observation_evidence"}
    }
    return digest_bytes(
        json.dumps(
            body, sort_keys=True, separators=(",", ":"), ensure_ascii=False
        ).encode("utf-8")
    )


def write_json(path: pathlib.Path, value: object) -> None:
    path.write_text(
        json.dumps(value, sort_keys=True, indent=2, ensure_ascii=False) + "\n",
        encoding="utf-8",
    )


def tar_info(name: str, *, directory: bool = False) -> tarfile.TarInfo:
    info = tarfile.TarInfo(name)
    info.uid = 0
    info.gid = 0
    info.uname = ""
    info.gname = ""
    info.mtime = 0
    info.mode = 0o755 if directory else 0o644
    if directory:
        info.type = tarfile.DIRTYPE
    return info


def write_archive(
    path: pathlib.Path,
    executable: bytes,
    *,
    executable_member: str,
    readme: bytes = b"release candidate\n",
    bad_member: tarfile.TarInfo | None = None,
    duplicate_executable: bool = False,
    member_mtime: int = 0,
    gzip_mtime: int = 0,
) -> None:
    root = executable_member.rsplit("/", 1)[0]
    entries: list[tuple[tarfile.TarInfo, bytes | None]] = [
        (tar_info(root, directory=True), None),
        (tar_info(f"{root}/README.md"), readme),
    ]
    executable_info = tar_info(executable_member)
    executable_info.mode = 0o755
    executable_info.mtime = member_mtime
    entries.append((executable_info, executable))
    if bad_member is not None:
        entries.append((bad_member, b"bad" if bad_member.isreg() else None))
    if duplicate_executable:
        duplicate = tar_info(executable_member)
        duplicate.mode = 0o755
        entries.append((duplicate, executable))
    entries.sort(key=lambda item: item[0].name)
    with path.open("wb") as raw:
        with gzip.GzipFile(filename="", mode="wb", fileobj=raw, mtime=gzip_mtime) as zipped:
            with tarfile.open(
                fileobj=zipped, mode="w", format=tarfile.GNU_FORMAT
            ) as archive:
                for info, data in entries:
                    if data is not None:
                        info.size = len(data)
                        archive.addfile(info, io.BytesIO(data))
                    else:
                        archive.addfile(info)


class ReleaseCandidateManifestTests(unittest.TestCase):
    def setUp(self) -> None:
        self.root = (
            REPO_ROOT
            / "artifacts"
            / f"test-release-candidate-manifest-{os.getpid()}-{self._testMethodName}"
        )
        shutil.rmtree(self.root, ignore_errors=True)
        (self.root / "artifacts").mkdir(parents=True)
        self.x64_bytes = b"fake native AOT linux-x64 executable\n"
        self.arm64_bytes = b"fake native AOT linux-arm64 executable\n"
        self.x64_executable = self.root / "artifacts" / "linux-x64" / "Aws2Azure.Proxy"
        self.arm64_executable = (
            self.root / "artifacts" / "linux-arm64" / "Aws2Azure.Proxy"
        )
        self.x64_executable.parent.mkdir(parents=True)
        self.arm64_executable.parent.mkdir(parents=True)
        self.x64_executable.write_bytes(self.x64_bytes)
        self.arm64_executable.write_bytes(self.arm64_bytes)
        self.x64_executable.chmod(0o755)
        self.arm64_executable.chmod(0o755)
        self.x64_archive = self.root / "artifacts" / "aws2azure-rc-linux-x64.tar.gz"
        self.arm64_archive = (
            self.root / "artifacts" / "aws2azure-rc-linux-arm64.tar.gz"
        )
        write_archive(
            self.x64_archive,
            self.x64_bytes,
            executable_member="aws2azure-rc-linux-x64/aws2azure",
        )
        write_archive(
            self.arm64_archive,
            self.arm64_bytes,
            executable_member="aws2azure-rc-linux-arm64/aws2azure",
        )
        self.sealed_manifest_path = (
            self.root / "artifacts" / "sealed-runtime-manifest.json"
        )
        self.sealed_manifest = self.make_sealed_manifest()
        write_json(self.sealed_manifest_path, self.sealed_manifest)
        self.descriptor_path = self.root / "descriptor.json"
        self.manifest_path = self.root / "release-candidate-manifest.json"
        self.descriptor = self.make_descriptor()
        write_json(self.descriptor_path, self.descriptor)

    def tearDown(self) -> None:
        shutil.rmtree(self.root, ignore_errors=True)
        try:
            (REPO_ROOT / "artifacts").rmdir()
        except OSError:
            pass

    def make_sealed_manifest(self) -> dict[str, object]:
        executable_digest = digest_bytes(self.x64_bytes)
        hash_lines = (
            f"{executable_digest.removeprefix('sha256:')}  ./Aws2Azure.Proxy\n"
        ).encode()
        aggregate_digest = digest_bytes(hash_lines)
        aggregate_hex = aggregate_digest.removeprefix("sha256:")
        artifact_name = (
            f"aws2azure-sealed-linux-x64-{aggregate_hex}-run-{SEALED_RUN_ID}-"
            f"attempt-{SEALED_RUN_ATTEMPT}"
        )
        run_url = (
            f"https://github.com/{SOURCE_REPOSITORY}/actions/runs/{SEALED_RUN_ID}"
        )
        return {
            "schema_version": 1,
            "source": {
                "repository": SOURCE_REPOSITORY,
                "git_sha": SOURCE_SHA,
                "git_ref": "refs/heads/main",
            },
            "target": {
                "operating_system": "linux",
                "architecture": "x64",
                "rid": "linux-x64",
            },
            "runtime": {
                "root": "runtime",
                "executable": {
                    "path": "runtime/Aws2Azure.Proxy",
                    "name": "Aws2Azure.Proxy",
                    "sha256": executable_digest,
                    "size_bytes": len(self.x64_bytes),
                },
                "files": [
                    {
                        "path": "runtime/Aws2Azure.Proxy",
                        "sha256": executable_digest,
                        "size_bytes": len(self.x64_bytes),
                        "executable": True,
                    }
                ],
                "aggregate_digest": aggregate_digest,
            },
            "artifact": {
                "name": artifact_name,
                "archive_name": f"{artifact_name}.tar",
                "format": "tar",
                "retention_days": 90,
                "selection": {
                    "repository": SOURCE_REPOSITORY,
                    "run_id": SEALED_RUN_ID,
                    "run_attempt": SEALED_RUN_ATTEMPT,
                },
            },
            "producer": {
                "event_name": "workflow_dispatch",
                "workflow_path": ".github/workflows/sealed-runtime.yml",
                "workflow_ref": (
                    f"{SOURCE_REPOSITORY}/.github/workflows/sealed-runtime.yml@"
                    "refs/heads/main"
                ),
                "workflow_url": (
                    f"https://github.com/{SOURCE_REPOSITORY}/actions/workflows/"
                    "sealed-runtime.yml"
                ),
                "run_id": SEALED_RUN_ID,
                "run_attempt": SEALED_RUN_ATTEMPT,
                "run_url": run_url,
                "attempt_url": SEALED_ATTEMPT_URL,
                "run_started_at": "2026-07-18T03:58:30Z",
                "produced_at": "2026-07-18T04:00:00Z",
            },
        }

    def approved_runtime(
        self, profile_id: str, ledger_seed: str
    ) -> dict[str, object]:
        return {
            "ledger_record_digest": digest_bytes(ledger_seed.encode()),
            "profile": {"id": profile_id, "version": 1},
            "status": "approved",
            "source_repository": SOURCE_REPOSITORY,
            "source_sha": SOURCE_SHA,
            "source_ref": "refs/heads/main",
            "aggregate_digest": self.sealed_manifest["runtime"]["aggregate_digest"],
            "executable_digest": digest_bytes(self.x64_bytes),
            "manifest_digest": digest_file(self.sealed_manifest_path),
            "producer": {
                "workflow": ".github/workflows/sealed-runtime.yml",
                "run_id": SEALED_RUN_ID,
                "run_attempt": SEALED_RUN_ATTEMPT,
                "attempt_url": SEALED_ATTEMPT_URL,
            },
            "artifact": {
                "id": 8425076927,
                "name": self.sealed_manifest["artifact"]["name"],
                "upload_digest": digest_bytes(b"uploaded sealed artifact"),
            },
        }

    def make_descriptor(self) -> dict[str, object]:
        producer_attempt = (
            "https://github.com/pedrosakuma/aws2azure/actions/runs/"
            "30000000001/attempts/1"
        )
        platform_provenance = {
            "predicate_type": "https://slsa.dev/provenance/v1",
            "bundle_digest": digest_bytes(b"release attestation bundle"),
            "producer_attempt_url": producer_attempt,
            "producer_source_sha": ORCHESTRATION_SHA,
            "candidate_source_sha": SOURCE_SHA,
        }
        return {
            "schema_version": 1,
            "candidate": {
                "identifier": "v1.0.0-rc.1",
                "source": {
                    "repository": SOURCE_REPOSITORY,
                    "sha": SOURCE_SHA,
                    "ref": "refs/tags/v1.0.0-rc.1",
                },
            },
            "producer": {
                "workflow": ".github/workflows/release-candidate.yml",
                "event_name": "workflow_dispatch",
                "run_id": 30000000001,
                "run_attempt": 1,
                "attempt_url": producer_attempt,
                "source_sha": ORCHESTRATION_SHA,
                "source_ref": "refs/heads/main",
            },
            "platforms": [
                {
                    "target": {
                        "operating_system": "linux",
                        "architecture": "x64",
                        "rid": "linux-x64",
                    },
                    "executable_path": "artifacts/linux-x64/Aws2Azure.Proxy",
                    "archive": {
                        "path": "artifacts/aws2azure-rc-linux-x64.tar.gz",
                        "executable_member": "aws2azure-rc-linux-x64/aws2azure",
                    },
                    "provenance": dict(platform_provenance),
                    "sealed_runtime": {
                        "manifest_path": "artifacts/sealed-runtime-manifest.json",
                        "artifact_id": 8425076927,
                        "upload_digest": digest_bytes(b"uploaded sealed artifact"),
                    },
                },
                {
                    "target": {
                        "operating_system": "linux",
                        "architecture": "arm64",
                        "rid": "linux-arm64",
                    },
                    "executable_path": "artifacts/linux-arm64/Aws2Azure.Proxy",
                    "archive": {
                        "path": "artifacts/aws2azure-rc-linux-arm64.tar.gz",
                        "executable_member": "aws2azure-rc-linux-arm64/aws2azure",
                    },
                    "provenance": dict(platform_provenance),
                    "sealed_runtime": None,
                },
            ],
            "container": {
                "repository": "ghcr.io/pedrosakuma/aws2azure",
                "index_digest": digest_bytes(b"OCI index"),
                "platforms": [
                    {
                        "platform": "linux/amd64",
                        "digest": digest_bytes(b"amd64 OCI manifest"),
                    },
                    {
                        "platform": "linux/arm64",
                        "digest": digest_bytes(b"arm64 OCI manifest"),
                    },
                ],
            },
            "workloads": [
                {
                    "profile": {
                        "id": "secretsmanager-basic-lifecycle",
                        "version": 1,
                        "digest": digest_bytes(b"secrets profile"),
                    },
                    "approved_runtime": self.approved_runtime(
                        "secretsmanager-basic-lifecycle", "secrets ledger"
                    ),
                },
                {
                    "profile": {
                        "id": "s3-basic-object-crud",
                        "version": 1,
                        "digest": digest_bytes(b"s3 profile"),
                    },
                    "approved_runtime": self.approved_runtime(
                        "s3-basic-object-crud", "s3 ledger"
                    ),
                },
            ],
            "compatibility_policy": {
                "identifier": "aws2azure-compatibility-policy-v1",
                "digest": digest_bytes(b"compatibility policy"),
            },
            "observation_evidence": [
                {
                    "profile": {
                        "id": "s3-basic-object-crud",
                        "version": 1,
                    },
                    "identifier": "rc-observation/s3/v1",
                    "digest": digest_bytes(b"s3 observation contract"),
                    "verdict": "pass",
                },
                {
                    "profile": {
                        "id": "secretsmanager-basic-lifecycle",
                        "version": 1,
                    },
                    "identifier": "rc-observation/secrets/v1",
                    "digest": digest_bytes(b"secrets observation contract"),
                    "verdict": "pass",
                },
            ],
        }

    def run_tool(
        self, *arguments: str, expect_success: bool = True
    ) -> subprocess.CompletedProcess[str]:
        result = subprocess.run(
            ["python3", str(TOOL), *arguments],
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

    def generate(self) -> dict[str, object]:
        self.run_tool(
            "generate", str(self.descriptor_path), str(self.manifest_path)
        )
        manifest = json.loads(self.manifest_path.read_text(encoding="utf-8"))
        self.trusted_content_digest = manifest["content_digest"]
        return manifest

    def validate_manifest(
        self,
        *,
        expect_success: bool = True,
        path: pathlib.Path | None = None,
        expected_content_digest: str | None = None,
    ) -> subprocess.CompletedProcess[str]:
        return self.run_tool(
            "validate",
            str(path or self.manifest_path),
            "--expected-source-sha",
            SOURCE_SHA,
            "--expected-content-digest",
            expected_content_digest or self.trusted_content_digest,
            expect_success=expect_success,
        )

    def rewrite_manifest(self, manifest: dict[str, object]) -> None:
        manifest["identity_digest"] = canonical_identity_digest(manifest)
        for observation in manifest["observation_evidence"]:
            observation["release_candidate_manifest_digest"] = manifest[
                "identity_digest"
            ]
        manifest["content_digest"] = canonical_body_digest(manifest)
        write_json(self.manifest_path, manifest)

    def test_generator_is_deterministic_and_validator_accepts_exact_bundle(self) -> None:
        manifest = self.generate()
        second = self.root / "release-candidate-manifest-second.json"
        self.run_tool("generate", str(self.descriptor_path), str(second))
        self.assertEqual(self.manifest_path.read_bytes(), second.read_bytes())
        self.validate_manifest()
        self.assertEqual(
            [item["target"]["rid"] for item in manifest["platforms"]],
            ["linux-arm64", "linux-x64"],
        )
        self.assertEqual(
            [item["profile"]["id"] for item in manifest["observation_evidence"]],
            ["s3-basic-object-crud", "secretsmanager-basic-lifecycle"],
        )
        self.assertEqual(manifest["candidate"]["source"]["sha"], SOURCE_SHA)
        self.assertEqual(manifest["producer"]["source_sha"], ORCHESTRATION_SHA)
        self.assertEqual(manifest["producer"]["source_ref"], "refs/heads/main")
        for platform in manifest["platforms"]:
            self.assertEqual(
                platform["provenance"]["candidate_source_sha"],
                SOURCE_SHA,
            )
            self.assertEqual(
                platform["provenance"]["producer_source_sha"],
                ORCHESTRATION_SHA,
            )
        self.assertTrue(
            all(
                item["release_candidate_manifest_digest"]
                == manifest["identity_digest"]
                for item in manifest["observation_evidence"]
            )
        )

    def test_identity_receipt_precedes_observations_without_fabricating_them(self) -> None:
        identity_descriptor = dict(self.descriptor)
        identity_descriptor.pop("observation_evidence")
        identity_descriptor_path = self.root / "release-candidate-identity-inputs.json"
        identity_receipt_path = self.root / "release-candidate-identity.json"
        write_json(identity_descriptor_path, identity_descriptor)

        self.run_tool(
            "identity",
            str(identity_descriptor_path),
            str(identity_receipt_path),
        )
        self.run_tool("validate-identity", str(identity_receipt_path))
        receipt = json.loads(identity_receipt_path.read_text(encoding="utf-8"))
        manifest = self.generate()

        self.assertEqual(receipt["artifact_kind"], "release_candidate_identity")
        self.assertNotIn("observation_evidence", receipt)
        self.assertEqual(receipt["identity_digest"], manifest["identity_digest"])
        linked_receipt = self.root / "linked-release-candidate-identity.json"
        linked_receipt.symlink_to(identity_receipt_path)
        self.run_tool(
            "validate-identity",
            str(linked_receipt),
            expect_success=False,
        )

    def test_finalizer_binds_exact_observation_receipts(self) -> None:
        identity_descriptor = dict(self.descriptor)
        observations = identity_descriptor.pop("observation_evidence")
        identity_descriptor_path = self.root / "release-candidate-identity-inputs.json"
        identity_receipt_path = self.root / "release-candidate-identity.json"
        finalized_path = self.root / "release-candidate-finalized.json"
        write_json(identity_descriptor_path, identity_descriptor)
        self.run_tool(
            "identity",
            str(identity_descriptor_path),
            str(identity_receipt_path),
        )
        identity = json.loads(identity_receipt_path.read_text(encoding="utf-8"))
        observation_paths: list[pathlib.Path] = []
        for index, observation in enumerate(observations):
            path = self.root / f"observation-selection-{index}.json"
            write_json(
                path,
                {
                    "schema_version": 1,
                    "release_candidate_id": identity["candidate"]["identifier"],
                    "release_candidate_identity_digest": identity["identity_digest"],
                    "profile_id": observation["profile"]["id"],
                    "verdict": observation["verdict"],
                    "evidence_digest": observation["digest"],
                    "artifact": {},
                    "archive_inputs": {},
                    "ghcr_inputs": {},
                    "producer": {},
                    "manifest_observation": observation,
                },
            )
            observation_paths.append(path)

        arguments = [
            "finalize",
            str(identity_receipt_path),
            str(finalized_path),
        ]
        for path in reversed(observation_paths):
            arguments.extend(["--observation", str(path)])
        self.run_tool(*arguments)
        finalized = json.loads(finalized_path.read_text(encoding="utf-8"))
        generated = self.generate()
        self.assertEqual(finalized, generated)

        tampered = json.loads(observation_paths[0].read_text(encoding="utf-8"))
        tampered["release_candidate_identity_digest"] = digest_bytes(b"other identity")
        write_json(observation_paths[0], tampered)
        finalized_path.unlink()
        self.run_tool(*arguments, expect_success=False)

    def test_finalizer_rejects_rollback_and_incomplete_coverage(self) -> None:
        identity_descriptor = dict(self.descriptor)
        observations = identity_descriptor.pop("observation_evidence")
        identity_descriptor_path = self.root / "release-candidate-identity-inputs.json"
        identity_receipt_path = self.root / "release-candidate-identity.json"
        finalized_path = self.root / "release-candidate-finalized.json"
        write_json(identity_descriptor_path, identity_descriptor)
        self.run_tool(
            "identity",
            str(identity_descriptor_path),
            str(identity_receipt_path),
        )
        identity = json.loads(identity_receipt_path.read_text(encoding="utf-8"))
        path = self.root / "observation-selection.json"
        observation = observations[0]
        write_json(
            path,
            {
                "schema_version": 1,
                "release_candidate_id": identity["candidate"]["identifier"],
                "release_candidate_identity_digest": identity["identity_digest"],
                "profile_id": observation["profile"]["id"],
                "verdict": "rollback",
                "evidence_digest": observation["digest"],
                "artifact": {},
                "archive_inputs": {},
                "ghcr_inputs": {},
                "producer": {},
                "manifest_observation": {
                    **observation,
                    "verdict": "rollback",
                },
            },
        )
        self.run_tool(
            "finalize",
            str(identity_receipt_path),
            str(finalized_path),
            "--observation",
            str(path),
            expect_success=False,
        )

    def test_descriptor_rejects_unknown_missing_and_duplicate_fields(self) -> None:
        descriptor = dict(self.descriptor)
        descriptor["unknown"] = True
        write_json(self.descriptor_path, descriptor)
        self.run_tool(
            "generate",
            str(self.descriptor_path),
            str(self.manifest_path),
            expect_success=False,
        )

        descriptor = dict(self.descriptor)
        del descriptor["container"]
        write_json(self.descriptor_path, descriptor)
        self.run_tool(
            "generate",
            str(self.descriptor_path),
            str(self.manifest_path),
            expect_success=False,
        )

        self.descriptor_path.write_text(
            '{"schema_version":1,"schema_version":1}\n', encoding="utf-8"
        )
        self.run_tool(
            "generate",
            str(self.descriptor_path),
            str(self.manifest_path),
            expect_success=False,
        )

    def test_manifest_rejects_unknown_missing_duplicate_and_noncanonical_json(self) -> None:
        manifest = self.generate()
        manifest["unknown"] = True
        write_json(self.manifest_path, manifest)
        self.validate_manifest(expect_success=False)

        manifest = self.generate_fresh_manifest()
        del manifest["container"]
        write_json(self.manifest_path, manifest)
        self.validate_manifest(expect_success=False)

        manifest = self.generate_fresh_manifest()
        canonical = json.dumps(manifest, sort_keys=True, separators=(",", ":"))
        self.manifest_path.write_text(canonical, encoding="utf-8")
        self.validate_manifest(expect_success=False)

        self.manifest_path.write_text(
            '{"schema_version":1,"schema_version":1}\n', encoding="utf-8"
        )
        self.validate_manifest(expect_success=False)

    def generate_fresh_manifest(self) -> dict[str, object]:
        if self.manifest_path.exists():
            self.manifest_path.unlink()
        write_json(self.descriptor_path, self.descriptor)
        return self.generate()

    def test_malformed_digest_and_content_tampering_fail(self) -> None:
        manifest = self.generate()
        manifest["container"]["index_digest"] = "sha256:not-a-digest"
        self.rewrite_manifest(manifest)
        self.validate_manifest(expect_success=False)

        manifest = self.generate_fresh_manifest()
        manifest["candidate"]["identifier"] = "v1.0.0-rc.tampered"
        write_json(self.manifest_path, manifest)
        self.validate_manifest(expect_success=False)

    def test_duplicate_workloads_and_observation_coverage_fail(self) -> None:
        self.descriptor["workloads"].append(self.descriptor["workloads"][0])
        write_json(self.descriptor_path, self.descriptor)
        self.run_tool(
            "generate",
            str(self.descriptor_path),
            str(self.manifest_path),
            expect_success=False,
        )

        self.descriptor = self.make_descriptor()
        self.descriptor["observation_evidence"].pop()
        write_json(self.descriptor_path, self.descriptor)
        self.run_tool(
            "generate",
            str(self.descriptor_path),
            str(self.manifest_path),
            expect_success=False,
        )

    def test_source_and_approved_runtime_identity_drift_fail(self) -> None:
        self.descriptor["workloads"][0]["approved_runtime"]["source_sha"] = "1" * 40
        write_json(self.descriptor_path, self.descriptor)
        self.run_tool(
            "generate",
            str(self.descriptor_path),
            str(self.manifest_path),
            expect_success=False,
        )

        self.descriptor = self.make_descriptor()
        self.descriptor["producer"]["source_ref"] = "refs/tags/v1.0.0-rc.1"
        write_json(self.descriptor_path, self.descriptor)
        self.run_tool(
            "generate",
            str(self.descriptor_path),
            str(self.manifest_path),
            expect_success=False,
        )

        self.descriptor = self.make_descriptor()
        self.descriptor["workloads"][0]["approved_runtime"]["producer"][
            "run_attempt"
        ] = 2
        write_json(self.descriptor_path, self.descriptor)
        self.run_tool(
            "generate",
            str(self.descriptor_path),
            str(self.manifest_path),
            expect_success=False,
        )

        self.descriptor = self.make_descriptor()
        self.sealed_manifest["source"]["git_sha"] = "2" * 40
        write_json(self.sealed_manifest_path, self.sealed_manifest)
        self.descriptor["workloads"][0]["approved_runtime"][
            "manifest_digest"
        ] = digest_file(self.sealed_manifest_path)
        self.descriptor["workloads"][1]["approved_runtime"][
            "manifest_digest"
        ] = digest_file(self.sealed_manifest_path)
        write_json(self.descriptor_path, self.descriptor)
        self.run_tool(
            "generate",
            str(self.descriptor_path),
            str(self.manifest_path),
            expect_success=False,
        )

    def test_profile_ledger_container_and_numeric_type_drift_fail(self) -> None:
        first = self.descriptor["workloads"][0]["approved_runtime"]
        second = self.descriptor["workloads"][1]["approved_runtime"]
        self.descriptor["workloads"][0]["approved_runtime"] = second
        self.descriptor["workloads"][1]["approved_runtime"] = first
        write_json(self.descriptor_path, self.descriptor)
        self.run_tool(
            "generate",
            str(self.descriptor_path),
            str(self.manifest_path),
            expect_success=False,
        )

        self.descriptor = self.make_descriptor()
        self.descriptor["container"]["repository"] = "ghcr.io/attacker/not-aws2azure"
        write_json(self.descriptor_path, self.descriptor)
        self.run_tool(
            "generate",
            str(self.descriptor_path),
            str(self.manifest_path),
            expect_success=False,
        )

        self.descriptor = self.make_descriptor()
        self.descriptor["schema_version"] = True
        write_json(self.descriptor_path, self.descriptor)
        self.run_tool(
            "generate",
            str(self.descriptor_path),
            str(self.manifest_path),
            expect_success=False,
        )

        self.descriptor = self.make_descriptor()
        self.descriptor["workloads"][0]["approved_runtime"]["producer"][
            "run_attempt"
        ] = 1.0
        write_json(self.descriptor_path, self.descriptor)
        self.run_tool(
            "generate",
            str(self.descriptor_path),
            str(self.manifest_path),
            expect_success=False,
        )

        self.descriptor = self.make_descriptor()
        self.descriptor["workloads"][0]["approved_runtime"]["artifact"][
            "id"
        ] = 8425076927.0
        write_json(self.descriptor_path, self.descriptor)
        self.run_tool(
            "generate",
            str(self.descriptor_path),
            str(self.manifest_path),
            expect_success=False,
        )

        self.descriptor = self.make_descriptor()
        self.sealed_manifest["artifact"]["selection"]["run_attempt"] = True
        write_json(self.sealed_manifest_path, self.sealed_manifest)
        write_json(self.descriptor_path, self.descriptor)
        self.run_tool(
            "generate",
            str(self.descriptor_path),
            str(self.manifest_path),
            expect_success=False,
        )

        self.sealed_manifest = self.make_sealed_manifest()
        write_json(self.sealed_manifest_path, self.sealed_manifest)
        self.descriptor = self.make_descriptor()
        write_json(self.descriptor_path, self.descriptor)
        manifest = self.generate()
        manifest["platforms"][0]["archive"]["size_bytes"] = float(
            manifest["platforms"][0]["archive"]["size_bytes"]
        )
        self.rewrite_manifest(manifest)
        self.validate_manifest(
            expect_success=False,
            expected_content_digest=manifest["content_digest"],
        )

    def test_file_tamper_symlink_and_special_file_fail(self) -> None:
        self.generate()
        self.arm64_executable.write_bytes(b"tampered arm64\n")
        self.validate_manifest(expect_success=False)

        self.arm64_executable.write_bytes(self.arm64_bytes)
        self.generate_fresh_manifest()
        original = self.arm64_executable
        target = original.with_name("real-proxy")
        original.rename(target)
        original.symlink_to(target.name)
        self.validate_manifest(expect_success=False)

        original.unlink()
        os.mkfifo(original)
        write_json(self.descriptor_path, self.descriptor)
        if self.manifest_path.exists():
            self.manifest_path.unlink()
        self.run_tool(
            "generate",
            str(self.descriptor_path),
            str(self.manifest_path),
            expect_success=False,
        )

    def test_path_traversal_inputs_fail(self) -> None:
        self.descriptor["platforms"][0]["executable_path"] = "../escape"
        write_json(self.descriptor_path, self.descriptor)
        self.run_tool(
            "generate",
            str(self.descriptor_path),
            str(self.manifest_path),
            expect_success=False,
        )

        self.descriptor = self.make_descriptor()
        self.descriptor["platforms"][0]["archive"]["executable_member"] = "../aws2azure"
        write_json(self.descriptor_path, self.descriptor)
        self.run_tool(
            "generate",
            str(self.descriptor_path),
            str(self.manifest_path),
            expect_success=False,
        )

    def test_archive_traversal_link_special_duplicate_and_metadata_fail(self) -> None:
        cases: list[tuple[str, tarfile.TarInfo | None, dict[str, object]]] = []
        traversing = tar_info("../escape")
        cases.append(("traversal", traversing, {}))
        link = tar_info("aws2azure-rc-linux-arm64/link")
        link.type = tarfile.SYMTYPE
        link.linkname = "../../escape"
        cases.append(("link", link, {}))
        fifo = tar_info("aws2azure-rc-linux-arm64/pipe")
        fifo.type = tarfile.FIFOTYPE
        cases.append(("special", fifo, {}))
        cases.append(("duplicate", None, {"duplicate_executable": True}))
        cases.append(("member-time", None, {"member_mtime": 1}))
        cases.append(("gzip-time", None, {"gzip_mtime": 1}))
        for label, bad_member, options in cases:
            with self.subTest(label=label):
                write_archive(
                    self.arm64_archive,
                    self.arm64_bytes,
                    executable_member="aws2azure-rc-linux-arm64/aws2azure",
                    bad_member=bad_member,
                    **options,
                )
                write_json(self.descriptor_path, self.descriptor)
                if self.manifest_path.exists():
                    self.manifest_path.unlink()
                self.run_tool(
                    "generate",
                    str(self.descriptor_path),
                    str(self.manifest_path),
                    expect_success=False,
                )
                write_archive(
                    self.arm64_archive,
                    self.arm64_bytes,
                    executable_member="aws2azure-rc-linux-arm64/aws2azure",
                )

    def test_archive_tamper_cannot_be_hidden_by_recomputed_file_identity(self) -> None:
        manifest = self.generate()
        write_archive(
            self.arm64_archive,
            self.arm64_bytes,
            executable_member="aws2azure-rc-linux-arm64/aws2azure",
            readme=b"tampered release notes\n",
        )
        arm = next(
            item
            for item in manifest["platforms"]
            if item["target"]["rid"] == "linux-arm64"
        )
        arm["archive"]["sha256"] = digest_file(self.arm64_archive)
        arm["archive"]["size_bytes"] = self.arm64_archive.stat().st_size
        readme = next(
            item
            for item in arm["archive"]["members"]
            if item["path"].endswith("README.md")
        )
        readme["sha256"] = digest_bytes(b"tampered release notes\n")
        readme["size_bytes"] = len(b"tampered release notes\n")
        archive_subject = next(
            item
            for item in arm["provenance"]["subjects"]
            if item["name"] == arm["archive"]["path"]
        )
        archive_subject["digest"] = arm["archive"]["sha256"]
        self.rewrite_manifest(manifest)
        self.validate_manifest(expect_success=False)

    def test_manifest_symlink_and_recomputed_identity_require_a_trusted_anchor(
        self,
    ) -> None:
        manifest = self.generate()
        symlink = self.root / "manifest-link.json"
        symlink.symlink_to(self.manifest_path.name)
        self.validate_manifest(expect_success=False, path=symlink)

        manifest["candidate"]["identifier"] = "v1.0.0-rc.2"
        manifest["candidate"]["source"]["ref"] = "refs/tags/v1.0.0-rc.2"
        self.rewrite_manifest(manifest)
        recomputed = manifest["content_digest"]
        self.assertNotEqual(recomputed, self.trusted_content_digest)
        self.validate_manifest(expect_success=False)
        self.validate_manifest(
            expect_success=True, expected_content_digest=recomputed
        )


if __name__ == "__main__":
    unittest.main(verbosity=2)
