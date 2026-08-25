# Design gaps {#design-gaps}

Architectural limitations that do **not** map to a single operation — the
consistency model, transaction scope, and control-plane surfaces that differ
between the AWS service and its Azure target. Per-operation behaviour lives on
each [service page](index.md). This page is an index whose links open
stable, independently searchable design-gap pages. Existing public anchors
remain on this index for compatibility.

Legend: 🔵 by design · 🟡 partial · ⛔ unsupported · 🗓️ planned

## Summary {#summary}

| Service | Area | Status | Disposition | Tracking |
|---|---|---|---|---|
| [dynamodb](#dynamodb) | <a id="dynamodb-absent-dynamodb-features"></a><a id="absent-dynamodb-features" data-legacy-fragment="true"></a>[Absent DynamoDB features](design-gaps/dynamodb/absent-dynamodb-features.md) | ⛔ unsupported | ⚫ non-goal | — |
| [dynamodb](#dynamodb) | <a id="dynamodb-consistency-and-read-your-writes"></a><a id="consistency-and-read-your-writes" data-legacy-fragment="true"></a>[Consistency and read-your-writes](design-gaps/dynamodb/consistency-and-read-your-writes.md) | 🔵 by design | 🔵 by design | — |
| [dynamodb](#dynamodb) | <a id="dynamodb-key-encoding-and-on-disk-storage-format"></a><a id="key-encoding-and-on-disk-storage-format" data-legacy-fragment="true"></a>[Key encoding and on-disk storage format](design-gaps/dynamodb/key-encoding-and-on-disk-storage-format.md) | 🔵 by design | 🔵 by design | — |
| [dynamodb](#dynamodb) | <a id="dynamodb-secondary-indexes--gsi---lsi"></a><a id="secondary-indexes-gsi-lsi" data-legacy-fragment="true"></a>[Secondary indexes (GSI / LSI)](design-gaps/dynamodb/secondary-indexes--gsi---lsi.md) | 🟡 partial | 🔵 by design | — |
| [dynamodb](#dynamodb) | <a id="dynamodb-throughput-and-throttling-model"></a><a id="throughput-and-throttling-model" data-legacy-fragment="true"></a>[Throughput and throttling model](design-gaps/dynamodb/throughput-and-throttling-model.md) | 🔵 by design | 🔵 by design | — |
| [dynamodb](#dynamodb) | <a id="dynamodb-transaction-execution-has-one-configured-cosmos-authority"></a><a id="transaction-execution-has-one-configured-cosmos-authority" data-legacy-fragment="true"></a>[Transaction execution has one configured Cosmos authority](design-gaps/dynamodb/transaction-execution-has-one-configured-cosmos-authority.md) | 🔵 by design | 🔵 by design | — |
| [dynamodb](#dynamodb) | <a id="dynamodb-transaction-scope-is-single-partition-single-table"></a><a id="transaction-scope-is-single-partition-single-table" data-legacy-fragment="true"></a>[Transaction scope is single-partition, single-table](design-gaps/dynamodb/transaction-scope-is-single-partition-single-table.md) | 🔵 by design | 🔵 by design | — |
| [kinesis](#kinesis) | <a id="kinesis-iterator-link-lifetime-and-durable-replay"></a><a id="iterator-link-lifetime-and-durable-replay" data-legacy-fragment="true"></a>[Iterator link lifetime and durable replay](design-gaps/kinesis/iterator-link-lifetime-and-durable-replay.md) | 🔵 by design | 🔵 by design | — |
| [kinesis](#kinesis) | <a id="kinesis-no-resharding---enhanced-fan-out---kcl-lease-model"></a><a id="no-resharding-enhanced-fan-out-kcl-lease-model" data-legacy-fragment="true"></a>[No resharding / enhanced fan-out / KCL lease model](design-gaps/kinesis/no-resharding---enhanced-fan-out---kcl-lease-model.md) | ⛔ unsupported | 🔵 by design | — |
| [kinesis](#kinesis) | <a id="kinesis-synthetic-sequence-numbers-and-iterator-positioning"></a><a id="synthetic-sequence-numbers-and-iterator-positioning" data-legacy-fragment="true"></a>[Synthetic sequence numbers and iterator positioning](design-gaps/kinesis/synthetic-sequence-numbers-and-iterator-positioning.md) | 🔵 by design | 🔵 by design | — |
| [s3](#s3) | <a id="s3-bucket-sub-resource-configs-are-not-translated"></a><a id="bucket-sub-resource-configs-are-not-translated" data-legacy-fragment="true"></a>[Bucket sub-resource configs are not translated](design-gaps/s3/bucket-sub-resource-configs-are-not-translated.md) | ⛔ unsupported | 🔵 by design | — |
| [s3](#s3) | <a id="s3-multipart-per-part-etag-validation-cannot-be-reproduced"></a><a id="multipart-per-part-etag-validation-cannot-be-reproduced" data-legacy-fragment="true"></a>[Multipart per-part ETag validation cannot be reproduced](design-gaps/s3/multipart-per-part-etag-validation-cannot-be-reproduced.md) | 🔵 by design | 🔵 by design | — |
| [s3](#s3) | <a id="s3-multipart-upload-keeps-bounded-durable-proxy-state"></a><a id="multipart-upload-keeps-bounded-durable-proxy-state" data-legacy-fragment="true"></a>[Multipart upload keeps bounded durable proxy state](design-gaps/s3/multipart-upload-keeps-bounded-durable-proxy-state.md) | 🔵 by design | 🔵 by design | — |
| [s3](#s3) | <a id="s3-no-iam---acl---bucket-policy-authorization-model"></a><a id="no-iam-acl-bucket-policy-authorization-model" data-legacy-fragment="true"></a>[No IAM / ACL / bucket-policy authorization model](design-gaps/s3/no-iam---acl---bucket-policy-authorization-model.md) | 🔵 by design | 🔵 by design | — |
| [s3](#s3) | <a id="s3-no-enforceable-server-side-encryption-configuration-surface"></a><a id="no-enforceable-server-side-encryption-configuration-surface" data-legacy-fragment="true"></a>[No enforceable server-side-encryption configuration surface](design-gaps/s3/no-enforceable-server-side-encryption-configuration-surface.md) | 🔵 by design | 🔵 by design | — |
| [secretsmanager](#secretsmanager) | <a id="secretsmanager-deletion-recovery-semantics-differ"></a><a id="deletion-recovery-semantics-differ" data-legacy-fragment="true"></a>[Deletion recovery semantics differ](design-gaps/secretsmanager/deletion-recovery-semantics-differ.md) | 🔵 by design | 🔵 by design | — |
| [secretsmanager](#secretsmanager) | <a id="secretsmanager-no-resource-policies-or-cross-account-access"></a><a id="no-resource-policies-or-cross-account-access" data-legacy-fragment="true"></a>[No resource policies or cross-account access](design-gaps/secretsmanager/no-resource-policies-or-cross-account-access.md) | ⛔ unsupported | 🔵 by design | — |
| [secretsmanager](#secretsmanager) | <a id="secretsmanager-rotation-has-no-lambda-equivalent"></a><a id="rotation-has-no-lambda-equivalent" data-legacy-fragment="true"></a>[Rotation has no Lambda equivalent](design-gaps/secretsmanager/rotation-has-no-lambda-equivalent.md) | 🟡 partial | ⚫ non-goal | — |
| [secretsmanager](#secretsmanager) | <a id="secretsmanager-synthetic-arns-use-a-proxy-specific-namespace"></a><a id="synthetic-arns-use-a-proxy-specific-namespace" data-legacy-fragment="true"></a>[Synthetic ARNs use a proxy-specific namespace](design-gaps/secretsmanager/synthetic-arns-use-a-proxy-specific-namespace.md) | 🔵 by design | 🔵 by design | — |
| [secretsmanager](#secretsmanager) | <a id="secretsmanager-versioning-and-staging-modelled-on-key-vault-version-tags"></a><a id="versioning-and-staging-modelled-on-key-vault-version-tags" data-legacy-fragment="true"></a>[Versioning and staging modelled on Key Vault version tags](design-gaps/secretsmanager/versioning-and-staging-modelled-on-key-vault-version-tags.md) | 🟡 partial | 🔵 by design | — |
| [sns](#sns) | <a id="sns-event-grid-subscription-management-is-excluded"></a><a id="event-grid-subscription-management-is-excluded" data-legacy-fragment="true"></a>[Event Grid subscription management is excluded](design-gaps/sns/event-grid-subscription-management-is-excluded.md) | ⛔ unsupported | 🔵 by design | — |
| [sns](#sns) | <a id="sns-fifo-topics-are-deferred"></a><a id="fifo-topics-are-deferred" data-legacy-fragment="true"></a>[FIFO topics are deferred](design-gaps/sns/fifo-topics-are-deferred.md) | 🟡 partial | 🔵 by design | — |
| [sns](#sns) | <a id="sns-no-aws-region---account-namespace"></a><a id="no-aws-region-account-namespace" data-legacy-fragment="true"></a>[No AWS region / account namespace](design-gaps/sns/no-aws-region---account-namespace.md) | 🔵 by design | 🔵 by design | — |
| [sns](#sns) | <a id="sns-no-iam-backed-policy-surface"></a><a id="no-iam-backed-policy-surface" data-legacy-fragment="true"></a>[No IAM-backed policy surface](design-gaps/sns/no-iam-backed-policy-surface.md) | ⛔ unsupported | 🔵 by design | — |
| [sns](#sns) | <a id="sns-two-backends-with-different-fidelity"></a><a id="two-backends-with-different-fidelity" data-legacy-fragment="true"></a>[Two backends with different fidelity](design-gaps/sns/two-backends-with-different-fidelity.md) | 🔵 by design | 🔵 by design | — |
| [sqs](#sqs) | <a id="sqs-fifo-ordering-requires-the-amqp-transport"></a><a id="fifo-ordering-requires-the-amqp-transport" data-legacy-fragment="true"></a>[FIFO ordering requires the AMQP transport](design-gaps/sqs/fifo-ordering-requires-the-amqp-transport.md) | 🟡 partial | 🔵 by design | — |
| [sqs](#sqs) | <a id="sqs-no-aws-region---account-namespace"></a><a id="no-aws-region-account-namespace_1" data-legacy-fragment="true"></a>[No AWS region / account namespace](design-gaps/sqs/no-aws-region---account-namespace.md) | 🔵 by design | 🔵 by design | — |
| [sqs](#sqs) | <a id="sqs-purgequeue-is-best-effort-emulation"></a><a id="purgequeue-is-best-effort-emulation" data-legacy-fragment="true"></a>[PurgeQueue is best-effort emulation](design-gaps/sqs/purgequeue-is-best-effort-emulation.md) | 🔵 by design | 🔵 by design | — |
| [sqs](#sqs) | <a id="sqs-queue-lifecycle-eventual-consistency"></a><a id="queue-lifecycle-eventual-consistency" data-legacy-fragment="true"></a>[Queue lifecycle eventual-consistency](design-gaps/sqs/queue-lifecycle-eventual-consistency.md) | 🔵 by design | 🔵 by design | — |
| [sqs](#sqs) | <a id="sqs-transport-dependent-capability-differences"></a><a id="transport-dependent-capability-differences" data-legacy-fragment="true"></a>[Transport-dependent capability differences](design-gaps/sqs/transport-dependent-capability-differences.md) | 🔵 by design | 🔵 by design | — |

## dynamodb {#dynamodb}

- [Absent DynamoDB features](design-gaps/dynamodb/absent-dynamodb-features.md) — ⛔ unsupported · `design-gap:dynamodb:absent-dynamodb-features`
- [Consistency and read-your-writes](design-gaps/dynamodb/consistency-and-read-your-writes.md) — 🔵 by design · `design-gap:dynamodb:consistency-and-read-your-writes`
- [Key encoding and on-disk storage format](design-gaps/dynamodb/key-encoding-and-on-disk-storage-format.md) — 🔵 by design · `design-gap:dynamodb:key-encoding-and-on-disk-storage-format`
- [Secondary indexes (GSI / LSI)](design-gaps/dynamodb/secondary-indexes--gsi---lsi.md) — 🟡 partial · `design-gap:dynamodb:secondary-indexes--gsi---lsi`
- [Throughput and throttling model](design-gaps/dynamodb/throughput-and-throttling-model.md) — 🔵 by design · `design-gap:dynamodb:throughput-and-throttling-model`
- [Transaction execution has one configured Cosmos authority](design-gaps/dynamodb/transaction-execution-has-one-configured-cosmos-authority.md) — 🔵 by design · `design-gap:dynamodb:transaction-execution-has-one-configured-cosmos-authority`
- [Transaction scope is single-partition, single-table](design-gaps/dynamodb/transaction-scope-is-single-partition-single-table.md) — 🔵 by design · `design-gap:dynamodb:transaction-scope-is-single-partition-single-table`

## kinesis {#kinesis}

- [Iterator link lifetime and durable replay](design-gaps/kinesis/iterator-link-lifetime-and-durable-replay.md) — 🔵 by design · `design-gap:kinesis:iterator-link-lifetime-and-durable-replay`
- [No resharding / enhanced fan-out / KCL lease model](design-gaps/kinesis/no-resharding---enhanced-fan-out---kcl-lease-model.md) — ⛔ unsupported · `design-gap:kinesis:no-resharding---enhanced-fan-out---kcl-lease-model`
- [Synthetic sequence numbers and iterator positioning](design-gaps/kinesis/synthetic-sequence-numbers-and-iterator-positioning.md) — 🔵 by design · `design-gap:kinesis:synthetic-sequence-numbers-and-iterator-positioning`

## s3 {#s3}

- [Bucket sub-resource configs are not translated](design-gaps/s3/bucket-sub-resource-configs-are-not-translated.md) — ⛔ unsupported · `design-gap:s3:bucket-sub-resource-configs-are-not-translated`
- [Multipart per-part ETag validation cannot be reproduced](design-gaps/s3/multipart-per-part-etag-validation-cannot-be-reproduced.md) — 🔵 by design · `design-gap:s3:multipart-per-part-etag-validation-cannot-be-reproduced`
- [Multipart upload keeps bounded durable proxy state](design-gaps/s3/multipart-upload-keeps-bounded-durable-proxy-state.md) — 🔵 by design · `design-gap:s3:multipart-upload-keeps-bounded-durable-proxy-state`
- [No IAM / ACL / bucket-policy authorization model](design-gaps/s3/no-iam---acl---bucket-policy-authorization-model.md) — 🔵 by design · `design-gap:s3:no-iam---acl---bucket-policy-authorization-model`
- [No enforceable server-side-encryption configuration surface](design-gaps/s3/no-enforceable-server-side-encryption-configuration-surface.md) — 🔵 by design · `design-gap:s3:no-enforceable-server-side-encryption-configuration-surface`

## secretsmanager {#secretsmanager}

- [Deletion recovery semantics differ](design-gaps/secretsmanager/deletion-recovery-semantics-differ.md) — 🔵 by design · `design-gap:secretsmanager:deletion-recovery-semantics-differ`
- [No resource policies or cross-account access](design-gaps/secretsmanager/no-resource-policies-or-cross-account-access.md) — ⛔ unsupported · `design-gap:secretsmanager:no-resource-policies-or-cross-account-access`
- [Rotation has no Lambda equivalent](design-gaps/secretsmanager/rotation-has-no-lambda-equivalent.md) — 🟡 partial · `design-gap:secretsmanager:rotation-has-no-lambda-equivalent`
- [Synthetic ARNs use a proxy-specific namespace](design-gaps/secretsmanager/synthetic-arns-use-a-proxy-specific-namespace.md) — 🔵 by design · `design-gap:secretsmanager:synthetic-arns-use-a-proxy-specific-namespace`
- [Versioning and staging modelled on Key Vault version tags](design-gaps/secretsmanager/versioning-and-staging-modelled-on-key-vault-version-tags.md) — 🟡 partial · `design-gap:secretsmanager:versioning-and-staging-modelled-on-key-vault-version-tags`

## sns {#sns}

- [Event Grid subscription management is excluded](design-gaps/sns/event-grid-subscription-management-is-excluded.md) — ⛔ unsupported · `design-gap:sns:event-grid-subscription-management-is-excluded`
- [FIFO topics are deferred](design-gaps/sns/fifo-topics-are-deferred.md) — 🟡 partial · `design-gap:sns:fifo-topics-are-deferred`
- [No AWS region / account namespace](design-gaps/sns/no-aws-region---account-namespace.md) — 🔵 by design · `design-gap:sns:no-aws-region---account-namespace`
- [No IAM-backed policy surface](design-gaps/sns/no-iam-backed-policy-surface.md) — ⛔ unsupported · `design-gap:sns:no-iam-backed-policy-surface`
- [Two backends with different fidelity](design-gaps/sns/two-backends-with-different-fidelity.md) — 🔵 by design · `design-gap:sns:two-backends-with-different-fidelity`

## sqs {#sqs}

- [FIFO ordering requires the AMQP transport](design-gaps/sqs/fifo-ordering-requires-the-amqp-transport.md) — 🟡 partial · `design-gap:sqs:fifo-ordering-requires-the-amqp-transport`
- [No AWS region / account namespace](design-gaps/sqs/no-aws-region---account-namespace.md) — 🔵 by design · `design-gap:sqs:no-aws-region---account-namespace`
- [PurgeQueue is best-effort emulation](design-gaps/sqs/purgequeue-is-best-effort-emulation.md) — 🔵 by design · `design-gap:sqs:purgequeue-is-best-effort-emulation`
- [Queue lifecycle eventual-consistency](design-gaps/sqs/queue-lifecycle-eventual-consistency.md) — 🔵 by design · `design-gap:sqs:queue-lifecycle-eventual-consistency`
- [Transport-dependent capability differences](design-gaps/sqs/transport-dependent-capability-differences.md) — 🔵 by design · `design-gap:sqs:transport-dependent-capability-differences`

