# Workload compatibility

Use this page before adopting the proxy. A module being available means it can
route that AWS wire protocol; it does **not** mean full AWS service parity.
The assessments below are generated from the operation and design-gap YAMLs.
Operation-seal counts show only that each referenced operation has positive
real-Azure evidence; they do not certify every sub-feature or accepted design gap.

Legend: ✅ supported · 🟡 conditional · ⛔ blocked

## Service coverage profile

| Service | Module | Implemented | Partial | Stub | Unsupported | Real-Azure sealed |
|---|---|---:|---:|---:|---:|---:|
| [dynamodb](dynamodb.md) | Available | 7 | 12 | 0 | 0 | 10/19 |
| [kinesis](kinesis.md) | Available | 0 | 7 | 0 | 0 | 7/7 |
| [s3](s3.md) | Available | 27 | 23 | 7 | 17 | 15/74 |
| [secretsmanager](secretsmanager.md) | Available | 6 | 1 | 0 | 1 | 7/8 |
| [sns](sns.md) | Available | 0 | 14 | 0 | 0 | 12/14 |
| [sqs](sqs.md) | Available | 10 | 8 | 2 | 0 | 9/20 |

## Adoption decision

1. Find the closest workload pattern below.
2. Confirm every operation your application calls in the [coverage matrix](coverage.md).
3. Read each linked design gap and decide whether its workaround is acceptable.
4. Treat missing real-Azure seals as validation work required in your own staging environment.
5. Stop the migration when a required pattern is blocked; do not assume the proxy emulates it.

## Automated workload check

Create a versioned manifest that lists every AWS operation the application calls
and enables the contextual requirement IDs from the profiles below:

```yaml
schema_version: 1
workload: checkout
operations:
  - dynamodb:TransactWriteItems
  - sqs:SendMessage
requirements:
  cross_partition_transactions: true
```

Run a human-readable discovery report:

```bash
dotnet run --project tools/Aws2Azure.GapDocs -- check-workload workload.yaml
```

For CI, emit source-generated JSON and opt into a non-zero exit code when
the valid workload contains blockers:

```bash
dotnet run --project tools/Aws2Azure.GapDocs -- check-workload workload.yaml \
  --format json --output compatibility.json --fail-on-blocked
```

Exit code `0` means the report was produced, `1` means the manifest or command
was invalid, and `2` means `--fail-on-blocked` found at least one blocker.
A `conditional` result does not fail CI; its guidance and workarounds require
an explicit migration decision.

## dynamodb

| Workload pattern | Assessment | Operation coverage | Operation seals | Decision guidance | Requirement ID |
|---|---|---|---:|---|---|
| Basic table and item CRUD | 🟡 conditional | 3 implemented, 4 partial | 7/7 | Table lifecycle and item CRUD are translated, with expression, consistency, key-encoding, and Cosmos storage-model caveats.<br>Use a proxy-managed Cosmos database, keep keys within the documented limit, and select an account consistency level that matches the workload.<br>[PutItem](dynamodb.md#putitem) is partial<br>[GetItem](dynamodb.md#getitem) is partial<br>[UpdateItem](dynamodb.md#updateitem) is partial<br>[DeleteItem](dynamodb.md#deleteitem) is partial<br>[Design gap](design-gaps.md#dynamodb-consistency-and-read-your-writes): Consistency and read-your-writes<br>[Design gap](design-gaps.md#dynamodb-key-encoding-and-on-disk-storage-format): Key encoding and on-disk storage format | `dynamodb_basic_crud` |
| Query, Scan, and secondary-index reads | 🟡 conditional | 1 implemented, 2 partial | 2/3 | Query and Scan work for documented expression subsets; GSI/LSI reads add cross-partition, collation, ordering, and backfill caveats.<br>Validate every expression and index access pattern against representative data before migration.<br>[Query](dynamodb.md#query) is partial<br>[Scan](dynamodb.md#scan) is partial<br>[Design gap](design-gaps.md#dynamodb-secondary-indexes--gsi---lsi): Secondary indexes (GSI / LSI)<br>[Design gap](design-gaps.md#dynamodb-throughput-and-throttling-model): Throughput and throttling model | `dynamodb_query_scan_indexes` |
| Single-partition transactions | 🟡 conditional | 2 partial | 0/2 | TransactGetItems and TransactWriteItems can be atomic only when every item belongs to one table and one logical partition.<br>Co-locate all transaction items under one partition key and test the supported condition-expression subset.<br>[TransactGetItems](dynamodb.md#transactgetitems) is partial<br>[TransactWriteItems](dynamodb.md#transactwriteitems) is partial<br>[Design gap](design-gaps.md#dynamodb-transaction-scope-is-single-partition-single-table): Transaction scope is single-partition, single-table | `dynamodb_single_partition_transactions` |
| Cross-table or cross-partition transactions | ⛔ blocked | 2 partial | 0/2 | Cosmos stored procedures cannot reproduce DynamoDB ACID transactions spanning tables or logical partitions.<br>Remodel the data or implement idempotent application-level compensation before routing this workload through the proxy.<br>[TransactGetItems](dynamodb.md#transactgetitems) is partial<br>[TransactWriteItems](dynamodb.md#transactwriteitems) is partial<br>[Design gap](design-gaps.md#dynamodb-transaction-scope-is-single-partition-single-table): Transaction scope is single-partition, single-table | `cross_partition_transactions` |
| Streams, global tables, backups, DAX, or auto-scaling control plane | ⛔ blocked | Design-level requirement | — | These DynamoDB control-plane and streaming capabilities are outside the translated wire-protocol surface.<br>Integrate directly with the corresponding Azure capabilities outside the proxy.<br>[Design gap](design-gaps.md#dynamodb-absent-dynamodb-features): Absent DynamoDB features | `dynamodb_absent_features` |

## kinesis

| Workload pattern | Assessment | Operation coverage | Operation seals | Decision guidance | Requirement ID |
|---|---|---|---:|---|---|
| Basic record ingestion | 🟡 conditional | 2 partial | 2/2 | PutRecord and PutRecords publish to provisioned Event Hubs partitions, but ordering-related request fields and returned sequence numbers differ.<br>Use partition keys for routing and do not depend on SequenceNumberForOrdering or exact AWS sequence-number semantics.<br>[PutRecord](kinesis.md#putrecord) is partial<br>[PutRecords](kinesis.md#putrecords) is partial<br>[Design gap](design-gaps.md#kinesis-synthetic-sequence-numbers-and-iterator-positioning): Synthetic sequence numbers and iterator positioning | `kinesis_record_ingestion` |
| Single consumer per shard | 🟡 conditional | 3 partial | 3/3 | Shard discovery and polling work best with one consumer loop per partition and timestamp/latest iterator positioning.<br>Prefer TRIM_HORIZON, LATEST, or AT_TIMESTAMP and dedicate a consumer group to each independent consumer.<br>[ListShards](kinesis.md#listshards) is partial<br>[GetShardIterator](kinesis.md#getsharditerator) is partial<br>[GetRecords](kinesis.md#getrecords) is partial<br>[Design gap](design-gaps.md#kinesis-synthetic-sequence-numbers-and-iterator-positioning): Synthetic sequence numbers and iterator positioning<br>[Design gap](design-gaps.md#kinesis-iterator-link-lifetime-and-durable-replay): Iterator link lifetime and durable replay | `kinesis_single_consumer_per_shard` |
| Independent replaying consumers on one consumer group | ⛔ blocked | 2 partial | 2/2 | Distinct iterator identities progress independently in a live proxy, but multiple independently operated consumers sharing one Event Hubs consumer group are outside the certified ownership, restart, and replay contract.<br>Assign distinct Event Hubs consumer groups or redesign around one consumer per shard.<br>[GetShardIterator](kinesis.md#getsharditerator) is partial<br>[GetRecords](kinesis.md#getrecords) is partial<br>[Design gap](design-gaps.md#kinesis-iterator-link-lifetime-and-durable-replay): Iterator link lifetime and durable replay | `kinesis_shared_consumer_group_replay` |
| Resharding, enhanced fan-out, or KCL lease management | ⛔ blocked | Design-level requirement | — | Dynamic shard topology and enhanced fan-out have no equivalent in the exposed proxy surface.<br>Provision partitions for peak demand and manage consumer isolation with Event Hubs consumer groups.<br>[Design gap](design-gaps.md#kinesis-no-resharding---enhanced-fan-out---kcl-lease-model): No resharding / enhanced fan-out / KCL lease model | `kinesis_resharding_enhanced_fanout_kcl` |

## s3

| Workload pattern | Assessment | Operation coverage | Operation seals | Decision guidance | Requirement ID |
|---|---|---|---:|---|---|
| Basic object CRUD | ✅ supported | 5 implemented | 5/5 | Upload, download, inspect, list, and delete ordinary objects through the standard S3 data-plane operations.<br>Suitable when the application does not depend on AWS IAM, bucket policies, lifecycle configuration, or an AWS-specific encryption mode. | `s3_basic_object_crud` |
| Multipart upload | 🟡 conditional | 5 implemented | 0/5 | Multipart initiation, part upload/listing, completion, and abort are translated to Azure block operations.<br>Accept only if clients do not overwrite a part number with different content and then depend on CompleteMultipartUpload rejecting the old ETag.<br>[Design gap](design-gaps.md#s3-stateless-multipart-upload-without-per-part-etag-validation): Stateless multipart upload without per-part ETag validation | `s3_multipart_upload` |
| Versioning and object-lock administration | 🟡 conditional | 4 implemented, 3 partial, 2 unsupported | 7/9 | Object versions, retention, and legal holds are exposed, but account-level Blob features must already be configured and bucket-level lock setup has no data-plane equivalent.<br>Validate the exact flow against real Azure and configure Blob versioning and immutability outside the proxy before adoption.<br>[GetBucketVersioning](s3.md#getbucketversioning) is partial<br>[PutBucketVersioning](s3.md#putbucketversioning) is partial<br>[ListObjectVersions](s3.md#listobjectversions) is partial<br>[GetObjectLockConfiguration](s3.md#getobjectlockconfiguration) is unsupported<br>[PutObjectLockConfiguration](s3.md#putobjectlockconfiguration) is unsupported | `s3_versioning_object_lock` |
| Metadata and compatibility control round-trip | 🟡 conditional | 1 implemented, 17 partial | 2/18 | Bucket tags, versioning intent, ownership controls, public-access-block, and SSE-S3/AES256 intent persist in Azure container metadata and survive proxy restart; request payment and acceleration expose stable disabled contracts.<br>Use these operations only when applications require configuration round-trip compatibility. Enforce authorization, public access, encryption keys, billing, and acceleration through Azure/operator controls; do not treat persisted intent as enforcement.<br>[GetBucketTagging](s3.md#getbuckettagging) is partial<br>[PutBucketTagging](s3.md#putbuckettagging) is partial<br>[GetBucketVersioning](s3.md#getbucketversioning) is partial<br>[PutBucketVersioning](s3.md#putbucketversioning) is partial<br>[GetBucketOwnershipControls](s3.md#getbucketownershipcontrols) is partial<br>[PutBucketOwnershipControls](s3.md#putbucketownershipcontrols) is partial<br>[DeleteBucketOwnershipControls](s3.md#deletebucketownershipcontrols) is partial<br>[GetPublicAccessBlock](s3.md#getpublicaccessblock) is partial<br>[PutPublicAccessBlock](s3.md#putpublicaccessblock) is partial<br>[DeletePublicAccessBlock](s3.md#deletepublicaccessblock) is partial<br>[GetBucketEncryption](s3.md#getbucketencryption) is partial<br>[PutBucketEncryption](s3.md#putbucketencryption) is partial<br>[DeleteBucketEncryption](s3.md#deletebucketencryption) is partial<br>[GetBucketRequestPayment](s3.md#getbucketrequestpayment) is partial<br>[PutBucketRequestPayment](s3.md#putbucketrequestpayment) is partial<br>[GetBucketAccelerateConfiguration](s3.md#getbucketaccelerateconfiguration) is partial<br>[PutBucketAccelerateConfiguration](s3.md#putbucketaccelerateconfiguration) is partial<br>[Design gap](design-gaps.md#s3-no-iam---acl---bucket-policy-authorization-model): No IAM / ACL / bucket-policy authorization model<br>[Design gap](design-gaps.md#s3-no-enforceable-server-side-encryption-configuration-surface): No enforceable server-side-encryption configuration surface<br>[Design gap](design-gaps.md#s3-bucket-sub-resource-configs-are-not-translated): Bucket sub-resource configs are not translated | `s3_metadata_compatibility_controls` |
| Bucket policy, lifecycle, and event automation | ⛔ blocked | 5 stub, 10 unsupported | 0/15 | Workloads that configure authorization, lifecycle, replication, website, logging, or notifications through S3 APIs cannot preserve those semantics.<br>Move these controls to Azure RBAC, Storage lifecycle policies, object replication, static website settings, and Event Grid before migration.<br>[GetBucketPolicy](s3.md#getbucketpolicy) is unsupported<br>[PutBucketPolicy](s3.md#putbucketpolicy) is unsupported<br>[GetBucketLifecycleConfiguration](s3.md#getbucketlifecycleconfiguration) is unsupported<br>[PutBucketLifecycleConfiguration](s3.md#putbucketlifecycleconfiguration) is unsupported<br>[DeleteBucketLifecycle](s3.md#deletebucketlifecycle) is stub<br>[GetBucketWebsite](s3.md#getbucketwebsite) is unsupported<br>[PutBucketWebsite](s3.md#putbucketwebsite) is unsupported<br>[DeleteBucketWebsite](s3.md#deletebucketwebsite) is stub<br>[GetBucketLogging](s3.md#getbucketlogging) is stub<br>[PutBucketLogging](s3.md#putbucketlogging) is unsupported<br>[GetBucketNotificationConfiguration](s3.md#getbucketnotificationconfiguration) is stub<br>[PutBucketNotificationConfiguration](s3.md#putbucketnotificationconfiguration) is unsupported<br>[GetBucketReplication](s3.md#getbucketreplication) is unsupported<br>[PutBucketReplication](s3.md#putbucketreplication) is unsupported<br>[DeleteBucketReplication](s3.md#deletebucketreplication) is stub<br>[Design gap](design-gaps.md#s3-no-iam---acl---bucket-policy-authorization-model): No IAM / ACL / bucket-policy authorization model<br>[Design gap](design-gaps.md#s3-bucket-sub-resource-configs-are-not-translated): Bucket sub-resource configs are not translated | `s3_policy_lifecycle_automation` |

## secretsmanager

| Workload pattern | Assessment | Operation coverage | Operation seals | Decision guidance | Requirement ID |
|---|---|---|---:|---|---|
| Basic secret lifecycle | 🟡 conditional | 6 implemented | 6/6 | Create, inspect, retrieve, update, list, and delete secrets through the corresponding Key Vault data-plane APIs.<br>Suitable after configuring Entra ID authentication and validating Key Vault soft-delete behavior for the deployment.<br>[Design gap](design-gaps.md#secretsmanager-deletion-recovery-semantics-differ): Deletion recovery semantics differ | `secretsmanager_basic_lifecycle` |
| Version stages | 🟡 conditional | 2 implemented, 1 partial | 3/3 | AWS version stages are represented through Key Vault versions and tags rather than a native staging model.<br>Do not edit proxy-owned aws2azure-* tags; test rapid successive writes and handle bounded ResourceExistsException conflicts.<br>[PutSecretValue](secretsmanager.md#putsecretvalue) is partial<br>[Design gap](design-gaps.md#secretsmanager-versioning-and-staging-modelled-on-key-vault-version-tags): Versioning and staging modelled on Key Vault version tags | `secretsmanager_version_stages` |
| Lambda-driven managed rotation | ⛔ blocked | 1 unsupported | 0/1 | RotateSecret cannot execute an AWS Lambda-compatible rotation workflow.<br>Run rotation from an external Azure Function or pipeline that writes the new version through the proxy.<br>[RotateSecret](secretsmanager.md#rotatesecret) is unsupported<br>[Design gap](design-gaps.md#secretsmanager-rotation-has-no-lambda-equivalent): Rotation has no Lambda equivalent | `secretsmanager_lambda_rotation` |
| Resource policies and cross-account sharing | ⛔ blocked | Design-level requirement | — | Secrets Manager resource-policy and cross-account authorization semantics are not exposed.<br>Use Key Vault RBAC and separate bindings to enforce access.<br>[Design gap](design-gaps.md#secretsmanager-no-resource-policies-or-cross-account-access): No resource policies or cross-account access | `secretsmanager_resource_policies` |

## sns

| Workload pattern | Assessment | Operation coverage | Operation seals | Decision guidance | Requirement ID |
|---|---|---|---:|---|---|
| Standard topic publish | 🟡 conditional | 4 partial | 4/4 | Publish and PublishBatch route through Service Bus Topics or Event Grid, whose delivery and partial-failure semantics differ.<br>Select the backend deliberately and validate message shape, delivery, and partial failures against that backend.<br>[CreateTopic](sns.md#createtopic) is partial<br>[Publish](sns.md#publish) is partial<br>[PublishBatch](sns.md#publishbatch) is partial<br>[DeleteTopic](sns.md#deletetopic) is partial<br>[Design gap](design-gaps.md#sns-two-backends-with-different-fidelity): Two backends with different fidelity | `sns_standard_publish` |
| Subscription management | 🟡 conditional | 7 partial | 7/7 | Subscription lifecycle and attributes are translated through Service Bus management APIs with synthetic AWS identifiers.<br>Use this profile only with the Service Bus Topics backend, avoid parsing AWS account identity from returned ARNs, and provision Event Grid event subscriptions separately when Event Grid is selected for publishing.<br>[Subscribe](sns.md#subscribe) is partial<br>[ConfirmSubscription](sns.md#confirmsubscription) is partial<br>[ListSubscriptions](sns.md#listsubscriptions) is partial<br>[ListSubscriptionsByTopic](sns.md#listsubscriptionsbytopic) is partial<br>[GetSubscriptionAttributes](sns.md#getsubscriptionattributes) is partial<br>[SetSubscriptionAttributes](sns.md#setsubscriptionattributes) is partial<br>[Unsubscribe](sns.md#unsubscribe) is partial<br>[Design gap](design-gaps.md#sns-two-backends-with-different-fidelity): Two backends with different fidelity<br>[Design gap](design-gaps.md#sns-no-aws-region---account-namespace): No AWS region / account namespace<br>[Design gap](design-gaps.md#sns-event-grid-subscription-management-is-excluded): Event Grid subscription management is excluded | `sns_subscription_management` |
| FIFO topic ordering and deduplication | ⛔ blocked | 2 partial | 2/2 | Strict SNS FIFO ordering and deduplication are not reproduced end to end.<br>Use a Service Bus-native design when strict FIFO and duplicate detection are mandatory.<br>[Publish](sns.md#publish) is partial<br>[PublishBatch](sns.md#publishbatch) is partial<br>[Design gap](design-gaps.md#sns-fifo-topics-are-deferred): FIFO topics are deferred | `sns_fifo` |
| IAM delivery and redrive policy administration | ⛔ blocked | 2 partial | 1/2 | SNS policy attributes are accepted as no-ops and do not configure Azure delivery authorization or reliability.<br>Configure authorization, retry, and dead-letter behavior directly on the Azure backend.<br>[SetTopicAttributes](sns.md#settopicattributes) is partial<br>[SetSubscriptionAttributes](sns.md#setsubscriptionattributes) is partial<br>[Design gap](design-gaps.md#sns-no-iam-backed-policy-surface): No IAM-backed policy surface | `sns_policy_administration` |

## sqs

| Workload pattern | Assessment | Operation coverage | Operation seals | Decision guidance | Requirement ID |
|---|---|---|---:|---|---|
| Standard queue messaging | 🟡 conditional | 7 implemented | 7/7 | Create, discover, send, receive, settle, and delete standard queues and messages through the core SQS operations.<br>Suitable for standard queues after validating visibility timeout and dead-letter behavior with the selected Service Bus transport.<br>[Design gap](design-gaps.md#sqs-transport-dependent-capability-differences): Transport-dependent capability differences | `sqs_standard_messaging` |
| FIFO queue messaging | 🟡 conditional | 5 implemented | 5/5 | FIFO group ordering is available only through the AMQP transport and receipt settlement remains connection-affine.<br>Configure transport: Amqp and test the full receive/delete lifecycle under concurrency before production.<br>[Design gap](design-gaps.md#sqs-fifo-ordering-requires-the-amqp-transport): FIFO ordering requires the AMQP transport<br>[Design gap](design-gaps.md#sqs-transport-dependent-capability-differences): Transport-dependent capability differences | `sqs_fifo` |
| FIFO queue messaging over AMQP | 🟡 conditional | 7 implemented, 1 partial | 7/8 | FIFO ordering, explicit deduplication, ordered batches, and connection-affine settlement are isolated from standard-queue GA.<br>Configure transport: Amqp. Treat restart or session-link eviction as a stale receipt-handle boundary and wait for lock expiry before receiving the group again.<br>[ChangeMessageVisibility](sqs.md#changemessagevisibility) is partial<br>[Design gap](design-gaps.md#sqs-fifo-ordering-requires-the-amqp-transport): FIFO ordering requires the AMQP transport<br>[Design gap](design-gaps.md#sqs-transport-dependent-capability-differences): Transport-dependent capability differences | `sqs_fifo_amqp` |
| Dead-letter and redrive | 🟡 conditional | 6 implemented, 3 partial | 5/9 | RedrivePolicy, receive-to-DLQ flow, attribution, and restart-safe source pagination require separate real-Azure qualification. The AMQP receive path enforces maxReceiveCount before exposing Service Bus's first over-limit delivery and explicitly dead-letters it for forwarding; the legacy REST receive transport retains the native Service Bus boundary.<br>Validate the complete source-to-target redrive path and synthetic ARN handling in the deployment namespace.<br>[SetQueueAttributes](sqs.md#setqueueattributes) is partial<br>[GetQueueAttributes](sqs.md#getqueueattributes) is partial<br>[ChangeMessageVisibility](sqs.md#changemessagevisibility) is partial<br>[Design gap](design-gaps.md#sqs-no-aws-region---account-namespace): No AWS region / account namespace<br>[Design gap](design-gaps.md#sqs-transport-dependent-capability-differences): Transport-dependent capability differences | `sqs_dlq_redrive` |
| Hard queue purge | 🟡 conditional | 1 partial | 0/1 | PurgeQueue is emulated with a bounded drain and may leave messages while producers remain active.<br>Pause producers before purge when the workload requires a guaranteed empty queue.<br>[PurgeQueue](sqs.md#purgequeue) is partial<br>[Design gap](design-gaps.md#sqs-purgequeue-is-best-effort-emulation): PurgeQueue is best-effort emulation | `sqs_hard_purge` |
| Queue permission administration | ⛔ blocked | 2 stub | 0/2 | AddPermission and RemovePermission validate the queue but cannot reproduce the SQS IAM permission model.<br>Enforce access through Azure RBAC, SAS credentials, and network controls.<br>[AddPermission](sqs.md#addpermission) is stub<br>[RemovePermission](sqs.md#removepermission) is stub | `sqs_permission_administration` |

