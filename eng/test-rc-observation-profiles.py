#!/usr/bin/env python3
"""Execute RC profile routing and backend export with offline Azure responses."""
import datetime as dt
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
SCRIPT = (ROOT / ".github/scripts/rc-observation-run-cohort.sh").read_text()
SCRATCH = ROOT / ".tools/rc-profile-tests"
SCRATCH.mkdir(parents=True, exist_ok=True)
APPEND = SCRIPT[SCRIPT.index("append_env()"):SCRIPT.index('policy="docs/')]


class ProfileRoutingTests(unittest.TestCase):
    def test_prepare_selects_exact_fixture_profile_bicep_and_filter_before_provisioning(self):
        preflight = SCRIPT[:SCRIPT.index('if [ -z "${AZURE_CLIENT_ID:-}" ]')]
        for profile, service, category in (
            ("dynamodb-basic-crud", "dynamodb", "DynamoDbRcObservation"),
            ("sqs-standard-messaging", "sqs", "SqsRcObservation"),
            ("s3-basic-object-crud", "s3", "S3RcObservation"),
            ("secretsmanager-basic-lifecycle", "secretsmanager", "SecretsManagerRcObservation"),
        ):
            with self.subTest(profile=profile), tempfile.TemporaryDirectory(dir=SCRATCH) as directory:
                output = Path(directory) / "env"
                env = {**os.environ, "PROFILE": profile, "WINDOW_MINUTES": "60",
                       "GITHUB_ENV": str(output),
                       "OBSERVATION_DEADLINE_UTC": (dt.datetime.now(dt.timezone.utc) + dt.timedelta(minutes=179)).isoformat()}
                result = subprocess.run(["bash", "-c", preflight], cwd=ROOT, env=env, text=True, capture_output=True)
                self.assertEqual(result.returncode, 0, result.stderr)
                settings = dict(line.split("=", 1) for line in output.read_text().splitlines())
                self.assertEqual(settings["AWS2AZURE_QUALIFICATION_PROFILE"], profile)
                self.assertEqual(settings["BICEP_PATH"], f"deploy/realazure/{service}-load.bicep")
                self.assertEqual(settings["TEST_FILTER"], f"Category={category}")
                for overrides in ({"PROFILE": "dynamodb-single-partition-transactions"},
                                  {"OBSERVATION_DEADLINE_UTC": "2000-01-01T00:00:00Z"},
                                  {"WINDOW_MINUTES": "1"}):
                    rejected = subprocess.run(["bash", "-c", preflight], cwd=ROOT,
                        env={**env, **overrides}, text=True, capture_output=True)
                    self.assertNotEqual(rejected.returncode, 0)

    def test_new_backend_exports_are_exact_and_missing_outputs_fail_without_blob_fallback(self):
        start = SCRIPT.index('\nif [ "$PROFILE" = secretsmanager-basic-lifecycle ]; then', SCRIPT.index("wait_for_vault_rbac()"))
        end = SCRIPT.index('\nif [ "$PROFILE" = secretsmanager-basic-lifecycle ]; then', start + 1)
        backend_script = "set -euo pipefail\n" + APPEND + SCRIPT[start:end]
        for profile, values, names in (
            ("dynamodb-basic-crud",
             {"cosmosAccountName": "owned-cosmos", "cosmosDatabaseName": "owned-database",
              "cosmosEndpoint": "https://owned.invalid/", "primaryMasterKey": "offline-cosmos-key"},
             {"AZURE_COSMOS_ENDPOINT", "AZURE_COSMOS_DATABASE", "AZURE_COSMOS_KEY"}),
            ("sqs-standard-messaging",
             {"serviceBusNamespaceName": "owned-servicebus", "primaryConnectionString": "offline-sb-connection"},
             {"AZURE_SB_CONNSTR"}),
        ):
            with self.subTest(profile=profile), tempfile.TemporaryDirectory(dir=SCRATCH) as directory:
                root = Path(directory)
                az = root / "az"
                az.write_text("""#!/usr/bin/env python3
import json, os, sys
from pathlib import Path
args = sys.argv[1:]
with open(os.environ["AZ_LOG"], "a") as log: log.write(json.dumps(args) + "\\n")
if args[:3] not in (["deployment", "group", "show"], ["cosmosdb", "keys", "list"], ["servicebus", "namespace", "authorization-rule"]):
    raise SystemExit("unexpected or destructive Azure invocation")
query = args[args.index("--query") + 1]
key = query.split(".")[2] if query.startswith("properties.outputs.") else query
print(json.loads(Path(os.environ["AZ_VALUES"]).read_text()).get(key, ""))
""")
                az.chmod(0o700)
                metadata = root / "values.json"
                output = root / "env"
                env = {**os.environ, "PATH": f"{root}:{os.environ['PATH']}", "PROFILE": profile,
                       "GITHUB_ENV": str(output), "RG_NAME": "owned-group", "DEPLOYMENT_NAME": "owned-deployment",
                       "AZ_LOG": str(root / "calls.jsonl"), "AZ_VALUES": str(metadata)}
                metadata.write_text(json.dumps(values))
                result = subprocess.run(["bash", "-c", backend_script], env=env, cwd=ROOT, text=True, capture_output=True)
                self.assertEqual(result.returncode, 0, result.stderr)
                settings = dict(line.split("=", 1) for line in output.read_text().splitlines())
                self.assertEqual(set(settings), names)
                self.assertIn("::add-mask::", result.stdout)
                for missing in values:
                    metadata.write_text(json.dumps({key: value for key, value in values.items() if key != missing}))
                    output.unlink(missing_ok=True)
                    result = subprocess.run(["bash", "-c", backend_script], env=env, cwd=ROOT, text=True, capture_output=True)
                    self.assertNotEqual(result.returncode, 0, missing)
                    self.assertFalse(output.exists(), missing)
                self.assertNotIn("storage", (root / "calls.jsonl").read_text())


if __name__ == "__main__":
    unittest.main()
