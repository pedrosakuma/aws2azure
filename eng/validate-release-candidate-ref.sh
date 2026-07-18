#!/usr/bin/env bash
set -euo pipefail

fail() {
  echo "release-candidate-ref: $*" >&2
  exit 1
}

candidate=
source_sha=
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
    --source-sha) source_sha="${1:-}"; shift ;;
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

for value in \
  candidate source_sha dispatch_ref ref_protected tag_sha rulesets_json \
  approval_sha main_sha main_protected compare_json; do
  [[ -n "${!value}" ]] || fail "missing --${value//_/-}"
done

semver_number='(0|[1-9][0-9]*)'
[[ "$candidate" =~ ^v${semver_number}\.${semver_number}\.${semver_number}-rc\.([1-9][0-9]*)$ ]] ||
  fail "candidate must be strict vMAJOR.MINOR.PATCH-rc.NUMBER SemVer"
[[ "$source_sha" =~ ^[0-9a-f]{40}$ ]] || fail "source SHA is invalid"
[[ "$tag_sha" =~ ^[0-9a-f]{40}$ ]] || fail "candidate tag SHA is invalid"
[[ "$approval_sha" =~ ^[0-9a-f]{40}$ ]] || fail "approval SHA is invalid"
[[ "$main_sha" =~ ^[0-9a-f]{40}$ ]] || fail "main SHA is invalid"
[[ "$ref_protected" == true ]] || fail "dispatch ref is not protected"
[[ "$main_protected" == true ]] || fail "main is not protected"
candidate_ref="refs/tags/$candidate"
[[ "$dispatch_ref" == "$candidate_ref" ]] ||
  fail "dispatch ref must be the exact protected candidate tag"
[[ "$tag_sha" == "$source_sha" ]] ||
  fail "candidate tag does not resolve to the exact producer source SHA"
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
    (.status == "ahead" or .status == "identical") and
    .base_commit.sha == $approval_sha and
    .merge_base_commit.sha == $approval_sha and
    .head_commit.sha == $main_sha
  ' "$compare_json" >/dev/null ||
  fail "approved-ledger SHA is not an exact ancestor of protected main"

printf '%s\n' "$candidate_ref"
