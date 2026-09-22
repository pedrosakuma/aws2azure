#!/usr/bin/env python3
"""Current-attempt immutable artifact rendezvous; never provisions Azure."""
import datetime as dt
import hashlib
import io
import json
import os
from pathlib import Path
import re
import signal
import subprocess
import sys
import time
import zipfile

WAIT_SECONDS = 45 * 60
LEAD_SECONDS = 120
LATE_SECONDS = 60
POLL_SECONDS = 45
TERM_GRACE_SECONDS = 20
KILL_GRACE_SECONDS = 5
STOP_SECONDS = TERM_GRACE_SECONDS + KILL_GRACE_SECONDS + 5
MAX_BYTES = 32768
ROLES = ("candidate", "stable")
PROFILES = ("s3-basic-object-crud", "secretsmanager-basic-lifecycle")


def require(condition, message):
    if not condition:
        raise ValueError(message)


def digest(data):
    return "sha256:" + hashlib.sha256(data).hexdigest()


def utc():
    return dt.datetime.now(dt.timezone.utc)


def timestamp(value):
    result = dt.datetime.fromisoformat(value.replace("Z", "+00:00"))
    require(result.utcoffset() == dt.timedelta(0), "timestamp must be UTC")
    return result


def pairs(items):
    result = {}
    for key, value in items:
        require(key not in result, "duplicate JSON field")
        result[key] = value
    return result


def decode(data):
    require(len(data) <= MAX_BYTES, "metadata exceeds size bound")
    result = json.loads(data, object_pairs_hook=pairs)
    require(isinstance(result, dict), "metadata must be an object")
    return result


def publish(path, value):
    path = Path(path)
    data = json.dumps(value, sort_keys=True, separators=(",", ":")).encode()
    require(len(data) <= MAX_BYTES, "metadata exceeds size bound")
    pending = path.with_suffix(path.suffix + ".pending")
    with pending.open("xb") as stream:
        stream.write(data)
        stream.flush()
        os.fsync(stream.fileno())
    # link is atomic and refuses replacement, unlike POSIX rename.
    os.link(pending, path)
    pending.unlink()


def artifact_name(context, kind, role=None):
    suffix = "" if role is None else "-" + role
    return (f"rc-observation-{kind}-{context['profile']}{suffix}"
            f"-run-{context['run_id']}-attempt-{context['run_attempt']}")


def validate_ready(context, role, value, now):
    require(type(value.get("schema_version")) is int and value["schema_version"] == 1
            and value.get("context_id") == context["context_id"],
            "foreign readiness context")
    require(value.get("cohort") == role and value.get("stage") == "sealed-runtime-and-canary-verified",
            "wrong cohort or preparation stage")
    require(re.fullmatch("[0-9a-f]{32}", value.get("instance", "")) is not None,
            "invalid ready instance")
    require(type(value.get("harness_pid")) is int and value["harness_pid"] > 0, "invalid harness PID")
    expected = context["runtimes"][role]
    require(value.get("runtime_identity_digest") == expected["identity_digest"]
            and value.get("runtime_digest") == expected["runtime_digest"], "runtime identity drift")
    ready = timestamp(value["ready_at_utc"])
    deadline = timestamp(value["readiness_deadline_utc"])
    require(-LATE_SECONDS <= (now - ready).total_seconds() <= WAIT_SECONDS,
            "stale or future readiness")
    require(ready < deadline <= timestamp(context["end_to_end_deadline_utc"]),
            "invalid readiness deadline")
    require((deadline - ready).total_seconds() <= WAIT_SECONDS, "unbounded readiness wait")
    require(now + dt.timedelta(seconds=LEAD_SECONDS) < deadline, "insufficient release budget")


def make_release(context, entries, now):
    require(set(entries) == set(ROLES), "both prepared cohorts are required")
    for role in ROLES:
        validate_ready(context, role, entries[role]["document"], now)
    require(entries["candidate"]["id"] != entries["stable"]["id"], "duplicate ready artifact")
    return {
        "schema_version": 1, "context_id": context["context_id"],
        "released_at_utc": now.isoformat(),
        "scheduled_at_utc": (now + dt.timedelta(seconds=LEAD_SECONDS)).isoformat(),
        "ready": {role: {key: entries[role][key] for key in ("id", "name", "digest", "content_digest")}
                  for role in ROLES},
    }


def validate_release(context, role, value, own, now):
    require(type(value.get("schema_version")) is int and value["schema_version"] == 1
            and value.get("context_id") == context["context_id"],
            "foreign release context")
    require(set(value.get("ready", {})) == set(ROLES), "release must bind both cohorts")
    for member in ROLES:
        entry = value["ready"][member]
        require(entry.get("name") == artifact_name(context, "ready", member), "wrong ready artifact name")
        require(type(entry.get("id")) is int and entry["id"] > 0, "invalid ready artifact ID")
        for field in ("digest", "content_digest"):
            require(re.fullmatch("sha256:[0-9a-f]{64}", entry.get(field, "")) is not None,
                    "invalid ready digest")
    require(value["ready"][role] == {key: own[key] for key in ("id", "name", "digest", "content_digest")},
            "release does not reference this exact immutable ready artifact")
    require(value["ready"]["candidate"]["id"] != value["ready"]["stable"]["id"], "duplicate ready artifact")
    scheduled, released = timestamp(value["scheduled_at_utc"]), timestamp(value["released_at_utc"])
    require((scheduled - released).total_seconds() == LEAD_SECONDS, "invalid release lead")
    require(released <= now + dt.timedelta(seconds=LATE_SECONDS)
            and scheduled >= now - dt.timedelta(seconds=LATE_SECONDS), "late or future release")
    require(scheduled < timestamp(context["readiness_deadline_utc"]), "release exceeds local deadline")


class GitHubArtifacts:
    def __init__(self, context):
        self.context = context
        self.root = f"/repos/{context['repository']}/actions"

    def api(self, path):
        result = subprocess.run(["gh", "api", path], check=True, capture_output=True, timeout=30)
        return result.stdout

    def check_attempt(self, require_prepared_jobs=False):
        c = self.context
        run = json.loads(self.api(f"{self.root}/runs/{c['run_id']}/attempts/{c['run_attempt']}"))
        require(str(run["id"]) == c["run_id"] and str(run["run_attempt"]) == c["run_attempt"]
                and run["head_sha"] == c["workflow_source_sha"]
                and run["event"] == "workflow_dispatch"
                and run["path"] == ".github/workflows/rc-observation-real-azure.yml"
                and run["status"] in ("queued", "in_progress"), "run attempt is not the active observation")
        jobs = json.loads(self.api(f"{self.root}/runs/{c['run_id']}/attempts/{c['run_attempt']}/jobs?per_page=100"))
        require(jobs["total_count"] == len(jobs["jobs"]) <= 100, "incomplete current-attempt job listing")
        for role in ROLES:
            name = f"RC observation {c['profile']} {role}"
            matches = [job for job in jobs["jobs"] if job["name"] == name]
            require(len(matches) <= 1, "ambiguous cohort job")
            if require_prepared_jobs:
                require(len(matches) == 1 and matches[0]["status"] == "in_progress",
                        "both prepared cohort jobs must still be running")
            if matches:
                require(matches[0]["head_sha"] == c["workflow_source_sha"]
                        and matches[0]["status"] in ("queued", "waiting", "in_progress"),
                        "peer cohort stopped before release")

    def find(self, name, filename):
        c = self.context
        metadata = json.loads(self.api(f"{self.root}/runs/{c['run_id']}/artifacts?name={name}&per_page=100"))
        require(metadata["total_count"] <= 1, "duplicate artifact publication")
        artifacts = metadata["artifacts"]
        require(len(artifacts) == metadata["total_count"], "incomplete artifact listing")
        if not artifacts:
            return None
        artifact = artifacts[0]
        require(type(artifact.get("id")) is int and artifact["id"] > 0, "invalid artifact ID")
        require(artifact["name"] == name and not artifact["expired"], "invalid artifact metadata")
        require(0 < artifact["size_in_bytes"] <= 65536, "artifact exceeds bound")
        require(artifact["workflow_run"]["id"] == int(c["run_id"])
                and artifact["workflow_run"]["head_sha"] == c["workflow_source_sha"], "foreign artifact producer")
        archive = self.api(f"{self.root}/artifacts/{artifact['id']}/zip")
        require(len(archive) <= 65536 and digest(archive) == artifact.get("digest"), "artifact digest mismatch")
        with zipfile.ZipFile(io.BytesIO(archive)) as zipped:
            require(zipped.namelist() == [filename], "unexpected artifact members")
            require(zipped.getinfo(filename).file_size <= MAX_BYTES, "artifact member exceeds bound")
            content = zipped.read(filename)
        return {"id": artifact["id"], "name": name, "digest": artifact["digest"],
                "content_digest": digest(content), "document": decode(content)}


def wait_artifact(transport, name, filename, deadline, alive, now=utc, sleep=time.sleep):
    monotonic_deadline = time.monotonic() + WAIT_SECONDS
    while now() < deadline and time.monotonic() < monotonic_deadline:
        require(alive(), "local prepared harness exited")
        transport.check_attempt()
        result = transport.find(name, filename)
        if result is not None:
            return result
        sleep(min(POLL_SECONDS, max(0, (deadline - now()).total_seconds())))
    raise TimeoutError("bounded readiness wait expired")


def process_start(pid):
    try:
        # Fields after comm (which may contain spaces): state is field 3, starttime is 22.
        fields = Path(f"/proc/{pid}/stat").read_text().rsplit(")", 1)[1].split()
        return None if fields[0] == "Z" else fields[19]
    except FileNotFoundError:
        return None


def validate_captured_readiness(candidate_directory, stable_directory, captures):
    """Require both actual-start receipts without changing schema-1's scheduled boundary."""
    releases = []
    actual_starts = []
    for role, directory in zip(ROLES, (candidate_directory, stable_directory)):
        root = directory / "readiness"
        ready_bytes = (root / "ready.json").read_bytes()
        ready = decode(ready_bytes)
        started = decode((root / "started.json").read_bytes())
        artifact = decode((root / "release-artifact.json").read_bytes())
        released = artifact["document"]
        require(started["schema_version"] == 1 and started["cohort"] == ready["cohort"] == role,
                "wrong actual-start cohort")
        require(started["context_id"] == ready["context_id"] == released["context_id"], "mixed readiness contexts")
        require(started["ready_content_digest"] == released["ready"][role]["content_digest"] == digest(ready_bytes),
                "actual start is not bound to the released ready instance")
        scheduled = timestamp(released["scheduled_at_utc"])
        actual = timestamp(started["actual_at_utc"])
        require(timestamp(started["scheduled_at_utc"]) == scheduled
                and 0 <= (actual - scheduled).total_seconds() <= LATE_SECONDS, "late actual start")
        capture = captures[role]
        require(timestamp(capture["observation"]["started_at_utc"]) == scheduled, "scheduled attribution drift")
        require(timestamp(capture["observation"]["measurement_ended_at_utc"]) - actual
                >= dt.timedelta(minutes=capture["observation"]["requested_window_minutes"]),
                "measurement did not cover its full requested duration")
        require(capture["cohort"]["runtime_digest"] == ready["runtime_digest"]
                and capture["cohort"]["runtime_identity_digest"] == ready["runtime_identity_digest"],
                "captured runtime differs from prepared runtime")
        releases.append(artifact)
        actual_starts.append(actual)
    require(releases[0] == releases[1], "cohorts consumed different immutable releases")
    skew = abs((actual_starts[0] - actual_starts[1]).total_seconds())
    require(skew <= LATE_SECONDS, "reported start skew exceeds tolerance")
    return {"schema_version": 1, "context_id": releases[0]["document"]["context_id"],
            "reported_start_skew_seconds": skew,
            "scope": "UTC-reported measurement starts; runner clock offsets are not independently calibrated."}


def alive(directory):
    if (directory / "result.json").exists() or (directory / "abort").exists():
        return False
    state = decode((directory / "process.json").read_bytes())
    return state["start"] is not None and process_start(state["pid"]) == state["start"]


def initialize(directory):
    env = os.environ
    require(env["PROFILE"] in PROFILES and env["COHORT"] in ROLES, "invalid profile/cohort")
    for name in ("GITHUB_RUN_ID", "GITHUB_RUN_ATTEMPT"):
        require(re.fullmatch("[1-9][0-9]*", env[name]) is not None, "invalid run identity")
    require(re.fullmatch("[0-9a-f]{40}", env["GITHUB_SHA"]) is not None, "invalid workflow source")
    require(re.fullmatch("[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+", env["GITHUB_REPOSITORY"]) is not None,
            "invalid repository")
    window = int(env["WINDOW_MINUTES"])
    require(60 <= window <= 180, "invalid observation window")
    end = timestamp(env["OBSERVATION_DEADLINE_UTC"])
    deadline = min(utc() + dt.timedelta(seconds=WAIT_SECONDS),
                   end - dt.timedelta(minutes=window + 22))
    require(deadline > utc() + dt.timedelta(seconds=LEAD_SECONDS), "end-to-end budget exhausted")
    context = {
        "schema_version": 1,
        "repository": env["GITHUB_REPOSITORY"], "run_id": env["GITHUB_RUN_ID"],
        "run_attempt": env["GITHUB_RUN_ATTEMPT"], "workflow_source_sha": env["GITHUB_SHA"],
        "profile": env["PROFILE"], "release_candidate_id": env["RELEASE_CANDIDATE_ID"],
        "candidate_source_sha": env["CANDIDATE_SOURCE_SHA"], "window_minutes": window,
        "end_to_end_deadline_utc": end.isoformat(), "runtimes": {},
    }
    for role, filename in (("candidate", "candidate-runtime.json"), ("stable", "prior-runtime.json")):
        raw = (directory.parent / filename).read_bytes()
        identity = json.loads(raw)
        context["runtimes"][role] = {
            "identity_digest": digest(raw), "runtime_digest": identity["runtime"]["aggregate_digest"],
        }
    context["context_id"] = digest(json.dumps(context, sort_keys=True, separators=(",", ":")).encode())
    context["readiness_deadline_utc"] = deadline.isoformat()
    directory.mkdir(parents=True, exist_ok=False)
    publish(directory / "context.json", context)
    return context


def supervise(directory, context):
    budget = (timestamp(context["end_to_end_deadline_utc"]) - utc()).total_seconds() - 120
    require(budget > 0, "end-to-end budget exhausted")
    command = ["dotnet", "test", "tests/Aws2Azure.IntegrationTests", "-c", "Release", "--no-build",
               "--nologo", "--filter", os.environ["TEST_FILTER"], "--logger", "console;verbosity=normal"]
    interrupted_during_spawn = False
    def spawning_interrupted(_signum, _frame):
        nonlocal interrupted_during_spawn
        interrupted_during_spawn = True
    def interrupted(_signum, _frame):
        raise InterruptedError("coordinator terminated the owned harness")
    signal.signal(signal.SIGTERM, spawning_interrupted)
    signal.signal(signal.SIGINT, spawning_interrupted)
    with (Path(os.environ["PRIVATE_ROOT"]) / "cohort-harness.log").open("xb") as log:
        process = subprocess.Popen(command, stdout=log, stderr=subprocess.STDOUT, start_new_session=True)
        try:
            signal.signal(signal.SIGTERM, interrupted)
            signal.signal(signal.SIGINT, interrupted)
            if interrupted_during_spawn:
                raise InterruptedError("coordinator cancelled harness startup")
            code = process.wait(timeout=budget)
        except (subprocess.TimeoutExpired, InterruptedError):
            signal.signal(signal.SIGTERM, signal.SIG_IGN)
            signal.signal(signal.SIGINT, signal.SIG_IGN)
            if process.poll() is None:
                os.killpg(process.pid, signal.SIGTERM)
            try:
                process.wait(timeout=TERM_GRACE_SECONDS)
            except subprocess.TimeoutExpired:
                if process.poll() is None:
                    os.killpg(process.pid, signal.SIGKILL)
                process.wait(timeout=KILL_GRACE_SECONDS)
            code = 124
    publish(directory / "result.json", {"exit_code": code})


def main():
    mode = sys.argv[1]
    if mode == "stop" and not os.environ.get("AWS2AZURE_RC_OBSERVATION_READINESS_DIR"):
        return  # Preparation failed before any harness could be launched.
    directory = Path(os.environ["AWS2AZURE_RC_OBSERVATION_READINESS_DIR"])
    if mode == "launch":
        context = initialize(directory)
        with (Path(os.environ["PRIVATE_ROOT"]) / "cohort-supervisor.log").open("xb") as log:
            process = subprocess.Popen([sys.executable, __file__, "supervise"], stdout=log,
                                       stderr=subprocess.STDOUT, start_new_session=True)
        try:
            started = process_start(process.pid)
            require(started is not None, "supervisor exited during startup")
            publish(directory / "process.json", {"pid": process.pid, "start": started})
        except (ValueError, OSError):
            process.terminate()
            process.wait(timeout=STOP_SECONDS)
            raise
        deadline = timestamp(context["readiness_deadline_utc"])
        monotonic_deadline = time.monotonic() + WAIT_SECONDS
        while not (directory / "ready.json").exists():
            require(alive(directory), "harness failed before readiness; inspect harness.log")
            require(utc() < deadline and time.monotonic() < monotonic_deadline, "harness readiness timed out")
            time.sleep(1)
        validate_ready(context, os.environ["COHORT"], decode((directory / "ready.json").read_bytes()), utc())
        return
    if mode == "stop":
        if not (directory / "process.json").exists():
            return
        (directory / "abort").touch(exist_ok=True)
        state = decode((directory / "process.json").read_bytes())
        if (directory / "result.json").exists() or process_start(state["pid"]) != state["start"]:
            return
        # Measurement does not poll abort; reserve cancellation grace for Azure cleanup.
        deadline = time.monotonic() + STOP_SECONDS
        os.kill(state["pid"], signal.SIGTERM)
        while time.monotonic() < deadline:
            if (directory / "result.json").exists() or process_start(state["pid"]) != state["start"]:
                return
            time.sleep(min(1, max(0, deadline - time.monotonic())))
        raise TimeoutError("owned harness did not settle after abort and termination")
    context = decode((directory / "context.json").read_bytes())
    if mode == "supervise":
        supervise(directory, context)
        return
    if mode == "finish":
        deadline = timestamp(context["end_to_end_deadline_utc"])
        while not (directory / "result.json").exists():
            require(alive(directory) and utc() < deadline, "harness exited or exceeded its end-to-end budget")
            time.sleep(5)
        sys.stdout.write((Path(os.environ["PRIVATE_ROOT"]) / "cohort-harness.log").read_text())
        require(decode((directory / "result.json").read_bytes())["exit_code"] == 0, "cohort harness failed")
        return
    require(mode in ("coordinate", "accept"), "unknown readiness command")
    role = os.environ["COHORT"]
    transport = GitHubArtifacts(context)
    deadline = timestamp(context["readiness_deadline_utc"])
    def get(kind, member, filename):
        return wait_artifact(transport, artifact_name(context, kind, member), filename,
                             deadline, lambda: alive(directory))
    own = get("ready", role, "ready.json")
    require(own["content_digest"] == digest((directory / "ready.json").read_bytes()), "local ready upload mismatch")
    if mode == "coordinate":
        require(role == "candidate", "only candidate coordinates release")
        stable = get("ready", "stable", "ready.json")
        transport.check_attempt(require_prepared_jobs=True)
        publish(directory / "release-publication.json", make_release(context, {"candidate": own, "stable": stable}, utc()))
    else:
        released = get("start", None, "release-publication.json")
        validate_release(context, role, released["document"], own, utc())
        publish(directory / "release-artifact.json", released)
        publish(directory / "release.json", released["document"])


if __name__ == "__main__":
    try:
        main()
    except (ValueError, KeyError, OSError, TimeoutError, subprocess.SubprocessError) as error:
        print(f"::error::Readiness failed: {type(error).__name__}: {error}", file=sys.stderr)
        sys.exit(1)
