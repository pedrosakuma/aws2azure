#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
scratch_dir="$script_dir/.cosmos-provisioning-classifier-test-$$"
trap 'rm -rf "$scratch_dir"' EXIT
mkdir -p "$scratch_dir"

cat > "$scratch_dir/retryable.json" <<'JSON'
[
  {
    "properties": {
      "provisioningState": "Failed",
      "targetResource": {
        "id": "/subscriptions/example/resourceGroups/example/providers/Microsoft.DocumentDB/databaseAccounts/a2aloadexample",
        "resourceType": "Microsoft.DocumentDB/databaseAccounts"
      },
      "statusMessage": {
        "status": "Failed",
        "error": {
          "code": "ResourceDeploymentFailure",
          "message": "The resource write operation reached terminal provisioning state Failed.",
          "details": [
            {
              "code": "RequestTimeout",
              "message": "Database account creation failed. Operation Id: example. StatusCode: 408, ActivityId: example."
            }
          ]
        }
      }
    }
  }
]
JSON

classify() {
  "$script_dir/classify-cosmos-provisioning-failure.sh" "$1" "$2"
}

actual="$(classify 1 "$scratch_dir/retryable.json")"
[ "$actual" = $'retry\tcosmos_account_request_timeout_408' ]

actual="$(classify 2 "$scratch_dir/retryable.json")"
[ "$actual" = $'fail\tretry_limit_reached' ]

jq '.[0].properties.statusMessage |= tojson' \
  "$scratch_dir/retryable.json" > "$scratch_dir/string-status-message.json"
actual="$(classify 1 "$scratch_dir/string-status-message.json")"
[ "$actual" = $'retry\tcosmos_account_request_timeout_408' ]

assert_non_retryable() {
  local name="$1"
  local filter="$2"
  jq "$filter" "$scratch_dir/retryable.json" > "$scratch_dir/$name.json"
  actual="$(classify 1 "$scratch_dir/$name.json")"
  [ "$actual" = $'fail\tnon_retryable_deployment_failure' ]
}

assert_non_retryable authorization \
  '.[0].properties.statusMessage.error.details[0].code = "AuthorizationFailed"'
assert_non_retryable quota \
  '.[0].properties.statusMessage.error.details[0].code = "QuotaExceeded"'
assert_non_retryable policy \
  '.[0].properties.statusMessage.error.details[0].code = "RequestDisallowedByPolicy"'
assert_non_retryable validation \
  '.[0].properties.statusMessage.error.details[0].code = "ValidationFailed"'
assert_non_retryable naming \
  '.[0].properties.statusMessage.error.details[0].code = "InvalidResourceName"'
assert_non_retryable template \
  '.[0].properties.statusMessage.error.details[0].code = "InvalidTemplate"'
assert_non_retryable conflict \
  '.[0].properties.statusMessage.error.details[0].code = "Conflict"'
assert_non_retryable generic-deployment-failed \
  '.[0].properties.statusMessage.error.code = "DeploymentFailed"
   | .[0].properties.statusMessage.error.details = []'
assert_non_retryable timeout-without-408 \
  '.[0].properties.statusMessage.error.details[0].message =
     "Database account creation failed because the operation timed out."'
assert_non_retryable http-408-without-timeout-code \
  '.[0].properties.statusMessage.error.details[0].code = "ServiceUnavailable"'
assert_non_retryable wrong-resource-type \
  '.[0].properties.targetResource.resourceType = "Microsoft.Storage/storageAccounts"
   | .[0].properties.targetResource.id =
       "/subscriptions/example/resourceGroups/example/providers/Microsoft.Storage/storageAccounts/example"'
assert_non_retryable mixed-deterministic-failure \
  '. += [{
    "properties": {
      "provisioningState": "Failed",
      "targetResource": {
        "id": "/subscriptions/example/resourceGroups/example/providers/Microsoft.DocumentDB/databaseAccounts/other",
        "resourceType": "Microsoft.DocumentDB/databaseAccounts"
      },
      "statusMessage": {
        "error": {
          "code": "ResourceDeploymentFailure",
          "details": [{"code": "AuthorizationFailed", "message": "Denied"}]
        }
      }
    }
  }]'

printf '{not-json' > "$scratch_dir/malformed.json"
actual="$(classify 1 "$scratch_dir/malformed.json")"
[ "$actual" = $'fail\tinvalid_classifier_input' ]

workflow="$script_dir/../workflows/workload-load-real-azure.yml"
[ "$(grep -F -c 'if ! provision 2 "$RETRY_RG_NAME" "$retry_deployment"; then' "$workflow")" -eq 1 ]
grep -F -q 'if [ "$provision_status" -ne 10 ]; then' "$workflow"
grep -F -q '"$PRIMARY_RG_NAME" "$RETRY_RG_NAME"' "$workflow"

echo "Cosmos provisioning failure classifier tests passed."
