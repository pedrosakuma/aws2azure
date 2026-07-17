#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
scratch="$repo_root/artifacts/test-sealed-runtime-manifest-$$"
trap 'rm -rf "$scratch"' EXIT

publish_dir="$scratch/publish"
bundle_one="$scratch/bundle-one"
bundle_two="$scratch/bundle-two"
mkdir -p "$publish_dir"
printf '#!/usr/bin/env sh\nexit 0\n' > "$publish_dir/Aws2Azure.Proxy"
chmod 0755 "$publish_dir/Aws2Azure.Proxy"
printf '{"Logging":{"LogLevel":{"Default":"Information"}}}\n' \
  > "$publish_dir/appsettings.json"
printf 'test assembly bytes\n' > "$publish_dir/Aws2Azure.Core.dll"

export SEALED_REPOSITORY="pedrosakuma/aws2azure"
export SEALED_GIT_SHA="05cf2ee000000000000000000000000000000000"
export SEALED_GIT_REF="refs/heads/main"
export SEALED_SERVER_URL="https://github.com"
export SEALED_WORKFLOW_REF="$SEALED_REPOSITORY/.github/workflows/sealed-runtime.yml@$SEALED_GIT_REF"
export SEALED_RUN_ID="123456789"
export SEALED_RUN_ATTEMPT="2"
export SEALED_RUN_STARTED_AT="2026-07-17T18:00:00Z"
export SEALED_PRODUCED_AT="2026-07-17T18:01:00Z"

"$repo_root/eng/sealed-runtime-manifest.sh" generate "$publish_dir" "$bundle_one" >/dev/null
"$repo_root/eng/sealed-runtime-manifest.sh" generate "$publish_dir" "$bundle_two" >/dev/null
cmp --silent \
  "$bundle_one/sealed-runtime-manifest.json" \
  "$bundle_two/sealed-runtime-manifest.json"
cmp --silent "$bundle_one/runtime-sha256.txt" "$bundle_two/runtime-sha256.txt"
cp "$bundle_two/sealed-runtime-manifest.json" "$scratch/original-manifest.json"

aggregate_hex="$(
  jq -r '.runtime.aggregate_digest' "$scratch/original-manifest.json" |
    sed 's/^sha256://'
)"
artifact_name="$(jq -r '.artifact.name' "$scratch/original-manifest.json")"
[[ "$aggregate_hex" =~ ^[0-9a-f]{64}$ ]]
[[ "$artifact_name" == "aws2azure-sealed-linux-x64-$aggregate_hex-run-$SEALED_RUN_ID-attempt-$SEALED_RUN_ATTEMPT" ]]

archive_name="$(jq -r '.artifact.archive_name' "$scratch/original-manifest.json")"
tar \
  --format=gnu \
  --sort=name \
  --owner=0 \
  --group=0 \
  --numeric-owner \
  --mtime='@0' \
  -cf "$scratch/$archive_name" \
  -C "$bundle_two" \
  .
mkdir -p "$scratch/roundtrip"
tar -xf "$scratch/$archive_name" -C "$scratch/roundtrip"
"$repo_root/eng/sealed-runtime-manifest.sh" validate \
  "$scratch/roundtrip/sealed-runtime-manifest.json" >/dev/null

mkfifo "$bundle_one/runtime/unexpected.pipe"
if "$repo_root/eng/sealed-runtime-manifest.sh" validate \
  "$bundle_one/sealed-runtime-manifest.json" >/dev/null 2>&1; then
  echo "runtime FIFO unexpectedly validated as a regular file" >&2
  exit 1
fi
rm "$bundle_one/runtime/unexpected.pipe"

chmod 0644 "$bundle_one/runtime/Aws2Azure.Proxy"
if "$repo_root/eng/sealed-runtime-manifest.sh" validate \
  "$bundle_one/sealed-runtime-manifest.json" >/dev/null 2>&1; then
  echo "runtime without an executable bit unexpectedly validated" >&2
  exit 1
fi
chmod 0755 "$bundle_one/runtime/Aws2Azure.Proxy"
printf 'tamper\n' >> "$bundle_one/runtime/appsettings.json"
if "$repo_root/eng/sealed-runtime-manifest.sh" validate \
  "$bundle_one/sealed-runtime-manifest.json" >/dev/null 2>&1; then
  echo "tampered runtime unexpectedly validated" >&2
  exit 1
fi

jq '.producer.run_attempt = 3' \
  "$scratch/original-manifest.json" \
  > "$bundle_two/sealed-runtime-manifest.json"
if "$repo_root/eng/sealed-runtime-manifest.sh" validate \
  "$bundle_two/sealed-runtime-manifest.json" >/dev/null 2>&1; then
  echo "inconsistent producer identity unexpectedly validated" >&2
  exit 1
fi

jq '.runtime.files[0].path = "runtime/../escape"' \
  "$scratch/original-manifest.json" \
  > "$bundle_two/sealed-runtime-manifest.json"
if "$repo_root/eng/sealed-runtime-manifest.sh" validate \
  "$bundle_two/sealed-runtime-manifest.json" >/dev/null 2>&1; then
  echo "unsafe runtime path unexpectedly validated" >&2
  exit 1
fi

workflow="$repo_root/.github/workflows/sealed-runtime.yml"
trigger_block="$(sed -n '/^on:/,/^permissions:/p' "$workflow")"
grep -q '^  workflow_dispatch:$' <<< "$trigger_block"
if grep -Eq '^  (push|pull_request|schedule|workflow_call):' <<< "$trigger_block"; then
  echo "sealed runtime workflow exposes an untrusted automatic trigger" >&2
  exit 1
fi
grep -q '^  attestations: write$' "$workflow"
grep -q '^  contents: read$' "$workflow"
grep -q '^  id-token: write$' "$workflow"

while IFS= read -r action; do
  [[ "$action" =~ @[0-9a-f]{40}$ ]] || {
    echo "workflow action is not pinned by commit SHA: $action" >&2
    exit 1
  }
done < <(
  sed -n 's/^[[:space:]]*uses:[[:space:]]*\([^[:space:]#]*\).*$/\1/p' "$workflow" |
    grep -v '^\./'
)

echo "Sealed runtime manifest tests passed."
