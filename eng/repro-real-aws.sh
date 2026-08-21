#!/usr/bin/env bash
set -euo pipefail

# Local reproduction of the `capture-real-aws` weekly/on-demand golden-capture
# job (issue #842, companion to eng/repro-real-azure.sh from #839). See
# docs/testing/local-real-aws-repro.md for the full walkthrough.
#
# Unlike the real-Azure side, the real-AWS capture flow is SELF-PROVISIONING:
# RealAwsConformanceCaptureTests / RealAwsConformanceCaptureFixture create
# (and, on their own success path, delete) every ephemeral aws2azure-it-*
# S3 bucket / DynamoDB table / Kinesis stream / SNS topic / SQS queue they
# exercise, exactly as capture-real-aws.yml's own
# `dotnet test --filter "Category=RealAws"` step does. This script therefore
# has NO Bicep/ARM-equivalent "provision infrastructure" step; it only:
#   - "setup-iam"          one-time: create a personal, aws2azure-it-*-scoped
#                           IAM user carrying the exact least-privilege
#                           policy already documented in
#                           docs/testing/real-aws-capture.md
#                           (eng/aws-least-privilege-policy.json), never a
#                           new one.
#   - "up"                 mint short-lived session credentials from that IAM
#                           user's long-lived access key (or from an
#                           operator-supplied --role-arn) and write them to a
#                           sourceable env file. RealAwsConformanceCaptureFixture
#                           requires AWS_SESSION_TOKEN, not just
#                           AWS_ACCESS_KEY_ID/AWS_SECRET_ACCESS_KEY, so a
#                           plain long-lived IAM user key will NOT satisfy it
#                           directly — CI's OIDC AssumeRoleWithWebIdentity
#                           always yields a session token, and this script
#                           mirrors that shape locally via
#                           `aws sts get-session-token` / `aws sts assume-role`.
#   - "down"                SAFE, SESSION-SCOPED teardown (default): reaps
#                           only aws2azure-it-* resources whose name embeds a
#                           creation epoch at/after this local session's own
#                           start (recorded by "up" as
#                           AWS2AZURE_REPRO_SESSION_START). It never scans the
#                           whole account by age. See "Incident" note below.
#   - "sweep-all-orphans"   ACCOUNT-WIDE, AGE-BASED reap — the same blast
#                           radius as real-aws-reaper.yml / the old
#                           unscoped "down". Can affect resources from ANY
#                           run in the account, including other engineers' or
#                           CI's in-flight runs. Requires a typed
#                           confirmation phrase in addition to the normal
#                           cost/safety warning. Prefer "down".
#   - "teardown-iam"        optional: delete the access key(s), inline
#                           policies, and IAM user created by "setup-iam".
#
# --- Incident note (why the identity guard and session-scoped "down" exist) ---
# During development of this script (issue #842), an unscoped, account-wide
# `down`-equivalent invocation was accidentally run in a shared sandbox whose
# ambient `aws` CLI turned out to be authenticated as the AWS ACCOUNT ROOT
# USER of a real, dedicated test account (not a mistake in identifying
# credentials as fake — the ambient environment genuinely had live root
# credentials configured, which the operator later confirmed is the
# project's own dedicated real-AWS test account, not a leaked third-party
# credential). The account-wide, age-based reap began deleting an
# aws2azure-it-* S3 bucket before the operator caught it and killed the
# process. Root cause: nothing in this script refused to run destructive
# operations under root credentials, and the only teardown mode scanned the
# entire account by age rather than scoping to the invoking session. Both
# gaps are fixed below: `require_non_root_identity` refuses to proceed under
# root (or an unrecognized identity, without blocking) before any AWS calls
# in "up"/"down"/"teardown-iam", and the default "down" is session-scoped
# rather than account-wide. See docs/testing/local-real-aws-repro.md for the
# full incident writeup.

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
policy_file="$repo_root/eng/aws-least-privilege-policy.json"
cleanup_script="$repo_root/.github/scripts/cleanup-real-aws-resources.sh"
session_scoped_name_regex='^aws2azure-it-([0-9]{10,})-'

fail() {
  echo "repro-real-aws: $*" >&2
  exit 1
}

usage() {
  cat >&2 <<'EOF'
usage:
  eng/repro-real-aws.sh setup-iam [options]
  eng/repro-real-aws.sh teardown-iam [options]
  eng/repro-real-aws.sh up [options]
  eng/repro-real-aws.sh down [options]
  eng/repro-real-aws.sh sweep-all-orphans [options]

Options for "setup-iam" / "teardown-iam":
  --user-name NAME        IAM user name (default: aws2azure-local-repro).
  --env-file PATH         Where "setup-iam" writes the long-lived access key
                           (default: <repo-root>/.local/real-aws-iam-user.env).
  --force                 "setup-iam": rotate the access key even if one
                           already exists (deletes the oldest key first, since
                           AWS allows at most two per user).
  --allow-root            Skip the AWS-identity safety check entirely (see
                           "Identity safety check" below). Rarely needed for
                           these two subcommands, since they run once under
                           whatever admin identity manages the account and
                           only warn (never block) on root by default.
  --yes                   Skip the interactive confirmation.

Options for "up":
  --user-env-file PATH    Long-lived IAM user credentials to read (default:
                           <repo-root>/.local/real-aws-iam-user.env, as
                           written by "setup-iam"). Ignored if --role-arn is
                           given or AWS_ACCESS_KEY_ID/AWS_SECRET_ACCESS_KEY
                           are already exported in the environment.
  --role-arn ARN           Use `aws sts assume-role` against this role instead
                           of `aws sts get-session-token` against an IAM
                           user's long-lived key (for operators who already
                           have a suitable local principal, per issue #842).
  --session-name NAME      Session/role-session name (default:
                           aws2azure-local-repro).
  --duration-seconds N     Session token lifetime (default: 3600; AWS caps
                           get-session-token at 129600s / 36h and assume-role
                           at the target role's own MaxSessionDuration).
  --region REGION          AWS region for the `aws` CLI itself (default:
                           us-east-1, matching capture-real-aws.yml;
                           RealAwsConformanceCaptureFixture always targets
                           us-east-1 regardless of this value).
  --env-file PATH          Where to write the sourceable session-credential
                           env file (default:
                           <repo-root>/.local/real-aws.env). Also records the
                           session start epoch (AWS2AZURE_REPRO_SESSION_START)
                           that "down" uses to scope its teardown to this run.
  --allow-root             Skip the AWS-identity safety check (see below).
                           Only pass this if you deliberately intend to mint
                           session credentials while the account root user
                           is the active ambient identity.
  --yes                    Skip the interactive cost/safety confirmation.

Options for "down" (SAFE, session-scoped — the default teardown):
  --since VALUE            Unix epoch seconds or an ISO-8601 timestamp: reap
                           only aws2azure-it-<epoch>-... resources whose
                           embedded epoch is at/after this value. Default:
                           read AWS2AZURE_REPRO_SESSION_START from --env-file
                           (i.e. the timestamp "up" recorded for this run).
  --env-file PATH          Env file to read AWS2AZURE_REPRO_SESSION_START
                           from if --since is not given (default:
                           <repo-root>/.local/real-aws.env, the same file
                           "up" writes).
  --region REGION          AWS region to operate in (default: us-east-1).
  --allow-root             Skip the AWS-identity safety check.
  --yes                    Skip the interactive confirmation before deleting
                           the matched resources.

Options for "sweep-all-orphans" (ACCOUNT-WIDE, age-based — NOT session-scoped;
this is what the old unscoped "down" did, and is exactly the shared
real-aws-reaper.yml's blast radius applied on demand):
  --region REGION          AWS_REGION passed to the cleanup script (default:
                           us-east-1).
  --max-age-hours N        MAX_AGE_HOURS passed to the cleanup script
                           (default: 6, matching real-aws-reaper.yml).
  --allow-root             Skip the AWS-identity safety check.
  --yes                    Skip the interactive cost/safety confirmation.
  --force-sweep-all        Skip the additional typed confirmation phrase
                           this subcommand requires beyond --yes, because it
                           can affect resources from OTHER runs/operators.

Identity safety check ("up"/"down"/"sweep-all-orphans", strict; "setup-iam"/
"teardown-iam", advisory-only): every AWS-touching subcommand calls
`aws sts get-caller-identity` first. If the active identity is the account
ROOT USER, "up"/"down"/"sweep-all-orphans" refuse to continue (pass
--allow-root to override); "setup-iam"/"teardown-iam" only print a warning,
since they legitimately need one-time elevated iam:* permissions. Every
subcommand also warns (never blocks) if the identity doesn't look like the
expected aws2azure-local-repro-scoped principal.

This script requires the `aws` CLI and `jq`. It never provisions AWS
resources for the capture matrix itself — the capture tests own that; see
docs/testing/local-real-aws-repro.md.
EOF
  exit 2
}

require_command() {
  command -v "$1" >/dev/null 2>&1 || fail "required command not found: $1"
}

# Defends against any AWS CLI build that emits CRLF (mirrors the same
# defensive strip_crlf helper in eng/repro-real-azure.sh / the CI cleanup
# scripts). A no-op against a native Linux `aws` CLI, which is the common
# case here — see docs/testing/local-real-aws-repro.md for why this has not
# been observed to matter on the AWS side in practice.
strip_crlf() {
  local value="$1"
  value="${value//$'\r'/}"
  printf '%s' "$value"
}

confirm() {
  local prompt="$1"
  if [ "$skip_confirm" = true ]; then
    return 0
  fi
  read -r -p "$prompt [y/N] " reply
  case "$reply" in
    y|Y|yes|YES) ;;
    *) fail "aborted by operator" ;;
  esac
}

# Refuses (mode=strict) or warns (mode=advisory) if the active AWS identity
# is the account root user, and always warns (never blocks) if the identity
# doesn't look like the expected aws2azure-local-repro-scoped principal. See
# the "Incident note" above this script's header for why this check exists:
# it is a direct response to a real near-incident where an unscoped teardown
# ran under ambient root credentials.
require_non_root_identity() {
  local mode="$1"

  if [ "$allow_root" = true ]; then
    echo "repro-real-aws: --allow-root given; skipping the AWS identity safety check." >&2
    return 0
  fi

  local identity_json arn
  if ! identity_json="$(aws sts get-caller-identity --output json 2>&1)"; then
    fail "could not call 'aws sts get-caller-identity' to verify the active AWS identity before continuing (this check exists precisely to prevent a repeat of the root-credential incident documented at the top of this script): $identity_json"
  fi
  arn="$(jq -r '.Arn' <<< "$identity_json")"
  echo "repro-real-aws: active AWS identity: $arn" >&2

  if [[ "$arn" == *":root" ]]; then
    if [ "$mode" = strict ]; then
      fail "refusing to continue: the active AWS identity is the ACCOUNT ROOT USER ($arn). Running destructive/session-minting operations as root is exactly the root cause of a real near-incident this guard exists to prevent (see the top of this script and docs/testing/local-real-aws-repro.md). Run 'eng/repro-real-aws.sh setup-iam' once, then 'eng/repro-real-aws.sh up' (which reads that IAM user's credentials automatically), or export the least-privilege IAM user's own AWS_ACCESS_KEY_ID/AWS_SECRET_ACCESS_KEY before retrying. Pass --allow-root only if you deliberately intend to proceed as the account root user."
    fi
    echo "::warning::repro-real-aws: active AWS identity is the ACCOUNT ROOT USER ($arn). Proceeding because this subcommand legitimately needs elevated iam:* permissions, but avoid leaving root credentials exported afterward — 'up' mints scoped session credentials for the actual capture run." >&2
  elif [[ "$arn" != *"aws2azure"* ]]; then
    echo "::warning::repro-real-aws: active AWS identity ($arn) does not look like the expected aws2azure-local-repro-scoped principal. Double-check this is the identity you intend to use before this subcommand touches AWS resources." >&2
  fi
}

confirm_cost_warning() {
  cat >&2 <<'EOF'

*******************************************************************
* WARNING: this runs the Tier-3 capture test matrix against REAL, *
* BILLED AWS resources (S3 buckets, DynamoDB tables, a Kinesis     *
* stream, SNS topics, SQS queues) named aws2azure-it-*.            *
* S3/DynamoDB/SNS/SQS have permanent always-free tiers that easily *
* cover this; Kinesis has no always-free tier but a short capture  *
* run is expected to cost fractions of a cent (see "Cost and       *
* safety model" in docs/testing/real-aws-capture.md).              *
* The tests create AND delete their own resources on the happy     *
* path — but if a run is interrupted/cancelled, always follow up   *
* with "eng/repro-real-aws.sh down" (or wait for the shared         *
* real-aws-reaper.yml) to avoid a leaked resource.                 *
*******************************************************************

EOF
  confirm "Continue and run the real-AWS capture?"
}

# Parses either a bare unix-epoch-seconds value or an ISO-8601 timestamp into
# unix-epoch-seconds, matching the aws2azure-it-<epoch>-<suffix> name
# contract documented in docs/testing/real-aws-capture.md.
parse_since_value() {
  local value="$1"
  if [[ "$value" =~ ^[0-9]{10,}$ ]]; then
    printf '%s' "$value"
    return 0
  fi
  date -u -d "$value" +%s 2>/dev/null || fail "could not parse --since value as a unix epoch or ISO-8601 timestamp: $value"
}

# Read-only discovery of aws2azure-it-<epoch>-... resources whose embedded
# epoch is at/after $1, across every resource type the capture tests and
# cleanup-real-aws-resources.sh know about. Emits "<kind> <name>" lines.
# This does NOT delete anything — see reap_session_scoped for that.
list_session_scoped_resources() {
  local since_epoch="$1"
  local name epoch arn

  while IFS= read -r name; do
    [ -z "$name" ] && continue
    if [[ "$name" =~ $session_scoped_name_regex ]]; then
      epoch="${BASH_REMATCH[1]}"
      [ "$epoch" -ge "$since_epoch" ] && printf 's3 %s\n' "$name"
    fi
  done < <(aws s3api list-buckets --query 'Buckets[].Name' --output text 2>/dev/null | tr '\t' '\n')

  while IFS= read -r name; do
    [ -z "$name" ] && continue
    if [[ "$name" =~ $session_scoped_name_regex ]]; then
      epoch="${BASH_REMATCH[1]}"
      [ "$epoch" -ge "$since_epoch" ] && printf 'dynamodb %s\n' "$name"
    fi
  done < <(aws dynamodb list-tables --query 'TableNames' --output text 2>/dev/null | tr '\t' '\n')

  while IFS= read -r name; do
    [ -z "$name" ] && continue
    if [[ "$name" =~ $session_scoped_name_regex ]]; then
      epoch="${BASH_REMATCH[1]}"
      [ "$epoch" -ge "$since_epoch" ] && printf 'kinesis %s\n' "$name"
    fi
  done < <(aws kinesis list-streams --query 'StreamNames' --output text 2>/dev/null | tr '\t' '\n')

  while IFS= read -r arn; do
    [ -z "$arn" ] && continue
    name="${arn##*:}"
    if [[ "$name" =~ $session_scoped_name_regex ]]; then
      epoch="${BASH_REMATCH[1]}"
      [ "$epoch" -ge "$since_epoch" ] && printf 'sns %s\n' "$name"
    fi
  done < <(aws sns list-topics --query 'Topics[].TopicArn' --output text 2>/dev/null | tr '\t' '\n')

  while IFS= read -r name; do
    [ -z "$name" ] && continue
    if [[ "$name" =~ $session_scoped_name_regex ]]; then
      epoch="${BASH_REMATCH[1]}"
      [ "$epoch" -ge "$since_epoch" ] && printf 'sqs %s\n' "$name"
    fi
  done < <(aws sqs list-queues --queue-name-prefix "aws2azure-it-" --query 'QueueUrls' --output text 2>/dev/null | tr '\t' '\n' | sed -E 's#.*/##')
}

# Deletes each session-scoped resource by delegating to
# cleanup-real-aws-resources.sh, never reimplementing its (nontrivial, e.g.
# S3 object-version/multipart-upload handling) deletion logic. It is invoked
# once per matched resource name with NAME_PREFIX pinned to that resource's
# exact name (a valid, self-matching "prefix") and MAX_AGE_HOURS=0, so the
# shared script's own age-based decision always reaps that one resource and
# nothing else. This costs one extra full account listing per resource
# (acceptable for local, non-performance-sensitive tooling) in exchange for
# never duplicating the shared script's deletion logic.
reap_session_scoped() {
  local since_epoch="$1"
  local region="$2"
  local found kind name overall_failed=0

  found="$(list_session_scoped_resources "$since_epoch")"
  if [ -z "$found" ]; then
    echo "repro-real-aws: no aws2azure-it-* resources found with an embedded creation epoch >= $since_epoch — nothing to reap." >&2
    return 0
  fi

  echo "repro-real-aws: resources scoped to this session (created at/after epoch $since_epoch):" >&2
  # shellcheck disable=SC2001
  echo "$found" | sed 's/^/  /' >&2
  echo "repro-real-aws: NOTE: since this matches by timestamp (not a unique per-run id), a concurrent local/CI run that started after yours could in principle also match — review the list above before confirming." >&2
  confirm "Delete the resources listed above?"

  while IFS=' ' read -r kind name; do
    [ -z "$name" ] && continue
    echo "repro-real-aws: reaping $kind resource $name (delegating to cleanup-real-aws-resources.sh, scoped to this one resource name)" >&2
    if ! NAME_PREFIX="$name" MAX_AGE_HOURS=0 AWS_REGION="$region" bash "$cleanup_script"; then
      echo "::error::repro-real-aws: cleanup-real-aws-resources.sh reported a failure while reaping $name" >&2
      overall_failed=1
    fi
  done <<< "$found"

  return "$overall_failed"
}

confirm_sweep_all_orphans() {
  cat >&2 <<'EOF'

*******************************************************************
* WARNING: ACCOUNT-WIDE, AGE-BASED REAP (not session-scoped).     *
* This has the same blast radius as the shared real-aws-reaper.yml*
* workflow: it can delete aws2azure-it-* resources belonging to    *
* ANY run in this account — including another engineer's or CI's  *
* currently in-progress or recently-finished run — not just        *
* resources this local session created.                            *
* Prefer "eng/repro-real-aws.sh down" (session-scoped) unless you  *
* specifically intend an account-wide sweep.                       *
*******************************************************************

EOF
  if [ "$force_sweep_all" != true ]; then
    read -r -p "Type EXACTLY 'REAP-ALL-ORPHANS' to confirm an account-wide sweep: " reply
    [ "$reply" = "REAP-ALL-ORPHANS" ] || fail "aborted: confirmation phrase did not match."
  fi
}

action="${1:-}"
[ -n "$action" ] || usage
shift || true

user_name="aws2azure-local-repro"
setup_env_file="$repo_root/.local/real-aws-iam-user.env"
force=false
skip_confirm=false
allow_root=false

user_env_file="$repo_root/.local/real-aws-iam-user.env"
role_arn=""
session_name="aws2azure-local-repro"
duration_seconds="3600"
region="us-east-1"
up_env_file="$repo_root/.local/real-aws.env"

since_value=""
down_env_file="$repo_root/.local/real-aws.env"

down_region="us-east-1"
max_age_hours="6"
force_sweep_all=false

while (($# > 0)); do
  option="$1"
  shift
  case "$option" in
    --user-name) user_name="${1:-}"; shift ;;
    --env-file)
      # Shared flag name across subcommands; routed below by $action.
      setup_env_file="${1:-}"
      up_env_file="${1:-}"
      down_env_file="${1:-}"
      shift
      ;;
    --force) force=true ;;
    --user-env-file) user_env_file="${1:-}"; shift ;;
    --role-arn) role_arn="${1:-}"; shift ;;
    --session-name) session_name="${1:-}"; shift ;;
    --duration-seconds) duration_seconds="${1:-}"; shift ;;
    --region) region="${1:-}"; down_region="${1:-}"; shift ;;
    --since) since_value="${1:-}"; shift ;;
    --max-age-hours) max_age_hours="${1:-}"; shift ;;
    --allow-root) allow_root=true ;;
    --force-sweep-all) force_sweep_all=true ;;
    --yes) skip_confirm=true ;;
    -h|--help) usage ;;
    *) fail "unknown option: $option" ;;
  esac
done

require_command aws
require_command jq

case "$action" in
  setup-iam)
    require_non_root_identity advisory
    [ -f "$policy_file" ] || fail "policy file not found: $policy_file"

    cat >&2 <<EOF

This creates IAM user "$user_name" with:
  - the exact least-privilege resource-access policy already documented in
    docs/testing/real-aws-capture.md (read from
    eng/aws-least-privilege-policy.json — never a new/looser policy), scoped
    to the aws2azure-it-* naming contract.
  - a minimal bootstrap policy granting only sts:GetSessionToken on this
    user's own identity, so "eng/repro-real-aws.sh up" can mint short-lived
    session credentials (RealAwsConformanceCaptureFixture requires
    AWS_SESSION_TOKEN; a plain long-lived key will not work).
  - one long-lived access key, written to: $setup_env_file (chmod 0600,
    git-ignored). Treat that file as a live credential: rotate/delete it
    (see "teardown-iam") when you are done with local real-AWS repro.

Requires your CURRENT AWS CLI identity to have iam:CreateUser,
iam:PutUserPolicy, and iam:CreateAccessKey permissions (typically an
administrator identity in the dedicated real-AWS test account — NOT the
IAM user this command is about to create).
EOF
    confirm "Create IAM user $user_name and a long-lived access key?"

    if aws iam get-user --user-name "$user_name" >/dev/null 2>&1; then
      echo "repro-real-aws: IAM user $user_name already exists" >&2
    else
      echo "repro-real-aws: creating IAM user $user_name" >&2
      aws iam create-user --user-name "$user_name" \
        --tags Key=purpose,Value=aws2azure-local-repro \
        --output none
    fi

    echo "repro-real-aws: attaching least-privilege policy from eng/aws-least-privilege-policy.json" >&2
    aws iam put-user-policy \
      --user-name "$user_name" \
      --policy-name aws2azure-conformance-least-privilege \
      --policy-document "file://$policy_file"

    echo "repro-real-aws: attaching sts:GetSessionToken bootstrap policy" >&2
    aws iam put-user-policy \
      --user-name "$user_name" \
      --policy-name aws2azure-local-repro-sts-bootstrap \
      --policy-document '{"Version":"2012-10-17","Statement":[{"Sid":"SelfGetSessionToken","Effect":"Allow","Action":"sts:GetSessionToken","Resource":"*"}]}'

    existing_keys="$(aws iam list-access-keys --user-name "$user_name" \
      --query 'AccessKeyMetadata[].AccessKeyId' --output text)"
    key_count=0
    if [ -n "$existing_keys" ]; then
      key_count="$(wc -w <<< "$existing_keys" | tr -d '[:space:]')"
    fi

    if [ "$key_count" -ge 1 ] && [ "$force" != true ]; then
      fail "IAM user $user_name already has an access key. Re-run with --force to rotate it (deletes the oldest key first), or reuse the existing credentials in $setup_env_file."
    fi

    if [ "$key_count" -ge 2 ]; then
      oldest_key="$(aws iam list-access-keys --user-name "$user_name" \
        --query 'sort_by(AccessKeyMetadata, &CreateDate)[0].AccessKeyId' --output text)"
      echo "repro-real-aws: deleting oldest access key $oldest_key (AWS allows at most 2 per user)" >&2
      aws iam delete-access-key --user-name "$user_name" --access-key-id "$oldest_key"
    elif [ "$key_count" -eq 1 ] && [ "$force" = true ]; then
      old_key="$(aws iam list-access-keys --user-name "$user_name" \
        --query 'AccessKeyMetadata[0].AccessKeyId' --output text)"
      echo "repro-real-aws: --force given, deleting existing access key $old_key before rotating" >&2
      aws iam delete-access-key --user-name "$user_name" --access-key-id "$old_key"
    fi

    echo "repro-real-aws: creating access key for $user_name" >&2
    key_json="$(aws iam create-access-key --user-name "$user_name" --output json)"
    access_key_id="$(jq -r '.AccessKey.AccessKeyId' <<< "$key_json")"
    secret_access_key="$(jq -r '.AccessKey.SecretAccessKey' <<< "$key_json")"

    mkdir -p "$(dirname "$setup_env_file")"
    umask 077
    {
      echo "# Generated by eng/repro-real-aws.sh setup-iam on $(date -u +%Y-%m-%dT%H:%M:%SZ)."
      echo "# LONG-LIVED credentials for IAM user $user_name — do not commit,"
      echo "# do not use directly for tests. \"eng/repro-real-aws.sh up\" reads"
      echo "# this file to mint short-lived session credentials."
      printf 'export AWS_IAM_USER_ACCESS_KEY_ID=%q\n' "$access_key_id"
      printf 'export AWS_IAM_USER_SECRET_ACCESS_KEY=%q\n' "$secret_access_key"
    } > "$setup_env_file"
    chmod 0600 "$setup_env_file"

    echo "repro-real-aws: IAM user: $user_name" >&2
    echo "repro-real-aws: long-lived credentials written to: $setup_env_file" >&2
    echo "repro-real-aws: next: eng/repro-real-aws.sh up" >&2
    ;;

  teardown-iam)
    require_non_root_identity advisory

    cat >&2 <<EOF

This deletes every access key, both inline policies
(aws2azure-conformance-least-privilege,
aws2azure-local-repro-sts-bootstrap), and the IAM user "$user_name" itself.
EOF
    confirm "Delete IAM user $user_name and all its access keys?"

    if ! aws iam get-user --user-name "$user_name" >/dev/null 2>&1; then
      echo "repro-real-aws: IAM user $user_name does not exist; nothing to do." >&2
      exit 0
    fi

    while IFS= read -r key_id; do
      [ -z "$key_id" ] && continue
      echo "repro-real-aws: deleting access key $key_id" >&2
      aws iam delete-access-key --user-name "$user_name" --access-key-id "$key_id"
    done < <(aws iam list-access-keys --user-name "$user_name" \
      --query 'AccessKeyMetadata[].AccessKeyId' --output text | tr '\t' '\n')

    for policy_name in aws2azure-conformance-least-privilege aws2azure-local-repro-sts-bootstrap; do
      if aws iam get-user-policy --user-name "$user_name" --policy-name "$policy_name" >/dev/null 2>&1; then
        echo "repro-real-aws: deleting inline policy $policy_name" >&2
        aws iam delete-user-policy --user-name "$user_name" --policy-name "$policy_name"
      fi
    done

    echo "repro-real-aws: deleting IAM user $user_name" >&2
    aws iam delete-user --user-name "$user_name"

    if [ -f "$setup_env_file" ]; then
      rm -f "$setup_env_file"
      echo "repro-real-aws: removed $setup_env_file" >&2
    fi
    ;;

  up)
    session_start_epoch="$(date -u +%s)"
    confirm_cost_warning

    if [ -n "$role_arn" ]; then
      require_non_root_identity strict
      echo "repro-real-aws: assuming role $role_arn" >&2
      creds_json="$(aws sts assume-role \
        --role-arn "$role_arn" \
        --role-session-name "$session_name" \
        --duration-seconds "$duration_seconds" \
        --output json)"
      access_key_id="$(jq -r '.Credentials.AccessKeyId' <<< "$creds_json")"
      secret_access_key="$(jq -r '.Credentials.SecretAccessKey' <<< "$creds_json")"
      session_token="$(jq -r '.Credentials.SessionToken' <<< "$creds_json")"
    else
      if [ -z "${AWS_ACCESS_KEY_ID:-}" ] || [ -z "${AWS_SECRET_ACCESS_KEY:-}" ]; then
        if [ -f "$user_env_file" ]; then
          echo "repro-real-aws: sourcing long-lived IAM user credentials from $user_env_file" >&2
          # shellcheck disable=SC1090
          source "$user_env_file"
          export AWS_ACCESS_KEY_ID="${AWS_IAM_USER_ACCESS_KEY_ID:-}"
          export AWS_SECRET_ACCESS_KEY="${AWS_IAM_USER_SECRET_ACCESS_KEY:-}"
        fi
      fi
      [ -n "${AWS_ACCESS_KEY_ID:-}" ] && [ -n "${AWS_SECRET_ACCESS_KEY:-}" ] \
        || fail "no long-lived AWS credentials found. Run 'eng/repro-real-aws.sh setup-iam' first, export AWS_ACCESS_KEY_ID/AWS_SECRET_ACCESS_KEY yourself, or pass --role-arn."

      require_non_root_identity strict

      echo "repro-real-aws: minting a session token via sts get-session-token" >&2
      creds_json="$(aws sts get-session-token --duration-seconds "$duration_seconds" --output json)"
      access_key_id="$(jq -r '.Credentials.AccessKeyId' <<< "$creds_json")"
      secret_access_key="$(jq -r '.Credentials.SecretAccessKey' <<< "$creds_json")"
      session_token="$(jq -r '.Credentials.SessionToken' <<< "$creds_json")"
    fi

    mkdir -p "$(dirname "$up_env_file")"
    umask 077
    {
      echo "# Generated by eng/repro-real-aws.sh up on $(date -u +%Y-%m-%dT%H:%M:%SZ)."
      echo "# SHORT-LIVED session credentials (expire after ${duration_seconds}s)."
      echo "# Source this file, then run the real-AWS capture, e.g.:"
      echo "#   source $up_env_file"
      echo "#   dotnet build -c Release --nologo"
      echo "#   dotnet test tests/Aws2Azure.IntegrationTests/Aws2Azure.IntegrationTests.csproj \\"
      echo "#     -c Release --no-build --filter \"Category=RealAws\""
      echo "#"
      echo "# Values are quoted with printf '%q' for the same reason as"
      echo "# eng/repro-real-azure.sh's env file: unquoted values containing"
      echo "# shell metacharacters would be mishandled by 'source'."
      printf 'export AWS_ACCESS_KEY_ID=%q\n' "$access_key_id"
      printf 'export AWS_SECRET_ACCESS_KEY=%q\n' "$secret_access_key"
      printf 'export AWS_SESSION_TOKEN=%q\n' "$session_token"
      printf 'export AWS_REGION=%q\n' "$region"
      printf 'export AWS_DEFAULT_REGION=%q\n' "$region"
      echo "#"
      echo "# Recorded so 'eng/repro-real-aws.sh down' can scope its teardown to"
      echo "# only the aws2azure-it-* resources this session may have created,"
      echo "# instead of scanning the whole account by age (see the incident"
      echo "# note at the top of eng/repro-real-aws.sh)."
      printf 'export AWS2AZURE_REPRO_SESSION_START=%q\n' "$session_start_epoch"
    } > "$up_env_file"
    chmod 0600 "$up_env_file"

    echo "repro-real-aws: session credentials written to: $up_env_file" >&2
    echo "repro-real-aws: run: source $up_env_file && dotnet build -c Release --nologo && dotnet test tests/Aws2Azure.IntegrationTests/Aws2Azure.IntegrationTests.csproj -c Release --no-build --filter \"Category=RealAws\"" >&2
    echo "repro-real-aws: after the run (especially if interrupted), reap this session's leftovers with: eng/repro-real-aws.sh down" >&2
    ;;

  down)
    require_non_root_identity strict
    [ -f "$cleanup_script" ] || fail "cleanup script not found: $cleanup_script"

    if [ -z "$since_value" ]; then
      if [ -f "$down_env_file" ]; then
        # shellcheck disable=SC1090
        session_start_from_file="$(grep -oE '^export AWS2AZURE_REPRO_SESSION_START=.*' "$down_env_file" | tail -1 | cut -d= -f2-)"
        if [ -n "$session_start_from_file" ]; then
          # The env file quotes with printf '%q'; eval-unquote it safely by
          # sourcing just this one line's value through bash's own parser.
          since_value="$(eval "printf '%s' $session_start_from_file")"
        fi
      fi
      [ -n "$since_value" ] || fail "no --since given and AWS2AZURE_REPRO_SESSION_START not found in $down_env_file. Run 'eng/repro-real-aws.sh up' first (it records this automatically), or pass --since <epoch|ISO-8601> explicitly."
    fi

    since_epoch="$(parse_since_value "$since_value")"
    echo "repro-real-aws: session-scoped teardown — reaping aws2azure-it-* resources created at/after epoch $since_epoch ($(date -u -d "@$since_epoch" +%Y-%m-%dT%H:%M:%SZ 2>/dev/null || echo "unparseable-for-display"))" >&2
    reap_session_scoped "$since_epoch" "$down_region"
    ;;

  sweep-all-orphans)
    require_non_root_identity strict
    [ -f "$cleanup_script" ] || fail "cleanup script not found: $cleanup_script"
    confirm_sweep_all_orphans
    echo "repro-real-aws: reaping ALL orphaned aws2azure-it-* resources account-wide (this reuses the CI/reaper cleanup script directly, unscoped)" >&2
    AWS_REGION="$down_region" MAX_AGE_HOURS="$max_age_hours" bash "$cleanup_script"
    ;;

  *)
    usage
    ;;
esac
