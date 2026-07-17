#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
executable_name="Aws2Azure.Proxy"
manifest_name="sealed-runtime-manifest.json"
hashes_name="runtime-sha256.txt"

fail() {
  echo "sealed-runtime: $*" >&2
  exit 1
}

usage() {
  cat >&2 <<'EOF'
usage:
  eng/sealed-runtime-manifest.sh runtime-hashes <runtime-dir> <output-file>
  eng/sealed-runtime-manifest.sh generate <published-runtime-dir> <bundle-dir>
  eng/sealed-runtime-manifest.sh validate <manifest-path>

generate requires these environment variables:
  SEALED_REPOSITORY SEALED_GIT_SHA SEALED_GIT_REF SEALED_SERVER_URL
  SEALED_WORKFLOW_REF SEALED_RUN_ID SEALED_RUN_ATTEMPT
  SEALED_RUN_STARTED_AT SEALED_PRODUCED_AT
EOF
  exit 2
}

require_command() {
  command -v "$1" >/dev/null 2>&1 || fail "required command not found: $1"
}

require_safe_name() {
  local name="$1"
  [[ -n "$name" && "$name" != "." && "$name" != ".." ]] ||
    fail "unsafe empty or dot runtime file name"
  [[ "$name" != *[!A-Za-z0-9._-]* ]] ||
    fail "unsafe runtime file name: $name"
}

list_runtime_files() {
  local runtime_dir="$1"
  [[ -d "$runtime_dir" ]] || fail "runtime directory not found: $runtime_dir"

  find "$runtime_dir" -mindepth 1 -maxdepth 1 -type f \
    \( -name "$executable_name" -o -name '*.dll' -o -name '*.json' \) \
    -printf '%f\0' |
    LC_ALL=C sort -z
}

load_runtime_files() {
  local runtime_dir="$1"
  mapfile -d '' -t runtime_files < <(list_runtime_files "$runtime_dir")
  ((${#runtime_files[@]} > 0)) ||
    fail "runtime contains no executable, DLL, or JSON files: $runtime_dir"

  local executable_count=0
  local name
  for name in "${runtime_files[@]}"; do
    require_safe_name "$name"
    [[ ! -L "$runtime_dir/$name" ]] ||
      fail "runtime file must not be a symlink: $runtime_dir/$name"
    if [[ "$name" == "$executable_name" ]]; then
      ((executable_count += 1))
    fi
  done

  [[ "$executable_count" -eq 1 ]] ||
    fail "runtime must contain exactly one $executable_name executable"
  [[ -x "$runtime_dir/$executable_name" ]] ||
    fail "runtime executable bit is not set: $runtime_dir/$executable_name"
}

emit_runtime_hashes() {
  local runtime_dir="$1"
  local name digest
  load_runtime_files "$runtime_dir"
  for name in "${runtime_files[@]}"; do
    digest="$(sha256sum "$runtime_dir/$name" | cut -d' ' -f1)"
    printf '%s  ./%s\n' "$digest" "$name"
  done
}

write_runtime_hashes() {
  local runtime_dir="$1"
  local output_file="$2"
  mkdir -p "$(dirname "$output_file")"
  emit_runtime_hashes "$runtime_dir" > "$output_file"
}

require_generate_environment() {
  local variable
  for variable in \
    SEALED_REPOSITORY \
    SEALED_GIT_SHA \
    SEALED_GIT_REF \
    SEALED_SERVER_URL \
    SEALED_WORKFLOW_REF \
    SEALED_RUN_ID \
    SEALED_RUN_ATTEMPT \
    SEALED_RUN_STARTED_AT \
    SEALED_PRODUCED_AT; do
    [[ -n "${!variable:-}" ]] || fail "required environment variable is empty: $variable"
  done
}

generate_manifest() {
  local published_dir="$1"
  local bundle_dir="$2"
  local runtime_dir="$bundle_dir/runtime"
  local hashes_path="$bundle_dir/$hashes_name"
  local manifest_path="$bundle_dir/$manifest_name"

  require_generate_environment
  [[ ! -e "$bundle_dir" ]] ||
    fail "bundle directory already exists: $bundle_dir"

  load_runtime_files "$published_dir"
  mkdir -p "$runtime_dir"

  local name
  for name in "${runtime_files[@]}"; do
    if [[ "$name" == "$executable_name" ]]; then
      install -m 0755 "$published_dir/$name" "$runtime_dir/$name"
    else
      install -m 0644 "$published_dir/$name" "$runtime_dir/$name"
    fi
  done

  write_runtime_hashes "$runtime_dir" "$hashes_path"

  local files_json
  files_json="$(
    for name in "${runtime_files[@]}"; do
      jq -cn \
        --arg path "runtime/$name" \
        --arg sha256 "sha256:$(sha256sum "$runtime_dir/$name" | cut -d' ' -f1)" \
        --argjson size_bytes "$(stat -c '%s' "$runtime_dir/$name")" \
        --argjson executable "$([[ "$name" == "$executable_name" ]] && echo true || echo false)" \
        '{
          path: $path,
          sha256: $sha256,
          size_bytes: $size_bytes,
          executable: $executable
        }'
    done | jq -s '.'
  )"

  local executable_sha256 executable_size aggregate_digest artifact_name archive_name
  executable_sha256="$(sha256sum "$runtime_dir/$executable_name" | cut -d' ' -f1)"
  executable_size="$(stat -c '%s' "$runtime_dir/$executable_name")"
  aggregate_digest="$(sha256sum "$hashes_path" | cut -d' ' -f1)"
  artifact_name="aws2azure-sealed-linux-x64-${aggregate_digest:0:16}-run-$SEALED_RUN_ID-attempt-$SEALED_RUN_ATTEMPT"
  archive_name="$artifact_name.tar"

  jq -S -n \
    --arg repository "$SEALED_REPOSITORY" \
    --arg git_sha "$SEALED_GIT_SHA" \
    --arg git_ref "$SEALED_GIT_REF" \
    --arg executable_path "runtime/$executable_name" \
    --arg executable_name "$executable_name" \
    --arg executable_sha256 "sha256:$executable_sha256" \
    --argjson executable_size "$executable_size" \
    --argjson files "$files_json" \
    --arg aggregate_digest "sha256:$aggregate_digest" \
    --arg artifact_name "$artifact_name" \
    --arg archive_name "$archive_name" \
    --arg workflow_ref "$SEALED_WORKFLOW_REF" \
    --arg workflow_url "$SEALED_SERVER_URL/$SEALED_REPOSITORY/actions/workflows/sealed-runtime.yml" \
    --argjson run_id "$SEALED_RUN_ID" \
    --argjson run_attempt "$SEALED_RUN_ATTEMPT" \
    --arg run_url "$SEALED_SERVER_URL/$SEALED_REPOSITORY/actions/runs/$SEALED_RUN_ID" \
    --arg attempt_url "$SEALED_SERVER_URL/$SEALED_REPOSITORY/actions/runs/$SEALED_RUN_ID/attempts/$SEALED_RUN_ATTEMPT" \
    --arg run_started_at "$SEALED_RUN_STARTED_AT" \
    --arg produced_at "$SEALED_PRODUCED_AT" \
    '{
      schema_version: 1,
      source: {
        repository: $repository,
        git_sha: $git_sha,
        git_ref: $git_ref
      },
      target: {
        operating_system: "linux",
        architecture: "x64",
        rid: "linux-x64"
      },
      runtime: {
        root: "runtime",
        executable: {
          path: $executable_path,
          name: $executable_name,
          sha256: $executable_sha256,
          size_bytes: $executable_size
        },
        files: $files,
        aggregate_digest: $aggregate_digest
      },
      artifact: {
        name: $artifact_name,
        archive_name: $archive_name,
        format: "tar",
        retention_days: 90,
        selection: {
          repository: $repository,
          run_id: $run_id,
          run_attempt: $run_attempt
        }
      },
      producer: {
        event_name: "workflow_dispatch",
        workflow_path: ".github/workflows/sealed-runtime.yml",
        workflow_ref: $workflow_ref,
        workflow_url: $workflow_url,
        run_id: $run_id,
        run_attempt: $run_attempt,
        run_url: $run_url,
        attempt_url: $attempt_url,
        run_started_at: $run_started_at,
        produced_at: $produced_at
      }
    }' > "$manifest_path"

  validate_manifest "$manifest_path"
  printf '%s\n' "$manifest_path"
}

validate_manifest() {
  local manifest_path="$1"
  [[ -f "$manifest_path" && ! -L "$manifest_path" ]] ||
    fail "manifest must be a regular file: $manifest_path"

  local bundle_dir
  bundle_dir="$(cd "$(dirname "$manifest_path")" && pwd)"

  jq -e '
    type == "object" and
    (keys | sort) == ["artifact", "producer", "runtime", "schema_version", "source", "target"] and
    .schema_version == 1 and
    (.source | type == "object" and
      (keys | sort) == ["git_ref", "git_sha", "repository"] and
      (.repository | type == "string") and
      (.git_sha | type == "string") and
      (.git_ref | type == "string")) and
    .target == {
      "operating_system": "linux",
      "architecture": "x64",
      "rid": "linux-x64"
    } and
    (.runtime | type == "object" and
      (keys | sort) == ["aggregate_digest", "executable", "files", "root"] and
      .root == "runtime" and
      (.aggregate_digest | type == "string") and
      (.files | type == "array" and length > 0) and
      (.executable | type == "object" and
        (keys | sort) == ["name", "path", "sha256", "size_bytes"])) and
    (.runtime.files | all(
      type == "object" and
      (keys | sort) == ["executable", "path", "sha256", "size_bytes"] and
      (.path | type == "string") and
      (.sha256 | type == "string") and
      (.size_bytes | type == "number" and . >= 0 and . == floor) and
      (.executable | type == "boolean"))) and
    (.artifact | type == "object" and
      (keys | sort) == ["archive_name", "format", "name", "retention_days", "selection"] and
      .format == "tar" and
      .retention_days == 90 and
      (.selection | type == "object" and
        (keys | sort) == ["repository", "run_attempt", "run_id"])) and
    (.producer | type == "object" and
      (keys | sort) == [
        "attempt_url",
        "event_name",
        "produced_at",
        "run_attempt",
        "run_id",
        "run_started_at",
        "run_url",
        "workflow_path",
        "workflow_ref",
        "workflow_url"
      ] and
      .event_name == "workflow_dispatch" and
      .workflow_path == ".github/workflows/sealed-runtime.yml" and
      (.run_id | type == "number" and . > 0 and . == floor) and
      (.run_attempt | type == "number" and . > 0 and . == floor))
  ' "$manifest_path" >/dev/null ||
    fail "manifest schema validation failed: $manifest_path"

  local repository git_sha git_ref run_id run_attempt run_url attempt_url
  local workflow_ref workflow_url run_started_at produced_at
  repository="$(jq -er '.source.repository' "$manifest_path")"
  git_sha="$(jq -er '.source.git_sha' "$manifest_path")"
  git_ref="$(jq -er '.source.git_ref' "$manifest_path")"
  run_id="$(jq -er '.producer.run_id' "$manifest_path")"
  run_attempt="$(jq -er '.producer.run_attempt' "$manifest_path")"
  run_url="$(jq -er '.producer.run_url' "$manifest_path")"
  attempt_url="$(jq -er '.producer.attempt_url' "$manifest_path")"
  workflow_ref="$(jq -er '.producer.workflow_ref' "$manifest_path")"
  workflow_url="$(jq -er '.producer.workflow_url' "$manifest_path")"
  run_started_at="$(jq -er '.producer.run_started_at' "$manifest_path")"
  produced_at="$(jq -er '.producer.produced_at' "$manifest_path")"

  [[ "$repository" =~ ^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$ ]] ||
    fail "invalid source repository: $repository"
  [[ "$git_sha" =~ ^[0-9a-f]{40}$ ]] ||
    fail "invalid source git SHA: $git_sha"
  if [[ "$git_ref" != "refs/heads/main" ]] &&
     [[ ! "$git_ref" =~ ^refs/tags/v[0-9]+\.[0-9]+\.[0-9]+-rc([.-]?[0-9A-Za-z]+)*$ ]]; then
    fail "source ref is not protected main or a release-candidate tag: $git_ref"
  fi

  [[ "$workflow_ref" == "$repository/.github/workflows/sealed-runtime.yml@$git_ref" ]] ||
    fail "workflow_ref does not bind the producer workflow to the source ref"
  [[ "$run_url" =~ ^https://[^/]+/$repository/actions/runs/$run_id$ ]] ||
    fail "invalid producer run URL: $run_url"
  local server_url="${run_url%/$repository/actions/runs/$run_id}"
  [[ "$workflow_url" == "$server_url/$repository/actions/workflows/sealed-runtime.yml" ]] ||
    fail "invalid producer workflow URL: $workflow_url"
  [[ "$attempt_url" == "$run_url/attempts/$run_attempt" ]] ||
    fail "invalid producer attempt URL: $attempt_url"

  jq -en \
    --arg started "$run_started_at" \
    --arg produced "$produced_at" \
    '($started | fromdateiso8601) <= ($produced | fromdateiso8601)' >/dev/null ||
    fail "producer timestamps must be UTC RFC 3339 values in chronological order"

  [[ "$(jq -er '.artifact.selection.repository' "$manifest_path")" == "$repository" ]] ||
    fail "artifact selection repository does not match source repository"
  [[ "$(jq -er '.artifact.selection.run_id' "$manifest_path")" == "$run_id" ]] ||
    fail "artifact selection run id does not match producer"
  [[ "$(jq -er '.artifact.selection.run_attempt' "$manifest_path")" == "$run_attempt" ]] ||
    fail "artifact selection run attempt does not match producer"

  local runtime_dir="$bundle_dir/runtime"
  [[ -d "$runtime_dir" && ! -L "$runtime_dir" ]] ||
    fail "runtime root must be a real directory: $runtime_dir"
  if find "$runtime_dir" -mindepth 1 -maxdepth 1 \( -type l -o -type d \) -print -quit |
     grep -q .; then
    fail "runtime root must contain regular files only"
  fi

  mapfile -t paths < <(jq -r '.runtime.files[].path' "$manifest_path")
  mapfile -t sorted_paths < <(printf '%s\n' "${paths[@]}" | LC_ALL=C sort -u)
  [[ "${paths[*]}" == "${sorted_paths[*]}" ]] ||
    fail "runtime file paths must be unique and sorted"

  mapfile -t actual_paths < <(
    find "$runtime_dir" -mindepth 1 -maxdepth 1 -type f -printf 'runtime/%f\n' |
      LC_ALL=C sort
  )
  [[ "${paths[*]}" == "${actual_paths[*]}" ]] ||
    fail "runtime file list is incomplete or names files outside the bundle"

  local index path name file expected_sha actual_sha expected_size actual_size
  local expected_executable executable_path executable_sha executable_size
  local executable_entries=0
  executable_path="$(jq -er '.runtime.executable.path' "$manifest_path")"
  executable_sha="$(jq -er '.runtime.executable.sha256' "$manifest_path")"
  executable_size="$(jq -er '.runtime.executable.size_bytes' "$manifest_path")"
  [[ "$(jq -er '.runtime.executable.name' "$manifest_path")" == "$executable_name" ]] ||
    fail "unexpected executable name"
  [[ "$executable_path" == "runtime/$executable_name" ]] ||
    fail "unexpected executable path: $executable_path"

  for index in "${!paths[@]}"; do
    path="${paths[$index]}"
    [[ "$path" =~ ^runtime/[A-Za-z0-9._-]+$ ]] ||
      fail "unsafe runtime path: $path"
    name="${path#runtime/}"
    require_safe_name "$name"
    file="$bundle_dir/$path"
    [[ -f "$file" && ! -L "$file" ]] ||
      fail "listed runtime file is missing or is a symlink: $path"

    expected_sha="$(jq -er ".runtime.files[$index].sha256" "$manifest_path")"
    [[ "$expected_sha" =~ ^sha256:[0-9a-f]{64}$ ]] ||
      fail "invalid SHA-256 value for $path"
    actual_sha="sha256:$(sha256sum "$file" | cut -d' ' -f1)"
    [[ "$actual_sha" == "$expected_sha" ]] ||
      fail "SHA-256 mismatch for $path"

    expected_size="$(jq -er ".runtime.files[$index].size_bytes" "$manifest_path")"
    actual_size="$(stat -c '%s' "$file")"
    [[ "$actual_size" == "$expected_size" ]] ||
      fail "size mismatch for $path"

    expected_executable="$(jq -r ".runtime.files[$index].executable" "$manifest_path")"
    if [[ "$path" == "$executable_path" ]]; then
      ((executable_entries += 1))
      [[ "$expected_executable" == "true" && -x "$file" ]] ||
        fail "runtime executable bit or manifest flag is missing"
      [[ "$expected_sha" == "$executable_sha" && "$expected_size" == "$executable_size" ]] ||
        fail "executable metadata does not match its runtime file entry"
    else
      [[ "$expected_executable" == "false" && ! -x "$file" ]] ||
        fail "non-executable runtime file is marked or installed as executable: $path"
    fi
  done
  [[ "$executable_entries" -eq 1 ]] ||
    fail "runtime file manifest must contain exactly one $executable_path entry"

  local hashes_path="$bundle_dir/$hashes_name"
  [[ -f "$hashes_path" && ! -L "$hashes_path" ]] ||
    fail "canonical runtime hash manifest is missing: $hashes_path"

  emit_manifest_hashes() {
    local listed_path listed_file listed_digest
    for listed_path in "${paths[@]}"; do
      listed_file="$bundle_dir/$listed_path"
      listed_digest="$(sha256sum "$listed_file" | cut -d' ' -f1)"
      printf '%s  ./%s\n' "$listed_digest" "${listed_path#runtime/}"
    done
  }

  cmp --silent <(emit_manifest_hashes) "$hashes_path" ||
    fail "$hashes_name does not match the complete runtime file list"
  local aggregate_digest="sha256:$(emit_manifest_hashes | sha256sum | cut -d' ' -f1)"
  [[ "$(jq -er '.runtime.aggregate_digest' "$manifest_path")" == "$aggregate_digest" ]] ||
    fail "aggregate runtime digest mismatch"

  local aggregate_hex="${aggregate_digest#sha256:}"
  local expected_artifact_name
  expected_artifact_name="aws2azure-sealed-linux-x64-${aggregate_hex:0:16}-run-$run_id-attempt-$run_attempt"
  [[ "$(jq -er '.artifact.name' "$manifest_path")" == "$expected_artifact_name" ]] ||
    fail "artifact name does not contain the complete runtime digest and exact run identity"
  [[ "$(jq -er '.artifact.archive_name' "$manifest_path")" == "$expected_artifact_name.tar" ]] ||
    fail "artifact archive name does not match the exact artifact identity"

  printf 'Validated sealed runtime %s (%s).\n' \
    "$aggregate_digest" "$expected_artifact_name"
}

require_command jq
require_command sha256sum
require_command stat

command_name="${1:-}"
case "$command_name" in
  runtime-hashes)
    [[ "$#" -eq 3 ]] || usage
    write_runtime_hashes "$2" "$3"
    ;;
  generate)
    [[ "$#" -eq 3 ]] || usage
    generate_manifest "$2" "$3"
    ;;
  validate)
    [[ "$#" -eq 2 ]] || usage
    validate_manifest "$2"
    ;;
  *)
    usage
    ;;
esac
