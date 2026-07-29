# Design gaps

Architectural limitations that do **not** map to a single operation — the
consistency model, transaction scope, and control-plane surfaces that differ
between the AWS service and its Azure target. Per-operation behaviour lives on
each [service page](index.md); this page is the cross-cutting story.

Legend: 🔵 by design · 🟡 partial · ⛔ unsupported · 🗓️ planned

## Summary

| Service | Area | Status | Disposition | Tracking |
|---|---|---|---|---|
| [dynamodb](#dynamodb) | Transaction scope is single-partition, single-table | 🔵 by design | 🔵 by design | — |
| [dynamodb](#dynamodb) | Transaction execution has one configured Cosmos authority | 🔵 by design | 🔵 by design | — |
| [dynamodb](#dynamodb) | Consistency and read-your-writes | 🔵 by design | 🔵 by design | — |
| [dynamodb](#dynamodb) | Throughput and throttling model | 🔵 by design | 🔵 by design | — |
| [dynamodb](#dynamodb) | Secondary indexes (GSI / LSI) | 🟡 partial | 🔵 by design | — |
| [dynamodb](#dynamodb) | Key encoding and on-disk storage format | 🔵 by design | 🔵 by design | — |
| [dynamodb](#dynamodb) | Absent DynamoDB features | ⛔ unsupported | ⚫ non-goal | — |
| [kinesis](#kinesis) | Synthetic sequence numbers and iterator positioning | 🔵 by design | 🔵 by design | — |
| [kinesis](#kinesis) | Iterator link lifetime and durable replay | 🔵 by design | 🔵 by design | — |
| [kinesis](#kinesis) | No resharding / enhanced fan-out / KCL lease model | ⛔ unsupported | 🔵 by design | — |
| [s3](#s3) | No IAM / ACL / bucket-policy authorization model | 🔵 by design | 🔵 by design | — |
| [s3](#s3) | No enforceable server-side-encryption configuration surface | 🔵 by design | 🔵 by design | — |
| [s3](#s3) | Multipart upload keeps bounded durable proxy state | 🔵 by design | 🔵 by design | — |
| [s3](#s3) | Multipart per-part ETag validation cannot be reproduced | 🔵 by design | 🔵 by design | — |
| [s3](#s3) | Bucket sub-resource configs are not translated | ⛔ unsupported | 🔵 by design | — |
| [secretsmanager](#secretsmanager) | Versioning and staging modelled on Key Vault version tags | 🟡 partial | 🔵 by design | — |
| [secretsmanager](#secretsmanager) | Rotation has no Lambda equivalent | 🟡 partial | ⚫ non-goal | — |
| [secretsmanager](#secretsmanager) | Deletion recovery semantics differ | 🔵 by design | 🔵 by design | — |
| [secretsmanager](#secretsmanager) | No resource policies or cross-account access | ⛔ unsupported | 🔵 by design | — |
| [sns](#sns) | Two backends with different fidelity | 🔵 by design | 🔵 by design | — |
| [sns](#sns) | FIFO topics are deferred | 🟡 partial | 🛠️ feasible backlog | [#692](https://github.com/pedrosakuma/aws2azure/issues/692) |
| [sns](#sns) | No AWS region / account namespace | 🔵 by design | 🔵 by design | — |
| [sns](#sns) | No IAM-backed policy surface | ⛔ unsupported | 🔵 by design | — |
| [sns](#sns) | Event Grid subscription management is excluded | ⛔ unsupported | 🔵 by design | — |
| [sqs](#sqs) | FIFO ordering requires the AMQP transport | 🟡 partial | 🔵 by design | — |
| [sqs](#sqs) | No AWS region / account namespace | 🔵 by design | 🔵 by design | — |
| [sqs](#sqs) | PurgeQueue is best-effort emulation | 🔵 by design | 🔵 by design | — |
| [sqs](#sqs) | Queue lifecycle eventual-consistency | 🔵 by design | 🔵 by design | — |
| [sqs](#sqs) | Transport-dependent capability differences | 🔵 by design | 🔵 by design | — |

## dynamodb

<a id="dynamodb-transaction-scope-is-single-partition-single-table"></a>

### Transaction scope is single-partition, single-table

- **Status:** 🔵 by design

TransactWriteItems / TransactGetItems are translated to a Cosmos DB stored-procedure transaction, which is scoped to one container and one logical partition. Operations spanning more than one table, or more than one partition-key value, are rejected with ValidationException.

**Impact.** DynamoDB's cross-table / cross-partition ACID surface (up to 100 items across tables and partitions) is not reproducible. Applications that rely on multi-entity transactions must be remodelled so all transacted items share a partition key.

**Workaround.** Co-locate transacted items under a single partition key, or fall back to idempotent application-level compensation.

References:

- <https://learn.microsoft.com/azure/cosmos-db/nosql/stored-procedures-triggers-udfs>

<a id="dynamodb-transaction-execution-has-one-configured-cosmos-authority"></a>

### Transaction execution has one configured Cosmos authority

- **Status:** 🔵 by design

Cosmos stored procedures execute through a writable region. To preserve one atomic snapshot and one durable ClientRequestToken history, every TransactGetItems/TransactWriteItems execution uses one deployment-stable regional authority. On a multi-write account this is exactly target.preferredRegions[0], not the first currently available match. The proxy never replays a transaction in a second independently writable region or through the dynamically routed global account endpoint.

**Impact.** Multi-write deployments must configure the same first preferred region on every replica and binding that targets the same data. Losing that region makes transactions unavailable even if another write region remains healthy; this is the availability cost of preserving one transaction/idempotency authority. Non-transactional routing and read failover are unchanged.

**Workaround.** Prefer a single-write Cosmos account for this profile, or configure an explicit preferredRegions list whose first entry is the intended transaction authority. Restore that region before retrying. Change the first entry only as a coordinated migration after outstanding 10-minute idempotency windows and replication have converged; restarting alone never changes authority.

References:

- <https://learn.microsoft.com/azure/cosmos-db/nosql/how-to-multi-master>
- <https://learn.microsoft.com/azure/cosmos-db/nosql/stored-procedures-triggers-udfs>

<a id="dynamodb-consistency-and-read-your-writes"></a>

### Consistency and read-your-writes

- **Status:** 🔵 by design

Ordinary operations issue independent Cosmos REST calls and do not propagate Cosmos session tokens between requests, so read-your-write determinism depends on the account's default consistency level. Single-partition TransactGetItems is the exception: it executes as one server-side stored-procedure snapshot. ConsistentRead effectiveness for non-transactional reads remains account-dependent.

**Impact.** A DynamoDB client that assumes strong read-your-writes may observe stale reads if the Cosmos account is configured for Session/Eventual consistency. GSI reads are always eventually consistent (ConsistentRead=true is rejected, matching DynamoDB).

**Workaround.** Provision the Cosmos account with Strong (or at least Bounded Staleness) default consistency for workloads that need DynamoDB-equivalent semantics; record the chosen level per deployment.

<a id="dynamodb-throughput-and-throttling-model"></a>

### Throughput and throttling model

- **Status:** 🔵 by design

Capacity is Cosmos RU/s, not DynamoDB RCU/WCU. Cosmos 429 (throttled) is surfaced to clients as ProvisionedThroughputExceededException (or as UnprocessedKeys for BatchGetItem), so the AWS SDK's native retry/backoff still engages, but the underlying accounting and limits are Azure's.

**Impact.** ConsumedCapacity figures, burst behaviour, and adaptive-capacity dynamics differ from DynamoDB. Large Scans throttle differently than on DynamoDB.

**Workaround.** Size the Cosmos container/database RU/s (or use autoscale/serverless) for the workload; do not rely on DynamoDB capacity semantics.

<a id="dynamodb-secondary-indexes--gsi---lsi"></a>

### Secondary indexes (GSI / LSI)

- **Status:** 🟡 partial
- **Disposition:** 🔵 by design

All attributes live in one base container; GSI queries are opt-in and LSI queries are always available over that container. GSI Query is a cross-partition fan-out (unlike a base-table Query's single-partition guarantee); string sort keys follow Cosmos code-point collation rather than DynamoDB UTF-8 byte order; numeric ordering relies on a synthetic order-preserving field written at item-write time.

**Impact.** Items written before the encoded-ordering field existed are excluded from ordered numeric-GSI results until rewritten (a backfill gap). Binary sort keys cannot be ordered. Index ItemCount / IndexSizeBytes / Backfilling remain unavailable for non-empty tables because there is no separate physical index resource to meter cheaply or truthfully.

**Workaround.** Keep GSI Query default-off unless the collation and live-base-document caveats are acceptable. Enable exact numeric LSI ordering only after rewriting pre-existing items to populate ordering fields.

<a id="dynamodb-key-encoding-and-on-disk-storage-format"></a>

### Key encoding and on-disk storage format

- **Status:** 🔵 by design

DynamoDB key attribute values are encoded into the internal Cosmos id/partition-key (S -> hex(UTF-8), B -> hex(raw), N -> order-preserving digit string) to accept Cosmos-forbidden characters and fix binary byte-ordering. Effective raw key limit is ~127 bytes.

**Impact.** Keys longer than the limit are rejected with ValidationException. The storage layout is a proxy-owned format, not portable to a raw Cosmos client, and changed across earlier builds (a breaking on-disk change).

**Workaround.** Keep key attributes within the size limit; treat the backing container as proxy-managed, not directly queryable with DynamoDB semantics.

<a id="dynamodb-absent-dynamodb-features"></a>

### Absent DynamoDB features

- **Status:** ⛔ unsupported
- **Disposition:** ⚫ non-goal

DynamoDB Streams, DAX, point-in-time recovery / on-demand backups, global tables, and auto-scaling have no in-scope Cosmos translation and are not exposed by the proxy.

**Impact.** Applications depending on these control-plane / streaming features cannot run through the proxy for those code paths.

**Workaround.** Use the corresponding Azure Cosmos capability directly (change feed, continuous backup, multi-region writes) outside the AWS wire protocol.

## kinesis

<a id="kinesis-synthetic-sequence-numbers-and-iterator-positioning"></a>

### Synthetic sequence numbers and iterator positioning

- **Status:** 🔵 by design

Kinesis sequence numbers are minted by the proxy as (unixMs << 20) | counter and mapped to an Event Hubs enqueue-time position. AT_SEQUENCE_NUMBER / AFTER_SEQUENCE_NUMBER are therefore best-effort at millisecond granularity, and MillisBehindLatest is derived from the last record's enqueue timestamp versus the proxy clock.

**Impact.** Records sharing a millisecond may be returned together at a sequence-based boundary; exact per-record sequence positioning is not reproducible. ExplicitHashKey and SequenceNumberForOrdering are accepted for wire compatibility but ignored.

**Workaround.** Prefer TRIM_HORIZON / LATEST / AT_TIMESTAMP iterators where exact sequence positioning is not required.

References:

- <https://learn.microsoft.com/rest/api/eventhub/>

<a id="kinesis-iterator-link-lifetime-and-durable-replay"></a>

### Iterator link lifetime and durable replay

- **Status:** 🔵 by design

Each proxy-issued shard iterator has a distinct identity and therefore a distinct pooled AMQP receiver link. Iterator chains advance independently while their links are live. The embedded continuation position recreates a link after failure, idle eviction, or restart, but the supported profile deliberately stops at one consumer loop per partition and consumer group.

**Impact.** Multiple iterator identities on one consumer group are not a certified durable consumer-ownership or replay topology. Recreated links resume from the best available Event Hubs offset/enqueue-time position and inherit the synthetic-sequence and millisecond-boundary differences documented above.

**Workaround.** Use one consumer loop per partition. Assign distinct Event Hubs consumer groups when consumers require independently operated replay/checkpoint lifecycles.

<a id="kinesis-no-resharding---enhanced-fan-out---kcl-lease-model"></a>

### No resharding / enhanced fan-out / KCL lease model

- **Status:** ⛔ unsupported
- **Disposition:** 🔵 by design

Kinesis shards map to Event Hubs partitions, which are fixed at the hub level. Dynamic resharding (SplitShard/MergeShards), enhanced fan-out (SubscribeToShard), and the KCL DynamoDB lease/checkpoint model have no in-scope translation.

**Impact.** Applications that resize streams at runtime or rely on enhanced-fan-out throughput isolation cannot drive those code paths through the proxy.

**Workaround.** Provision Event Hubs partition count for peak load up front; use consumer groups for isolation.

## s3

<a id="s3-no-iam---acl---bucket-policy-authorization-model"></a>

### No IAM / ACL / bucket-policy authorization model

- **Status:** 🔵 by design

Authorization is the static AWS-key-to-Azure-credential mapping validated by SigV4; there is no server-side IAM. ACLs are synthesised as owner-only and bucket-policy enforcement is not translated.

**Impact.** Fine-grained S3 access control remains outside the proxy.

**Workaround.** Enforce authorization with Azure RBAC, SAS, and network controls.

References:

- <https://docs.aws.amazon.com/AmazonS3/latest/API/API_GetBucketPolicy.html>

<a id="s3-no-enforceable-server-side-encryption-configuration-surface"></a>

### No enforceable server-side-encryption configuration surface

- **Status:** 🔵 by design

Azure Blob Storage encrypts at rest transparently. The proxy can persist some S3 encryption intent, but it does not map SSE request headers to distinct Azure key material or KMS workflows.

**Impact.** Applications that require SSE-C/SSE-KMS semantics cannot preserve them.

**Workaround.** Configure encryption and customer-managed keys at the Azure Storage account level.

<a id="s3-multipart-upload-keeps-bounded-durable-proxy-state"></a>

### Multipart upload keeps bounded durable proxy state

- **Status:** 🔵 by design

Azure Blob Storage has no native cross-blob multipart-upload enumeration primitive, so aws2azure persists one bounded state record per active multipart upload in a hidden Azure container. Record names begin with the zero-padded initiation timestamp, record bodies store only the headers needed to finish the upload (16 KiB cap), and Create/List opportunistically delete a bounded number of expired records.

**Impact.** Multipart enumeration and metadata fidelity now survive proxy restart, but the proxy deliberately owns a small amount of durable control-plane state.

**Workaround.** Treat the hidden multipart-state container as proxy-owned implementation data; do not manage or replicate it through S3.

<a id="s3-multipart-per-part-etag-validation-cannot-be-reproduced"></a>

### Multipart per-part ETag validation cannot be reproduced

- **Status:** 🔵 by design

Even with durable multipart state, Azure still exposes no primitive that lets the proxy re-read the true MD5/ETag of each staged uncommitted block. CompleteMultipartUpload and ListParts therefore cannot validate or replay AWS's per-part ETag contract exactly.

**Impact.** Workloads that overwrite a PartNumber with different bytes and rely on AWS rejecting the stale ETag at CompleteMultipartUpload will observe different behaviour.

**Workaround.** Gate those workloads out or avoid re-uploading the same PartNumber with different content inside one multipart session.

<a id="s3-bucket-sub-resource-configs-are-not-translated"></a>

### Bucket sub-resource configs are not translated

- **Status:** ⛔ unsupported
- **Disposition:** 🔵 by design

Lifecycle, replication, website hosting, event notifications, Requester Pays, acceleration, and logging bucket configurations have no Blob-storage equivalent in the wire-protocol path.

**Impact.** Automated tiering/expiry, cross-region replication, static-website hosting, and S3 event automation configured through these APIs have no effect.

**Workaround.** Use Azure Blob lifecycle-management policies, object replication, static website settings, and Event Grid subscriptions configured directly on the storage account.

## secretsmanager

<a id="secretsmanager-versioning-and-staging-modelled-on-key-vault-version-tags"></a>

### Versioning and staging modelled on Key Vault version tags

- **Status:** 🟡 partial
- **Disposition:** 🔵 by design

Secrets Manager version stages (AWSCURRENT / AWSPREVIOUS / custom labels) are modelled with Key Vault secret versions plus per-version tags. Key Vault's created timestamp has one-second granularity, so deterministic resolution uses created time plus version id and relies on tag bookkeeping rather than a native staging concept.

**Impact.** Version creation, inventory, and per-version tag patches cannot be one Key Vault transaction. The proxy uses empty-stage creation, loser-first publication, fresh tag merges, and bounded verification/repair, but strict cross-instance atomicity remains structurally impossible without an external coordinator. Key Vault secret PATCH does not expose a contractual ETag/If-Match primitive, so a tag edit that lands in the narrow interval between the proxy's fresh GET and PATCH can still be overwritten.

**Workaround.** Retry an explicit ResourceExistsException after propagation settles and use a single writer when stronger ordering is required. Unrelated out-of-band tags are preserved by fresh merges, but out-of-band edits to proxy-owned aws2azure-* tags are unsupported. Before rollback, drain writes and let the candidate runtime finish or repair every pending publication; the previous runtime can read completed versions but cannot interpret the candidate-only pending-publication intent metadata.

<a id="secretsmanager-rotation-has-no-lambda-equivalent"></a>

### Rotation has no Lambda equivalent

- **Status:** 🟡 partial
- **Disposition:** ⚫ non-goal

Secrets Manager rotation is driven by a customer Lambda function; Azure Key Vault has no equivalent in-line rotation-function contract, so RotateSecret cannot execute an arbitrary rotation workflow.

**Impact.** Automatic, function-driven credential rotation as configured in AWS is not reproduced end-to-end.

**Workaround.** Rotate secrets via an external process (e.g. an Azure Function / pipeline) that calls PutSecretValue through the proxy.

<a id="secretsmanager-deletion-recovery-semantics-differ"></a>

### Deletion recovery semantics differ

- **Status:** 🔵 by design

DeleteSecret's RecoveryWindowInDays / ForceDeleteWithoutRecovery map onto Key Vault soft-delete and purge, whose retention model and immediate-purge permissions are governed by the vault, not by AWS parameters.

**Impact.** Recovery-window timing and force-delete behaviour follow the Key Vault soft-delete configuration rather than the exact AWS window semantics.

**Workaround.** Configure the Key Vault soft-delete retention to match the intended recovery window; grant purge permission only where force-delete is needed.

<a id="secretsmanager-no-resource-policies-or-cross-account-access"></a>

### No resource policies or cross-account access

- **Status:** ⛔ unsupported
- **Disposition:** 🔵 by design

Secrets Manager resource policies and cross-account secret sharing have no Key Vault equivalent in the wire-protocol path; authorization is the static AWS-key-to-Azure-credential mapping, not server-side IAM.

**Impact.** Policy-based or cross-account access patterns cannot be expressed through the proxy.

**Workaround.** Use Key Vault RBAC / access policies at the Azure level for authorization.

## sns

<a id="sns-two-backends-with-different-fidelity"></a>

### Two backends with different fidelity

- **Status:** 🔵 by design

A topic can be backed by Service Bus Topics (AMQP) or Event Grid, and the two backends do not offer identical semantics. On Event Grid the proxy emits the classic Event Grid schema (eventType=aws.sns.Message), the subject is always the TopicArn, and PublishBatch uses proxied per-entry outcomes; on Service Bus the delivery model and partial-failure shape differ.

**Impact.** The same SNS Publish/PublishBatch can behave differently depending on the configured backend; partial-failure semantics may diverge from SNS.

**Workaround.** Pick the backend per topic based on the delivery semantics required and test partial-failure handling against it.

References:

- <https://learn.microsoft.com/azure/event-grid/post-to-custom-topic>

<a id="sns-fifo-topics-are-deferred"></a>

### FIFO topics are deferred

- **Status:** 🟡 partial
- **Disposition:** 🛠️ feasible backlog
- **Tracking issue:** [#692](https://github.com/pedrosakuma/aws2azure/issues/692)

SNS FIFO topic semantics are only approximated. FifoTopic is inferred from a .fifo suffix or RequiresDuplicateDetection; MessageGroupId / MessageDeduplicationId are not honoured on the Event Grid backend (dropped with a warning), and strict FIFO ordering/dedup is not modelled.

**Impact.** Applications relying on SNS FIFO ordering and content-based deduplication cannot depend on it through the proxy.

**Workaround.** Use the Service Bus backend with duplicate detection where approximate dedup is acceptable; do not rely on strict FIFO ordering.

<a id="sns-no-aws-region---account-namespace"></a>

### No AWS region / account namespace

- **Status:** 🔵 by design

Topic and subscription ARNs are synthesised as arn:aws:sns:{sigv4-region}:000000000000:{name}; the account id is a stable placeholder because the proxy is not backed by an AWS account namespace.

**Impact.** Applications that parse account id or cross-account references out of an ARN will see placeholder values.

**Workaround.** Do not depend on the account/region portion of returned ARNs.

References:

- <https://github.com/pedrosakuma/aws2azure/issues/267>

<a id="sns-no-iam-backed-policy-surface"></a>

### No IAM-backed policy surface

- **Status:** ⛔ unsupported
- **Disposition:** 🔵 by design

DeliveryPolicy, RedrivePolicy, and SubscriptionRoleArn are accepted as no-ops because Service Bus / Event Grid expose no matching SNS attribute contract, and there is no server-side IAM evaluation.

**Impact.** Retry/redrive policy and role-based delivery configured via these attributes have no effect.

**Workaround.** Configure delivery reliability at the Azure backend level; do not rely on SNS policy attributes being enforced.

<a id="sns-event-grid-subscription-management-is-excluded"></a>

### Event Grid subscription management is excluded

- **Status:** ⛔ unsupported
- **Disposition:** 🔵 by design

Subscribe, ConfirmSubscription, ListSubscriptions, ListSubscriptionsByTopic, GetSubscriptionAttributes, SetSubscriptionAttributes, and Unsubscribe translate only to Azure Service Bus topic subscriptions. They never create, enumerate, mutate, confirm, or delete Azure Event Grid event subscriptions.

**Impact.** An SNS topic whose publish backend is Event Grid does not gain Event Grid delivery fan-out from the SNS subscription-management APIs, and its events are not delivered into Service Bus subscriptions created by those APIs.

**Workaround.** Provision and operate Event Grid event subscriptions with Azure-native tooling, or select the Service Bus Topics publish backend when this profile is required.

References:

- <https://learn.microsoft.com/azure/event-grid/manage-event-delivery>

## sqs

<a id="sqs-fifo-ordering-requires-the-amqp-transport"></a>

### FIFO ordering requires the AMQP transport

- **Status:** 🟡 partial
- **Disposition:** 🔵 by design

Strict per-MessageGroupId ordering is implemented only when a queue is configured with transport: Amqp — the receive path acquires a broker-assigned Service Bus session and holds the session lock so a group's in-flight messages stay pinned to one consumer. The REST transport cannot express session-receive and therefore does not provide strict per-group ordering (it surfaces MessageGroupId but does not block concurrent delivery of the same group).

**Impact.** Workloads that need SQS FIFO guarantees must use the AMQP transport. FIFO settle is connection-affine: an in-flight FIFO message cannot be settled from a different live connection while its session lock is held.

**Workaround.** Set transport: Amqp for .fifo queues; keep the receive-then-delete cycle on the same connection.

References:

- <https://learn.microsoft.com/azure/service-bus-messaging/service-bus-amqp-protocol-guide>

<a id="sqs-no-aws-region---account-namespace"></a>

### No AWS region / account namespace

- **Status:** 🔵 by design

The proxy is not backed by an AWS account, so queue ARNs are synthesised with a placeholder account id (000000000000) and the region taken from the SigV4 credential scope. Dead-letter source ARNs use us-east-1 as a placeholder.

**Impact.** Applications that parse the account id or region out of a queue ARN, or that assert cross-account/cross-region topology, will see placeholder values rather than real AWS identifiers.

**Workaround.** Do not depend on the account/region portion of returned ARNs. Region awareness is tracked as opt-in future work.

References:

- <https://github.com/pedrosakuma/aws2azure/issues/267>

<a id="sqs-purgequeue-is-best-effort-emulation"></a>

### PurgeQueue is best-effort emulation

- **Status:** 🔵 by design

Service Bus has no native purge. The proxy emulates it by draining peek-locked messages and deleting them within a bounded 60s budget, so the queue is best-effort empty rather than guaranteed empty at the end of the call.

**Impact.** Under sustained high producer rates the drain may not keep up, so a purge can leave residual messages — unlike the SQS contract, which guarantees all messages enqueued at the time of the call are deleted.

**Workaround.** Pause producers before purging when a hard empty is required.

<a id="sqs-queue-lifecycle-eventual-consistency"></a>

### Queue lifecycle eventual-consistency

- **Status:** 🔵 by design

Service Bus deletes queues synchronously, whereas SQS may take up to 60s of eventual consistency and returns QueueDeletedRecently on immediate re-create. The proxy does not currently synthesise QueueDeletedRecently.

**Impact.** Delete-then-recreate-within-seconds patterns that expect the AWS eventual-consistency error will instead succeed immediately.

**Workaround.** Do not rely on QueueDeletedRecently timing behaviour.

<a id="sqs-transport-dependent-capability-differences"></a>

### Transport-dependent capability differences

- **Status:** 🔵 by design

REST and AMQP transports differ beyond FIFO: receipt-handle formats, VisibilityTimeout=0 immediate release (AMQP only, via Abandon), per-entry partial-failure granularity on batch sends (real on AMQP, coarser on REST), and dead-letter attribution (AMQP only).

**Impact.** The same SQS operation can behave differently depending on the queue's configured transport; receipt handles are not interchangeable across transports.

**Workaround.** Choose the transport per queue based on the semantics required and keep receive/settle on the same transport.

