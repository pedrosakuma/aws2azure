#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
attempts="${1:-3}"

if ! [[ "$attempts" =~ ^[2-9][0-9]*$ ]]; then
  echo "usage: $0 [attempts >= 2]" >&2
  exit 2
fi

scratch="$repo_root/artifacts/verify-deterministic-runtime-$$"
mkdir -p "$scratch"
trap 'rm -rf "$scratch"' EXIT

baseline="$scratch/runtime-1.sha256"
artifacts="$scratch/build"
for attempt in $(seq 1 "$attempts"); do
  dotnet build-server shutdown >/dev/null
  rm -rf "$artifacts"
  dotnet build "$repo_root/src/Aws2Azure.Proxy/Aws2Azure.Proxy.csproj" \
    -c Release \
    --artifacts-path "$artifacts" \
    --disable-build-servers \
    --nologo \
    --verbosity quiet

  runtime="$artifacts/bin/Aws2Azure.Proxy/release"
  manifest="$scratch/runtime-$attempt.sha256"
  "$repo_root/eng/sealed-runtime-manifest.sh" \
    runtime-hashes "$runtime" "$manifest"

  if [ "$attempt" -gt 1 ] && ! cmp --silent "$baseline" "$manifest"; then
    echo "Proxy runtime is not reproducible between isolated builds 1 and $attempt." >&2
    diff --unified "$baseline" "$manifest" >&2 || true
    exit 1
  fi
done

echo "Proxy runtime manifest is reproducible across $attempts isolated builds."
