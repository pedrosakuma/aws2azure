#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
test_root="$repo_root/artifacts/test-release-candidate-ref-$$"
rm -rf "$test_root"
mkdir -p "$test_root"
trap 'rm -rf "$test_root"; rmdir "$repo_root/artifacts" 2>/dev/null || true' EXIT

candidate_sha=0123456789abcdef0123456789abcdef01234567
orchestration_sha=1123456789abcdef0123456789abcdef01234567
approval_sha=$orchestration_sha
main_sha=2123456789abcdef0123456789abcdef01234567
candidate=v1.2.3-rc.4
cat > "$test_root/compact-rulesets.json" <<'JSON'
[
  {
    "id": 19148912,
    "name": "Protect release candidate tags",
    "target": "tag",
    "source_type": "Repository",
    "source": "pedrosakuma/aws2azure",
    "enforcement": "active",
    "node_id": "RRS_lACqUmVwb3NpdG9yec5KFHRFzgEkMHA",
    "_links": {
      "self": {
        "href": "https://api.github.com/repos/pedrosakuma/aws2azure/rulesets/19148912"
      },
      "html": {
        "href": "https://github.com/pedrosakuma/aws2azure/rules/19148912"
      }
    },
    "created_at": "2026-07-18T18:13:18.367Z",
    "updated_at": "2026-07-18T18:13:18.386Z"
  }
]
JSON
mkdir -p "$test_root/mock-bin" "$test_root/details"
cat > "$test_root/details/19148912.json" <<'JSON'
{
  "id": 19148912,
  "name": "Protect release candidate tags",
  "target": "tag",
  "source_type": "Repository",
  "source": "pedrosakuma/aws2azure",
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
cat > "$test_root/mock-bin/gh" <<'SH'
#!/usr/bin/env bash
set -euo pipefail
[[ "$#" == 6 ]]
[[ "$1" == api ]]
[[ "$2" == -H ]]
[[ "$3" == "Accept: application/vnd.github+json" ]]
[[ "$4" == -H ]]
[[ "$5" == "X-GitHub-Api-Version: 2022-11-28" ]]
case "$6" in
  /repos/pedrosakuma/aws2azure/rulesets/[1-9]*)
    ruleset_id="${6##*/}"
    cat "$MOCK_RULESET_DETAIL_DIR/$ruleset_id.json"
    ;;
  *)
    exit 1
    ;;
esac
SH
chmod +x "$test_root/mock-bin/gh"

resolve_rulesets() {
  rm -f "$test_root/rulesets.json"
  GH_BIN="$test_root/mock-bin/gh" \
    MOCK_RULESET_DETAIL_DIR="$test_root/details" \
    "$repo_root/eng/resolve-release-candidate-rulesets.sh" \
      --repository pedrosakuma/aws2azure \
      --rulesets-json "${1:-$test_root/compact-rulesets.json}" \
      --output-json "$test_root/rulesets.json"
}

resolve_rulesets
cp "$test_root/rulesets.json" "$test_root/resolved-first.json"
resolve_rulesets
cmp "$test_root/resolved-first.json" "$test_root/rulesets.json"

expect_fail() {
  if "$@" >"$test_root/unexpected.out" 2>"$test_root/expected.err"; then
    echo "expected command to fail: $*" >&2
    exit 1
  fi
}

expect_resolve_fail() {
  rm -f "$test_root/rulesets.json"
  expect_fail env \
    GH_BIN="$test_root/mock-bin/gh" \
    MOCK_RULESET_DETAIL_DIR="$test_root/details" \
    "$repo_root/eng/resolve-release-candidate-rulesets.sh" \
      --repository pedrosakuma/aws2azure \
      --rulesets-json "$1" \
      --output-json "$test_root/rulesets.json"
  [[ ! -e "$test_root/rulesets.json" ]]
}

printf '[\n' > "$test_root/malformed-list.json"
expect_resolve_fail "$test_root/malformed-list.json"
jq '[.]' "$test_root/compact-rulesets.json" > "$test_root/unflattened-pages.json"
expect_resolve_fail "$test_root/unflattened-pages.json"
jq '[.[0], .[0]]' "$test_root/compact-rulesets.json" > "$test_root/duplicate-id.json"
expect_resolve_fail "$test_root/duplicate-id.json"
jq '.[0] |= del(.id)' "$test_root/compact-rulesets.json" > "$test_root/missing-id.json"
expect_resolve_fail "$test_root/missing-id.json"
jq '.[0].target = "branch"' "$test_root/compact-rulesets.json" > "$test_root/wrong-target.json"
expect_resolve_fail "$test_root/wrong-target.json"
jq '.[0].enforcement = "disabled"' "$test_root/compact-rulesets.json" > "$test_root/inactive.json"
expect_resolve_fail "$test_root/inactive.json"

cp "$test_root/details/19148912.json" "$test_root/detail-valid.json"
for mutation in \
  '.id = 19148913' \
  '.source = "other/repository"' \
  '.target = "branch"' \
  '.enforcement = "evaluate"' \
  '.updated_at = "2026-07-18T18:14:00.000Z"' \
  'del(.conditions)'; do
  jq "$mutation" "$test_root/detail-valid.json" > "$test_root/details/19148912.json"
  expect_resolve_fail "$test_root/compact-rulesets.json"
done
printf '{\n' > "$test_root/details/19148912.json"
expect_resolve_fail "$test_root/compact-rulesets.json"
rm "$test_root/details/19148912.json"
expect_resolve_fail "$test_root/compact-rulesets.json"
cp "$test_root/detail-valid.json" "$test_root/details/19148912.json"
resolve_rulesets

cat > "$test_root/compare.json" <<JSON
{
  "status": "ahead",
  "ahead_by": 1,
  "behind_by": 0,
  "total_commits": 1,
  "base_commit": { "sha": "$approval_sha" },
  "merge_base_commit": { "sha": "$approval_sha" },
  "head_commit": { "sha": "$main_sha" }
}
JSON

"$repo_root/eng/validate-release-candidate-ref.sh" \
  --candidate "$candidate" \
  --orchestration-sha "$orchestration_sha" \
  --dispatch-ref refs/heads/main \
  --ref-protected true \
  --tag-sha "$candidate_sha" \
  --rulesets-json "$test_root/rulesets.json" \
  --approval-sha "$approval_sha" \
  --main-sha "$main_sha" \
  --main-protected true \
  --compare-json "$test_root/compare.json" \
  > "$test_root/ref.txt"
[[ "$(cat "$test_root/ref.txt")" == "refs/tags/$candidate" ]]

base=(
  "$repo_root/eng/validate-release-candidate-ref.sh"
  --orchestration-sha "$orchestration_sha"
  --dispatch-ref refs/heads/main
  --ref-protected true
  --tag-sha "$candidate_sha"
  --rulesets-json "$test_root/rulesets.json"
  --approval-sha "$approval_sha"
  --main-sha "$main_sha"
  --main-protected true
  --compare-json "$test_root/compare.json"
)
expect_fail "${base[@]}" --candidate v1.2.3-rc.04
expect_fail "${base[@]}" --candidate v01.2.3-rc.4
expect_fail "${base[@]}" --candidate v1.2.3-rc4
expect_fail "$repo_root/eng/validate-release-candidate-ref.sh" \
  --candidate "$candidate" --orchestration-sha "$orchestration_sha" \
  --dispatch-ref refs/pull/1/merge \
  --ref-protected true --tag-sha "$candidate_sha" --rulesets-json "$test_root/rulesets.json" \
  --approval-sha "$approval_sha" --main-sha "$main_sha" --main-protected true \
  --compare-json "$test_root/compare.json"
expect_fail "$repo_root/eng/validate-release-candidate-ref.sh" \
  --candidate "$candidate" --orchestration-sha "$orchestration_sha" \
  --dispatch-ref "refs/tags/$candidate" \
  --ref-protected true --tag-sha "$candidate_sha" --rulesets-json "$test_root/rulesets.json" \
  --approval-sha "$approval_sha" --main-sha "$main_sha" --main-protected true \
  --compare-json "$test_root/compare.json"
expect_fail "$repo_root/eng/validate-release-candidate-ref.sh" \
  --candidate "$candidate" --orchestration-sha "$orchestration_sha" \
  --dispatch-ref refs/heads/main \
  --ref-protected false --tag-sha "$candidate_sha" --rulesets-json "$test_root/rulesets.json" \
  --approval-sha "$approval_sha" --main-sha "$main_sha" --main-protected true \
  --compare-json "$test_root/compare.json"
expect_fail "$repo_root/eng/validate-release-candidate-ref.sh" \
  --candidate "$candidate" --orchestration-sha "$orchestration_sha" \
  --dispatch-ref refs/heads/main \
  --ref-protected true --tag-sha "$candidate_sha" \
  --rulesets-json "$test_root/rulesets.json" \
  --approval-sha 3123456789abcdef0123456789abcdef01234567 \
  --main-sha "$main_sha" --main-protected true \
  --compare-json "$test_root/compare.json"

validate_compare() {
  local compare_path="$1"
  local comparison_main_sha="${2:-$main_sha}"
  "$repo_root/eng/validate-release-candidate-ref.sh" \
    --candidate "$candidate" \
    --orchestration-sha "$orchestration_sha" \
    --dispatch-ref refs/heads/main \
    --ref-protected true \
    --tag-sha "$candidate_sha" \
    --rulesets-json "$test_root/rulesets.json" \
    --approval-sha "$approval_sha" \
    --main-sha "$comparison_main_sha" \
    --main-protected true \
    --compare-json "$compare_path"
}

cat > "$test_root/identical.json" <<JSON
{
  "url": "https://api.github.com/repos/pedrosakuma/aws2azure/compare/$approval_sha...$approval_sha",
  "html_url": "https://github.com/pedrosakuma/aws2azure/compare/$approval_sha...$approval_sha",
  "permalink_url": "https://github.com/pedrosakuma/aws2azure/compare/pedrosakuma:$approval_sha...pedrosakuma:$approval_sha",
  "diff_url": "https://github.com/pedrosakuma/aws2azure/compare/$approval_sha...$approval_sha.diff",
  "patch_url": "https://github.com/pedrosakuma/aws2azure/compare/$approval_sha...$approval_sha.patch",
  "status": "identical",
  "ahead_by": 0,
  "behind_by": 0,
  "total_commits": 0,
  "base_commit": { "sha": "$approval_sha" },
  "merge_base_commit": { "sha": "$approval_sha" },
  "commits": [],
  "files": []
}
JSON
validate_compare "$test_root/identical.json" "$approval_sha" \
  > "$test_root/identical-ref.txt"
[[ "$(cat "$test_root/identical-ref.txt")" == "refs/tags/$candidate" ]]

jq '.head_commit = null' \
  "$test_root/identical.json" > "$test_root/identical-head-null.json"
validate_compare "$test_root/identical-head-null.json" "$approval_sha" >/dev/null

jq --arg sha "$approval_sha" '.head_commit = {sha: $sha}' \
  "$test_root/identical.json" > "$test_root/identical-head-object.json"
validate_compare "$test_root/identical-head-object.json" "$approval_sha" >/dev/null

expect_fail validate_compare "$test_root/identical.json" "$main_sha"
for mutation in \
  '.status = "behind"' \
  '.status = "diverged"' \
  '.status = "unknown"' \
  '.status = null' \
  '.ahead_by = 1' \
  '.ahead_by = null' \
  '.behind_by = 1' \
  '.behind_by = null' \
  '.total_commits = 1' \
  '.total_commits = null' \
  'del(.base_commit)' \
  'del(.merge_base_commit)' \
  '.base_commit = null' \
  '.merge_base_commit = null' \
  '.base_commit.sha = null' \
  '.merge_base_commit.sha = null' \
  '.base_commit.sha = "3123456789abcdef0123456789abcdef01234567"' \
  '.merge_base_commit.sha = "3123456789abcdef0123456789abcdef01234567"' \
  '.head_commit = {"sha":"3123456789abcdef0123456789abcdef01234567"}'; do
  jq "$mutation" "$test_root/identical.json" > "$test_root/invalid-identical.json"
  expect_fail validate_compare "$test_root/invalid-identical.json" "$approval_sha"
done

for mutation in \
  '.ahead_by = 0' \
  '.ahead_by = 1.5' \
  '.behind_by = 1' \
  '.total_commits = 2' \
  'del(.ahead_by)' \
  'del(.behind_by)' \
  'del(.total_commits)' \
  '.head_commit = null' \
  'del(.head_commit)' \
  '.head_commit.sha = "3123456789abcdef0123456789abcdef01234567"' \
  'del(.base_commit.sha)' \
  'del(.merge_base_commit.sha)'; do
  jq "$mutation" "$test_root/compare.json" > "$test_root/invalid-ahead.json"
  expect_fail validate_compare "$test_root/invalid-ahead.json"
done
printf '{\n' > "$test_root/malformed-compare.json"
expect_fail validate_compare "$test_root/malformed-compare.json"

cat > "$test_root/excluded.json" <<'JSON'
[
  {
    "target": "tag",
    "enforcement": "active",
    "conditions": {
      "ref_name": {
        "include": ["~ALL"],
        "exclude": ["refs/tags/v1.2.3-rc.*"]
      }
    }
  }
]
JSON
expect_fail "$repo_root/eng/validate-release-candidate-ref.sh" \
  --candidate "$candidate" --orchestration-sha "$orchestration_sha" \
  --dispatch-ref refs/heads/main \
  --ref-protected true --tag-sha "$candidate_sha" --rulesets-json "$test_root/excluded.json" \
  --approval-sha "$approval_sha" --main-sha "$main_sha" --main-protected true \
  --compare-json "$test_root/compare.json"

cat > "$test_root/diverged.json" <<JSON
{
  "status": "diverged",
  "ahead_by": 1,
  "behind_by": 1,
  "total_commits": 1,
  "base_commit": { "sha": "$approval_sha" },
  "merge_base_commit": { "sha": "$candidate_sha" },
  "head_commit": { "sha": "$main_sha" }
}
JSON
expect_fail "$repo_root/eng/validate-release-candidate-ref.sh" \
  --candidate "$candidate" --orchestration-sha "$orchestration_sha" \
  --dispatch-ref refs/heads/main \
  --ref-protected true --tag-sha "$candidate_sha" --rulesets-json "$test_root/rulesets.json" \
  --approval-sha "$approval_sha" --main-sha "$main_sha" --main-protected true \
  --compare-json "$test_root/diverged.json"

orchestration_root="$test_root/orchestration"
candidate_root="$test_root/candidate-source"
mkdir -p \
  "$orchestration_root/.github/workflows" \
  "$orchestration_root/.github/actions/dotnet-setup" \
  "$orchestration_root/eng" \
  "$candidate_root/docker" \
  "$candidate_root/src/Aws2Azure.Proxy"
for path in \
  .github/workflows/release-candidate.yml \
  .github/actions/dotnet-setup/action.yml \
  eng/release-candidate-inputs.py \
  eng/release-candidate-package.py \
  eng/resolve-release-candidate-rulesets.sh \
  eng/resolve-sealed-runtime.sh \
  eng/smoke-release-candidate.sh; do
  printf 'trusted orchestration\n' > "$orchestration_root/$path"
done
printf 'license\n' > "$candidate_root/LICENSE"
printf '{}\n' > "$candidate_root/docker/config.json"
printf '<Project />\n' > "$candidate_root/src/Aws2Azure.Proxy/Aws2Azure.Proxy.csproj"
for root in "$orchestration_root" "$candidate_root"; do
  git -C "$root" init -q
  git -C "$root" add .
  git -C "$root" \
    -c user.name=Test \
    -c user.email=test@example.invalid \
    commit -qm initial
done
actual_orchestration="$(git -C "$orchestration_root" rev-parse HEAD)"
actual_candidate="$(git -C "$candidate_root" rev-parse HEAD)"
"$repo_root/eng/validate-release-candidate-checkouts.sh" \
  --orchestration-root "$orchestration_root" \
  --orchestration-sha "$actual_orchestration" \
  --candidate-root "$candidate_root" \
  --candidate-sha "$actual_candidate"
[[ ! -e "$candidate_root/.github/workflows/release-candidate.yml" ]]
rm "$orchestration_root/eng/resolve-sealed-runtime.sh"
git -C "$orchestration_root" add -u
git -C "$orchestration_root" \
  -c user.name=Test \
  -c user.email=test@example.invalid \
  commit -qm 'remove required helper'
missing_helper_sha="$(git -C "$orchestration_root" rev-parse HEAD)"
expect_fail "$repo_root/eng/validate-release-candidate-checkouts.sh" \
  --orchestration-root "$orchestration_root" \
  --orchestration-sha "$missing_helper_sha" \
  --candidate-root "$candidate_root" \
  --candidate-sha "$actual_candidate"
git -C "$orchestration_root" reset --hard -q "$actual_orchestration"
expect_fail "$repo_root/eng/validate-release-candidate-checkouts.sh" \
  --orchestration-root "$orchestration_root" \
  --orchestration-sha "$actual_candidate" \
  --candidate-root "$candidate_root" \
  --candidate-sha "$actual_candidate"
printf 'dirty\n' >> "$candidate_root/LICENSE"
expect_fail "$repo_root/eng/validate-release-candidate-checkouts.sh" \
  --orchestration-root "$orchestration_root" \
  --orchestration-sha "$actual_orchestration" \
  --candidate-root "$candidate_root" \
  --candidate-sha "$actual_candidate"
git -C "$candidate_root" checkout -- LICENSE

case "$(uname -m)" in
  x86_64|amd64) wrong_rid=linux-arm64 ;;
  aarch64|arm64) wrong_rid=linux-x64 ;;
  *) wrong_rid=linux-x64 ;;
esac
expect_fail "$repo_root/eng/smoke-release-candidate.sh" \
  --rid "$wrong_rid" \
  --executable /bin/true \
  --work-dir "$test_root/smoke"

python3 "$repo_root/eng/test-release-candidate-producer.py"
