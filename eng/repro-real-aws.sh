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
#   - "setup-iam"    one-time: create a personal, aws2azure-it-*-scoped IAM
#                    user carrying the exact least-privilege policy already
#                    documented in docs/testing/real-aws-capture.md
#                    (eng/aws-least-privilege-policy.json), never a new one.
#   - "up"           mint short-lived session credentials from that IAM
#                    user's long-lived access key (or from an operator-
#                    supplied --role-arn) and write them to a sourceable env
#                    file. RealAwsConformanceCaptureFixture requires
#                    AWS_SESSION_TOKEN, not just AWS_ACCESS_KEY_ID/
#                    AWS_SECRET_ACCESS_KEY, so a plain long-lived IAM user
#                    key will NOT satisfy it directly — CI's OIDC
#                    AssumeRoleWithWebIdentity always yields a session token,
#                    and this script mirrors that shape locally via
#                    `aws sts get-session-token` / `aws sts assume-role`.
#   - "down"         safety-net cleanup: invoke the existing
#                    .github/scripts/cleanup-real-aws-resources.sh directly
#                    (never reimplemented here) to reap anything left behind
#                    by an interrupted/cancelled local run.
#   - "teardown-iam" optional: delete the access key(s), inline policies, and
#                    IAM user created by "setup-iam".

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
policy_file="$repo_root/eng/aws-least-privilege-policy.json"
cleanup_script="$repo_root/.github/scripts/cleanup-real-aws-resources.sh"

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

Options for "setup-iam" / "teardown-iam":
  --user-name NAME        IAM user name (default: aws2azure-local-repro).
  --env-file PATH         Where "setup-iam" writes the long-lived access key
                           (default: <repo-root>/.local/real-aws-iam-user.env).
  --force                 "setup-iam": rotate the access key even if one
                           already exists (deletes the oldest key first, since
                           AWS allows at most two per user).
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
                           <repo-root>/.local/real-aws.env).
  --yes                    Skip the interactive cost/safety confirmation.

Options for "down":
  --region REGION          AWS_REGION passed to the cleanup script (default:
                           us-east-1).
  --max-age-hours N        MAX_AGE_HOURS passed to the cleanup script
                           (default: 6, matching real-aws-reaper.yml; pass 0
                           to reap everything under the aws2azure-it-* prefix
                           immediately, e.g. right after a cancelled run).

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

aws_text() {
  strip_crlf "$(aws "$@" --output text)"
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

action="${1:-}"
[ -n "$action" ] || usage
shift || true

user_name="aws2azure-local-repro"
setup_env_file="$repo_root/.local/real-aws-iam-user.env"
force=false
skip_confirm=false

user_env_file="$repo_root/.local/real-aws-iam-user.env"
role_arn=""
session_name="aws2azure-local-repro"
duration_seconds="3600"
region="us-east-1"
up_env_file="$repo_root/.local/real-aws.env"

down_region="us-east-1"
max_age_hours="6"

while (($# > 0)); do
  option="$1"
  shift
  case "$option" in
    --user-name) user_name="${1:-}"; shift ;;
    --env-file)
      # Shared flag name across subcommands; routed below by $action.
      setup_env_file="${1:-}"
      up_env_file="${1:-}"
      shift
      ;;
    --force) force=true ;;
    --user-env-file) user_env_file="${1:-}"; shift ;;
    --role-arn) role_arn="${1:-}"; shift ;;
    --session-name) session_name="${1:-}"; shift ;;
    --duration-seconds) duration_seconds="${1:-}"; shift ;;
    --region) region="${1:-}"; down_region="${1:-}"; shift ;;
    --max-age-hours) max_age_hours="${1:-}"; shift ;;
    --yes) skip_confirm=true ;;
    -h|--help) usage ;;
    *) fail "unknown option: $option" ;;
  esac
done

require_command aws
require_command jq

case "$action" in
  setup-iam)
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
    confirm_cost_warning

    if [ -n "$role_arn" ]; then
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
    } > "$up_env_file"
    chmod 0600 "$up_env_file"

    echo "repro-real-aws: session credentials written to: $up_env_file" >&2
    echo "repro-real-aws: run: source $up_env_file && dotnet build -c Release --nologo && dotnet test tests/Aws2Azure.IntegrationTests/Aws2Azure.IntegrationTests.csproj -c Release --no-build --filter \"Category=RealAws\"" >&2
    echo "repro-real-aws: after the run (especially if interrupted), reap orphans with: eng/repro-real-aws.sh down" >&2
    ;;

  down)
    [ -f "$cleanup_script" ] || fail "cleanup script not found: $cleanup_script"
    echo "repro-real-aws: reaping orphaned aws2azure-it-* resources (this reuses the CI/reaper cleanup script)" >&2
    AWS_REGION="$down_region" MAX_AGE_HOURS="$max_age_hours" bash "$cleanup_script"
    ;;

  *)
    usage
    ;;
esac
