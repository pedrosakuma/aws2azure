#!/usr/bin/env bash
set -euo pipefail

if [ "$#" -ne 2 ]; then
  echo "usage: $0 <http-status> <response-json>" >&2
  exit 2
fi

status="$1"
response_file="$2"
if [[ ! "$status" =~ ^[0-9]{3}$ ]] || [ ! -f "$response_file" ]; then
  printf 'fail\tinvalid_classifier_input\tnone\n'
  exit 0
fi

# A newly created FIC can briefly be invisible as either "no matching FIC"
# (70021/700212) or "no configured FIC" (70025). No other auth code is retryable.
if ! fields="$(jq -er '
  def safe_error:
    .error as $error
    | if ($error | type) == "string"
         and ($error | test("^[A-Za-z0-9._-]{1,64}$"))
      then $error
      else "unknown_error"
      end;
  safe_error as $error_name
  | [
    ((.error_codes // [])[] | tostring),
    (try ((.error_description // "")
      | capture("AADSTS(?<code>[0-9]+)").code) catch empty)
  ]
  | map(select(test("^[0-9]+$")))
  | unique as $codes
  | [
      $error_name,
      (if ($codes | index("70021")) != null then "70021"
       elif ($codes | index("700212")) != null then "700212"
       elif ($codes | index("70025")) != null then "70025"
       else ($codes[0] // "none")
       end),
      (if (($codes | index("70021")) != null
           or ($codes | index("700212")) != null
           or ($codes | index("70025")) != null)
       then "expected_fic_mismatch"
       else "other"
       end)
    ]
  | @tsv
' "$response_file")"; then
  printf 'fail\tmalformed_response\tnone\n'
  exit 0
fi

IFS=$'\t' read -r error_name aadsts_code code_class <<< "$fields"
if { [ "$status" = 400 ] || [ "$status" = 401 ]; } \
   && [ "$code_class" = expected_fic_mismatch ]; then
  disposition=retry
else
  disposition=fail
fi

printf '%s\t%s\t%s\n' "$disposition" "$error_name" "$aadsts_code"
