#!/usr/bin/env bash
set -euo pipefail
# Run from the repository root. All build inputs come from the resolved commit,
# never from the harness checkout's potentially different shipping sources.
revision="${1:?source revision required}"
destination="${2:?project-relative output directory required}"
case "$destination" in
  /*|*..*) echo "Use a project-relative destination without parent traversal." >&2; exit 1 ;;
esac
sha="$(git rev-parse --verify "$revision^{commit}")"
mkdir -p "$destination"
destination="$(realpath "$destination")"
if [[ -e "$destination/source" || -e "$destination/app" || -e "$destination/runtime-identity.json" ]]; then
  echo "Destination must not contain a previous runtime." >&2
  exit 1
fi
git worktree add --detach "$destination/source" "$sha"
trap 'git worktree remove "$destination/source"' EXIT
dotnet publish "$destination/source/src/Aws2Azure.Proxy/Aws2Azure.Proxy.csproj" \
  -c Release -r linux-x64 --self-contained true -p:PublishAot=false -p:EnableRequestDelegateGenerator=true \
  -o "$destination/app" --nologo
git -C "$destination/source" diff --exit-code
dotnet --info > "$destination/build-dotnet-info.txt"
python3 - "$destination" "$sha" <<'PY'
import hashlib, json, pathlib, sys
root = pathlib.Path(sys.argv[1])
app = root / "app"
files = {p.relative_to(app).as_posix(): hashlib.sha256(p.read_bytes()).hexdigest()
         for p in sorted(app.rglob("*")) if p.is_file()}
assert "Aws2Azure.Proxy" in files
(root / "runtime-identity.json").write_text(json.dumps({
    "Commit": sys.argv[2], "Executable": "Aws2Azure.Proxy", "Files": files
}, indent=2) + "\n")
PY
