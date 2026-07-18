#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
gh_bin="${GH_BIN:-gh}"

fail() {
  echo "sealed-runtime-consumer: $*" >&2
  exit 1
}

usage() {
  cat >&2 <<'EOF'
usage: eng/resolve-sealed-runtime.sh
  --repository OWNER/REPO
  --run-id ID
  --run-attempt ATTEMPT
  --expected-sha SHA
  --expected-ref REF
  --profile PROFILE
  --profile-version VERSION
  --role candidate|prior
  --destination PRIVATE-DIRECTORY
  --identity-output JSON
  [--artifact-id ID]
  [--ledger-json JSON]
  [--github-env PATH]
EOF
  exit 2
}

require_command() {
  command -v "$1" >/dev/null 2>&1 || fail "required command not found: $1"
}

repository=
run_id=
run_attempt=
expected_sha=
expected_ref=
profile=
profile_version=
role=
destination=
identity_output=
artifact_id=
ledger_json=
github_env=

while (($# > 0)); do
  option="$1"
  shift
  case "$option" in
    --repository) repository="${1:-}"; shift ;;
    --run-id) run_id="${1:-}"; shift ;;
    --run-attempt) run_attempt="${1:-}"; shift ;;
    --expected-sha) expected_sha="${1:-}"; shift ;;
    --expected-ref) expected_ref="${1:-}"; shift ;;
    --profile) profile="${1:-}"; shift ;;
    --profile-version) profile_version="${1:-}"; shift ;;
    --role) role="${1:-}"; shift ;;
    --destination) destination="${1:-}"; shift ;;
    --identity-output) identity_output="${1:-}"; shift ;;
    --artifact-id) artifact_id="${1:-}"; shift ;;
    --ledger-json) ledger_json="${1:-}"; shift ;;
    --github-env) github_env="${1:-}"; shift ;;
    *) fail "unknown option: $option" ;;
  esac
done

for value in \
  repository run_id run_attempt expected_sha expected_ref profile profile_version \
  role destination identity_output; do
  [[ -n "${!value}" ]] || usage
done
[[ "$repository" =~ ^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$ ]] ||
  fail "invalid repository: $repository"
[[ "$run_id" =~ ^[1-9][0-9]*$ ]] || fail "run id must be a positive integer"
[[ "$run_attempt" =~ ^[1-9][0-9]*$ ]] ||
  fail "run attempt must be a positive integer"
[[ "$expected_sha" =~ ^[0-9a-f]{40}$ ]] || fail "expected SHA is invalid"
[[ "$profile" =~ ^[a-z0-9][a-z0-9-]*$ ]] || fail "profile id is invalid"
[[ "$profile_version" =~ ^[1-9][0-9]*$ ]] || fail "profile version is invalid"
[[ "$role" == candidate || "$role" == prior ]] || fail "role must be candidate or prior"
if [[ "$expected_ref" != refs/heads/main ]] &&
   [[ ! "$expected_ref" =~ ^refs/tags/v[0-9]+\.[0-9]+\.[0-9]+-rc([.-]?[0-9A-Za-z]+)*$ ]]; then
  fail "expected ref is not protected main or an allowed release-candidate tag"
fi
if [[ "$role" == prior && -z "$ledger_json" ]]; then
  fail "prior runtime resolution requires --ledger-json"
fi
if [[ "$role" == candidate && -n "$ledger_json" ]]; then
  fail "candidate runtime resolution must not use a ledger record"
fi
if [[ -n "$artifact_id" && ! "$artifact_id" =~ ^[1-9][0-9]*$ ]]; then
  fail "artifact id must be a positive integer"
fi

require_command "$gh_bin"
require_command jq
require_command python3
require_command sha256sum

umask 077
[[ ! -e "$destination" ]] || fail "destination already exists: $destination"
install -d -m 0700 "$destination"
private_dir="$(cd "$destination" && pwd)"
identity_output="$(python3 -c 'import os,sys; print(os.path.abspath(sys.argv[1]))' "$identity_output")"
install -d -m 0700 "$(dirname "$identity_output")"

run_json="$private_dir/producer-run.json"
artifacts_json="$private_dir/producer-artifacts.json"
"$gh_bin" api -H "Accept: application/vnd.github+json" \
  "/repos/$repository/actions/runs/$run_id" > "$run_json"

jq -e \
  --arg repository "$repository" \
  --argjson run_id "$run_id" \
  --argjson run_attempt "$run_attempt" \
  --arg sha "$expected_sha" \
  '
    .id == $run_id and
    .run_attempt == $run_attempt and
    .event == "workflow_dispatch" and
    .status == "completed" and
    .conclusion == "success" and
    .path == ".github/workflows/sealed-runtime.yml" and
    .head_sha == $sha and
    .repository.full_name == $repository and
    .head_repository.full_name == $repository
  ' "$run_json" >/dev/null ||
  fail "selected run is not a successful same-repository sealed-runtime dispatch"

expected_head_name="${expected_ref#refs/heads/}"
expected_head_name="${expected_head_name#refs/tags/}"
[[ "$(jq -er '.head_branch' "$run_json")" == "$expected_head_name" ]] ||
  fail "selected run head ref does not match $expected_ref"

require_protected_ref() {
  local ref="$1"
  if [[ "$ref" == refs/heads/main ]]; then
    local branch_json="$private_dir/protected-main.json"
    "$gh_bin" api -H "Accept: application/vnd.github+json" \
      "/repos/$repository/branches/main" > "$branch_json"
    jq -e '.name == "main" and .protected == true' "$branch_json" >/dev/null ||
      fail "main is not protected"
    return
  fi

  local rulesets_json="$private_dir/tag-rulesets.json"
  "$repo_root/eng/resolve-release-candidate-rulesets.sh" \
    --repository "$repository" \
    --fetch-rulesets \
    --output-json "$rulesets_json"
  python3 - "$ref" "$rulesets_json" <<'PY' ||
import fnmatch
import json
import pathlib
import sys

ref = sys.argv[1]
rulesets = json.loads(pathlib.Path(sys.argv[2]).read_text())
for ruleset in rulesets:
    if ruleset.get("target") != "tag" or ruleset.get("enforcement") != "active":
        continue
    ref_name = (ruleset.get("conditions") or {}).get("ref_name") or {}
    includes = ref_name.get("include") or []
    excludes = ref_name.get("exclude") or []
    included = any(value == "~ALL" or fnmatch.fnmatchcase(ref, value) for value in includes)
    excluded = any(fnmatch.fnmatchcase(ref, value) for value in excludes)
    if included and not excluded:
        raise SystemExit(0)
raise SystemExit(1)
PY
    fail "release-candidate tag is not protected by an active tag ruleset"
}
require_protected_ref "$expected_ref"

"$gh_bin" api -H "Accept: application/vnd.github+json" \
  "/repos/$repository/actions/runs/$run_id/artifacts?per_page=100" > "$artifacts_json"

artifact_selector='
  [
    .artifacts[]
    | select(.expired == false)
    | select(.workflow_run.id == $run_id)
    | select(.workflow_run.head_sha == $sha)
    | select(.name | test(
        "^aws2azure-sealed-linux-x64-[0-9a-f]{64}-run-" +
        ($run_id | tostring) + "-attempt-" + ($run_attempt | tostring) + "$"))
    | select($artifact_id == 0 or .id == $artifact_id)
  ]
'
selected_count="$(
  jq \
    --argjson run_id "$run_id" \
    --argjson run_attempt "$run_attempt" \
    --arg sha "$expected_sha" \
    --argjson artifact_id "${artifact_id:-0}" \
    "$artifact_selector | length" \
    "$artifacts_json"
)"
[[ "$selected_count" == 1 ]] ||
  fail "selected run must contain exactly one matching unexpired sealed artifact"
artifact_json="$private_dir/selected-artifact.json"
jq \
  --argjson run_id "$run_id" \
  --argjson run_attempt "$run_attempt" \
  --arg sha "$expected_sha" \
  --argjson artifact_id "${artifact_id:-0}" \
  "$artifact_selector | .[0]" \
  "$artifacts_json" > "$artifact_json"

resolved_artifact_id="$(jq -er '.id' "$artifact_json")"
resolved_artifact_name="$(jq -er '.name' "$artifact_json")"
upload_digest="$(jq -er '.digest' "$artifact_json")"
[[ "$upload_digest" =~ ^sha256:[0-9a-f]{64}$ ]] ||
  fail "artifact API did not return a valid upload digest"
jq -e '(.expires_at | fromdateiso8601) > now' "$artifact_json" >/dev/null ||
  fail "selected artifact has expired"

archive_zip="$private_dir/github-artifact.zip"
"$gh_bin" api -H "Accept: application/vnd.github+json" \
  "/repos/$repository/actions/artifacts/$resolved_artifact_id/zip" > "$archive_zip"
chmod 0600 "$archive_zip"
actual_upload_digest="sha256:$(sha256sum "$archive_zip" | cut -d' ' -f1)"
[[ "$actual_upload_digest" == "$upload_digest" ]] ||
  fail "downloaded GitHub artifact does not match its upload digest"

zip_dir="$private_dir/artifact-zip"
bundle_dir="$private_dir/bundle"
python3 "$repo_root/eng/safe-extract.py" zip "$archive_zip" "$zip_dir"
mapfile -t archive_files < <(find "$zip_dir" -mindepth 1 -maxdepth 1 -type f -name '*.tar' -print)
[[ "${#archive_files[@]}" -eq 1 ]] ||
  fail "sealed artifact ZIP must contain exactly one top-level TAR"
python3 "$repo_root/eng/safe-extract.py" tar "${archive_files[0]}" "$bundle_dir"

manifest="$bundle_dir/sealed-runtime-manifest.json"
executable="$bundle_dir/runtime/Aws2Azure.Proxy"
"$repo_root/eng/sealed-runtime-manifest.sh" validate "$manifest" >/dev/null

manifest_repository="$(jq -er '.source.repository' "$manifest")"
manifest_sha="$(jq -er '.source.git_sha' "$manifest")"
manifest_ref="$(jq -er '.source.git_ref' "$manifest")"
manifest_run_id="$(jq -er '.producer.run_id' "$manifest")"
manifest_run_attempt="$(jq -er '.producer.run_attempt' "$manifest")"
manifest_artifact_name="$(jq -er '.artifact.name' "$manifest")"
manifest_archive_name="$(jq -er '.artifact.archive_name' "$manifest")"
runtime_digest="$(jq -er '.runtime.aggregate_digest' "$manifest")"
executable_digest="$(jq -er '.runtime.executable.sha256' "$manifest")"
manifest_digest="sha256:$(sha256sum "$manifest" | cut -d' ' -f1)"

[[ "$manifest_repository" == "$repository" ]] ||
  fail "manifest repository does not match selected repository"
[[ "$manifest_sha" == "$expected_sha" && "$manifest_ref" == "$expected_ref" ]] ||
  fail "manifest source identity does not match selected qualification source"
[[ "$manifest_run_id" == "$run_id" && "$manifest_run_attempt" == "$run_attempt" ]] ||
  fail "manifest producer attempt does not match selected run"
[[ "$manifest_artifact_name" == "$resolved_artifact_name" ]] ||
  fail "artifact API name does not match sealed manifest"
[[ "$(basename "${archive_files[0]}")" == "$manifest_archive_name" ]] ||
  fail "downloaded TAR name does not match sealed manifest"
[[ "$(jq -er '.producer.run_started_at' "$manifest")" == \
   "$(jq -er '.run_started_at' "$run_json")" ]] ||
  fail "manifest producer start time does not match the selected run"

status=candidate
rollback_eligible=false
promotion_eligible=false
ledger_record_digest=
if [[ "$role" == prior ]]; then
  [[ -f "$ledger_json" && ! -L "$ledger_json" ]] ||
    fail "ledger JSON must be a regular file"
  jq -e \
    --arg profile "$profile" \
    --argjson version "$profile_version" \
    --arg repository "$repository" \
    --arg sha "$expected_sha" \
    --arg ref "$expected_ref" \
    --arg runtime_digest "$runtime_digest" \
    --arg executable_digest "$executable_digest" \
    --arg manifest_digest "$manifest_digest" \
    --argjson run_id "$run_id" \
    --argjson run_attempt "$run_attempt" \
    --argjson artifact_id "$resolved_artifact_id" \
    --arg artifact_name "$resolved_artifact_name" \
    --arg upload_digest "$upload_digest" \
    '
      .schema_version == 1 and
      (.ledger_record_digest | test("^sha256:[0-9a-f]{64}$")) and
      .record.profile.id == $profile and
      .record.profile.version == $version and
      (.record.status == "bootstrap" or .record.status == "approved") and
      .record.eligibility.rollback_baseline_eligible == true and
      (if .record.status == "bootstrap"
       then .record.eligibility.promotion_eligible == false
       else .record.eligibility.promotion_eligible == true end) and
      .record.runtime.source_repository == $repository and
      .record.runtime.source_sha == $sha and
      .record.runtime.aggregate_digest == $runtime_digest and
      .record.runtime.executable_digest == $executable_digest and
      .record.producer.workflow == ".github/workflows/sealed-runtime.yml" and
      .record.producer.run_id == $run_id and
      .record.producer.run_attempt == $run_attempt and
      .record.artifact.id == $artifact_id and
      .record.artifact.name == $artifact_name and
      .record.artifact.upload_digest == $upload_digest and
      .record.attestation.predicate_type == "https://slsa.dev/provenance/v1" and
      .record.attestation.repository == $repository and
      .record.attestation.signer_workflow ==
        ($repository + "/.github/workflows/sealed-runtime.yml") and
      .record.attestation.source_sha == $sha and
      .record.attestation.source_ref == $ref and
      .record.attestation.subject_name == "Aws2Azure.Proxy" and
      .record.attestation.subject_digest == $executable_digest and
      .record.attestation.manifest_subject_name == "sealed-runtime-manifest.json" and
      .record.attestation.manifest_subject_digest == $manifest_digest
    ' "$ledger_json" >/dev/null ||
    fail "downloaded prior runtime does not exactly match the committed profile ledger"

  for field in created_at expires_at; do
    api_time="$(jq -er ".$field" "$artifact_json")"
    ledger_time="$(jq -er ".record.artifact.$field" "$ledger_json")"
    [[ "$(date -u -d "$api_time" +%s)" == "$(date -u -d "$ledger_time" +%s)" ]] ||
      fail "prior ledger artifact $field does not match GitHub"
  done
  status="$(jq -er '.record.status' "$ledger_json")"
  rollback_eligible="$(jq -r '.record.eligibility.rollback_baseline_eligible' "$ledger_json")"
  promotion_eligible="$(jq -r '.record.eligibility.promotion_eligible' "$ledger_json")"
  ledger_record_digest="$(jq -er '.ledger_record_digest' "$ledger_json")"
fi

signer_workflow="$repository/.github/workflows/sealed-runtime.yml"
predicate_type="https://slsa.dev/provenance/v1"
executable_attestation="$private_dir/executable-attestation.json"
manifest_attestation="$private_dir/manifest-attestation.json"
for subject in executable manifest; do
  if [[ "$subject" == executable ]]; then
    subject_path="$executable"
    output_path="$executable_attestation"
  else
    subject_path="$manifest"
    output_path="$manifest_attestation"
  fi
  "$gh_bin" attestation verify "$subject_path" \
    --repo "$repository" \
    --signer-workflow "$signer_workflow" \
    --source-digest "$expected_sha" \
    --source-ref "$expected_ref" \
    --predicate-type "$predicate_type" \
    --deny-self-hosted-runners \
    --format json > "$output_path"
done

attempt_url="https://github.com/$repository/actions/runs/$run_id/attempts/$run_attempt"
attestation_filter='
  [
    .[]
    | select(.verificationResult.statement.predicateType == $predicate_type)
    | select(.verificationResult.signature.certificate.githubWorkflowTrigger ==
        "workflow_dispatch")
    | select(.verificationResult.signature.certificate.githubWorkflowRepository ==
        $repository)
    | select(.verificationResult.signature.certificate.githubWorkflowRef == $ref)
    | select(.verificationResult.signature.certificate.githubWorkflowSHA == $sha)
    | select(.verificationResult.signature.certificate.sourceRepositoryDigest == $sha)
    | select(.verificationResult.signature.certificate.sourceRepositoryRef == $ref)
    | select(.verificationResult.signature.certificate.runInvocationURI == $attempt_url)
    | select(.verificationResult.statement.predicate.runDetails.metadata.invocationId ==
        $attempt_url)
    | select(any(.verificationResult.statement.subject[];
        .name == "Aws2Azure.Proxy" and .digest.sha256 == $executable_hex))
    | select(any(.verificationResult.statement.subject[];
        .name == "sealed-runtime-manifest.json" and .digest.sha256 == $manifest_hex))
  ]
'
for attestation_file in "$executable_attestation" "$manifest_attestation"; do
  count="$(
    jq \
      --arg predicate_type "$predicate_type" \
      --arg repository "$repository" \
      --arg ref "$expected_ref" \
      --arg sha "$expected_sha" \
      --arg attempt_url "$attempt_url" \
      --arg executable_hex "${executable_digest#sha256:}" \
      --arg manifest_hex "${manifest_digest#sha256:}" \
      "$attestation_filter | length" \
      "$attestation_file"
  )"
  [[ "$count" == 1 ]] ||
    fail "attestation must bind both exact subjects to the selected producer attempt"
done

canonical_attestation="$private_dir/canonical-attestation.json"
jq -S \
  --arg predicate_type "$predicate_type" \
  --arg repository "$repository" \
  --arg ref "$expected_ref" \
  --arg sha "$expected_sha" \
  --arg attempt_url "$attempt_url" \
  --arg executable_hex "${executable_digest#sha256:}" \
  --arg manifest_hex "${manifest_digest#sha256:}" \
  "$attestation_filter | .[0]" \
  "$executable_attestation" > "$canonical_attestation"
attestation_bundle_digest="sha256:$(sha256sum "$canonical_attestation" | cut -d' ' -f1)"

jq -S -n \
  --argjson schema_version 1 \
  --arg role "$role" \
  --arg profile "$profile" \
  --argjson profile_version "$profile_version" \
  --arg status "$status" \
  --argjson rollback_eligible "$rollback_eligible" \
  --argjson promotion_eligible "$promotion_eligible" \
  --arg ledger_record_digest "$ledger_record_digest" \
  --arg repository "$repository" \
  --arg source_sha "$expected_sha" \
  --arg source_ref "$expected_ref" \
  --arg runtime_digest "$runtime_digest" \
  --arg executable_digest "$executable_digest" \
  --arg manifest_digest "$manifest_digest" \
  --arg workflow ".github/workflows/sealed-runtime.yml" \
  --arg event_name "workflow_dispatch" \
  --argjson run_id "$run_id" \
  --argjson run_attempt "$run_attempt" \
  --arg run_url "https://github.com/$repository/actions/runs/$run_id" \
  --arg attempt_url "$attempt_url" \
  --arg run_started_at "$(jq -er '.run_started_at' "$run_json")" \
  --argjson artifact_id "$resolved_artifact_id" \
  --arg artifact_name "$resolved_artifact_name" \
  --arg upload_digest "$upload_digest" \
  --arg created_at "$(jq -er '.created_at' "$artifact_json")" \
  --arg expires_at "$(jq -er '.expires_at' "$artifact_json")" \
  --arg predicate_type "$predicate_type" \
  --arg signer_workflow "$signer_workflow" \
  --arg attestation_bundle_digest "$attestation_bundle_digest" \
  '{
    schema_version: $schema_version,
    role: $role,
    profile: {
      id: $profile,
      version: $profile_version
    },
    status: $status,
    eligibility: {
      rollback_baseline_eligible: $rollback_eligible,
      promotion_eligible: $promotion_eligible
    },
    ledger_record_digest:
      (if $ledger_record_digest == "" then null else $ledger_record_digest end),
    source: {
      repository: $repository,
      sha: $source_sha,
      ref: $source_ref
    },
    runtime: {
      aggregate_digest: $runtime_digest,
      executable_digest: $executable_digest,
      manifest_digest: $manifest_digest
    },
    producer: {
      workflow: $workflow,
      event_name: $event_name,
      run_id: $run_id,
      run_attempt: $run_attempt,
      run_url: $run_url,
      attempt_url: $attempt_url,
      run_started_at: $run_started_at
    },
    artifact: {
      id: $artifact_id,
      name: $artifact_name,
      upload_digest: $upload_digest,
      created_at: $created_at,
      expires_at: $expires_at
    },
    attestation: {
      predicate_type: $predicate_type,
      repository: $repository,
      signer_workflow: $signer_workflow,
      source_sha: $source_sha,
      source_ref: $source_ref,
      run_invocation_url: $attempt_url,
      bundle_digest: $attestation_bundle_digest,
      executable_subject_name: "Aws2Azure.Proxy",
      executable_subject_digest: $executable_digest,
      manifest_subject_name: "sealed-runtime-manifest.json",
      manifest_subject_digest: $manifest_digest
    }
  }' > "$identity_output"
chmod 0600 "$identity_output"

if [[ -n "$github_env" ]]; then
  prefix="AWS2AZURE_SEALED_${role^^}"
  {
    echo "${prefix}_EXECUTABLE=$(cd "$(dirname "$executable")" && pwd)/$(basename "$executable")"
    echo "${prefix}_MANIFEST=$(cd "$(dirname "$manifest")" && pwd)/$(basename "$manifest")"
    echo "${prefix}_IDENTITY=$identity_output"
  } >> "$github_env"
fi

echo "Resolved $role runtime $runtime_digest from run $run_id attempt $run_attempt." >&2
