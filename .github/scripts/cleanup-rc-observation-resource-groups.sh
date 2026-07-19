#!/usr/bin/env bash
set -euo pipefail

usage() {
  echo "usage: $0 '<json-array-of-rc-observation-resource-groups>' [cleanup-script]" >&2
}

fail() {
  echo "::error::RC observation cleanup: $*" >&2
  exit 2
}

if [ "$#" -lt 1 ] || [ "$#" -gt 2 ]; then
  usage
  exit 2
fi

groups_json="$1"
cleanup_script="${2:-.github/scripts/cleanup-real-azure-resource-groups.sh}"

[ -n "$groups_json" ] || fail "manual resource group input is empty"
[ -f "$cleanup_script" ] || fail "cleanup script not found: $cleanup_script"

validated_groups_json="$(
  jq -cer '
    if type != "array" then
      error("input must be a JSON array")
    elif all(.[]; type == "string" and length > 0) then
      .
    else
      error("group names must be non-empty strings")
    end
  ' <<< "$groups_json"
)" || fail "manual resource group input must be a JSON array of exact group names"
mapfile -t requested_groups < <(jq -r '.[]' <<< "$validated_groups_json")

if [ "${#requested_groups[@]}" -eq 0 ]; then
  echo "No exact RC observation resource groups requested."
  exit 0
fi

declare -A seen=()
declare -a candidates=()

for resource_group in "${requested_groups[@]}"; do
  if [[ ! "$resource_group" =~ ^aws2azure-rc-observe-(s3-basic-object-crud|secretsmanager-basic-lifecycle)-([1-9][0-9]*)-([1-9][0-9]*)$ ]]; then
    fail "resource group '$resource_group' is not an exact RC observation group name"
  fi
  expected_profile="${BASH_REMATCH[1]}"
  expected_run_id="${BASH_REMATCH[2]}"
  expected_run_attempt="${BASH_REMATCH[3]}"

  if [ -n "${seen[$resource_group]:-}" ]; then
    fail "duplicate resource group '$resource_group'"
  fi
  seen[$resource_group]=1

  if ! exists="$(az group exists --name "$resource_group" -o tsv)"; then
    fail "could not determine whether resource group '$resource_group' exists"
  fi
  exists="${exists//$'\r'/}"
  case "$exists" in
    false)
      echo "Resource group $resource_group is already absent; nothing to delete."
      continue
      ;;
    true) ;;
    *) fail "Azure returned an unexpected existence result for '$resource_group': $exists" ;;
  esac

  tags="$(az group show --name "$resource_group" --query tags -o json)"
  purpose="$(jq -er '."purpose" // empty' <<< "$tags")" ||
    fail "resource group '$resource_group' does not have a purpose tag"
  profile="$(jq -er '."profile" // empty' <<< "$tags")" ||
    fail "resource group '$resource_group' does not have a profile tag"
  run_id="$(jq -er '."run-id" // empty' <<< "$tags")" ||
    fail "resource group '$resource_group' does not have a run-id tag"
  run_attempt="$(jq -er '."run-attempt" // empty' <<< "$tags" || true)"
  rc="$(jq -er '."rc" // empty' <<< "$tags" || true)"

  [ "$purpose" = aws2azure-rc-observation ] ||
    fail "resource group '$resource_group' purpose tag is '$purpose', not aws2azure-rc-observation"
  [ "$profile" = "$expected_profile" ] ||
    fail "resource group '$resource_group' profile tag '$profile' does not match its exact name"
  [ "$run_id" = "$expected_run_id" ] ||
    fail "resource group '$resource_group' run-id tag '$run_id' does not match its exact name"
  if [ -n "$run_attempt" ] && [ "$run_attempt" != "$expected_run_attempt" ]; then
    fail "resource group '$resource_group' run-attempt tag '$run_attempt' does not match its exact name"
  fi
  if [ -n "$rc" ] &&
     [[ ! "$rc" =~ ^v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)-rc\.[1-9][0-9]*$ ]]; then
    fail "resource group '$resource_group' rc tag is not an exact release-candidate id"
  fi

  echo "Verified exact RC observation group $resource_group for cleanup."
  candidates+=("$resource_group")
done

if [ "${#candidates[@]}" -eq 0 ]; then
  echo "No existing exact RC observation resource groups require cleanup."
  exit 0
fi

bash "$cleanup_script" "${candidates[@]}"
