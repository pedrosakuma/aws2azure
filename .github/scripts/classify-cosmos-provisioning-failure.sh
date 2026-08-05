#!/usr/bin/env bash
set -euo pipefail

if [ "$#" -ne 2 ]; then
  echo "usage: $0 <attempt-number> <deployment-operations-json>" >&2
  exit 2
fi

attempt="$1"
operations_file="$2"
if [[ ! "$attempt" =~ ^[1-9][0-9]*$ ]] ||
   [ ! -f "$operations_file" ] ||
   ! jq -e 'type == "array"' "$operations_file" >/dev/null 2>&1; then
  printf 'fail\tinvalid_classifier_input\n'
  exit 0
fi

if [ "$attempt" -ne 1 ]; then
  printf 'fail\tretry_limit_reached\n'
  exit 0
fi

if jq -e '
  def status_message:
    .properties.statusMessage
    | if type == "string" then (try fromjson catch null) else . end;
  def error_objects:
    [status_message | .. | objects | select(has("code"))];
  def target_is_cosmos_account:
    ((.properties.targetResource.resourceType // "") | ascii_downcase)
      == "microsoft.documentdb/databaseaccounts"
    or ((.properties.targetResource.id // "")
      | test("/providers/Microsoft[.]DocumentDB/databaseAccounts/"; "i"));
  def forbidden_error_code:
    (.code | tostring) as $code
    | ($code != "ResourceDeploymentFailure" and $code != "RequestTimeout");
  def is_retryable_cosmos_failure:
    target_is_cosmos_account
    and (error_objects | length) == 2
    and any(error_objects[]; .code == "ResourceDeploymentFailure")
    and any(error_objects[];
      .code == "RequestTimeout"
      and ((.message // "") | test("Database account creation failed"; "i"))
      and ((.message // "") | test(
        "(^|[^0-9])HTTP[ :]*408([^0-9]|$)|StatusCode[\" ]*:[ ]*408([^0-9]|$)";
        "i")))
    and (any(error_objects[]; forbidden_error_code) | not);

  [.[] | select(.properties.provisioningState == "Failed")] as $failed
  | (($failed | length) > 0 and all($failed[]; is_retryable_cosmos_failure))
' "$operations_file" >/dev/null; then
  printf 'retry\tcosmos_account_request_timeout_408\n'
else
  printf 'fail\tnon_retryable_deployment_failure\n'
fi
