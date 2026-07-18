#!/usr/bin/env bash
set -euo pipefail

fail() {
  echo "release-candidate-checkouts: $*" >&2
  exit 1
}

orchestration_root=
orchestration_sha=
candidate_root=
candidate_sha=

while (($# > 0)); do
  option="$1"
  shift
  case "$option" in
    --orchestration-root) orchestration_root="${1:-}"; shift ;;
    --orchestration-sha) orchestration_sha="${1:-}"; shift ;;
    --candidate-root) candidate_root="${1:-}"; shift ;;
    --candidate-sha) candidate_sha="${1:-}"; shift ;;
    *) fail "unknown option: $option" ;;
  esac
done

[[ -n "$orchestration_root" ]] || fail "missing --orchestration-root"
[[ -n "$orchestration_sha" ]] || fail "missing --orchestration-sha"
[[ -n "$candidate_root" ]] || fail "missing --candidate-root"
[[ -n "$candidate_sha" ]] || fail "missing --candidate-sha"
[[ "$orchestration_sha" =~ ^[0-9a-f]{40}$ ]] ||
  fail "orchestration SHA is invalid"
[[ "$candidate_sha" =~ ^[0-9a-f]{40}$ ]] || fail "candidate SHA is invalid"

for command in git python3; do
  command -v "$command" >/dev/null 2>&1 || fail "$command is required"
done
for root in "$orchestration_root" "$candidate_root"; do
  [[ -d "$root" && ! -L "$root" ]] || fail "checkout is not a regular directory: $root"
done

actual_orchestration="$(git -C "$orchestration_root" rev-parse HEAD)"
actual_candidate="$(git -C "$candidate_root" rev-parse HEAD)"
[[ "$actual_orchestration" == "$orchestration_sha" ]] ||
  fail "orchestration checkout does not match its exact SHA"
[[ "$actual_candidate" == "$candidate_sha" ]] ||
  fail "candidate checkout does not match its exact SHA"
[[ -z "$(git -C "$orchestration_root" status --short)" ]] ||
  fail "orchestration checkout is not clean"
[[ -z "$(git -C "$candidate_root" status --short)" ]] ||
  fail "candidate checkout is not clean"

for relative in \
  .github/workflows/release-candidate.yml \
  .github/actions/dotnet-setup/action.yml \
  eng/release-candidate-inputs.py \
  eng/release-candidate-package.py \
  eng/resolve-release-candidate-rulesets.sh \
  eng/resolve-sealed-runtime.sh \
  eng/smoke-release-candidate.sh; do
  path="$orchestration_root/$relative"
  [[ -f "$path" && ! -L "$path" ]] ||
    fail "orchestration checkout is missing trusted helper: $relative"
done
for relative in \
  LICENSE \
  docker/config.json \
  src/Aws2Azure.Proxy/Aws2Azure.Proxy.csproj; do
  path="$candidate_root/$relative"
  [[ -f "$path" && ! -L "$path" ]] ||
    fail "candidate checkout is missing source material: $relative"
done

python3 - "$orchestration_root" "$candidate_root" <<'PY'
import pathlib
import sys

orchestration = pathlib.Path(sys.argv[1]).resolve()
candidate = pathlib.Path(sys.argv[2]).resolve()
if orchestration == candidate or orchestration in candidate.parents or candidate in orchestration.parents:
    raise SystemExit("release-candidate-checkouts: checkouts must be separate paths")
PY
