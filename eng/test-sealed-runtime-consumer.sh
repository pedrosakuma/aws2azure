#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
scratch="$repo_root/artifacts/test-sealed-runtime-consumer-$$"
trap 'rm -rf "$scratch"' EXIT
install -d -m 0700 "$scratch/publish" "$scratch/mock" "$scratch/mock-bin"

repository="example/repository"
source_sha="0123456789abcdef0123456789abcdef01234567"
run_id=123456789
run_attempt=2
run_started_at="2026-07-17T18:00:00Z"
produced_at="2026-07-17T18:01:00Z"
printf '#!/usr/bin/env bash\nexit 0\n' > "$scratch/publish/Aws2Azure.Proxy"
chmod 0700 "$scratch/publish/Aws2Azure.Proxy"
printf '{"Logging":{"LogLevel":{"Default":"Information"}}}\n' \
  > "$scratch/publish/appsettings.json"

export SEALED_REPOSITORY="$repository"
export SEALED_GIT_SHA="$source_sha"
export SEALED_GIT_REF="refs/heads/main"
export SEALED_SERVER_URL="https://github.com"
export SEALED_WORKFLOW_REF="$repository/.github/workflows/sealed-runtime.yml@refs/heads/main"
export SEALED_RUN_ID="$run_id"
export SEALED_RUN_ATTEMPT="$run_attempt"
export SEALED_RUN_STARTED_AT="$run_started_at"
export SEALED_PRODUCED_AT="$produced_at"
"$repo_root/eng/sealed-runtime-manifest.sh" generate \
  "$scratch/publish" "$scratch/bundle" >/dev/null

manifest="$scratch/bundle/sealed-runtime-manifest.json"
artifact_name="$(jq -er '.artifact.name' "$manifest")"
archive_name="$(jq -er '.artifact.archive_name' "$manifest")"
runtime_digest="$(jq -er '.runtime.aggregate_digest' "$manifest")"
executable_digest="$(jq -er '.runtime.executable.sha256' "$manifest")"
manifest_digest="sha256:$(sha256sum "$manifest" | cut -d' ' -f1)"
tar \
  --format=gnu \
  --sort=name \
  --owner=0 \
  --group=0 \
  --numeric-owner \
  --mtime='@0' \
  -cf "$scratch/$archive_name" \
  -C "$scratch/bundle" \
  .
python3 - "$scratch/$archive_name" "$scratch/mock/artifact.zip" <<'PY'
import pathlib
import sys
import zipfile

source = pathlib.Path(sys.argv[1])
with zipfile.ZipFile(sys.argv[2], "w", compression=zipfile.ZIP_STORED) as archive:
    archive.write(source, source.name)
PY
upload_digest="sha256:$(sha256sum "$scratch/mock/artifact.zip" | cut -d' ' -f1)"

jq -n \
  --argjson id "$run_id" \
  --argjson attempt "$run_attempt" \
  --arg sha "$source_sha" \
  --arg repository "$repository" \
  --arg started "$run_started_at" \
  '{
    id: $id,
    run_attempt: $attempt,
    event: "workflow_dispatch",
    status: "completed",
    conclusion: "success",
    path: ".github/workflows/sealed-runtime.yml",
    head_sha: $sha,
    head_branch: "main",
    repository: { full_name: $repository },
    head_repository: { full_name: $repository },
    run_started_at: $started
  }' > "$scratch/mock/run.json"
jq -n \
  --argjson run_id "$run_id" \
  --arg sha "$source_sha" \
  --arg name "$artifact_name" \
  --arg digest "$upload_digest" \
  '{
    total_count: 1,
    artifacts: [{
      id: 77,
      name: $name,
      expired: false,
      digest: $digest,
      created_at: "2026-07-17T18:01:00Z",
      expires_at: "2099-07-17T18:01:00Z",
      workflow_run: {
        id: $run_id,
        head_sha: $sha
      }
    }]
  }' > "$scratch/mock/artifacts.json"
printf '{"name":"main","protected":true}\n' > "$scratch/mock/branch.json"
cat > "$scratch/mock/ruleset-pages.json" <<'JSON'
[
  [
    {
      "id": 19148912,
      "name": "Protect release candidate tags",
      "target": "tag",
      "source_type": "Repository",
      "source": "example/repository",
      "enforcement": "active",
      "node_id": "RRS_compact",
      "_links": {
        "self": {
          "href": "https://api.github.com/repos/example/repository/rulesets/19148912"
        },
        "html": {
          "href": "https://github.com/example/repository/rules/19148912"
        }
      },
      "created_at": "2026-07-18T18:13:18.367Z",
      "updated_at": "2026-07-18T18:13:18.386Z"
    }
  ],
  [
    {
      "id": 19148913,
      "name": "Protect internal tags",
      "target": "tag",
      "source_type": "Repository",
      "source": "example/repository",
      "enforcement": "active",
      "node_id": "RRS_compact_other",
      "_links": {
        "self": {
          "href": "https://api.github.com/repos/example/repository/rulesets/19148913"
        },
        "html": {
          "href": "https://github.com/example/repository/rules/19148913"
        }
      },
      "created_at": "2026-07-18T18:14:59.000Z",
      "updated_at": "2026-07-18T18:15:00.000Z"
    }
  ]
]
JSON
cat > "$scratch/mock/ruleset-19148912.json" <<'JSON'
{
  "id": 19148912,
  "name": "Protect release candidate tags",
  "target": "tag",
  "source_type": "Repository",
  "source": "example/repository",
  "enforcement": "active",
  "conditions": {
    "ref_name": {
      "exclude": [],
      "include": ["refs/tags/v*-rc.*"]
    }
  },
  "rules": [
    {"type": "deletion"},
    {"type": "non_fast_forward"}
  ],
  "updated_at": "2026-07-18T18:13:18.386Z"
}
JSON
cat > "$scratch/mock/ruleset-19148913.json" <<'JSON'
{
  "id": 19148913,
  "name": "Protect internal tags",
  "target": "tag",
  "source_type": "Repository",
  "source": "example/repository",
  "enforcement": "active",
  "conditions": {
    "ref_name": {
      "exclude": [],
      "include": ["refs/tags/internal-*"]
    }
  },
  "rules": [
    {"type": "deletion"},
    {"type": "non_fast_forward"}
  ],
  "updated_at": "2026-07-18T18:15:00.000Z"
}
JSON

install -d -m 0700 "$scratch/evidence/artifacts/workload"
printf '{"candidate":{"qualification_mode":"sealed"}}\n' \
  > "$scratch/evidence/artifacts/workload/load-evidence.json"
python3 - "$scratch/evidence" "$scratch/mock/evidence.zip" <<'PY'
import pathlib
import sys
import zipfile

root = pathlib.Path(sys.argv[1])
with zipfile.ZipFile(sys.argv[2], "w", compression=zipfile.ZIP_STORED) as archive:
    for path in root.rglob("*"):
        if path.is_file():
            archive.write(path, path.relative_to(root).as_posix())
PY
evidence_digest="sha256:$(sha256sum "$scratch/mock/evidence.zip" | cut -d' ' -f1)"
jq -n \
  --arg sha "$source_sha" \
  --arg repository "$repository" \
  '{
    id: 987654321,
    run_attempt: 3,
    event: "workflow_dispatch",
    status: "completed",
    conclusion: "success",
    path: ".github/workflows/workload-load-real-azure.yml",
    head_sha: $sha,
    head_branch: "main",
    repository: { full_name: $repository },
    head_repository: { full_name: $repository }
  }' > "$scratch/mock/evidence-run.json"
jq -n \
  --arg sha "$source_sha" \
  --arg digest "$evidence_digest" \
  '{
    total_count: 1,
    artifacts: [{
      id: 88,
      name: "real-azure-workload-load-s3-basic-object-crud",
      expired: false,
      digest: $digest,
      created_at: "2026-07-17T19:01:00Z",
      expires_at: "2099-07-17T19:01:00Z",
      workflow_run: {
        id: 987654321,
        head_sha: $sha
      }
    }]
  }' > "$scratch/mock/evidence-artifacts.json"

jq -n \
  --arg repository "$repository" \
  --arg sha "$source_sha" \
  --arg executable "${executable_digest#sha256:}" \
  --arg manifest "${manifest_digest#sha256:}" \
  --arg invocation "https://github.com/$repository/actions/runs/$run_id/attempts/$run_attempt" \
  '[{
    verificationResult: {
      statement: {
        predicateType: "https://slsa.dev/provenance/v1",
        subject: [
          { name: "Aws2Azure.Proxy", digest: { sha256: $executable } },
          { name: "sealed-runtime-manifest.json", digest: { sha256: $manifest } }
        ],
        predicate: {
          runDetails: {
            metadata: { invocationId: $invocation }
          }
        }
      },
      signature: {
        certificate: {
          githubWorkflowTrigger: "workflow_dispatch",
          githubWorkflowRepository: $repository,
          githubWorkflowRef: "refs/heads/main",
          githubWorkflowSHA: $sha,
          sourceRepositoryDigest: $sha,
          sourceRepositoryRef: "refs/heads/main",
          runInvocationURI: $invocation
        }
      }
    }
  }]' > "$scratch/mock/attestation.json"

cat > "$scratch/mock-bin/gh" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
if [[ "$1" == api ]]; then
  endpoint="${@: -1}"
  printf '%s\n' "$*" >> "$MOCK_GH_ROOT/gh.log"
  case "$endpoint" in
    */actions/runs/123456789) cat "$MOCK_GH_ROOT/run.json" ;;
    */actions/runs/123456789/artifacts?per_page=100) cat "$MOCK_GH_ROOT/artifacts.json" ;;
    */actions/artifacts/77/zip) cat "$MOCK_GH_ROOT/artifact.zip" ;;
    */actions/runs/987654321) cat "$MOCK_GH_ROOT/evidence-run.json" ;;
    */actions/runs/987654321/artifacts?per_page=100) cat "$MOCK_GH_ROOT/evidence-artifacts.json" ;;
    */actions/artifacts/88/zip) cat "$MOCK_GH_ROOT/evidence.zip" ;;
    */branches/main) cat "$MOCK_GH_ROOT/branch.json" ;;
    */rulesets\?includes_parents=true\&targets=tag\&per_page=100)
      [[ "$#" == 8 ]]
      [[ "$2" == --paginate && "$3" == --slurp ]]
      [[ "$4" == -H && "$5" == "Accept: application/vnd.github+json" ]]
      [[ "$6" == -H && "$7" == "X-GitHub-Api-Version: 2022-11-28" ]]
      [[ "${GH_TOKEN:-}" == test-token ]]
      [[ "${MOCK_RULESET_LIST_FAILURE:-false}" != true ]]
      cat "${MOCK_RULESET_PAGES:-$MOCK_GH_ROOT/ruleset-pages.json}"
      ;;
    */rulesets/19148912)
      [[ "${MOCK_RULESET_DETAIL_FAILURE:-}" != 19148912 ]]
      cat "${MOCK_RULESET_DETAIL_19148912:-$MOCK_GH_ROOT/ruleset-19148912.json}"
      ;;
    */rulesets/19148913)
      [[ "${MOCK_RULESET_DETAIL_FAILURE:-}" != 19148913 ]]
      cat "$MOCK_GH_ROOT/ruleset-19148913.json"
      ;;
    *) echo "unexpected mock API endpoint: $endpoint" >&2; exit 1 ;;
  esac
elif [[ "$1" == attestation && "$2" == verify ]]; then
  cat "$MOCK_GH_ROOT/attestation.json"
else
  echo "unexpected mock gh command: $*" >&2
  exit 1
fi
EOF
chmod 0700 "$scratch/mock-bin/gh"
export MOCK_GH_ROOT="$scratch/mock"
export GH_TOKEN=test-token
: > "$scratch/mock/gh.log"

GH_BIN="$scratch/mock-bin/gh" "$repo_root/eng/resolve-sealed-runtime.sh" \
  --repository "$repository" \
  --run-id "$run_id" \
  --run-attempt "$run_attempt" \
  --expected-sha "$source_sha" \
  --expected-ref refs/heads/main \
  --profile s3-basic-object-crud \
  --profile-version 1 \
  --role candidate \
  --destination "$scratch/resolved" \
  --identity-output "$scratch/candidate.json" >/dev/null

jq -e \
  --arg runtime "$runtime_digest" \
  --arg executable "$executable_digest" \
  --arg manifest "$manifest_digest" \
  --arg upload "$upload_digest" \
  '
    .role == "candidate" and
    .status == "candidate" and
    .runtime.aggregate_digest == $runtime and
    .runtime.executable_digest == $executable and
    .runtime.manifest_digest == $manifest and
    .artifact.upload_digest == $upload and
    .attestation.executable_subject_digest == $executable and
    .attestation.manifest_subject_digest == $manifest
  ' "$scratch/candidate.json" >/dev/null

jq -n \
  --arg profile "s3-basic-object-crud" \
  --arg repository "$repository" \
  --arg sha "$source_sha" \
  --arg runtime "$runtime_digest" \
  --arg executable "$executable_digest" \
  --arg manifest "$manifest_digest" \
  --arg artifact_name "$artifact_name" \
  --arg upload "$upload_digest" \
  --argjson run_id "$run_id" \
  --argjson run_attempt "$run_attempt" \
  '{
    schema_version: 1,
    ledger_record_digest: ("sha256:" + ("9" * 64)),
    record: {
      profile: { id: $profile, version: 1 },
      status: "bootstrap",
      eligibility: {
        rollback_baseline_eligible: true,
        promotion_eligible: false
      },
      runtime: {
        source_repository: $repository,
        source_sha: $sha,
        aggregate_digest: $runtime,
        executable_digest: $executable
      },
      producer: {
        workflow: ".github/workflows/sealed-runtime.yml",
        run_id: $run_id,
        run_attempt: $run_attempt
      },
      artifact: {
        id: 77,
        name: $artifact_name,
        upload_digest: $upload,
        created_at: "2026-07-17T18:01:00+00:00",
        expires_at: "2099-07-17T18:01:00+00:00"
      },
      attestation: {
        predicate_type: "https://slsa.dev/provenance/v1",
        repository: $repository,
        signer_workflow: ($repository + "/.github/workflows/sealed-runtime.yml"),
        source_sha: $sha,
        source_ref: "refs/heads/main",
        subject_name: "Aws2Azure.Proxy",
        subject_digest: $executable,
        manifest_subject_name: "sealed-runtime-manifest.json",
        manifest_subject_digest: $manifest
      }
    }
  }' > "$scratch/prior-ledger.json"
GH_BIN="$scratch/mock-bin/gh" "$repo_root/eng/resolve-sealed-runtime.sh" \
  --repository "$repository" \
  --run-id "$run_id" \
  --run-attempt "$run_attempt" \
  --expected-sha "$source_sha" \
  --expected-ref refs/heads/main \
  --profile s3-basic-object-crud \
  --profile-version 1 \
  --role prior \
  --artifact-id 77 \
  --ledger-json "$scratch/prior-ledger.json" \
  --destination "$scratch/prior-resolved" \
  --identity-output "$scratch/prior.json" >/dev/null
jq -e '
  .role == "prior" and
  .status == "bootstrap" and
  .eligibility.rollback_baseline_eligible == true and
  .eligibility.promotion_eligible == false and
  (.ledger_record_digest | test("^sha256:[0-9a-f]{64}$"))
' "$scratch/prior.json" >/dev/null

evidence_content="$(
  GH_BIN="$scratch/mock-bin/gh" "$repo_root/eng/download-qualified-run-artifact.sh" \
    --repository "$repository" \
    --run-id 987654321 \
    --run-attempt 3 \
    --workflow .github/workflows/workload-load-real-azure.yml \
    --event workflow_dispatch \
    --profile s3-basic-object-crud \
    --expected-sha "$source_sha" \
    --expected-ref refs/heads/main \
    --artifact-name real-azure-workload-load-s3-basic-object-crud \
    --destination "$scratch/evidence-resolved" \
    --identity-output "$scratch/evidence-identity.json"
)"
[[ -f "$evidence_content/artifacts/workload/load-evidence.json" ]]
jq -e \
  --arg digest "$evidence_digest" \
  '
    .profile_id == "s3-basic-object-crud" and
    .workflow_path == ".github/workflows/workload-load-real-azure.yml" and
    .event_name == "workflow_dispatch" and
    .conclusion == "success" and
    .run_id == 987654321 and
    .run_attempt == 3 and
    .artifact.id == 88 and
    .artifact.upload_digest == $digest
  ' "$scratch/evidence-identity.json" >/dev/null
if grep -q '/rulesets' "$scratch/mock/gh.log"; then
  echo "main-ref consumers unexpectedly required tag rulesets" >&2
  exit 1
fi

candidate=v1.2.3-rc.4
candidate_ref="refs/tags/$candidate"
jq --arg branch "$candidate" '.head_branch = $branch' \
  "$scratch/mock/evidence-run.json" > "$scratch/mock/evidence-run-tag.json"
mv "$scratch/mock/evidence-run.json" "$scratch/mock/evidence-run-main.json"
mv "$scratch/mock/evidence-run-tag.json" "$scratch/mock/evidence-run.json"
tag_evidence_content="$(
  cd "$scratch"
  GH_BIN="$scratch/mock-bin/gh" \
    "$repo_root/eng/download-qualified-run-artifact.sh" \
      --repository "$repository" \
      --run-id 987654321 \
      --run-attempt 3 \
      --workflow .github/workflows/workload-load-real-azure.yml \
      --event workflow_dispatch \
      --profile s3-basic-object-crud \
      --expected-sha "$source_sha" \
      --expected-ref "$candidate_ref" \
      --artifact-name real-azure-workload-load-s3-basic-object-crud \
      --destination "$scratch/tag-evidence-resolved" \
      --identity-output "$scratch/tag-evidence-identity.json"
)"
[[ -f "$tag_evidence_content/artifacts/workload/load-evidence.json" ]]
jq -e --arg ref "$candidate_ref" '.head_ref == $ref' \
  "$scratch/tag-evidence-identity.json" >/dev/null
grep -Fq 'api --paginate --slurp' "$scratch/mock/gh.log"
grep -Fq '/rulesets/19148912' "$scratch/mock/gh.log"
grep -Fq '/rulesets/19148913' "$scratch/mock/gh.log"

jq --arg branch "$candidate" '.head_branch = $branch' \
  "$scratch/mock/run.json" > "$scratch/mock/run-tag.json"
mv "$scratch/mock/run.json" "$scratch/mock/run-main.json"
mv "$scratch/mock/run-tag.json" "$scratch/mock/run.json"
if (
  cd "$scratch"
  GH_BIN="$scratch/mock-bin/gh" "$repo_root/eng/resolve-sealed-runtime.sh" \
    --repository "$repository" \
    --run-id "$run_id" \
    --run-attempt "$run_attempt" \
    --expected-sha "$source_sha" \
    --expected-ref "$candidate_ref" \
    --profile s3-basic-object-crud \
    --profile-version 1 \
    --role candidate \
    --destination "$scratch/tag-resolved" \
    --identity-output "$scratch/tag-candidate.json"
) >"$scratch/tag-sealed.out" 2>"$scratch/tag-sealed.err"; then
  echo "tag sealed-runtime probe unexpectedly passed a main-bound manifest" >&2
  exit 1
fi
grep -Fq 'manifest source identity does not match selected qualification source' \
  "$scratch/tag-sealed.err"
mv "$scratch/mock/run.json" "$scratch/mock/run-tag.json"
mv "$scratch/mock/run-main.json" "$scratch/mock/run.json"

jq '.conditions.ref_name.exclude = ["refs/tags/v1.2.3-rc.*"]' \
  "$scratch/mock/ruleset-19148912.json" > "$scratch/mock/ruleset-excluded.json"
if MOCK_RULESET_DETAIL_19148912="$scratch/mock/ruleset-excluded.json" \
  GH_BIN="$scratch/mock-bin/gh" \
  "$repo_root/eng/download-qualified-run-artifact.sh" \
    --repository "$repository" \
    --run-id 987654321 \
    --run-attempt 3 \
    --workflow .github/workflows/workload-load-real-azure.yml \
    --event workflow_dispatch \
    --profile s3-basic-object-crud \
    --expected-sha "$source_sha" \
    --expected-ref "$candidate_ref" \
    --artifact-name real-azure-workload-load-s3-basic-object-crud \
    --destination "$scratch/excluded-evidence" \
    --identity-output "$scratch/excluded-evidence.json" \
    >"$scratch/excluded.out" 2>"$scratch/excluded.err"; then
  echo "qualified consumer accepted an excluded candidate tag" >&2
  exit 1
fi
grep -Fq 'release-candidate tag is not protected' "$scratch/excluded.err"

jq '.id = 19148914' "$scratch/mock/ruleset-19148912.json" \
  > "$scratch/mock/ruleset-mismatch.json"
if MOCK_RULESET_DETAIL_19148912="$scratch/mock/ruleset-mismatch.json" \
  GH_BIN="$scratch/mock-bin/gh" \
  "$repo_root/eng/download-qualified-run-artifact.sh" \
    --repository "$repository" \
    --run-id 987654321 \
    --run-attempt 3 \
    --workflow .github/workflows/workload-load-real-azure.yml \
    --event workflow_dispatch \
    --profile s3-basic-object-crud \
    --expected-sha "$source_sha" \
    --expected-ref "$candidate_ref" \
    --artifact-name real-azure-workload-load-s3-basic-object-crud \
    --destination "$scratch/mismatch-evidence" \
    --identity-output "$scratch/mismatch-evidence.json" \
    >"$scratch/mismatch.out" 2>"$scratch/mismatch.err"; then
  echo "qualified consumer accepted mismatched ruleset detail" >&2
  exit 1
fi
grep -Fq 'detailed tag rulesets are malformed, incomplete, or inconsistent' \
  "$scratch/mismatch.err"

if MOCK_RULESET_LIST_FAILURE=true GH_BIN="$scratch/mock-bin/gh" \
  "$repo_root/eng/download-qualified-run-artifact.sh" \
    --repository "$repository" \
    --run-id 987654321 \
    --run-attempt 3 \
    --workflow .github/workflows/workload-load-real-azure.yml \
    --event workflow_dispatch \
    --profile s3-basic-object-crud \
    --expected-sha "$source_sha" \
    --expected-ref "$candidate_ref" \
    --artifact-name real-azure-workload-load-s3-basic-object-crud \
    --destination "$scratch/fetch-failure-evidence" \
    --identity-output "$scratch/fetch-failure-evidence.json" \
    >"$scratch/fetch-failure.out" 2>"$scratch/fetch-failure.err"; then
  echo "qualified consumer accepted a ruleset list fetch failure" >&2
  exit 1
fi
grep -Fq 'failed to fetch compact tag rulesets' "$scratch/fetch-failure.err"

jq '.[0][0].enforcement = "disabled"' \
  "$scratch/mock/ruleset-pages.json" > "$scratch/mock/ruleset-inactive-pages.json"
if MOCK_RULESET_PAGES="$scratch/mock/ruleset-inactive-pages.json" \
  GH_BIN="$scratch/mock-bin/gh" \
  "$repo_root/eng/download-qualified-run-artifact.sh" \
    --repository "$repository" \
    --run-id 987654321 \
    --run-attempt 3 \
    --workflow .github/workflows/workload-load-real-azure.yml \
    --event workflow_dispatch \
    --profile s3-basic-object-crud \
    --expected-sha "$source_sha" \
    --expected-ref "$candidate_ref" \
    --artifact-name real-azure-workload-load-s3-basic-object-crud \
    --destination "$scratch/inactive-evidence" \
    --identity-output "$scratch/inactive-evidence.json" \
    >"$scratch/inactive.out" 2>"$scratch/inactive.err"; then
  echo "qualified consumer accepted inactive compact ruleset metadata" >&2
  exit 1
fi
grep -Fq 'compact tag rulesets are malformed, incomplete, duplicated, inactive' \
  "$scratch/inactive.err"
mv "$scratch/mock/evidence-run.json" "$scratch/mock/evidence-run-tag.json"
mv "$scratch/mock/evidence-run-main.json" "$scratch/mock/evidence-run.json"

jq '.path = ".github/workflows/not-trusted.yml"' \
  "$scratch/mock/run.json" > "$scratch/mock/run-invalid.json"
mv "$scratch/mock/run.json" "$scratch/mock/run-valid.json"
mv "$scratch/mock/run-invalid.json" "$scratch/mock/run.json"
if GH_BIN="$scratch/mock-bin/gh" "$repo_root/eng/resolve-sealed-runtime.sh" \
  --repository "$repository" \
  --run-id "$run_id" \
  --run-attempt "$run_attempt" \
  --expected-sha "$source_sha" \
  --expected-ref refs/heads/main \
  --profile s3-basic-object-crud \
  --profile-version 1 \
  --role candidate \
  --destination "$scratch/rejected" \
  --identity-output "$scratch/rejected.json" >/dev/null 2>&1; then
  echo "consumer accepted an artifact from the wrong workflow" >&2
  exit 1
fi

python3 - "$scratch/malicious.zip" <<'PY'
import sys
import zipfile

with zipfile.ZipFile(sys.argv[1], "w") as archive:
    archive.writestr("../escape", "bad")
PY
install -d -m 0700 "$scratch/malicious-output"
if python3 "$repo_root/eng/safe-extract.py" \
  zip "$scratch/malicious.zip" "$scratch/malicious-output" >/dev/null 2>&1; then
  echo "safe extractor accepted ZIP traversal" >&2
  exit 1
fi
python3 - "$scratch/malicious.tar" <<'PY'
import io
import sys
import tarfile

with tarfile.open(sys.argv[1], "w") as archive:
    link = tarfile.TarInfo("runtime/link")
    link.type = tarfile.SYMTYPE
    link.linkname = "../../escape"
    archive.addfile(link)
    data = b"ok"
    regular = tarfile.TarInfo("runtime/Aws2Azure.Proxy")
    regular.mode = 0o700
    regular.size = len(data)
    archive.addfile(regular, io.BytesIO(data))
PY
install -d -m 0700 "$scratch/malicious-tar-output"
if python3 "$repo_root/eng/safe-extract.py" \
  tar "$scratch/malicious.tar" "$scratch/malicious-tar-output" >/dev/null 2>&1; then
  echo "safe extractor accepted a TAR symbolic link" >&2
  exit 1
fi

for workflow in \
  "$repo_root/.github/workflows/integration-real-azure.yml" \
  "$repo_root/.github/workflows/workload-load-real-azure.yml" \
  "$repo_root/.github/workflows/qualification-real-azure.yml" \
  "$repo_root/.github/workflows/rc-observation-real-azure.yml" \
  "$repo_root/.github/workflows/release-candidate-image.yml"; do
  while IFS= read -r action; do
    [[ "$action" =~ @[0-9a-f]{40}$ ]] || {
      echo "workflow action is not pinned by commit SHA: $action" >&2
      exit 1
    }
  done < <(
    sed -n 's/^[[:space:]]*uses:[[:space:]]*\([^[:space:]#]*\).*$/\1/p' "$workflow" |
      grep -v '^\./'
  )
  if grep -q 'dotnet run' "$workflow"; then
    echo "final qualification workflow path still invokes dotnet run: $workflow" >&2
    exit 1
  fi
  if grep -Eq \
    'resolve-release-candidate-archives|resolve-sealed-runtime|download-qualified-run-artifact' \
    "$workflow" &&
    ! grep -Fq 'GH_TOKEN: ${{ github.token }}' "$workflow"; then
    echo "trusted artifact consumer workflow does not pass GH_TOKEN: $workflow" >&2
    exit 1
  fi
done

echo "Sealed runtime consumer tests passed."
