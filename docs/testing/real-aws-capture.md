# Real-AWS capture for Tier-3 differential

This document is the AWS-side companion to the existing
[nightly real-Azure guide](./real-azure-nightly.md). It records the one-time
operator setup already completed for the dedicated AWS account and documents the
intended Tier-3 capture/diff model tracked in
[#708](https://github.com/pedrosakuma/aws2azure/issues/708).

## Why this exists

`aws2azure` already treats Azure emulators as **necessary but not sufficient**:
that is why the repository has a live-Azure nightly in addition to Azurite /
Service Bus / Cosmos emulator coverage. The AWS side needs the same discipline.
Today the strongest AWS-side oracle in the checked-out tree is Tier 2
**LocalStack S3** differential coverage plus the general offline conformance
substrate documented in
[`tests/Aws2Azure.Conformance/README.md`](../../tests/Aws2Azure.Conformance/README.md).
LocalStack is useful, but it has known fidelity gaps and currently only covers
S3, so it cannot be the final word on whether the proxy still looks like **real
AWS** across the broader case matrix.

Issue [#708](https://github.com/pedrosakuma/aws2azure/issues/708) closes that
asymmetry with a **decoupled capture** design:

1. An infrequent `capture-real-aws.yml` workflow provisions ephemeral AWS
   resources, runs the conformance case matrix directly against **real AWS**,
   canonicalizes each response, and commits `real-aws`-provenance goldens.
2. The existing
   [`integration-real-azure`](../../.github/workflows/integration-real-azure.yml)
   workflow continues running the same matrix through the proxy against **real
   Azure** and emitting evidence artifacts.
3. A separate, credential-free diff job compares proxy-over-real-Azure evidence
   with the latest committed real-AWS goldens on every PR/nightly run, without
   touching either cloud again.

That split keeps the expensive/high-risk part (real AWS) on a low cadence while
letting the differential itself run cheaply and often.

### Current repository state at the time of writing

The checked-out `main`-based tree already contains the reusable conformance
primitives the eventual Tier-3 flow will build on:
[`CanonicalDiff`](../../tests/Aws2Azure.Conformance/Canonicalization/CanonicalDiff.cs),
[`GoldenStore`](../../tests/Aws2Azure.Conformance/Goldens/GoldenStore.cs), and
`GoldenProvenance.SourceRealAws` in the same store.

What is present vs. still pending in this checkout:

- **Exists today:** the live-Azure nightly and its operator guide
  ([`integration-real-azure.yml`](../../.github/workflows/integration-real-azure.yml),
  [this guide's Azure-side companion](./real-azure-nightly.md)). The workflow
  already uploads `real-azure-conformance` /
  `source-validation-real-azure-conformance` artifacts, so there is already a
  real-Azure evidence path in the tree.
- **Not present in this checkout:** `.github/workflows/capture-real-aws.yml`.
- **Not present in this checkout:** `.github/workflows/real-aws-reaper.yml`.
- **Not yet wired in this checkout:** a dedicated credential-free Tier-3 diff
  runner under `tests/Aws2Azure.Conformance/` that consumes committed real-AWS
  goldens plus proxy-over-real-Azure evidence.
- **Still uncertain from this checkout alone:** whether any in-flight #708 PR
  is reshaping the real-Azure evidence format specifically for the Tier-3 diff.
  Check issue [#708](https://github.com/pedrosakuma/aws2azure/issues/708) and
  its linked PRs before assuming that the current artifact layout is final.

Several #708 changes are landing independently; before relying on this as a
status board, check issue [#708](https://github.com/pedrosakuma/aws2azure/issues/708)
and its linked PRs for the current rollout state.

## One-time operator setup already completed

The current operator has already completed this setup in a **dedicated AWS
account**. Keep this section as the reproducible recipe if the account or role
must ever be recreated.

> **Budget prerequisite (manual, already done).** Before enabling any capture
> workflow, create a low-threshold AWS Budget alarm for the dedicated account
> and route it to a live alert channel owned by the operator. Do not enable
> real-AWS capture without that guardrail.

The CI path uses **GitHub OIDC + `AssumeRoleWithWebIdentity` only**. No root
credentials or long-lived AWS access keys are stored in GitHub.

```bash
ACCOUNT_ID=<aws-account-id>
REPO=pedrosakuma/aws2azure
ROLE_NAME=aws2azure-conformance-real-aws
POLICY_NAME=aws2azure-conformance-least-privilege
OIDC_PROVIDER_ARN="arn:aws:iam::$ACCOUNT_ID:oidc-provider/token.actions.githubusercontent.com"
AWS_ROLE_ARN="arn:aws:iam::$ACCOUNT_ID:role/$ROLE_NAME"

# 1. GitHub Actions OIDC identity provider.
#    Verify the current GitHub thumbprint before recreating the provider.
GITHUB_OIDC_THUMBPRINT=6938fd4d98bab03faadb97b34396831e3780aea1
aws iam create-open-id-connect-provider \
  --url https://token.actions.githubusercontent.com \
  --client-id-list sts.amazonaws.com \
  --thumbprint-list "$GITHUB_OIDC_THUMBPRINT"

# 2. Trust policy: only the repository's main branch may assume the role.
cat > aws2azure-real-aws-trust-policy.json <<JSON
{
  "Version": "2012-10-17",
  "Statement": [
    {
      "Effect": "Allow",
      "Principal": {
        "Federated": "$OIDC_PROVIDER_ARN"
      },
      "Action": "sts:AssumeRoleWithWebIdentity",
      "Condition": {
        "StringEquals": {
          "token.actions.githubusercontent.com:aud": "sts.amazonaws.com"
        },
        "StringLike": {
          "token.actions.githubusercontent.com:sub": "repo:$REPO:ref:refs/heads/main"
        }
      }
    }
  ]
}
JSON

aws iam create-role \
  --role-name "$ROLE_NAME" \
  --assume-role-policy-document file://aws2azure-real-aws-trust-policy.json

# 3. Least-privilege inline policy.
#    Resource-level scoping is used wherever the AWS service supports it; any
#    remaining create/list APIs stay tightly action-limited and rely on the
#    fixed aws2azure-it-* naming contract.
cat > aws2azure-real-aws-policy.json <<'JSON'
{
  "Version": "2012-10-17",
  "Statement": [
    {
      "Sid": "S3CreateAndDiscover",
      "Effect": "Allow",
      "Action": [
        "s3:CreateBucket",
        "s3:GetBucketLocation",
        "s3:ListAllMyBuckets"
      ],
      "Resource": "*"
    },
    {
      "Sid": "S3PrefixScopedData",
      "Effect": "Allow",
      "Action": [
        "s3:DeleteBucket",
        "s3:DeleteObject",
        "s3:DeleteObjects",
        "s3:GetObject",
        "s3:HeadBucket",
        "s3:ListBucket",
        "s3:ListBucketVersions",
        "s3:PutObject"
      ],
      "Resource": [
        "arn:aws:s3:::aws2azure-it-*",
        "arn:aws:s3:::aws2azure-it-*/*"
      ]
    },
    {
      "Sid": "DynamoDbCreateAndDiscover",
      "Effect": "Allow",
      "Action": [
        "dynamodb:CreateTable",
        "dynamodb:ListTables"
      ],
      "Resource": "*"
    },
    {
      "Sid": "DynamoDbPrefixScopedData",
      "Effect": "Allow",
      "Action": [
        "dynamodb:BatchGetItem",
        "dynamodb:BatchWriteItem",
        "dynamodb:DeleteItem",
        "dynamodb:DeleteTable",
        "dynamodb:DescribeTable",
        "dynamodb:GetItem",
        "dynamodb:PutItem",
        "dynamodb:Query",
        "dynamodb:Scan",
        "dynamodb:UpdateItem"
      ],
      "Resource": "arn:aws:dynamodb:*:*:table/aws2azure-it-*"
    },
    {
      "Sid": "KinesisCreateAndDiscover",
      "Effect": "Allow",
      "Action": [
        "kinesis:CreateStream",
        "kinesis:ListStreams"
      ],
      "Resource": "*"
    },
    {
      "Sid": "KinesisPrefixScopedData",
      "Effect": "Allow",
      "Action": [
        "kinesis:DeleteStream",
        "kinesis:DescribeStream",
        "kinesis:DescribeStreamSummary",
        "kinesis:GetRecords",
        "kinesis:GetShardIterator",
        "kinesis:ListShards",
        "kinesis:PutRecord",
        "kinesis:PutRecords"
      ],
      "Resource": "arn:aws:kinesis:*:*:stream/aws2azure-it-*"
    },
    {
      "Sid": "SnsCreateAndDiscover",
      "Effect": "Allow",
      "Action": [
        "sns:CreateTopic",
        "sns:ListTopics",
        "sns:Subscribe",
        "sns:Unsubscribe"
      ],
      "Resource": "*"
    },
    {
      "Sid": "SnsPrefixScopedData",
      "Effect": "Allow",
      "Action": [
        "sns:DeleteTopic",
        "sns:GetTopicAttributes",
        "sns:ListSubscriptionsByTopic",
        "sns:Publish",
        "sns:PublishBatch"
      ],
      "Resource": "arn:aws:sns:*:*:aws2azure-it-*"
    },
    {
      "Sid": "SqsCreateAndDiscover",
      "Effect": "Allow",
      "Action": [
        "sqs:CreateQueue",
        "sqs:ListQueues"
      ],
      "Resource": "*"
    },
    {
      "Sid": "SqsPrefixScopedData",
      "Effect": "Allow",
      "Action": [
        "sqs:DeleteMessage",
        "sqs:DeleteMessageBatch",
        "sqs:DeleteQueue",
        "sqs:GetQueueAttributes",
        "sqs:GetQueueUrl",
        "sqs:PurgeQueue",
        "sqs:ReceiveMessage",
        "sqs:SendMessage",
        "sqs:SendMessageBatch",
        "sqs:SetQueueAttributes"
      ],
      "Resource": "arn:aws:sqs:*:*:aws2azure-it-*"
    },
    {
      "Sid": "ReadOnlyDiscoveryForReaper",
      "Effect": "Allow",
      "Action": [
        "tag:GetResources"
      ],
      "Resource": "*"
    }
  ]
}
JSON

aws iam put-role-policy \
  --role-name "$ROLE_NAME" \
  --policy-name "$POLICY_NAME" \
  --policy-document file://aws2azure-real-aws-policy.json

# 4. Publish the role ARN to GitHub Actions.
#    This ARN is not a secret by itself; it is the OIDC assume-role target.
gh secret set AWS_ROLE_ARN --repo "$REPO" --body "$AWS_ROLE_ARN"
```

If the capture matrix grows, update the inline policy by **adding only the
newly required AWS actions** and keep the same `aws2azure-it-*` boundary. Do
not introduce a long-lived access key as a shortcut.

## Cost and safety model

This Tier-3 lane is intentionally **capture-only**, not nightly. S3,
DynamoDB, SNS, and SQS each have permanent always-free tiers that comfortably
cover small ephemeral lifecycle tests. Kinesis is the outlier: it has no
always-free tier, but short-lived on-demand usage for a bounded capture run is
still expected to cost only fractions of a cent per execution. That is why
issue [#708](https://github.com/pedrosakuma/aws2azure/issues/708) proposes a
weekly or on-demand capture cadence rather than making real AWS part of every
PR.

The real risk is not the happy-path request volume; it is a leaked resource
left running after a failed or cancelled capture. The first safety net is the
operator-owned low-threshold Budget alarm, which is already in place for the
dedicated account. The second safety net is an AWS orphan-resource reaper,
intended to mirror the existing
[`real-azure-reaper`](../../.github/workflows/real-azure-reaper.yml). The
checked-out tree does **not** yet contain `.github/workflows/real-aws-reaper.yml`,
so treat that backstop as planned work tracked in
[#708](https://github.com/pedrosakuma/aws2azure/issues/708), not as a finished
control in this checkout.

## Tagging and naming contract for ephemeral AWS resources

The only confirmed contract in the checked-out tree today is the shared
resource-name prefix:

- S3 buckets: `aws2azure-it-*`
- DynamoDB tables: `aws2azure-it-*`
- Kinesis streams: `aws2azure-it-*`
- SNS topics: `aws2azure-it-*`
- SQS queues: `aws2azure-it-*`

That prefix is already baked into the current IAM policy boundary above and
must remain true for any future real-AWS capture workflow.

A dedicated AWS reaper workflow is **not** present in this checkout, so there is
no confirmed repository-level tag schema to cite yet. When
`real-aws-reaper.yml` lands, this section should be updated with the exact tag
keys/values it discovers by (for example) age, workflow, run id, or purpose.
Until then, treat any tag contract beyond the `aws2azure-it-*` name prefix as
**to be finalized in #708**, not as established repository behavior.

## How to trigger an on-demand capture

This is **not yet available in the checked-out tree** because
`.github/workflows/capture-real-aws.yml` does not exist here yet. Once the
workflow lands, the intended operator flow is `workflow_dispatch` on
`capture-real-aws.yml`, as described in
[#708](https://github.com/pedrosakuma/aws2azure/issues/708).

Until then, use this document as the setup/audit record only, and treat the
current live-Azure path in [`integration-real-azure.yml`](../../.github/workflows/integration-real-azure.yml)
as the existing real-cloud operational reference.

## Related documents

- [Nightly real-Azure integration tests](./real-azure-nightly.md)
- [`integration-real-azure.yml`](../../.github/workflows/integration-real-azure.yml)
- [`real-azure-reaper.yml`](../../.github/workflows/real-azure-reaper.yml)
- [`tests/Aws2Azure.Conformance/README.md`](../../tests/Aws2Azure.Conformance/README.md)
- Issue [#708](https://github.com/pedrosakuma/aws2azure/issues/708)
