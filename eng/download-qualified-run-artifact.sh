#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
gh_bin="${GH_BIN:-gh}"

fail() {
  echo "qualification-artifact: $*" >&2
  exit 1
}

usage() {
  cat >&2 <<'EOF'
usage: eng/download-qualified-run-artifact.sh
  --repository OWNER/REPO
  --run-id ID
  --run-attempt ATTEMPT
  --workflow .github/workflows/WORKFLOW.yml
  --event workflow_dispatch
  --profile PROFILE
  --expected-sha SHA
  --expected-ref REF
  --artifact-name NAME
  --destination PRIVATE-DIRECTORY
  --identity-output JSON
  [--artifact-id ID]
EOF
  exit 2
}

repository=
run_id=
run_attempt=
workflow=
event_name=
profile=
expected_sha=
expected_ref=
artifact_name=
destination=
identity_output=
artifact_id=

while (($# > 0)); do
  option="$1"
  shift
  case "$option" in
    --repository) repository="${1:-}"; shift ;;
    --run-id) run_id="${1:-}"; shift ;;
    --run-attempt) run_attempt="${1:-}"; shift ;;
    --workflow) workflow="${1:-}"; shift ;;
    --event) event_name="${1:-}"; shift ;;
    --profile) profile="${1:-}"; shift ;;
    --expected-sha) expected_sha="${1:-}"; shift ;;
    --expected-ref) expected_ref="${1:-}"; shift ;;
    --artifact-name) artifact_name="${1:-}"; shift ;;
    --destination) destination="${1:-}"; shift ;;
    --identity-output) identity_output="${1:-}"; shift ;;
    --artifact-id) artifact_id="${1:-}"; shift ;;
    *) fail "unknown option: $option" ;;
  esac
done

for value in \
  repository run_id run_attempt workflow event_name profile expected_sha expected_ref \
  artifact_name destination identity_output; do
  [[ -n "${!value}" ]] || usage
done
[[ "$repository" =~ ^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$ ]] ||
  fail "invalid repository"
[[ "$run_id" =~ ^[1-9][0-9]*$ && "$run_attempt" =~ ^[1-9][0-9]*$ ]] ||
  fail "run id and attempt must be positive integers"
[[ "$workflow" =~ ^\.github/workflows/[A-Za-z0-9._-]+\.yml$ ]] ||
  fail "workflow path is invalid"
[[ "$event_name" == workflow_dispatch ]] ||
  fail "qualification evidence must come from workflow_dispatch"
[[ "$profile" =~ ^[a-z0-9][a-z0-9-]*$ ]] || fail "profile id is invalid"
[[ "$expected_sha" =~ ^[0-9a-f]{40}$ ]] || fail "expected SHA is invalid"
if [[ "$expected_ref" != refs/heads/main ]] &&
   [[ ! "$expected_ref" =~ ^refs/tags/v[0-9]+\.[0-9]+\.[0-9]+-rc([.-]?[0-9A-Za-z]+)*$ ]]; then
  fail "expected ref is not protected main or an allowed release-candidate tag"
fi
[[ "$artifact_name" =~ ^[A-Za-z0-9._-]+$ ]] || fail "artifact name is unsafe"
if [[ -n "$artifact_id" && ! "$artifact_id" =~ ^[1-9][0-9]*$ ]]; then
  fail "artifact id must be a positive integer"
fi

command -v "$gh_bin" >/dev/null 2>&1 || fail "GitHub CLI not found"
command -v jq >/dev/null 2>&1 || fail "jq not found"
command -v python3 >/dev/null 2>&1 || fail "python3 not found"

umask 077
[[ ! -e "$destination" ]] || fail "destination already exists: $destination"
install -d -m 0700 "$destination"
private_dir="$(cd "$destination" && pwd)"
identity_output="$(python3 -c 'import os,sys; print(os.path.abspath(sys.argv[1]))' "$identity_output")"
install -d -m 0700 "$(dirname "$identity_output")"

run_json="$private_dir/run.json"
artifacts_json="$private_dir/artifacts.json"
"$gh_bin" api -H "Accept: application/vnd.github+json" \
  "/repos/$repository/actions/runs/$run_id" > "$run_json"
jq -e \
  --arg repository "$repository" \
  --argjson run_id "$run_id" \
  --argjson run_attempt "$run_attempt" \
  --arg workflow "$workflow" \
  --arg event_name "$event_name" \
  --arg sha "$expected_sha" \
  '
    .id == $run_id and
    .run_attempt == $run_attempt and
    .repository.full_name == $repository and
    .head_repository.full_name == $repository and
    .path == $workflow and
    .event == $event_name and
    .status == "completed" and
    .conclusion == "success" and
    .head_sha == $sha
  ' "$run_json" >/dev/null ||
  fail "selected evidence run has the wrong repository, workflow, event, SHA, or conclusion"

expected_head_name="${expected_ref#refs/heads/}"
expected_head_name="${expected_head_name#refs/tags/}"
[[ "$(jq -er '.head_branch' "$run_json")" == "$expected_head_name" ]] ||
  fail "selected evidence run head ref does not match $expected_ref"

if [[ "$expected_ref" == refs/heads/main ]]; then
  branch_json="$private_dir/protected-main.json"
  "$gh_bin" api -H "Accept: application/vnd.github+json" \
    "/repos/$repository/branches/main" > "$branch_json"
  jq -e '.name == "main" and .protected == true' "$branch_json" >/dev/null ||
    fail "main is not protected"
else
  rulesets_json="$private_dir/tag-rulesets.json"
  "$gh_bin" api -H "Accept: application/vnd.github+json" \
    "/repos/$repository/rulesets?includes_parents=true&targets=tag&per_page=100" \
    > "$rulesets_json"
  if ! python3 - "$expected_ref" "$rulesets_json" <<'PY'
import fnmatch
import json
import pathlib
import sys

ref = sys.argv[1]
rulesets = json.loads(pathlib.Path(sys.argv[2]).read_text())
for ruleset in rulesets:
    if ruleset.get("target") != "tag" or ruleset.get("enforcement") != "active":
        continue
    condition = (ruleset.get("conditions") or {}).get("ref_name") or {}
    includes = condition.get("include") or []
    excludes = condition.get("exclude") or []
    if any(value == "~ALL" or fnmatch.fnmatchcase(ref, value) for value in includes) \
            and not any(fnmatch.fnmatchcase(ref, value) for value in excludes):
        raise SystemExit(0)
raise SystemExit(1)
PY
  then
    fail "release-candidate tag is not protected by an active tag ruleset"
  fi
fi

"$gh_bin" api -H "Accept: application/vnd.github+json" \
  "/repos/$repository/actions/runs/$run_id/artifacts?per_page=100" > "$artifacts_json"
selector='
  [
    .artifacts[]
    | select(.expired == false)
    | select(.name == $artifact_name)
    | select(.workflow_run.id == $run_id)
    | select(.workflow_run.head_sha == $sha)
    | select($artifact_id == 0 or .id == $artifact_id)
  ]
'
count="$(
  jq \
    --arg artifact_name "$artifact_name" \
    --argjson run_id "$run_id" \
    --arg sha "$expected_sha" \
    --argjson artifact_id "${artifact_id:-0}" \
    "$selector | length" \
    "$artifacts_json"
)"
[[ "$count" == 1 ]] ||
  fail "selected run must contain exactly one unexpired artifact named $artifact_name"

artifact_json="$private_dir/artifact.json"
jq \
  --arg artifact_name "$artifact_name" \
  --argjson run_id "$run_id" \
  --arg sha "$expected_sha" \
  --argjson artifact_id "${artifact_id:-0}" \
  "$selector | .[0]" \
  "$artifacts_json" > "$artifact_json"
jq -e '(.expires_at | fromdateiso8601) > now' "$artifact_json" >/dev/null ||
  fail "selected evidence artifact has expired"
resolved_artifact_id="$(jq -er '.id' "$artifact_json")"
upload_digest="$(jq -er '.digest' "$artifact_json")"
[[ "$upload_digest" =~ ^sha256:[0-9a-f]{64}$ ]] ||
  fail "artifact API did not return a valid upload digest"

archive_zip="$private_dir/artifact.zip"
"$gh_bin" api -H "Accept: application/vnd.github+json" \
  "/repos/$repository/actions/artifacts/$resolved_artifact_id/zip" > "$archive_zip"
chmod 0600 "$archive_zip"
actual_digest="sha256:$(sha256sum "$archive_zip" | cut -d' ' -f1)"
[[ "$actual_digest" == "$upload_digest" ]] ||
  fail "downloaded evidence artifact does not match its upload digest"

content_dir="$private_dir/content"
python3 "$repo_root/eng/safe-extract.py" zip "$archive_zip" "$content_dir"

jq -S -n \
  --argjson schema_version 1 \
  --arg profile "$profile" \
  --arg repository "$repository" \
  --arg workflow "$workflow" \
  --arg event_name "$event_name" \
  --arg conclusion "success" \
  --argjson run_id "$run_id" \
  --argjson run_attempt "$run_attempt" \
  --arg run_url "https://github.com/$repository/actions/runs/$run_id" \
  --arg head_sha "$expected_sha" \
  --arg head_ref "$expected_ref" \
  --argjson artifact_id "$resolved_artifact_id" \
  --arg artifact_name "$artifact_name" \
  --arg upload_digest "$upload_digest" \
  --arg created_at "$(jq -er '.created_at' "$artifact_json")" \
  --arg expires_at "$(jq -er '.expires_at' "$artifact_json")" \
  '{
    schema_version: $schema_version,
    profile_id: $profile,
    repository: $repository,
    workflow_path: $workflow,
    event_name: $event_name,
    conclusion: $conclusion,
    run_id: $run_id,
    run_attempt: $run_attempt,
    run_url: $run_url,
    head_sha: $head_sha,
    head_ref: $head_ref,
    artifact: {
      id: $artifact_id,
      name: $artifact_name,
      upload_digest: $upload_digest,
      created_at: $created_at,
      expires_at: $expires_at
    }
  }' > "$identity_output"
chmod 0600 "$identity_output"

echo "$content_dir"
