#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
scratch_dir="$script_dir/.entra-classifier-test-$$"
trap 'rm -rf "$scratch_dir"' EXIT
mkdir -p "$scratch_dir"

cat > "$scratch_dir/expected-401.json" <<'JSON'
{
  "error": "invalid_client",
  "error_description": "AADSTS70021: No matching federated identity record found.",
  "error_codes": [70021]
}
JSON
actual="$("$script_dir/classify-entra-federation-response.sh" \
  401 "$scratch_dir/expected-401.json")"
[ "$actual" = $'retry\tinvalid_client\t70021' ]

cat > "$scratch_dir/expected-70025-401.json" <<'JSON'
{
  "error": "invalid_client",
  "error_description": "AADSTS70025: The client has no configured federated identity credentials.",
  "error_codes": [70025]
}
JSON
actual="$("$script_dir/classify-entra-federation-response.sh" \
  401 "$scratch_dir/expected-70025-401.json")"
[ "$actual" = $'retry\tinvalid_client\t70025' ]

cat > "$scratch_dir/unexpected-401.json" <<'JSON'
{
  "error": "invalid_client",
  "error_description": "AADSTS700016: Application was not found.",
  "error_codes": [700016]
}
JSON
actual="$("$script_dir/classify-entra-federation-response.sh" \
  401 "$scratch_dir/unexpected-401.json")"
[ "$actual" = $'fail\tinvalid_client\t700016' ]

cat > "$scratch_dir/expected-400.json" <<'JSON'
{
  "error": "invalid_request",
  "error_description": "AADSTS700212: No matching federated identity record found."
}
JSON
actual="$("$script_dir/classify-entra-federation-response.sh" \
  400 "$scratch_dir/expected-400.json")"
[ "$actual" = $'retry\tinvalid_request\t700212' ]

echo "Entra federation response classifier tests passed."
