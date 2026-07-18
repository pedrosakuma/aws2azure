#!/usr/bin/env bash
set -euo pipefail

gh_bin="${GH_BIN:-gh}"

fail() {
  echo "release-candidate-rulesets: $*" >&2
  exit 1
}

repository=
rulesets_json=
output_json=
fetch_rulesets=false

while (($# > 0)); do
  option="$1"
  shift
  case "$option" in
    --repository) repository="${1:-}"; shift ;;
    --rulesets-json) rulesets_json="${1:-}"; shift ;;
    --fetch-rulesets) fetch_rulesets=true ;;
    --output-json) output_json="${1:-}"; shift ;;
    *) fail "unknown option: $option" ;;
  esac
done

[[ -n "$repository" ]] || fail "missing --repository"
[[ -n "$output_json" ]] || fail "missing --output-json"
[[ "$repository" =~ ^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$ ]] ||
  fail "invalid repository: $repository"
if [[ "$fetch_rulesets" == true ]]; then
  [[ -z "$rulesets_json" ]] ||
    fail "--fetch-rulesets and --rulesets-json are mutually exclusive"
else
  [[ -n "$rulesets_json" ]] || fail "missing --rulesets-json"
  [[ -f "$rulesets_json" && ! -L "$rulesets_json" ]] ||
    fail "compact rulesets input must be a regular file"
fi
command -v "$gh_bin" >/dev/null 2>&1 || fail "required command not found: $gh_bin"
command -v python3 >/dev/null 2>&1 || fail "python3 is required"

output_parent="$(dirname "$output_json")"
[[ -d "$output_parent" && ! -L "$output_parent" ]] ||
  fail "output parent must be a regular directory"
staging_dir="${output_json}.details.$$"
mkdir -m 0700 "$staging_dir" ||
  fail "could not create private detail staging directory"
trap 'rm -rf "$staging_dir"' EXIT

if [[ "$fetch_rulesets" == true ]]; then
  ruleset_pages="$staging_dir/compact-pages.json"
  rulesets_json="$staging_dir/compact.json"
  if ! "$gh_bin" api --paginate --slurp \
    -H "Accept: application/vnd.github+json" \
    -H "X-GitHub-Api-Version: 2022-11-28" \
    "/repos/$repository/rulesets?includes_parents=true&targets=tag&per_page=100" \
    > "$ruleset_pages"; then
    fail "failed to fetch compact tag rulesets"
  fi
  python3 - "$ruleset_pages" "$rulesets_json" <<'PY' ||
import json
import pathlib
import sys

pages_path, output_path = map(pathlib.Path, sys.argv[1:])
try:
    pages = json.loads(pages_path.read_text(encoding="utf-8"))
except (OSError, UnicodeError, json.JSONDecodeError):
    raise SystemExit(1)
if not isinstance(pages, list) or not all(isinstance(page, list) for page in pages):
    raise SystemExit(1)
output_path.write_text(
    json.dumps(
        [ruleset for page in pages for ruleset in page],
        sort_keys=True,
        separators=(",", ":"),
    )
    + "\n",
    encoding="utf-8",
)
PY
    fail "compact tag ruleset pagination response is malformed"
fi

python3 - "$rulesets_json" "$staging_dir/expected.json" "$staging_dir/ids.txt" <<'PY' ||
import json
import pathlib
import sys

source_path, expected_path, ids_path = map(pathlib.Path, sys.argv[1:])
try:
    rulesets = json.loads(source_path.read_text(encoding="utf-8"))
except (OSError, UnicodeError, json.JSONDecodeError):
    raise SystemExit(1)
if not isinstance(rulesets, list) or not rulesets:
    raise SystemExit(1)

expected = {}
for ruleset in rulesets:
    if not isinstance(ruleset, dict):
        raise SystemExit(1)
    ruleset_id = ruleset.get("id")
    if isinstance(ruleset_id, bool) or not isinstance(ruleset_id, int) or ruleset_id <= 0:
        raise SystemExit(1)
    if str(ruleset_id) in expected:
        raise SystemExit(1)
    if ruleset.get("target") != "tag" or ruleset.get("enforcement") != "active":
        raise SystemExit(1)
    for key in ("source", "source_type", "updated_at"):
        if not isinstance(ruleset.get(key), str) or not ruleset[key]:
            raise SystemExit(1)
    expected[str(ruleset_id)] = {
        key: ruleset[key]
        for key in ("id", "source", "source_type", "target", "enforcement", "updated_at")
    }

expected_path.write_text(
    json.dumps(expected, sort_keys=True, separators=(",", ":")) + "\n",
    encoding="utf-8",
)
ids_path.write_text(
    "".join(f"{ruleset_id}\n" for ruleset_id in sorted(map(int, expected))),
    encoding="ascii",
)
PY
  fail "compact tag rulesets are malformed, incomplete, duplicated, inactive, or wrong-target"

while IFS= read -r ruleset_id; do
  detail_json="$staging_dir/$ruleset_id.json"
  if ! "$gh_bin" api -H "Accept: application/vnd.github+json" \
    -H "X-GitHub-Api-Version: 2022-11-28" \
    "/repos/$repository/rulesets/$ruleset_id" > "$detail_json"; then
    fail "failed to fetch exact ruleset detail: $ruleset_id"
  fi
done < "$staging_dir/ids.txt"

staged_output="$staging_dir/resolved.json"
python3 - "$staging_dir/expected.json" "$staging_dir" "$staged_output" <<'PY' ||
import json
import pathlib
import sys

expected_path = pathlib.Path(sys.argv[1])
detail_dir = pathlib.Path(sys.argv[2])
output_path = pathlib.Path(sys.argv[3])
try:
    expected = json.loads(expected_path.read_text(encoding="utf-8"))
except (OSError, UnicodeError, json.JSONDecodeError):
    raise SystemExit(1)

resolved = []
for ruleset_id in sorted(expected, key=int):
    try:
        detail = json.loads(
            (detail_dir / f"{ruleset_id}.json").read_text(encoding="utf-8")
        )
    except (OSError, UnicodeError, json.JSONDecodeError):
        raise SystemExit(1)
    if not isinstance(detail, dict):
        raise SystemExit(1)
    metadata = expected[ruleset_id]
    if any(detail.get(key) != value for key, value in metadata.items()):
        raise SystemExit(1)
    conditions = detail.get("conditions")
    if not isinstance(conditions, dict):
        raise SystemExit(1)
    ref_name = conditions.get("ref_name")
    if not isinstance(ref_name, dict):
        raise SystemExit(1)
    includes = ref_name.get("include")
    excludes = ref_name.get("exclude")
    if (
        not isinstance(includes, list)
        or not includes
        or not all(isinstance(value, str) for value in includes)
        or not isinstance(excludes, list)
        or not all(isinstance(value, str) for value in excludes)
    ):
        raise SystemExit(1)
    resolved.append(detail)

output_path.write_text(
    json.dumps(resolved, indent=2, sort_keys=True) + "\n",
    encoding="utf-8",
)
PY
  fail "detailed tag rulesets are malformed, incomplete, or inconsistent"

if [[ -e "$output_json" && (! -f "$output_json" || -L "$output_json") ]]; then
  fail "resolved rulesets output must be a regular file"
fi
mv -f "$staged_output" "$output_json"
