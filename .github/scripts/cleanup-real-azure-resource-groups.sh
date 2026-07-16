#!/usr/bin/env bash
set -euo pipefail

if [ "$#" -eq 0 ]; then
  echo "usage: $0 <resource-group> [<resource-group> ...]" >&2
  exit 2
fi

readonly max_container_delete_attempts=18
readonly container_delete_retry_seconds=10
readonly group_delete_timeout_seconds=1200

subscription_id="$(az account show --query id -o tsv)"
subscription_id="${subscription_id//$'\r'/}"
declare -a pending_groups=()
failed=0

for resource_group in "$@"; do
  if ! group_exists="$(az group exists --name "$resource_group" -o tsv)"; then
    echo "::error::Could not determine whether resource group $resource_group exists."
    failed=1
    continue
  fi
  group_exists="${group_exists//$'\r'/}"

  case "$group_exists" in
    true) ;;
    false)
      echo "Resource group $resource_group is already absent."
      continue
      ;;
    *)
      echo "::error::Azure returned an unexpected existence result for $resource_group: $group_exists"
      failed=1
      continue
      ;;
  esac

  echo "Preparing $resource_group for deletion."

  vault_names="$(az keyvault list --resource-group "$resource_group" --query "[].name" -o tsv)"
  while IFS= read -r vault_name; do
    vault_name="${vault_name//$'\r'/}"
    [ -z "$vault_name" ] && continue
    az keyvault delete --name "$vault_name" --resource-group "$resource_group" -o none
    if ! az keyvault purge --name "$vault_name" --no-wait -o none; then
      echo "::warning::Could not purge Key Vault $vault_name; its soft-deleted name may remain reserved."
    fi
  done <<< "$vault_names"

  account_names="$(az storage account list --resource-group "$resource_group" --query "[].name" -o tsv)"
  storage_cleanup_failed=0

  while IFS= read -r account_name; do
    account_name="${account_name//$'\r'/}"
    [ -z "$account_name" ] && continue
    account_key="$(az storage account keys list \
      --resource-group "$resource_group" \
      --account-name "$account_name" \
      --query "[0].value" -o tsv)"
    account_key="${account_key//$'\r'/}"
    sas_expiry="$(date -u -d '+1 hour' '+%Y-%m-%dT%H:%MZ')"
    account_sas="$(az storage account generate-sas \
      --account-name "$account_name" \
      --account-key "$account_key" \
      --services b \
      --resource-types sco \
      --permissions dlxy \
      --expiry "$sas_expiry" \
      --https-only \
      -o tsv)"
    account_sas="${account_sas//$'\r'/}"

    account_cleaned=0
    for attempt in $(seq 1 "$max_container_delete_attempts"); do
      container_names="$(az storage container list \
        --account-name "$account_name" \
        --account-key "$account_key" \
        --query "[].name" -o tsv)"

      if [ -z "$container_names" ]; then
        account_cleaned=1
        break
      fi

      delete_failed=0
      versions_remaining=0
      while IFS= read -r container_name; do
        container_name="${container_name//$'\r'/}"
        [ -z "$container_name" ] && continue

        version_rows="$(az storage blob list \
          --account-name "$account_name" \
          --account-key "$account_key" \
          --container-name "$container_name" \
          --include vli \
          --query "[?versionId != null].{name:name, versionId:versionId, legalHold:properties.hasLegalHold}" \
          -o json | jq -c '.[]')"

        root_blob_names="$(jq -s -r 'map(.name) | unique[]' <<< "$version_rows")"
        while IFS= read -r blob_name; do
          [ -z "$blob_name" ] && continue
          has_legal_hold="$(jq -s --arg name "$blob_name" \
            'any(.[]; .name == $name and .legalHold == true)' <<< "$version_rows")"
          if [ "$has_legal_hold" = true ]; then
            az storage blob set-legal-hold \
              --account-name "$account_name" \
              --account-key "$account_key" \
              --container-name "$container_name" \
              --name "$blob_name" \
              --legal-hold false \
              -o none
          fi

          if ! output="$(az storage blob delete \
            --account-name "$account_name" \
            --account-key "$account_key" \
            --container-name "$container_name" \
            --name "$blob_name" \
            --delete-snapshots include \
            -o none 2>&1)" && ! grep -q 'BlobNotFound' <<< "$output"; then
            delete_failed=1
            echo "::warning::Could not mark root blob $account_name/$container_name/$blob_name for deletion: $output"
          fi
        done <<< "$root_blob_names"

        while IFS= read -r version_row; do
          [ -z "$version_row" ] && continue
          blob_name="$(jq -r '.name' <<< "$version_row")"
          version_id="$(jq -r '.versionId' <<< "$version_row")"
          legal_hold="$(jq -r '.legalHold // false' <<< "$version_row")"

          if [ "$legal_hold" = true ] && ! output="$(az storage blob set-legal-hold \
              --account-name "$account_name" \
              --account-key "$account_key" \
              --container-name "$container_name" \
              --name "$blob_name" \
              --legal-hold false \
              -o none 2>&1)"; then
            delete_failed=1
            echo "::warning::Could not clear legal hold on $account_name/$container_name/$blob_name: $output"
          fi

          encoded_container="$(jq -rn --arg value "$container_name" '$value | @uri')"
          encoded_blob="$(jq -rn --arg value "$blob_name" '$value | split("/") | map(@uri) | join("/")')"
          encoded_version="$(jq -rn --arg value "$version_id" '$value | @uri')"
          blob_url="https://${account_name}.blob.core.windows.net/${encoded_container}/${encoded_blob}?versionid=${encoded_version}&${account_sas}"
          response="$(curl -sS -w $'\n%{http_code}' \
            -X DELETE \
            -H 'x-ms-version: 2023-11-03' \
            "$blob_url")"
          http_status="${response##*$'\n'}"
          response_body="${response%$'\n'*}"
          if [ "$http_status" != 202 ] && [ "$http_status" != 404 ]; then
            delete_failed=1
            echo "::warning::Could not permanently delete version $version_id of $account_name/$container_name/$blob_name (HTTP $http_status): $response_body"
          fi
        done <<< "$version_rows"

        remaining_count="$(az storage blob list \
          --account-name "$account_name" \
          --account-key "$account_key" \
          --container-name "$container_name" \
          --include v \
          --query "length([?versionId != null])" \
          -o tsv)"
        remaining_count="${remaining_count//$'\r'/}"
        versions_remaining=$(( versions_remaining + remaining_count ))

        if [ "$remaining_count" -eq 0 ]; then
          encoded_container="$(jq -rn --arg value "$container_name" '$value | @uri')"
          container_url="https://management.azure.com/subscriptions/${subscription_id}/resourceGroups/${resource_group}/providers/Microsoft.Storage/storageAccounts/${account_name}/blobServices/default/containers/${encoded_container}?api-version=2023-05-01"
          if ! output="$(az rest --method delete --url "$container_url" -o none 2>&1)"; then
            delete_failed=1
            echo "::warning::Could not delete empty container $account_name/$container_name through ARM: $output"
          fi
        fi
      done <<< "$container_names"

      if [ "$delete_failed" -eq 0 ] && [ "$versions_remaining" -eq 0 ]; then
        remaining_container_names="$(az storage container list \
          --account-name "$account_name" \
          --account-key "$account_key" \
          --query "[].name" \
          -o tsv)"
        remaining_container_names="${remaining_container_names//$'\r'/}"
        if [ -z "$remaining_container_names" ]; then
          account_cleaned=1
          break
        fi
      fi

      if [ "$attempt" -lt "$max_container_delete_attempts" ]; then
        sleep "$container_delete_retry_seconds"
      fi
    done

    if [ "$account_cleaned" -ne 1 ]; then
      echo "::error::Storage account $account_name still contains protected data or containers."
      storage_cleanup_failed=1
      continue
    fi

    if ! output="$(az storage account delete \
      --resource-group "$resource_group" \
      --name "$account_name" \
      --yes 2>&1)"; then
      echo "::error::Could not delete transient storage account $account_name: $output"
      storage_cleanup_failed=1
    fi
  done <<< "$account_names"

  if [ "$storage_cleanup_failed" -ne 0 ]; then
    failed=1
    continue
  fi

  az group delete --name "$resource_group" --yes --no-wait
  pending_groups+=("$resource_group")
done

deadline=$(( $(date -u +%s) + group_delete_timeout_seconds ))
while [ "${#pending_groups[@]}" -gt 0 ] && [ "$(date -u +%s)" -lt "$deadline" ]; do
  declare -a still_pending=()
  for resource_group in "${pending_groups[@]}"; do
    if ! group_exists="$(az group exists --name "$resource_group" -o tsv)"; then
      echo "::error::Could not verify deletion of resource group $resource_group."
      failed=1
      continue
    fi
    group_exists="${group_exists//$'\r'/}"
    if [ "$group_exists" = false ]; then
      echo "Deleted resource group $resource_group."
    else
      still_pending+=("$resource_group")
    fi
  done
  pending_groups=("${still_pending[@]}")
  if [ "${#pending_groups[@]}" -gt 0 ]; then
    sleep 15
  fi
done

for resource_group in "${pending_groups[@]}"; do
  echo "::error::Azure did not confirm deletion of resource group $resource_group."
  failed=1
done

exit "$failed"
