#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
test_root="$repo_root/artifacts/test-release-candidate-ref-$$"
rm -rf "$test_root"
mkdir -p "$test_root"
trap 'rm -rf "$test_root"; rmdir "$repo_root/artifacts" 2>/dev/null || true' EXIT

sha=0123456789abcdef0123456789abcdef01234567
approval_sha=1123456789abcdef0123456789abcdef01234567
main_sha=2123456789abcdef0123456789abcdef01234567
candidate=v1.2.3-rc.4
cat > "$test_root/rulesets.json" <<'JSON'
[
  {
    "target": "tag",
    "enforcement": "active",
    "conditions": {
      "ref_name": {
        "include": ["refs/tags/v*-rc.*"],
        "exclude": []
      }
    }
  }
]
JSON
cat > "$test_root/compare.json" <<JSON
{
  "status": "ahead",
  "base_commit": { "sha": "$approval_sha" },
  "merge_base_commit": { "sha": "$approval_sha" },
  "head_commit": { "sha": "$main_sha" }
}
JSON

"$repo_root/eng/validate-release-candidate-ref.sh" \
  --candidate "$candidate" \
  --source-sha "$sha" \
  --dispatch-ref "refs/tags/$candidate" \
  --ref-protected true \
  --tag-sha "$sha" \
  --rulesets-json "$test_root/rulesets.json" \
  --approval-sha "$approval_sha" \
  --main-sha "$main_sha" \
  --main-protected true \
  --compare-json "$test_root/compare.json" \
  > "$test_root/ref.txt"
[[ "$(cat "$test_root/ref.txt")" == "refs/tags/$candidate" ]]

expect_fail() {
  if "$@" >"$test_root/unexpected.out" 2>"$test_root/expected.err"; then
    echo "expected command to fail: $*" >&2
    exit 1
  fi
}

base=(
  "$repo_root/eng/validate-release-candidate-ref.sh"
  --source-sha "$sha"
  --dispatch-ref "refs/tags/$candidate"
  --ref-protected true
  --tag-sha "$sha"
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
  --candidate "$candidate" --source-sha "$sha" --dispatch-ref refs/pull/1/merge \
  --ref-protected true --tag-sha "$sha" --rulesets-json "$test_root/rulesets.json" \
  --approval-sha "$approval_sha" --main-sha "$main_sha" --main-protected true \
  --compare-json "$test_root/compare.json"
expect_fail "$repo_root/eng/validate-release-candidate-ref.sh" \
  --candidate "$candidate" --source-sha "$sha" --dispatch-ref refs/heads/main \
  --ref-protected true --tag-sha "$sha" --rulesets-json "$test_root/rulesets.json" \
  --approval-sha "$approval_sha" --main-sha "$main_sha" --main-protected true \
  --compare-json "$test_root/compare.json"
expect_fail "$repo_root/eng/validate-release-candidate-ref.sh" \
  --candidate "$candidate" --source-sha "$sha" --dispatch-ref "refs/tags/$candidate" \
  --ref-protected false --tag-sha "$sha" --rulesets-json "$test_root/rulesets.json" \
  --approval-sha "$approval_sha" --main-sha "$main_sha" --main-protected true \
  --compare-json "$test_root/compare.json"
expect_fail "$repo_root/eng/validate-release-candidate-ref.sh" \
  --candidate "$candidate" --source-sha "$sha" --dispatch-ref "refs/tags/$candidate" \
  --ref-protected true --tag-sha 3123456789abcdef0123456789abcdef01234567 \
  --rulesets-json "$test_root/rulesets.json" \
  --approval-sha "$approval_sha" --main-sha "$main_sha" --main-protected true \
  --compare-json "$test_root/compare.json"

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
  --candidate "$candidate" --source-sha "$sha" --dispatch-ref "refs/tags/$candidate" \
  --ref-protected true --tag-sha "$sha" --rulesets-json "$test_root/excluded.json" \
  --approval-sha "$approval_sha" --main-sha "$main_sha" --main-protected true \
  --compare-json "$test_root/compare.json"

cat > "$test_root/diverged.json" <<JSON
{
  "status": "diverged",
  "base_commit": { "sha": "$approval_sha" },
  "merge_base_commit": { "sha": "$sha" },
  "head_commit": { "sha": "$main_sha" }
}
JSON
expect_fail "$repo_root/eng/validate-release-candidate-ref.sh" \
  --candidate "$candidate" --source-sha "$sha" --dispatch-ref "refs/tags/$candidate" \
  --ref-protected true --tag-sha "$sha" --rulesets-json "$test_root/rulesets.json" \
  --approval-sha "$approval_sha" --main-sha "$main_sha" --main-protected true \
  --compare-json "$test_root/diverged.json"

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
