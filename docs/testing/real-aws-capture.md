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
S3. The real-AWS capture workflow therefore records the authoritative AWS side
of the Tier-3 differential across the broader case matrix.

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

### Current repository state

The repository contains the complete decoupled Tier-3 flow:
[`CanonicalDiff`](../../tests/Aws2Azure.Conformance/Canonicalization/CanonicalDiff.cs),
[`GoldenStore`](../../tests/Aws2Azure.Conformance/Goldens/GoldenStore.cs), and
`GoldenProvenance.SourceRealAws` in the same store.

- [`capture-real-aws.yml`](../../.github/workflows/capture-real-aws.yml) runs
  weekly or by `workflow_dispatch`, captures real-AWS goldens, and opens or
  updates the `automation/real-aws-goldens` refresh PR when files change.
- [`integration-real-azure.yml`](../../.github/workflows/integration-real-azure.yml)
  exports canonical `proxy-real-azure` evidence for the shared case catalog.
- [`tier3-real-diff.yml`](../../.github/workflows/tier3-real-diff.yml) runs the
  credential-free `OfflineConformanceDiffRunner` after a successful real-Azure
  workflow or by manual dispatch. Individual cases skip when either side has no
  corresponding evidence.
- [`real-aws-reaper.yml`](../../.github/workflows/real-aws-reaper.yml) and
  [`cleanup-real-aws-resources.sh`](../../.github/scripts/cleanup-real-aws-resources.sh)
  provide scheduled and post-capture orphan cleanup.

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
        "s3:DeleteObjectVersion",
        "s3:GetObject",
        "s3:HeadBucket",
        "s3:ListBucket",
        "s3:ListBucketVersions",
        "s3:PutBucketVersioning",
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
        "dynamodb:TagResource",
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
        "kinesis:AddTagsToStream",
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
        "sns:PublishBatch",
        "sns:TagResource"
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
the capture workflow uses a weekly or on-demand cadence rather than making real
AWS part of every PR.

The real risk is not the happy-path request volume; it is a leaked resource
left running after a failed or cancelled capture. The first safety net is the
operator-owned low-threshold Budget alarm, which is already in place for the
dedicated account. The second safety net is an AWS orphan-resource reaper,
[`real-aws-reaper.yml`](../../.github/workflows/real-aws-reaper.yml), mirroring the existing
[`real-azure-reaper`](../../.github/workflows/real-azure-reaper.yml) pattern.
It deletes S3 buckets, DynamoDB tables, Kinesis streams, SNS topics, and SQS
queues named `aws2azure-it-*` and/or tagged `purpose=aws2azure-it` that are
older than its safety age threshold.

## Tagging and naming contract for ephemeral AWS resources

The capture and reaper workflows share this resource-name prefix:

- S3 buckets: `aws2azure-it-*`
- DynamoDB tables: `aws2azure-it-*`
- Kinesis streams: `aws2azure-it-*`
- SNS topics: `aws2azure-it-*`
- SQS queues: `aws2azure-it-*`

That prefix is baked into the current IAM policy boundary and must remain stable
for capture and cleanup.

`real-aws-reaper.yml` primarily
discovers orphans by the `aws2azure-it-<unix-epoch>-<suffix>` name prefix (the
timestamp encodes resource age directly in the name) and cross-checks the
`purpose=aws2azure-it`
/ `created=<ISO8601>` tags where the resource type supports tagging, falling
back to the name-embedded timestamp when tag visibility lags. Treat the exact
age threshold and per-service tag keys as subject to change — read the
workflow/script directly rather than relying on this paragraph as the source
of truth.

## How to trigger an on-demand capture

Run `capture-real-aws.yml` with `workflow_dispatch`. The workflow requires the
`AWS_ROLE_ARN` repository secret; without it, the capture reports a notice and
skips without accessing AWS. A successful run always invokes orphan cleanup and,
when goldens change, uploads them as an artifact and opens or updates the
`automation/real-aws-goldens` pull request. Review that PR before merging the
new oracle data.

## Related documents

- [Nightly real-Azure integration tests](./real-azure-nightly.md)
- [`integration-real-azure.yml`](../../.github/workflows/integration-real-azure.yml)
- [`real-azure-reaper.yml`](../../.github/workflows/real-azure-reaper.yml)
- [`capture-real-aws.yml`](../../.github/workflows/capture-real-aws.yml)
- [`real-aws-reaper.yml`](../../.github/workflows/real-aws-reaper.yml)
- [`tier3-real-diff.yml`](../../.github/workflows/tier3-real-diff.yml)
- [`OfflineConformanceDiffRunner.cs`](../../tests/Aws2Azure.Conformance/Diff/OfflineConformanceDiffRunner.cs)
- [`tests/Aws2Azure.Conformance/README.md`](../../tests/Aws2Azure.Conformance/README.md)
- Issue [#708](https://github.com/pedrosakuma/aws2azure/issues/708)
