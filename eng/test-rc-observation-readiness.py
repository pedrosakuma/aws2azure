#!/usr/bin/env python3
"""Offline protocol/transport/process tests; no credentials or live resources."""
import copy
import datetime as dt
import importlib.util
import io
import json
import os
from pathlib import Path
import signal
import subprocess
import tempfile
import unittest
from unittest.mock import Mock, patch
import zipfile

ROOT = Path(__file__).resolve().parents[1]
SPEC = importlib.util.spec_from_file_location("readiness", ROOT / ".github/scripts/rc-observation-readiness.py")
protocol = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(protocol)
SCRATCH = ROOT / ".tools/readiness-tests"
SCRATCH.mkdir(parents=True, exist_ok=True)


class ReadinessTests(unittest.TestCase):
    def setUp(self):
        self.now = dt.datetime(2026, 9, 21, tzinfo=dt.timezone.utc)
        self.context = {
            "context_id": "sha256:" + "c" * 64, "repository": "owner/repo",
            "profile": "s3-basic-object-crud", "run_id": "123", "run_attempt": "2",
            "workflow_source_sha": "a" * 40,
            "readiness_deadline_utc": (self.now + dt.timedelta(minutes=10)).isoformat(),
            "end_to_end_deadline_utc": (self.now + dt.timedelta(hours=2)).isoformat(),
            "runtimes": {role: {"identity_digest": "sha256:" + digit * 64, "runtime_digest": "sha256:" + digit * 64}
                         for role, digit in (("candidate", "1"), ("stable", "2"))},
        }
        self.entries = {}
        for index, role in enumerate(protocol.ROLES, 1):
            value = {
                "schema_version": 1, "context_id": self.context["context_id"], "cohort": role,
                "instance": str(index) * 32, "harness_pid": index,
                "stage": "sealed-runtime-and-canary-verified",
                "runtime_identity_digest": self.context["runtimes"][role]["identity_digest"],
                "runtime_digest": self.context["runtimes"][role]["runtime_digest"],
                "ready_at_utc": self.now.isoformat(),
                "readiness_deadline_utc": self.context["readiness_deadline_utc"],
            }
            self.entries[role] = {
                "id": index, "name": protocol.artifact_name(self.context, "ready", role),
                "digest": "sha256:" + str(index) * 64,
                "content_digest": protocol.digest(json.dumps(value).encode()), "document": value,
            }

    def test_only_both_prepared_cohorts_release_a_shared_future_start(self):
        release = protocol.make_release(self.context, self.entries, self.now)
        self.assertEqual(protocol.timestamp(release["scheduled_at_utc"]), self.now + dt.timedelta(seconds=120))
        for role in protocol.ROLES:
            protocol.validate_release(self.context, role, release, self.entries[role], self.now)
        with self.assertRaises(ValueError):
            protocol.make_release(self.context, {"candidate": self.entries["candidate"]}, self.now)

    def test_all_required_profiles_have_isolated_current_attempt_rendezvous(self):
        names = set()
        for profile in protocol.PROFILES:
            context = {**self.context, "profile": profile}
            entries = copy.deepcopy(self.entries)
            for role in protocol.ROLES:
                name = protocol.artifact_name(context, "ready", role)
                self.assertNotIn(name, names)
                names.add(name)
                entries[role]["name"] = name
            release = protocol.make_release(context, entries, self.now)
            for role in protocol.ROLES:
                protocol.validate_release(context, role, release, entries[role], self.now)
            foreign = copy.deepcopy(entries)
            foreign["stable"]["name"] = protocol.artifact_name(
                {**context, "profile": "unrelated-profile"}, "ready", "stable")
            with self.assertRaises(ValueError):
                protocol.validate_release(context, "candidate",
                    protocol.make_release(context, foreign, self.now), entries["candidate"], self.now)
        self.assertEqual(len(names), 8)

    def test_invalid_readiness_is_not_a_prepared_process(self):
        changes = (
            ("schema_version", True), ("context_id", "foreign-run-or-attempt"),
            ("cohort", "candidate"), ("stage", "job-started"), ("instance", "bad"),
            ("harness_pid", 0), ("runtime_identity_digest", "other"), ("runtime_digest", "other"),
            ("ready_at_utc", (self.now - dt.timedelta(minutes=46)).isoformat()),
            ("ready_at_utc", (self.now + dt.timedelta(minutes=2)).isoformat()),
            ("readiness_deadline_utc", (self.now + dt.timedelta(seconds=100)).isoformat()),
        )
        for field, value in changes:
            with self.subTest(field=field, value=value):
                entries = copy.deepcopy(self.entries)
                entries["stable"]["document"][field] = value
                with self.assertRaises(ValueError):
                    protocol.make_release(self.context, entries, self.now)

    def test_old_attempt_artifact_names_cannot_be_reused(self):
        changed = {**self.context, "run_attempt": "3"}
        self.assertNotEqual(protocol.artifact_name(self.context, "ready", "candidate"),
                            protocol.artifact_name(changed, "ready", "candidate"))
        release = protocol.make_release(self.context, self.entries, self.now)
        with self.assertRaises(ValueError):
            protocol.validate_release(changed, "candidate", release, self.entries["candidate"], self.now)

    def test_duplicate_or_substituted_artifact_is_rejected(self):
        entries = copy.deepcopy(self.entries)
        entries["stable"]["id"] = entries["candidate"]["id"]
        with self.assertRaises(ValueError):
            protocol.make_release(self.context, entries, self.now)
        release = protocol.make_release(self.context, self.entries, self.now)
        for field, value in (("id", 99), ("digest", "sha256:" + "9" * 64),
                             ("content_digest", "sha256:" + "8" * 64), ("name", "other-attempt")):
            with self.subTest(field=field):
                altered = copy.deepcopy(release)
                altered["ready"]["candidate"][field] = value
                with self.assertRaises(ValueError):
                    protocol.validate_release(self.context, "candidate", altered, self.entries["candidate"], self.now)

    def test_late_release_and_invalid_lead_are_not_success(self):
        release = protocol.make_release(self.context, self.entries, self.now)
        with self.assertRaises(ValueError):
            protocol.validate_release(self.context, "candidate", release, self.entries["candidate"],
                                      self.now + dt.timedelta(seconds=181))
        release["scheduled_at_utc"] = self.now.isoformat()
        with self.assertRaises(ValueError):
            protocol.validate_release(self.context, "candidate", release, self.entries["candidate"], self.now)

    def test_wait_is_bounded_and_rechecks_liveness_and_run(self):
        transport = Mock()
        transport.find.return_value = None
        now = [self.now]
        def sleep(seconds):
            now[0] += dt.timedelta(seconds=seconds)
        with self.assertRaises(TimeoutError):
            protocol.wait_artifact(transport, "ready", "ready.json", self.now + dt.timedelta(seconds=90),
                                   lambda: True, lambda: now[0], sleep)
        self.assertEqual(transport.find.call_count, 2)
        self.assertEqual(transport.check_attempt.call_count, 2)
        with self.assertRaises(ValueError):
            protocol.wait_artifact(transport, "ready", "ready.json", self.now + dt.timedelta(seconds=90),
                                   lambda: False, lambda: self.now, sleep)
        transport.check_attempt.side_effect = ValueError("cancelled run")
        with self.assertRaises(ValueError):
            protocol.wait_artifact(transport, "ready", "ready.json", self.now + dt.timedelta(seconds=90),
                                   lambda: True, lambda: self.now, sleep)

    def test_artifact_arrival_and_transport_failure_are_distinct(self):
        transport = Mock()
        transport.find.side_effect = [None, self.entries["stable"]]
        now = [self.now]
        result = protocol.wait_artifact(transport, "ready", "ready.json", self.now + dt.timedelta(minutes=5),
                                       lambda: True, lambda: now[0],
                                       lambda seconds: now.__setitem__(0, now[0] + dt.timedelta(seconds=seconds)))
        self.assertEqual(result, self.entries["stable"])
        transport.find.side_effect = subprocess.CalledProcessError(1, ["gh", "api"])
        with self.assertRaises(subprocess.CalledProcessError):
            protocol.wait_artifact(transport, "ready", "ready.json", self.now + dt.timedelta(minutes=5),
                                   lambda: True, lambda: self.now, lambda _: None)

    def test_atomic_immutable_publication_and_duplicate_json_fields(self):
        with tempfile.TemporaryDirectory(dir=SCRATCH) as directory:
            target = Path(directory) / "ready.json"
            protocol.publish(target, {"id": 1})
            with self.assertRaises(FileExistsError):
                protocol.publish(target, {"id": 2})
            self.assertEqual(json.loads(target.read_text()), {"id": 1})
        with self.assertRaises(ValueError):
            protocol.decode(b'{"id":1,"id":2}')
        with self.assertRaises(ValueError):
            protocol.decode(b" " * (protocol.MAX_BYTES + 1))

    def archive(self, filename="ready.json", content=None):
        buffer = io.BytesIO()
        with zipfile.ZipFile(buffer, "w") as archive:
            archive.writestr(filename, content or json.dumps(self.entries["stable"]["document"]))
        return buffer.getvalue()

    def test_download_checks_identity_digest_size_and_zip_members(self):
        name = self.entries["stable"]["name"]
        archive = self.archive()
        metadata = {"total_count": 1, "artifacts": [{
            "id": 2, "name": name, "expired": False, "size_in_bytes": len(archive),
            "digest": protocol.digest(archive),
            "workflow_run": {"id": 123, "head_sha": "a" * 40},
        }]}
        transport = protocol.GitHubArtifacts(self.context)
        with patch.object(transport, "api", side_effect=[json.dumps(metadata).encode(), archive]):
            self.assertEqual(transport.find(name, "ready.json")["document"], self.entries["stable"]["document"])
        variants = []
        for field, value in (("name", "foreign"), ("expired", True), ("id", "../other"),
                             ("size_in_bytes", 65537), ("digest", "sha256:" + "0" * 64),
                             ("workflow_run", {"id": 124, "head_sha": "a" * 40})):
            altered = copy.deepcopy(metadata)
            altered["artifacts"][0][field] = value
            variants.append((altered, archive))
        wrong_zip = self.archive("../ready.json")
        altered = copy.deepcopy(metadata)
        altered["artifacts"][0]["digest"] = protocol.digest(wrong_zip)
        variants.append((altered, wrong_zip))
        variants.append(({"total_count": 2, "artifacts": []}, archive))
        for altered, payload in variants:
            with self.subTest(metadata=altered):
                with patch.object(transport, "api", side_effect=[json.dumps(altered).encode(), payload]):
                    with self.assertRaises(ValueError):
                        transport.find(name, "ready.json")

    def test_current_attempt_and_failed_peer_are_checked_without_write_permissions(self):
        run = {"id": 123, "run_attempt": 2, "head_sha": "a" * 40, "event": "workflow_dispatch",
               "path": ".github/workflows/rc-observation-real-azure.yml", "status": "in_progress"}
        jobs = {"total_count": 2, "jobs": [
            {"name": f"RC observation s3-basic-object-crud {role}", "head_sha": "a" * 40,
             "status": "in_progress"} for role in protocol.ROLES]}
        transport = protocol.GitHubArtifacts(self.context)
        with patch.object(transport, "api", side_effect=[json.dumps(run), json.dumps(jobs)]):
            transport.check_attempt(require_prepared_jobs=True)
        for status in ("queued", "waiting"):
            altered = copy.deepcopy(jobs)
            altered["jobs"][1]["status"] = status
            with patch.object(transport, "api", side_effect=[json.dumps(run), json.dumps(altered)]):
                transport.check_attempt()
            with patch.object(transport, "api", side_effect=[json.dumps(run), json.dumps(altered)]):
                with self.assertRaises(ValueError):
                    transport.check_attempt(require_prepared_jobs=True)
        with patch.object(transport, "api", side_effect=[json.dumps(run), json.dumps({"total_count": 0, "jobs": []})]):
            with self.assertRaises(ValueError):
                transport.check_attempt(require_prepared_jobs=True)
        jobs["jobs"][1]["status"] = "completed"
        with patch.object(transport, "api", side_effect=[json.dumps(run), json.dumps(jobs)]):
            with self.assertRaises(ValueError):
                transport.check_attempt()
        run["run_attempt"] = 1
        with patch.object(transport, "api", return_value=json.dumps(run)):
            with self.assertRaises(ValueError):
                transport.check_attempt()

    def test_supervisor_timeout_terminates_then_kills_only_its_owned_group(self):
        with tempfile.TemporaryDirectory(dir=SCRATCH) as directory:
            child = Mock(pid=4321)
            child.poll.return_value = None
            child.wait.side_effect = [subprocess.TimeoutExpired("test", 1),
                                      subprocess.TimeoutExpired("test", protocol.TERM_GRACE_SECONDS), 0]
            context = {**self.context, "end_to_end_deadline_utc": (protocol.utc() + dt.timedelta(minutes=10)).isoformat()}
            with patch.dict(os.environ, {"PRIVATE_ROOT": directory, "TEST_FILTER": "offline"}), \
                 patch.object(protocol.subprocess, "Popen", return_value=child), \
                 patch.object(protocol.signal, "signal"), patch.object(protocol.os, "killpg") as kill:
                protocol.supervise(Path(directory), context)
            self.assertEqual(kill.call_args_list[0].args, (4321, signal.SIGTERM))
            self.assertEqual(kill.call_args_list[1].args, (4321, signal.SIGKILL))
            self.assertEqual(child.wait.call_args_list[1].kwargs, {"timeout": 20})
            self.assertEqual(child.wait.call_args_list[2].kwargs, {"timeout": 5})
            self.assertEqual(json.loads((Path(directory) / "result.json").read_text()), {"exit_code": 124})

    def test_stop_signals_active_measurement_before_waiting_and_bounds_settlement(self):
        for settles in (True, False):
            with self.subTest(settles=settles), tempfile.TemporaryDirectory(dir=SCRATCH) as directory:
                root = Path(directory)
                protocol.publish(root / "process.json", {"pid": 1234, "start": "owned"})
                elapsed = [0]
                signals = []
                def kill(pid, number):
                    self.assertTrue((root / "abort").exists())
                    self.assertEqual(elapsed[0], 0, "measurement ignores abort; signal must be immediate")
                    signals.append((pid, number))
                def sleep(seconds):
                    self.assertEqual(signals, [(1234, signal.SIGTERM)])
                    elapsed[0] += seconds
                    if settles and elapsed[0] == 25:
                        protocol.publish(root / "result.json", {"exit_code": 124})
                with patch.dict(os.environ, {"AWS2AZURE_RC_OBSERVATION_READINESS_DIR": directory}), \
                     patch.object(protocol.sys, "argv", ["readiness", "stop"]), \
                     patch.object(protocol, "process_start", return_value="owned"), \
                     patch.object(protocol.os, "kill", side_effect=kill), \
                     patch.object(protocol.time, "monotonic", side_effect=lambda: elapsed[0]), \
                     patch.object(protocol.time, "sleep", side_effect=sleep):
                    if settles:
                        protocol.main()
                        self.assertEqual(protocol.decode((root / "result.json").read_bytes())["exit_code"], 124)
                    else:
                        with self.assertRaises(TimeoutError):
                            protocol.main()
                        self.assertFalse((root / "result.json").exists())
                self.assertEqual(elapsed[0], 25 if settles else 30)

    def test_stop_never_signals_reused_supervisor_pid(self):
        with tempfile.TemporaryDirectory(dir=SCRATCH) as directory:
            protocol.publish(Path(directory) / "process.json", {"pid": 1234, "start": "owned"})
            with patch.dict(os.environ, {"AWS2AZURE_RC_OBSERVATION_READINESS_DIR": directory}), \
                 patch.object(protocol.sys, "argv", ["readiness", "stop"]), \
                 patch.object(protocol, "process_start", return_value="replacement"), \
                 patch.object(protocol.os, "kill") as kill, patch.object(protocol.time, "sleep") as sleep:
                protocol.main()
            kill.assert_not_called()
            sleep.assert_not_called()

    def test_mid_measurement_signal_forwards_term_then_kill_without_success_receipt(self):
        with tempfile.TemporaryDirectory(dir=SCRATCH) as directory:
            child = Mock(pid=4321)
            child.poll.return_value = None
            handlers = {}
            waits = []
            def register(number, handler):
                handlers[number] = handler
            def wait(timeout):
                waits.append(timeout)
                if len(waits) == 1:
                    # An active measurement never observes the readiness abort file.
                    (Path(directory) / "abort").touch()
                    handlers[signal.SIGTERM](signal.SIGTERM, None)
                if len(waits) == 2:
                    raise subprocess.TimeoutExpired("test", timeout)
                return -signal.SIGKILL
            child.wait.side_effect = wait
            context = {**self.context, "end_to_end_deadline_utc": (protocol.utc() + dt.timedelta(hours=2)).isoformat()}
            with patch.dict(os.environ, {"PRIVATE_ROOT": directory, "TEST_FILTER": "offline"}), \
                 patch.object(protocol.subprocess, "Popen", return_value=child), \
                 patch.object(protocol.signal, "signal", side_effect=register), \
                 patch.object(protocol.os, "killpg") as kill:
                protocol.supervise(Path(directory), context)
            self.assertGreater(waits[0], 3600)
            self.assertEqual(waits[1:], [20, 5])
            self.assertEqual([call.args for call in kill.call_args_list],
                             [(4321, signal.SIGTERM), (4321, signal.SIGKILL)])
            self.assertEqual(protocol.decode((Path(directory) / "result.json").read_bytes())["exit_code"], 124)

    def test_cancellation_during_spawn_still_settles_the_owned_child(self):
        with tempfile.TemporaryDirectory(dir=SCRATCH) as directory:
            child = Mock(pid=4321)
            child.poll.return_value = None
            child.wait.return_value = 0
            handlers = {}
            def register(number, handler):
                handlers[number] = handler
            def spawn(*_args, **_kwargs):
                handlers[signal.SIGTERM](signal.SIGTERM, None)
                return child
            context = {**self.context, "end_to_end_deadline_utc": (protocol.utc() + dt.timedelta(minutes=10)).isoformat()}
            with patch.dict(os.environ, {"PRIVATE_ROOT": directory, "TEST_FILTER": "offline"}), \
                 patch.object(protocol.subprocess, "Popen", side_effect=spawn), \
                 patch.object(protocol.signal, "signal", side_effect=register), \
                 patch.object(protocol.os, "killpg") as kill:
                protocol.supervise(Path(directory), context)
            kill.assert_called_once_with(4321, signal.SIGTERM)
            child.wait.assert_called_once_with(timeout=20)
            self.assertEqual(json.loads((Path(directory) / "result.json").read_text()), {"exit_code": 124})

    def test_assembly_preserves_and_checks_actual_start_not_fabricated_synchrony(self):
        with tempfile.TemporaryDirectory(dir=SCRATCH) as directory:
            folders = {role: Path(directory) / role for role in protocol.ROLES}
            release = protocol.make_release(self.context, self.entries, self.now)
            captures = {}
            for index, role in enumerate(protocol.ROLES):
                folder = folders[role] / "readiness"
                folder.mkdir(parents=True)
                protocol.publish(folder / "ready.json", self.entries[role]["document"])
                content_digest = protocol.digest((folder / "ready.json").read_bytes())
                release["ready"][role]["content_digest"] = content_digest
                actual = self.now + dt.timedelta(seconds=120 + index * 2)
                protocol.publish(folder / "started.json", {
                    "schema_version": 1, "context_id": self.context["context_id"], "cohort": role,
                    "ready_content_digest": content_digest, "actual_at_utc": actual.isoformat(),
                    "scheduled_at_utc": release["scheduled_at_utc"],
                })
                captures[role] = {
                    "observation": {"started_at_utc": release["scheduled_at_utc"],
                                    "measurement_ended_at_utc": (actual + dt.timedelta(minutes=60)).isoformat(),
                                    "requested_window_minutes": 60},
                    "cohort": {"runtime_digest": self.entries[role]["document"]["runtime_digest"],
                               "runtime_identity_digest": self.entries[role]["document"]["runtime_identity_digest"]},
                }
            for folder in folders.values():
                protocol.publish(folder / "readiness/release-artifact.json", {"id": 9, "document": release})
            result = protocol.validate_captured_readiness(folders["candidate"], folders["stable"], captures)
            self.assertEqual(result["reported_start_skew_seconds"], 2)
            stable_root = folders["stable"] / "readiness"
            for filename, field, value in (
                ("started.json", "context_id", "foreign"),
                ("started.json", "ready_content_digest", "sha256:" + "0" * 64),
                ("started.json", "actual_at_utc", (self.now + dt.timedelta(seconds=181)).isoformat()),
                ("release-artifact.json", "id", 10),
                ("ready.json", "runtime_digest", "sha256:" + "0" * 64),
            ):
                path = stable_root / filename
                original = path.read_bytes()
                changed = json.loads(original)
                changed[field] = value
                path.write_text(json.dumps(changed))
                with self.subTest(filename=filename, field=field):
                    with self.assertRaises(ValueError):
                        protocol.validate_captured_readiness(folders["candidate"], folders["stable"], captures)
                path.write_bytes(original)
            captures["stable"]["observation"]["measurement_ended_at_utc"] = (self.now + dt.timedelta(minutes=1)).isoformat()
            with self.assertRaises(ValueError):
                protocol.validate_captured_readiness(folders["candidate"], folders["stable"], captures)


if __name__ == "__main__":
    unittest.main()
