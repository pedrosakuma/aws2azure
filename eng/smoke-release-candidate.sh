#!/usr/bin/env bash
set -euo pipefail

fail() {
  echo "release-candidate-smoke: $*" >&2
  exit 1
}

rid=
executable=
work_dir=
while (($# > 0)); do
  option="$1"
  shift
  case "$option" in
    --rid) rid="${1:-}"; shift ;;
    --executable) executable="${1:-}"; shift ;;
    --work-dir) work_dir="${1:-}"; shift ;;
    *) fail "unknown option: $option" ;;
  esac
done

[[ "$rid" == linux-x64 || "$rid" == linux-arm64 ]] || fail "unsupported RID"
[[ -f "$executable" && ! -L "$executable" && -x "$executable" ]] ||
  fail "executable must be a regular executable file"
[[ -n "$work_dir" && ! -e "$work_dir" ]] || fail "work directory already exists or is empty"
command -v readelf >/dev/null 2>&1 || fail "readelf is required"
command -v curl >/dev/null 2>&1 || fail "curl is required"
command -v python3 >/dev/null 2>&1 || fail "python3 is required"

host_arch="$(uname -m)"
machine="$(readelf -h "$executable" | sed -n 's/^[[:space:]]*Machine:[[:space:]]*//p')"
case "$rid" in
  linux-x64)
    [[ "$host_arch" == x86_64 || "$host_arch" == amd64 ]] ||
      fail "linux-x64 smoke requires a native x64 runner"
    [[ "$machine" == "Advanced Micro Devices X86-64" ]] ||
      fail "ELF machine is not x86-64"
    ;;
  linux-arm64)
    [[ "$host_arch" == aarch64 || "$host_arch" == arm64 ]] ||
      fail "linux-arm64 smoke requires a native arm64 runner"
    [[ "$machine" == AArch64 ]] || fail "ELF machine is not AArch64"
    ;;
esac

install -d -m 0700 "$work_dir"
cat > "$work_dir/config.json" <<'JSON'
{
  "services": { "s3": { "enabled": true } },
  "bindings": [{
    "aws": { "accessKeyId": "rc-smoke", "secretAccessKey": "rc-smoke-secret" },
    "azure": {
      "s3": {
        "kind": "blob",
        "target": { "accountName": "rcsmoke" },
        "auth": { "mode": "sharedKey", "key": "dGVzdA==" }
      }
    }
  }]
}
JSON
port="$(
  python3 - <<'PY'
import socket
with socket.socket() as sock:
    sock.bind(("127.0.0.1", 0))
    print(sock.getsockname()[1])
PY
)"

pid=
cleanup() {
  if [[ -n "$pid" ]] && kill -0 "$pid" 2>/dev/null; then
    kill "$pid"
    wait "$pid" 2>/dev/null || true
  fi
}
trap cleanup EXIT

ASPNETCORE_URLS="http://127.0.0.1:$port" \
AWS2AZURE_CONFIG_FILE="$work_dir/config.json" \
  "$executable" >"$work_dir/proxy.log" 2>&1 &
pid=$!

for _ in $(seq 1 30); do
  if curl --fail --silent \
      "http://127.0.0.1:$port/_aws2azure/health" >"$work_dir/health.json"; then
    cleanup
    pid=
    exit 0
  fi
  kill -0 "$pid" 2>/dev/null ||
    fail "proxy exited before becoming healthy; see $work_dir/proxy.log"
  sleep 1
done
fail "proxy did not become healthy; see $work_dir/proxy.log"
