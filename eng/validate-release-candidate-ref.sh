#!/usr/bin/env bash
set -euo pipefail

fail() {
  echo "release-candidate-ref: $*" >&2
  exit 1
}

candidate=
orchestration_sha=
dispatch_ref=
ref_protected=
tag_sha=
rulesets_json=
approval_sha=
main_sha=
main_protected=
compare_json=

while (($# > 0)); do
  option="$1"
  shift
  case "$option" in
    --candidate) candidate="${1:-}"; shift ;;
    --orchestration-sha) orchestration_sha="${1:-}"; shift ;;
    --dispatch-ref) dispatch_ref="${1:-}"; shift ;;
    --ref-protected) ref_protected="${1:-}"; shift ;;
    --tag-sha) tag_sha="${1:-}"; shift ;;
    --rulesets-json) rulesets_json="${1:-}"; shift ;;
    --approval-sha) approval_sha="${1:-}"; shift ;;
    --main-sha) main_sha="${1:-}"; shift ;;
    --main-protected) main_protected="${1:-}"; shift ;;
    --compare-json) compare_json="${1:-}"; shift ;;
    *) fail "unknown option: $option" ;;
  esac
done

[[ -n "$candidate" ]] || fail "missing --candidate"
[[ -n "$orchestration_sha" ]] || fail "missing --orchestration-sha"
[[ -n "$dispatch_ref" ]] || fail "missing --dispatch-ref"
[[ -n "$ref_protected" ]] || fail "missing --ref-protected"
[[ -n "$tag_sha" ]] || fail "missing --tag-sha"
[[ -n "$rulesets_json" ]] || fail "missing --rulesets-json"
[[ -n "$approval_sha" ]] || fail "missing --approval-sha"
[[ -n "$main_sha" ]] || fail "missing --main-sha"
[[ -n "$main_protected" ]] || fail "missing --main-protected"
[[ -n "$compare_json" ]] || fail "missing --compare-json"

semver_number='(0|[1-9][0-9]*)'
[[ "$candidate" =~ ^v${semver_number}\.${semver_number}\.${semver_number}-rc\.([1-9][0-9]*)$ ]] ||
  fail "candidate must be strict vMAJOR.MINOR.PATCH-rc.NUMBER SemVer"
[[ "$orchestration_sha" =~ ^[0-9a-f]{40}$ ]] ||
  fail "orchestration SHA is invalid"
[[ "$tag_sha" =~ ^[0-9a-f]{40}$ ]] || fail "candidate tag SHA is invalid"
[[ "$approval_sha" =~ ^[0-9a-f]{40}$ ]] || fail "approval SHA is invalid"
[[ "$main_sha" =~ ^[0-9a-f]{40}$ ]] || fail "main SHA is invalid"
[[ "$ref_protected" == true ]] || fail "dispatch ref is not protected"
[[ "$main_protected" == true ]] || fail "main is not protected"
candidate_ref="refs/tags/$candidate"
[[ "$dispatch_ref" == "refs/heads/main" ]] ||
  fail "dispatch ref must be protected main, never the candidate tag"
[[ "$approval_sha" == "$orchestration_sha" ]] ||
  fail "approved-ledger SHA must equal the exact orchestration SHA"
[[ -f "$rulesets_json" && ! -L "$rulesets_json" ]] ||
  fail "tag rulesets input must be a regular file"
[[ -f "$compare_json" && ! -L "$compare_json" ]] ||
  fail "main comparison input must be a regular file"
command -v python3 >/dev/null 2>&1 || fail "python3 is required"
command -v jq >/dev/null 2>&1 || fail "jq is required"

python3 - "$candidate_ref" "$rulesets_json" <<'PY' ||
import fnmatch
import json
import pathlib
import sys

ref = sys.argv[1]
try:
    rulesets = json.loads(pathlib.Path(sys.argv[2]).read_text(encoding="utf-8"))
except (OSError, UnicodeError, json.JSONDecodeError):
    raise SystemExit(1)
if not isinstance(rulesets, list):
    raise SystemExit(1)
for ruleset in rulesets:
    if not isinstance(ruleset, dict):
        continue
    if ruleset.get("target") != "tag" or ruleset.get("enforcement") != "active":
        continue
    ref_name = (ruleset.get("conditions") or {}).get("ref_name") or {}
    includes = ref_name.get("include") or []
    excludes = ref_name.get("exclude") or []
    included = any(
        value == "~ALL" or fnmatch.fnmatchcase(ref, value)
        for value in includes
        if isinstance(value, str)
    )
    excluded = any(
        fnmatch.fnmatchcase(ref, value)
        for value in excludes
        if isinstance(value, str)
    )
    if included and not excluded:
        raise SystemExit(0)
raise SystemExit(1)
PY
  fail "candidate tag is not protected by an active matching tag ruleset"

jq -e \
  --arg approval_sha "$approval_sha" \
  --arg main_sha "$main_sha" \
  '
    def exact_commit($sha):
      type == "object" and
      has("sha") and
      .sha == $sha;

    type == "object" and
    (.base_commit | exact_commit($approval_sha)) and
    (.merge_base_commit | exact_commit($approval_sha)) and
    if .status == "identical" then
      $approval_sha == $main_sha and
      .ahead_by == 0 and
      .behind_by == 0 and
      .total_commits == 0 and
      (
        .head_commit == null or
        (.head_commit | exact_commit($main_sha))
      )
    elif .status == "ahead" then
      $approval_sha != $main_sha and
      (.ahead_by | type == "number" and . > 0 and floor == .) and
      .behind_by == 0 and
      .total_commits == .ahead_by and
      (.head_commit | exact_commit($main_sha))
    else
      false
    end
  ' "$compare_json" >/dev/null ||
  fail "approved-ledger SHA is not an exact ancestor of protected main"

printf '%s\n' "$candidate_ref"
