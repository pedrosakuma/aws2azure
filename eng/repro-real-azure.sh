#!/usr/bin/env bash
set -euo pipefail

# Local reproduction of the `integration-real-azure` nightly job (issue #839,
# follow-up to #838). Provisions the same ephemeral resource group + Bicep
# deployment (deploy/realazure/main.bicep) the CI workflow uses, exports the
# exact environment variables RealAzureProxyFixture.cs reads, and tears
# everything down again. See docs/testing/local-real-azure-repro.md for the
# full walkthrough.
#
# This script does NOT re-implement provisioning with ad-hoc `az` resource
# creates: it drives the same `deploy/realazure/main.bicep` template CI uses
# (see .github/workflows/integration-real-azure.yml), so local and CI
# provisioning never drift.
#
# Known sharp edges this script works around (see #838):
#   - The `az` CLI under WSL may resolve to the Windows executable, which
#     emits CRLF line endings even with `-o tsv`. Every captured value is
#     stripped of `\r` before use.
#   - Writing secrets containing `;` (e.g. Service Bus connection strings)
#     unquoted into a file meant to be `source`d truncates at the first `;`
#     because bash's `source` treats bare `;` as a command separator. The
#     emitted env file quotes every value with `printf '%q'`.

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
bicep_file="$repo_root/deploy/realazure/main.bicep"
deployment_name="aws2azure-repro"

fail() {
  echo "repro-real-azure: $*" >&2
  exit 1
}

usage() {
  cat >&2 <<'EOF'
usage:
  eng/repro-real-azure.sh up [options]
  eng/repro-real-azure.sh down --resource-group NAME

Options for "up":
  --resource-group NAME    Resource group to create (default:
                            aws2azure-repro-<unix-epoch>).
  --location LOCATION      Azure region (default: eastus2, matching CI).
  --cosmos-database NAME   Cosmos SQL database name (default: dynamodb).
  --event-hub-name NAME    Event Hub entity name (default: kinesis-smoke).
  --principal-id ID        Object id of a principal to grant the Workload
                            Identity data-plane RBAC roles to (optional;
                            omit to provision the shared-key/SAS smoke
                            matrix only, matching CI when
                            AZURE_CLIENT_OBJECT_ID is unset).
  --env-file PATH          Where to write the sourceable env file (default:
                            <repo-root>/.local/real-azure.env).
  --yes                    Skip the interactive cost/safety confirmation.

Options for "down":
  --resource-group NAME    Resource group to delete (required).

This script requires `az` to already be logged in (`az login`) with a
subscription that has Contributor on the target subscription/resource
group scope. It provisions REAL, BILLED Azure resources.
EOF
  exit 2
}

require_command() {
  command -v "$1" >/dev/null 2>&1 || fail "required command not found: $1"
}

strip_crlf() {
  # Defends against the Windows `az` CLI under WSL emitting CRLF even with
  # `-o tsv` (see #838). Safe no-op against a native Linux `az`.
  local value="$1"
  value="${value//$'\r'/}"
  printf '%s' "$value"
}

az_tsv() {
  strip_crlf "$(az "$@" -o tsv)"
}

confirm_cost_warning() {
  if [ "$skip_confirm" = true ]; then
    return 0
  fi
  cat >&2 <<'EOF'

*******************************************************************
* WARNING: this provisions REAL, BILLED Azure resources:          *
*   - one Standard LRS Storage account (+ a Storage Queue)        *
*   - one Standard Service Bus namespace                          *
*   - one serverless Cosmos DB account + database                 *
*   - one capacity-1 Standard Event Hubs namespace + hub          *
*   - one Event Grid custom topic + event subscription            *
*   - one Key Vault                                                *
* Cosmos DB account creation alone typically takes 5-10 minutes.  *
* Remember to run "down" when finished; nothing here auto-expires.*
*******************************************************************

EOF
  read -r -p "Continue and provision these resources? [y/N] " reply
  case "$reply" in
    y|Y|yes|YES) ;;
    *) fail "aborted by operator" ;;
  esac
}

action="${1:-}"
[ -n "$action" ] || usage
shift || true

resource_group=
location="eastus2"
cosmos_database="dynamodb"
event_hub_name="kinesis-smoke"
principal_id=""
env_file="$repo_root/.local/real-azure.env"
skip_confirm=false

while (($# > 0)); do
  option="$1"
  shift
  case "$option" in
    --resource-group) resource_group="${1:-}"; shift ;;
    --location) location="${1:-}"; shift ;;
    --cosmos-database) cosmos_database="${1:-}"; shift ;;
    --event-hub-name) event_hub_name="${1:-}"; shift ;;
    --principal-id) principal_id="${1:-}"; shift ;;
    --env-file) env_file="${1:-}"; shift ;;
    --yes) skip_confirm=true ;;
    -h|--help) usage ;;
    *) fail "unknown option: $option" ;;
  esac
done

require_command az
require_command jq

case "$action" in
  up)
    [ -f "$bicep_file" ] || fail "bicep template not found: $bicep_file"
    if [ -z "$resource_group" ]; then
      resource_group="aws2azure-repro-$(date -u +%s)"
    fi

    confirm_cost_warning

    echo "repro-real-azure: creating resource group $resource_group in $location" >&2
    az group create -n "$resource_group" -l "$location" \
      --tags purpose=aws2azure-local-repro \
             created="$(date -u +%Y-%m-%dT%H:%M:%SZ)" \
      -o none

    echo "repro-real-azure: deploying deploy/realazure/main.bicep (this can take 10-20 minutes, Cosmos DB dominates)" >&2
    az deployment group create -g "$resource_group" -n "$deployment_name" \
      -f "$bicep_file" \
      -p cosmosDatabaseName="$cosmos_database" \
         eventHubName="$event_hub_name" \
         principalId="$principal_id" \
      -o none

    echo "repro-real-azure: reading deployment outputs and account keys" >&2
    out() {
      az_tsv deployment group show -g "$resource_group" -n "$deployment_name" \
        --query "properties.outputs.$1.value"
    }
    storage_account="$(out storageAccountName)"
    sb_namespace="$(out serviceBusNamespaceName)"
    cosmos_account="$(out cosmosAccountName)"
    cosmos_endpoint="$(out cosmosEndpoint)"
    eh_namespace="$(out eventHubsNamespaceName)"
    kv_uri="$(out keyVaultUri)"
    eg_topic="$(out eventGridTopicName)"
    eg_endpoint="$(out eventGridTopicEndpoint)"
    eg_queue="$(out eventGridEvidenceQueueName)"

    blob_key="$(az_tsv storage account keys list -n "$storage_account" -g "$resource_group" --query '[0].value')"
    sb_conn="$(az_tsv servicebus namespace authorization-rule keys list -g "$resource_group" \
      --namespace-name "$sb_namespace" --name RootManageSharedAccessKey \
      --query primaryConnectionString)"
    cosmos_key="$(az_tsv cosmosdb keys list -n "$cosmos_account" -g "$resource_group" --query primaryMasterKey)"
    eh_conn="$(az_tsv eventhubs namespace authorization-rule keys list -g "$resource_group" \
      --namespace-name "$eh_namespace" --name RootManageSharedAccessKey \
      --query primaryConnectionString)"
    eg_key="$(az_tsv eventgrid topic key list -n "$eg_topic" -g "$resource_group" --query key1)"

    mkdir -p "$(dirname "$env_file")"
    umask 077
    {
      echo "# Generated by eng/repro-real-azure.sh on $(date -u +%Y-%m-%dT%H:%M:%SZ)."
      echo "# Source this file, then run the real-Azure suite, e.g.:"
      echo "#   source $env_file"
      echo "#   dotnet test tests/Aws2Azure.IntegrationTests --filter Category=RealAzure"
      echo "#"
      echo "# Every value is quoted with printf '%q' because unquoted values"
      echo "# containing ';' (e.g. the Service Bus/Event Hubs connection"
      echo "# strings) are silently truncated by bash's 'source' at the"
      echo "# first unescaped ';' (see docs/testing/local-real-azure-repro.md)."
      printf 'export AZURE_BLOB_ACCOUNT=%q\n' "$storage_account"
      printf 'export AZURE_BLOB_KEY=%q\n' "$blob_key"
      printf 'export AZURE_COSMOS_ENDPOINT=%q\n' "$cosmos_endpoint"
      printf 'export AZURE_COSMOS_KEY=%q\n' "$cosmos_key"
      printf 'export AZURE_COSMOS_DATABASE=%q\n' "$cosmos_database"
      printf 'export AZURE_SB_CONNSTR=%q\n' "$sb_conn"
      printf 'export AZURE_EVENTHUBS_CONNSTR=%q\n' "$eh_conn"
      printf 'export AZURE_EVENTHUBS_STREAM=%q\n' "$event_hub_name"
      printf 'export AZURE_EVENTHUBS_PARTITION_COUNT=%q\n' "2"
      printf 'export AZURE_KEYVAULT_URL=%q\n' "$kv_uri"
      printf 'export AZURE_EVENTGRID_TOPIC_ENDPOINT=%q\n' "$eg_endpoint"
      printf 'export AZURE_EVENTGRID_TOPIC_KEY=%q\n' "$eg_key"
      printf 'export AZURE_EVENTGRID_EVIDENCE_QUEUE_NAME=%q\n' "$eg_queue"
      echo "#"
      echo "# AZURE_KEYVAULT_URL is set, but the SecretsManager suite (and the"
      echo "# Workload Identity DynamoDB/Kinesis scenarios) also require"
      echo "# AZURE_FEDERATED_TOKEN_FILE / AZURE_TENANT_ID / AZURE_CLIENT_ID"
      echo "# pointing at a valid federated-credential token; this script does"
      echo "# not mint one locally. See docs/testing/local-real-azure-repro.md"
      echo "# for the manual steps, or leave these unset to skip that suite."
    } > "$env_file"
    chmod 0600 "$env_file"

    echo "repro-real-azure: resource group: $resource_group" >&2
    echo "repro-real-azure: env file written to: $env_file" >&2
    echo "repro-real-azure: run: source $env_file && dotnet test tests/Aws2Azure.IntegrationTests --filter Category=RealAzure" >&2
    echo "repro-real-azure: tear down with: eng/repro-real-azure.sh down --resource-group $resource_group" >&2
    ;;

  down)
    [ -n "$resource_group" ] || fail "--resource-group is required for 'down'"
    echo "repro-real-azure: deleting resource group $resource_group (this reuses the CI cleanup script)" >&2
    "$repo_root/.github/scripts/cleanup-real-azure-resource-groups.sh" "$resource_group"
    ;;

  *)
    usage
    ;;
esac
