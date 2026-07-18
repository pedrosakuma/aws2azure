#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
gh_bin="${GH_BIN:-gh}"
rc_workflow=".github/workflows/release-candidate.yml"

fail() {
  echo "release-candidate-archive-consumer: $*" >&2
  exit 1
}

usage() {
  cat >&2 <<'EOF'
usage: eng/resolve-release-candidate-archives.sh
  --repository OWNER/REPO
  --candidate vMAJOR.MINOR.PATCH-rc.NUMBER
  --source-sha SHA
  --run-id ID
  --run-attempt ATTEMPT
  --artifact-id ID
  --artifact-name NAME
  --artifact-digest sha256:...
  --archive-content-digest sha256:...
  --destination PRIVATE-DIRECTORY
  --identity-output JSON
EOF
  exit 2
}

repository=
candidate=
source_sha=
run_id=
run_attempt=
artifact_id=
artifact_name=
artifact_digest=
archive_content_digest=
destination=
identity_output=

while (($# > 0)); do
  option="$1"
  shift
  case "$option" in
    --repository) repository="${1:-}"; shift ;;
    --candidate) candidate="${1:-}"; shift ;;
    --source-sha) source_sha="${1:-}"; shift ;;
    --run-id) run_id="${1:-}"; shift ;;
    --run-attempt) run_attempt="${1:-}"; shift ;;
    --artifact-id) artifact_id="${1:-}"; shift ;;
    --artifact-name) artifact_name="${1:-}"; shift ;;
    --artifact-digest) artifact_digest="${1:-}"; shift ;;
    --archive-content-digest) archive_content_digest="${1:-}"; shift ;;
    --destination) destination="${1:-}"; shift ;;
    --identity-output) identity_output="${1:-}"; shift ;;
    *) fail "unknown option: $option" ;;
  esac
done

for value in \
  "$repository" "$candidate" "$source_sha" "$run_id" "$run_attempt" \
  "$artifact_id" "$artifact_name" "$artifact_digest" "$archive_content_digest" \
  "$destination" "$identity_output"; do
  [[ -n "$value" ]] || usage
done

[[ "$repository" =~ ^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$ ]] ||
  fail "repository is invalid"
[[ "$candidate" =~ ^v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)-rc\.([1-9][0-9]*)$ ]] ||
  fail "candidate must be strict vMAJOR.MINOR.PATCH-rc.NUMBER SemVer"
[[ "$source_sha" =~ ^[0-9a-f]{40}$ ]] || fail "source SHA is invalid"
[[ "$run_id" =~ ^[1-9][0-9]*$ ]] || fail "run id must be a positive integer"
[[ "$run_attempt" =~ ^[1-9][0-9]*$ ]] ||
  fail "run attempt must be a positive integer"
[[ "$artifact_id" =~ ^[1-9][0-9]*$ ]] ||
  fail "artifact id must be a positive integer"
[[ "$artifact_name" =~ ^[A-Za-z0-9][A-Za-z0-9._-]{0,255}$ ]] ||
  fail "artifact name is invalid"
[[ "$artifact_digest" =~ ^sha256:[0-9a-f]{64}$ ]] ||
  fail "artifact digest is invalid"
[[ "$archive_content_digest" =~ ^sha256:[0-9a-f]{64}$ ]] ||
  fail "archive-input content digest is invalid"

for command in "$gh_bin" python3 sha256sum; do
  command -v "$command" >/dev/null 2>&1 || fail "required command not found: $command"
done

umask 077
[[ ! -e "$destination" ]] || fail "destination already exists: $destination"
install -d -m 0700 "$destination"
private_dir="$(cd "$destination" && pwd)"
identity_output="$(python3 -c 'import os,sys; print(os.path.abspath(sys.argv[1]))' "$identity_output")"
install -d -m 0700 "$(dirname "$identity_output")"

run_json="$private_dir/producer-run.json"
artifact_json="$private_dir/producer-artifact.json"
rulesets_json="$private_dir/tag-rulesets.json"
selection_json="$private_dir/archive-selection.json"
tag_ref_json="$private_dir/candidate-tag-ref.json"

"$gh_bin" api -H "Accept: application/vnd.github+json" \
  "/repos/$repository/actions/runs/$run_id/attempts/$run_attempt" > "$run_json"
"$gh_bin" api -H "Accept: application/vnd.github+json" \
  "/repos/$repository/actions/artifacts/$artifact_id" > "$artifact_json"
"$gh_bin" api -H "Accept: application/vnd.github+json" \
  "/repos/$repository/rulesets?includes_parents=true&targets=tag&per_page=100" \
  > "$rulesets_json"
"$gh_bin" api -H "Accept: application/vnd.github+json" \
  "/repos/$repository/git/ref/tags/$candidate" > "$tag_ref_json"

tag_object_type="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["object"]["type"])' "$tag_ref_json")"
tag_object_sha="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["object"]["sha"])' "$tag_ref_json")"
for depth in $(seq 1 8); do
  if [[ "$tag_object_type" == commit ]]; then
    break
  fi
  [[ "$tag_object_type" == tag && "$tag_object_sha" =~ ^[0-9a-f]{40}$ ]] ||
    fail "candidate ref does not resolve through commit or annotated-tag objects"
  tag_object_json="$private_dir/candidate-tag-object-$depth.json"
  "$gh_bin" api -H "Accept: application/vnd.github+json" \
    "/repos/$repository/git/tags/$tag_object_sha" > "$tag_object_json"
  tag_object_type="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["object"]["type"])' "$tag_object_json")"
  tag_object_sha="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["object"]["sha"])' "$tag_object_json")"
done
[[ "$tag_object_type" == commit && "$tag_object_sha" == "$source_sha" ]] ||
  fail "current protected candidate tag does not resolve to the selected source SHA"

python3 - "$candidate" "$rulesets_json" <<'PY' ||
import fnmatch
import json
import pathlib
import sys

candidate_ref = f"refs/tags/{sys.argv[1]}"
rulesets = json.loads(pathlib.Path(sys.argv[2]).read_text(encoding="utf-8"))
for ruleset in rulesets:
    if ruleset.get("target") != "tag" or ruleset.get("enforcement") != "active":
        continue
    ref_name = (ruleset.get("conditions") or {}).get("ref_name") or {}
    includes = ref_name.get("include") or []
    excludes = ref_name.get("exclude") or []
    included = any(
        value == "~ALL" or fnmatch.fnmatchcase(candidate_ref, value)
        for value in includes
    )
    excluded = any(fnmatch.fnmatchcase(candidate_ref, value) for value in excludes)
    if included and not excluded:
        raise SystemExit(0)
raise SystemExit(1)
PY
  fail "candidate tag is not covered by an active protection ruleset"

python3 "$repo_root/eng/release-candidate-image.py" validate-selection \
  --repository "$repository" \
  --candidate "$candidate" \
  --source-sha "$source_sha" \
  --run-id "$run_id" \
  --run-attempt "$run_attempt" \
  --artifact-id "$artifact_id" \
  --artifact-name "$artifact_name" \
  --artifact-digest "$artifact_digest" \
  --archive-content-digest "$archive_content_digest" \
  --run-json "$run_json" \
  --artifact-json "$artifact_json" \
  --output "$selection_json"

archive_zip="$private_dir/github-artifact.zip"
"$gh_bin" api -H "Accept: application/vnd.github+json" \
  "/repos/$repository/actions/artifacts/$artifact_id/zip" > "$archive_zip"
chmod 0600 "$archive_zip"
actual_upload_digest="sha256:$(sha256sum "$archive_zip" | cut -d' ' -f1)"
[[ "$actual_upload_digest" == "$artifact_digest" ]] ||
  fail "downloaded artifact does not match its exact upload digest"

bundle="$private_dir/bundle"
python3 "$repo_root/eng/safe-extract.py" zip "$archive_zip" "$bundle"
python3 "$repo_root/eng/release-candidate-image.py" validate-bundle \
  --bundle "$bundle" \
  --selection "$selection_json" \
  --output "$identity_output"

signer_workflow="$repository/$rc_workflow"
source_ref="refs/tags/$candidate"
payload_verification="$private_dir/payload-attestation-verification.json"
inputs_verification="$private_dir/archive-input-attestation-verification.json"

"$gh_bin" attestation verify \
  "$bundle/platforms/linux-x64/Aws2Azure.Proxy" \
  --bundle "$bundle/provenance/archive-payload-provenance.json" \
  --repo "$repository" \
  --signer-workflow "$signer_workflow" \
  --source-digest "$source_sha" \
  --source-ref "$source_ref" \
  --predicate-type "https://slsa.dev/provenance/v1" \
  --deny-self-hosted-runners \
  --format json > "$payload_verification"

"$gh_bin" attestation verify \
  "$bundle/release-candidate-archive-inputs.json" \
  --repo "$repository" \
  --signer-workflow "$signer_workflow" \
  --source-digest "$source_sha" \
  --source-ref "$source_ref" \
  --predicate-type "https://slsa.dev/provenance/v1" \
  --deny-self-hosted-runners \
  --format json > "$inputs_verification"

python3 "$repo_root/eng/release-candidate-image.py" validate-attestation \
  --kind payload \
  --identity "$identity_output" \
  --verification "$payload_verification"
python3 "$repo_root/eng/release-candidate-image.py" validate-attestation \
  --kind archive-inputs \
  --identity "$identity_output" \
  --verification "$inputs_verification"

echo "$private_dir/bundle"
