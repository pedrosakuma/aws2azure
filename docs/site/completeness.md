# Maximum-viable completeness

This page separates feasible backlog from permanent AWS/Azure boundaries and explicit project non-goals.
It complements the raw [coverage matrix](coverage.md): status alone is **not** an AWS-parity claim.

Workload/profile maturity lives in [workload-compatibility](workload-compatibility.md) and [workload-ga](workload-ga.md).

## Service summary

| Service | Implemented | Partial | Stub | Unsupported | Feasible ops | By-design ops | Non-goal ops | Feasible sub-features | Feasible design gaps | Structural boundaries |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| [dynamodb](dynamodb.md) | 7 | 12 | 0 | 0 | 0 | 12 | 0 | 2 | 0 | 45 |
| [kinesis](kinesis.md) | 0 | 7 | 0 | 0 | 0 | 7 | 0 | 0 | 0 | 12 |
| [s3](s3.md) | 27 | 23 | 7 | 17 | 0 | 46 | 1 | 0 | 0 | 121 |
| [secretsmanager](secretsmanager.md) | 6 | 3 | 0 | 2 | 0 | 4 | 1 | 0 | 0 | 16 |
| [sns](sns.md) | 2 | 12 | 0 | 0 | 2 | 9 | 1 | 1 | 0 | 17 |
| [sqs](sqs.md) | 10 | 8 | 2 | 0 | 3 | 7 | 0 | 1 | 0 | 28 |

## dynamodb

| Status | Feasible backlog | By design | Non-goal |
|---|---:|---:|---:|
| ✅ implemented | 0 | 0 | 0 |
| 🟡 partial | 0 | 12 | 0 |
| ⚪ stub | 0 | 0 | 0 |
| ⛔ unsupported | 0 | 0 | 0 |

### Feasible backlog

- Sub-feature [TransactWriteItems](operations/dynamodb/transactwriteitems.md#sub-feature-update) / Update — [#798](https://github.com/pedrosakuma/aws2azure/issues/798)
- Sub-feature [TransactWriteItems](operations/dynamodb/transactwriteitems.md#sub-feature-returnvaluesonconditioncheckfailure) / ReturnValuesOnConditionCheckFailure — [#798](https://github.com/pedrosakuma/aws2azure/issues/798)

### Workload maturity

5 workload pattern(s) are documented for this service. See [workload-compatibility](workload-compatibility.md#dynamodb) and [workload-ga](workload-ga.md).

### Structural boundaries

- Design gap [Absent DynamoDB features](design-gaps/dynamodb/absent-dynamodb-features.md) — ⚫ non-goal
- Design gap [Consistency and read-your-writes](design-gaps/dynamodb/consistency-and-read-your-writes.md) — 🔵 by design
- Design gap [Key encoding and on-disk storage format](design-gaps/dynamodb/key-encoding-and-on-disk-storage-format.md) — 🔵 by design
- Design gap [Secondary indexes (GSI / LSI)](design-gaps/dynamodb/secondary-indexes--gsi---lsi.md) — 🔵 by design
- Design gap [Throughput and throttling model](design-gaps/dynamodb/throughput-and-throttling-model.md) — 🔵 by design
- Design gap [Transaction execution has one configured Cosmos authority](design-gaps/dynamodb/transaction-execution-has-one-configured-cosmos-authority.md) — 🔵 by design
- Design gap [Transaction scope is single-partition, single-table](design-gaps/dynamodb/transaction-scope-is-single-partition-single-table.md) — 🔵 by design
- Operation [BatchGetItem](operations/dynamodb/batchgetitem.md) — 🔵 by design
- Operation [BatchWriteItem](operations/dynamodb/batchwriteitem.md) — 🔵 by design
- Operation [DeleteItem](operations/dynamodb/deleteitem.md) — 🔵 by design
- Operation [DescribeTimeToLive](operations/dynamodb/describetimetolive.md) — 🔵 by design
- Operation [GetItem](operations/dynamodb/getitem.md) — 🔵 by design
- Operation [PutItem](operations/dynamodb/putitem.md) — 🔵 by design
- Operation [Query](operations/dynamodb/query.md) — 🔵 by design
- Operation [Scan](operations/dynamodb/scan.md) — 🔵 by design
- Operation [TransactGetItems](operations/dynamodb/transactgetitems.md) — 🔵 by design
- Operation [TransactWriteItems](operations/dynamodb/transactwriteitems.md) — 🔵 by design
- Operation [UpdateItem](operations/dynamodb/updateitem.md) — 🔵 by design
- Operation [UpdateTimeToLive](operations/dynamodb/updatetimetolive.md) — 🔵 by design
- Sub-feature [BatchGetItem](operations/dynamodb/batchgetitem.md#sub-feature-legacy-attributestoget) / Legacy AttributesToGet — ⚫ non-goal
- Sub-feature [BatchGetItem](operations/dynamodb/batchgetitem.md#sub-feature-returnconsumedcapacity) / ReturnConsumedCapacity — 🔵 by design
- Sub-feature [BatchWriteItem](operations/dynamodb/batchwriteitem.md#sub-feature-returnconsumedcapacity---returnitemcollectionmetrics) / ReturnConsumedCapacity / ReturnItemCollectionMetrics — 🔵 by design
- Sub-feature [CreateTable](operations/dynamodb/createtable.md#sub-feature-ssespecification) / SSESpecification — 🔵 by design
- Sub-feature [CreateTable](operations/dynamodb/createtable.md#sub-feature-streamspecification) / StreamSpecification — ⚫ non-goal
- Sub-feature [CreateTable](operations/dynamodb/createtable.md#sub-feature-tags) / Tags — ⚫ non-goal
- Sub-feature [DeleteItem](operations/dynamodb/deleteitem.md#sub-feature-returnconsumedcapacity---returnitemcollectionmetrics) / ReturnConsumedCapacity / ReturnItemCollectionMetrics — 🔵 by design
- Sub-feature [DescribeTable](operations/dynamodb/describetable.md#sub-feature-gsi-lsi-itemcount---indexsizebytes---backfilling---provisionedthroughput-description) / GSI/LSI ItemCount / IndexSizeBytes / Backfilling / ProvisionedThroughput description — 🔵 by design
- Sub-feature [GetItem](operations/dynamodb/getitem.md#sub-feature-attributestoget) / AttributesToGet — ⚫ non-goal
- Sub-feature [GetItem](operations/dynamodb/getitem.md#sub-feature-consistentread) / ConsistentRead — 🔵 by design
- Sub-feature [GetItem](operations/dynamodb/getitem.md#sub-feature-returnconsumedcapacity) / ReturnConsumedCapacity — 🔵 by design
- Sub-feature [ListTagsOfResource](operations/dynamodb/listtagsofresource.md#sub-feature-pagination) / Pagination — ⚫ non-goal
- Sub-feature [PutItem](operations/dynamodb/putitem.md#sub-feature-returnconsumedcapacity---returnitemcollectionmetrics) / ReturnConsumedCapacity / ReturnItemCollectionMetrics — 🔵 by design
- Sub-feature [Query](operations/dynamodb/query.md#sub-feature-indexname--gsi---lsi) / IndexName (GSI / LSI) — 🔵 by design
- Sub-feature [Query](operations/dynamodb/query.md#sub-feature-legacy-keyconditions---queryfilter---conditionaloperator) / Legacy KeyConditions / QueryFilter / ConditionalOperator — ⚫ non-goal
- Sub-feature [Query](operations/dynamodb/query.md#sub-feature-returnconsumedcapacity) / ReturnConsumedCapacity — 🔵 by design
- Sub-feature [Query](operations/dynamodb/query.md#sub-feature-select) / Select — 🔵 by design
- Sub-feature [Scan](operations/dynamodb/scan.md#sub-feature-indexname--gsi---lsi) / IndexName (GSI / LSI) — 🔵 by design
- Sub-feature [Scan](operations/dynamodb/scan.md#sub-feature-legacy-scanfilter---conditionaloperator---attributestoget) / Legacy ScanFilter / ConditionalOperator / AttributesToGet — ⚫ non-goal
- Sub-feature [Scan](operations/dynamodb/scan.md#sub-feature-parallel-scan--segment---totalsegments) / Parallel scan (Segment / TotalSegments) — ⚫ non-goal
- Sub-feature [Scan](operations/dynamodb/scan.md#sub-feature-returnconsumedcapacity) / ReturnConsumedCapacity — 🔵 by design
- Sub-feature [Scan](operations/dynamodb/scan.md#sub-feature-select) / Select — 🔵 by design
- Sub-feature [TransactGetItems](operations/dynamodb/transactgetitems.md#sub-feature-returnconsumedcapacity) / ReturnConsumedCapacity — 🔵 by design
- Sub-feature [TransactWriteItems](operations/dynamodb/transactwriteitems.md#sub-feature-returnconsumedcapacity---returnitemcollectionmetrics) / ReturnConsumedCapacity / ReturnItemCollectionMetrics — 🔵 by design
- Sub-feature [TransactWriteItems](operations/dynamodb/transactwriteitems.md#sub-feature-serialized-transaction-body-limit) / Serialized transaction body limit — 🔵 by design
- Sub-feature [UpdateItem](operations/dynamodb/updateitem.md#sub-feature-returnconsumedcapacity---returnitemcollectionmetrics) / ReturnConsumedCapacity / ReturnItemCollectionMetrics — 🔵 by design

## kinesis

| Status | Feasible backlog | By design | Non-goal |
|---|---:|---:|---:|
| ✅ implemented | 0 | 0 | 0 |
| 🟡 partial | 0 | 7 | 0 |
| ⚪ stub | 0 | 0 | 0 |
| ⛔ unsupported | 0 | 0 | 0 |

### Feasible backlog

_No remaining feasible backlog is documented for this service._

### Workload maturity

4 workload pattern(s) are documented for this service. See [workload-compatibility](workload-compatibility.md#kinesis) and [workload-ga](workload-ga.md).

### Structural boundaries

- Design gap [Iterator link lifetime and durable replay](design-gaps/kinesis/iterator-link-lifetime-and-durable-replay.md) — 🔵 by design
- Design gap [No resharding / enhanced fan-out / KCL lease model](design-gaps/kinesis/no-resharding---enhanced-fan-out---kcl-lease-model.md) — 🔵 by design
- Design gap [Synthetic sequence numbers and iterator positioning](design-gaps/kinesis/synthetic-sequence-numbers-and-iterator-positioning.md) — 🔵 by design
- Operation [DescribeStreamSummary](operations/kinesis/describestreamsummary.md) — 🔵 by design
- Operation [DescribeStream](operations/kinesis/describestream.md) — 🔵 by design
- Operation [GetRecords](operations/kinesis/getrecords.md) — 🔵 by design
- Operation [GetShardIterator](operations/kinesis/getsharditerator.md) — 🔵 by design
- Operation [ListShards](operations/kinesis/listshards.md) — 🔵 by design
- Operation [PutRecord](operations/kinesis/putrecord.md) — 🔵 by design
- Operation [PutRecords](operations/kinesis/putrecords.md) — 🔵 by design
- Sub-feature [ListShards](operations/kinesis/listshards.md#sub-feature-attimestamp-shard-filter) / AT_TIMESTAMP shard filter — 🔵 by design
- Sub-feature [ListShards](operations/kinesis/listshards.md#sub-feature-attrimhorizon-shard-filter) / AT_TRIM_HORIZON shard filter — 🔵 by design

## s3

| Status | Feasible backlog | By design | Non-goal |
|---|---:|---:|---:|
| ✅ implemented | 0 | 0 | 0 |
| 🟡 partial | 0 | 23 | 0 |
| ⚪ stub | 0 | 7 | 0 |
| ⛔ unsupported | 0 | 16 | 1 |

### Feasible backlog

_No remaining feasible backlog is documented for this service._

### Workload maturity

5 workload pattern(s) are documented for this service. See [workload-compatibility](workload-compatibility.md#s3) and [workload-ga](workload-ga.md).

### Structural boundaries

- Design gap [Bucket sub-resource configs are not translated](design-gaps/s3/bucket-sub-resource-configs-are-not-translated.md) — 🔵 by design
- Design gap [Multipart per-part ETag validation cannot be reproduced](design-gaps/s3/multipart-per-part-etag-validation-cannot-be-reproduced.md) — 🔵 by design
- Design gap [Multipart upload keeps bounded durable proxy state](design-gaps/s3/multipart-upload-keeps-bounded-durable-proxy-state.md) — 🔵 by design
- Design gap [No IAM / ACL / bucket-policy authorization model](design-gaps/s3/no-iam---acl---bucket-policy-authorization-model.md) — 🔵 by design
- Design gap [No enforceable server-side-encryption configuration surface](design-gaps/s3/no-enforceable-server-side-encryption-configuration-surface.md) — 🔵 by design
- Operation [DeleteBucketCors](operations/s3/deletebucketcors.md) — 🔵 by design
- Operation [DeleteBucketEncryption](operations/s3/deletebucketencryption.md) — 🔵 by design
- Operation [DeleteBucketLifecycle](operations/s3/deletebucketlifecycle.md) — 🔵 by design
- Operation [DeleteBucketOwnershipControls](operations/s3/deletebucketownershipcontrols.md) — 🔵 by design
- Operation [DeleteBucketPolicy](operations/s3/deletebucketpolicy.md) — 🔵 by design
- Operation [DeleteBucketReplication](operations/s3/deletebucketreplication.md) — 🔵 by design
- Operation [DeleteBucketWebsite](operations/s3/deletebucketwebsite.md) — 🔵 by design
- Operation [DeletePublicAccessBlock](operations/s3/deletepublicaccessblock.md) — 🔵 by design
- Operation [GetBucketAccelerateConfiguration](operations/s3/getbucketaccelerateconfiguration.md) — 🔵 by design
- Operation [GetBucketAcl](operations/s3/getbucketacl.md) — 🔵 by design
- Operation [GetBucketCors](operations/s3/getbucketcors.md) — 🔵 by design
- Operation [GetBucketEncryption](operations/s3/getbucketencryption.md) — 🔵 by design
- Operation [GetBucketLifecycleConfiguration](operations/s3/getbucketlifecycleconfiguration.md) — 🔵 by design
- Operation [GetBucketLogging](operations/s3/getbucketlogging.md) — 🔵 by design
- Operation [GetBucketNotificationConfiguration](operations/s3/getbucketnotificationconfiguration.md) — 🔵 by design
- Operation [GetBucketOwnershipControls](operations/s3/getbucketownershipcontrols.md) — 🔵 by design
- Operation [GetBucketPolicyStatus](operations/s3/getbucketpolicystatus.md) — 🔵 by design
- Operation [GetBucketPolicy](operations/s3/getbucketpolicy.md) — 🔵 by design
- Operation [GetBucketReplication](operations/s3/getbucketreplication.md) — 🔵 by design
- Operation [GetBucketRequestPayment](operations/s3/getbucketrequestpayment.md) — 🔵 by design
- Operation [GetBucketTagging](operations/s3/getbuckettagging.md) — 🔵 by design
- Operation [GetBucketVersioning](operations/s3/getbucketversioning.md) — 🔵 by design
- Operation [GetBucketWebsite](operations/s3/getbucketwebsite.md) — 🔵 by design
- Operation [GetObjectAcl](operations/s3/getobjectacl.md) — 🔵 by design
- Operation [GetObjectLockConfiguration](operations/s3/getobjectlockconfiguration.md) — 🔵 by design
- Operation [GetObjectTorrent](operations/s3/getobjecttorrent.md) — ⚫ non-goal
- Operation [GetPublicAccessBlock](operations/s3/getpublicaccessblock.md) — 🔵 by design
- Operation [ListMultipartUploads](operations/s3/listmultipartuploads.md) — 🔵 by design
- Operation [ListObjectVersions](operations/s3/listobjectversions.md) — 🔵 by design
- Operation [PutBucketAccelerateConfiguration](operations/s3/putbucketaccelerateconfiguration.md) — 🔵 by design
- Operation [PutBucketAcl](operations/s3/putbucketacl.md) — 🔵 by design
- Operation [PutBucketCors](operations/s3/putbucketcors.md) — 🔵 by design
- Operation [PutBucketEncryption](operations/s3/putbucketencryption.md) — 🔵 by design
- Operation [PutBucketLifecycleConfiguration](operations/s3/putbucketlifecycleconfiguration.md) — 🔵 by design
- Operation [PutBucketLogging](operations/s3/putbucketlogging.md) — 🔵 by design
- Operation [PutBucketNotificationConfiguration](operations/s3/putbucketnotificationconfiguration.md) — 🔵 by design
- Operation [PutBucketOwnershipControls](operations/s3/putbucketownershipcontrols.md) — 🔵 by design
- Operation [PutBucketPolicy](operations/s3/putbucketpolicy.md) — 🔵 by design
- Operation [PutBucketReplication](operations/s3/putbucketreplication.md) — 🔵 by design
- Operation [PutBucketRequestPayment](operations/s3/putbucketrequestpayment.md) — 🔵 by design
- Operation [PutBucketTagging](operations/s3/putbuckettagging.md) — 🔵 by design
- Operation [PutBucketVersioning](operations/s3/putbucketversioning.md) — 🔵 by design
- Operation [PutBucketWebsite](operations/s3/putbucketwebsite.md) — 🔵 by design
- Operation [PutObjectAcl](operations/s3/putobjectacl.md) — 🔵 by design
- Operation [PutObjectLockConfiguration](operations/s3/putobjectlockconfiguration.md) — 🔵 by design
- Operation [PutPublicAccessBlock](operations/s3/putpublicaccessblock.md) — 🔵 by design
- Operation [RestoreObject](operations/s3/restoreobject.md) — 🔵 by design
- Sub-feature [CompleteMultipartUpload](operations/s3/completemultipartupload.md#sub-feature-per-part-etag-validation) / per-part-etag-validation — 🔵 by design
- Sub-feature [CopyObject](operations/s3/copyobject.md#sub-feature-arn-copy-source--s3-on-outposts) / ARN copy-source (S3-on-Outposts) — ⚫ non-goal
- Sub-feature [CopyObject](operations/s3/copyobject.md#sub-feature-cross-account-copy--source-in-a-different-azure-storage-account) / cross-account copy (source in a different Azure storage account) — 🔵 by design
- Sub-feature [CreateBucket](operations/s3/createbucket.md#sub-feature-createbucketconfigurationlocationconstraint) / CreateBucketConfiguration.LocationConstraint — 🔵 by design
- Sub-feature [CreateBucket](operations/s3/createbucket.md#sub-feature-objectlock---objectownership-headers) / ObjectLock / ObjectOwnership headers — 🔵 by design
- Sub-feature [CreateBucket](operations/s3/createbucket.md#sub-feature-x-amz-acl---x-amz-grant) / x-amz-acl / x-amz-grant-* — 🔵 by design
- Sub-feature [CreateMultipartUpload](operations/s3/createmultipartupload.md#sub-feature-object-lock) / object-lock — 🔵 by design
- Sub-feature [CreateMultipartUpload](operations/s3/createmultipartupload.md#sub-feature-server-side-encryption) / server-side-encryption — 🔵 by design
- Sub-feature [CreateMultipartUpload](operations/s3/createmultipartupload.md#sub-feature-storage-class) / storage-class — 🔵 by design
- Sub-feature [DeleteBucketEncryption](operations/s3/deletebucketencryption.md#sub-feature-azure-encryption-change) / Azure encryption change — 🔵 by design
- Sub-feature [DeleteBucketOwnershipControls](operations/s3/deletebucketownershipcontrols.md#sub-feature-authorization-change) / authorization change — 🔵 by design
- Sub-feature [DeleteObject](operations/s3/deleteobject.md#sub-feature-bypass-governance) / bypass-governance — 🔵 by design
- Sub-feature [DeleteObject](operations/s3/deleteobject.md#sub-feature-mfa-delete--x-amz-mfa) / MFA delete (x-amz-mfa) — 🔵 by design
- Sub-feature [DeleteObject](operations/s3/deleteobject.md#sub-feature-versioning--versionid-query) / versioning (versionId query) — 🔵 by design
- Sub-feature [DeleteObjects](operations/s3/deleteobjects.md#sub-feature-mfa-delete) / mfa-delete — 🔵 by design
- Sub-feature [DeleteObjects](operations/s3/deleteobjects.md#sub-feature-versionid) / versionid — 🔵 by design
- Sub-feature [DeletePublicAccessBlock](operations/s3/deletepublicaccessblock.md#sub-feature-public-access-enforcement) / public-access enforcement — 🔵 by design
- Sub-feature [GetBucketAccelerateConfiguration](operations/s3/getbucketaccelerateconfiguration.md#sub-feature-enabled-acceleration) / Enabled acceleration — 🔵 by design
- Sub-feature [GetBucketAcl](operations/s3/getbucketacl.md#sub-feature-non-owner-grants) / non-owner grants — 🔵 by design
- Sub-feature [GetBucketCors](operations/s3/getbucketcors.md#sub-feature-configuration-storage) / configuration storage — 🔵 by design
- Sub-feature [GetBucketEncryption](operations/s3/getbucketencryption.md#sub-feature-sse-kms-and-sse-c) / SSE-KMS and SSE-C — 🔵 by design
- Sub-feature [GetBucketLifecycleConfiguration](operations/s3/getbucketlifecycleconfiguration.md#sub-feature-configuration-storage) / configuration storage — 🔵 by design
- Sub-feature [GetBucketLogging](operations/s3/getbucketlogging.md#sub-feature-configuration-storage) / configuration storage — 🔵 by design
- Sub-feature [GetBucketNotificationConfiguration](operations/s3/getbucketnotificationconfiguration.md#sub-feature-configuration-storage) / configuration storage — 🔵 by design
- Sub-feature [GetBucketOwnershipControls](operations/s3/getbucketownershipcontrols.md#sub-feature-acl-ownership-enforcement) / ACL ownership enforcement — 🔵 by design
- Sub-feature [GetBucketPolicyStatus](operations/s3/getbucketpolicystatus.md#sub-feature-configuration-storage) / configuration storage — 🔵 by design
- Sub-feature [GetBucketPolicy](operations/s3/getbucketpolicy.md#sub-feature-configuration-storage) / configuration storage — 🔵 by design
- Sub-feature [GetBucketReplication](operations/s3/getbucketreplication.md#sub-feature-configuration-storage) / configuration storage — 🔵 by design
- Sub-feature [GetBucketRequestPayment](operations/s3/getbucketrequestpayment.md#sub-feature-requester-pays) / Requester Pays — 🔵 by design
- Sub-feature [GetBucketTagging](operations/s3/getbuckettagging.md#sub-feature-server-side-enforcement--cost-allocation-iam-tag-conditions) / server-side enforcement (cost allocation, IAM tag conditions) — 🔵 by design
- Sub-feature [GetBucketWebsite](operations/s3/getbucketwebsite.md#sub-feature-configuration-storage) / configuration storage — 🔵 by design
- Sub-feature [GetObjectAcl](operations/s3/getobjectacl.md#sub-feature-per-object-grants) / per-object grants — 🔵 by design
- Sub-feature [GetObjectLockConfiguration](operations/s3/getobjectlockconfiguration.md#sub-feature-configuration-storage) / configuration storage — 🔵 by design
- Sub-feature [GetObjectTorrent](operations/s3/getobjecttorrent.md#sub-feature-operation) / operation — ⚫ non-goal
- Sub-feature [GetObject](operations/s3/getobject.md#sub-feature-server-side-encryption-customer-keys--sse-c) / server-side encryption customer keys (SSE-C) — 🔵 by design
- Sub-feature [GetObject](operations/s3/getobject.md#sub-feature-versioning--versionid-query) / versioning (versionId query) — 🔵 by design
- Sub-feature [GetPublicAccessBlock](operations/s3/getpublicaccessblock.md#sub-feature-public-access-enforcement) / public-access enforcement — 🔵 by design
- Sub-feature [ListBuckets](operations/s3/listbuckets.md#sub-feature-owner-identity) / owner-identity — 🔵 by design
- Sub-feature [ListObjectVersions](operations/s3/listobjectversions.md#sub-feature-delete-markers) / delete markers — 🔵 by design
- Sub-feature [ListObjectsV2](operations/s3/listobjectsv2.md#sub-feature-fetch-owner) / fetch-owner — 🔵 by design
- Sub-feature [ListParts](operations/s3/listparts.md#sub-feature-encoding-type) / encoding-type — 🔵 by design
- Sub-feature [ListParts](operations/s3/listparts.md#sub-feature-requester-pays) / requester-pays — 🔵 by design
- Sub-feature [PresignedUrl](operations/s3/presignedurl.md#sub-feature-azure-blob-sas-issuance---redirect-mode) / Azure Blob SAS issuance / redirect mode — 🔵 by design
- Sub-feature [PresignedUrl](operations/s3/presignedurl.md#sub-feature-x-amz-security-token--sts-session-credentials) / X-Amz-Security-Token (STS session credentials) — 🔵 by design
- Sub-feature [PutBucketAccelerateConfiguration](operations/s3/putbucketaccelerateconfiguration.md#sub-feature-enabled) / Enabled — 🔵 by design
- Sub-feature [PutBucketAcl](operations/s3/putbucketacl.md#sub-feature-other-canned-acls--public-read-public-read-write-log-delivery-write) / other canned ACLs (public-read, public-read-write, log-delivery-write, …) — 🔵 by design
- Sub-feature [PutBucketAcl](operations/s3/putbucketacl.md#sub-feature-x-amz-grant--headers-and-explicit-acl-bodies) / x-amz-grant-* headers and explicit ACL bodies — 🔵 by design
- Sub-feature [PutBucketCors](operations/s3/putbucketcors.md#sub-feature-configuration-storage) / configuration storage — 🔵 by design
- Sub-feature [PutBucketEncryption](operations/s3/putbucketencryption.md#sub-feature-sse-c) / SSE-C — 🔵 by design
- Sub-feature [PutBucketEncryption](operations/s3/putbucketencryption.md#sub-feature-sse-kms) / SSE-KMS — 🔵 by design
- Sub-feature [PutBucketLifecycleConfiguration](operations/s3/putbucketlifecycleconfiguration.md#sub-feature-configuration-storage) / configuration storage — 🔵 by design
- Sub-feature [PutBucketLogging](operations/s3/putbucketlogging.md#sub-feature-configuration-storage) / configuration storage — 🔵 by design
- Sub-feature [PutBucketNotificationConfiguration](operations/s3/putbucketnotificationconfiguration.md#sub-feature-configuration-storage) / configuration storage — 🔵 by design
- Sub-feature [PutBucketOwnershipControls](operations/s3/putbucketownershipcontrols.md#sub-feature-acl-ownership-enforcement) / ACL ownership enforcement — 🔵 by design
- Sub-feature [PutBucketPolicy](operations/s3/putbucketpolicy.md#sub-feature-configuration-storage) / configuration storage — 🔵 by design
- Sub-feature [PutBucketReplication](operations/s3/putbucketreplication.md#sub-feature-configuration-storage) / configuration storage — 🔵 by design
- Sub-feature [PutBucketRequestPayment](operations/s3/putbucketrequestpayment.md#sub-feature-requester) / Requester — 🔵 by design
- Sub-feature [PutBucketVersioning](operations/s3/putbucketversioning.md#sub-feature-mfadelete) / MFADelete — 🔵 by design
- Sub-feature [PutBucketWebsite](operations/s3/putbucketwebsite.md#sub-feature-configuration-storage) / configuration storage — 🔵 by design
- Sub-feature [PutObjectAcl](operations/s3/putobjectacl.md#sub-feature-other-canned-acls---x-amz-grant--headers---non-owner-grants) / other canned ACLs / x-amz-grant-* headers / non-owner grants — 🔵 by design
- Sub-feature [PutObjectLockConfiguration](operations/s3/putobjectlockconfiguration.md#sub-feature-configuration-storage) / configuration storage — 🔵 by design
- Sub-feature [PutObject](operations/s3/putobject.md#sub-feature-acls--x-amz-acl) / ACLs (x-amz-acl) — 🔵 by design
- Sub-feature [PutObject](operations/s3/putobject.md#sub-feature-object-lock---legal-hold---retention) / Object Lock / Legal Hold / Retention — 🔵 by design
- Sub-feature [PutObject](operations/s3/putobject.md#sub-feature-server-side-encryption--sse-s3-sse-kms-sse-c) / server-side encryption (SSE-S3, SSE-KMS, SSE-C) — 🔵 by design
- Sub-feature [PutObject](operations/s3/putobject.md#sub-feature-versioning--x-amz-version-id) / versioning (x-amz-version-id) — 🔵 by design
- Sub-feature [PutPublicAccessBlock](operations/s3/putpublicaccessblock.md#sub-feature-public-access-enforcement) / public-access enforcement — 🔵 by design
- Sub-feature [RestoreObject](operations/s3/restoreobject.md#sub-feature-operation) / operation — 🔵 by design
- Sub-feature [UploadPartCopy](operations/s3/uploadpartcopy.md#sub-feature-cross-account-copy) / cross-account-copy — 🔵 by design
- Sub-feature [UploadPart](operations/s3/uploadpart.md#sub-feature-server-side-encryption-customer) / server-side-encryption-customer — 🔵 by design

## secretsmanager

| Status | Feasible backlog | By design | Non-goal |
|---|---:|---:|---:|
| ✅ implemented | 0 | 0 | 0 |
| 🟡 partial | 0 | 3 | 0 |
| ⚪ stub | 0 | 0 | 0 |
| ⛔ unsupported | 0 | 1 | 1 |

### Feasible backlog

_No remaining feasible backlog is documented for this service._

### Workload maturity

4 workload pattern(s) are documented for this service. See [workload-compatibility](workload-compatibility.md#secretsmanager) and [workload-ga](workload-ga.md).

### Structural boundaries

- Design gap [Deletion recovery semantics differ](design-gaps/secretsmanager/deletion-recovery-semantics-differ.md) — 🔵 by design
- Design gap [No resource policies or cross-account access](design-gaps/secretsmanager/no-resource-policies-or-cross-account-access.md) — 🔵 by design
- Design gap [Rotation has no Lambda equivalent](design-gaps/secretsmanager/rotation-has-no-lambda-equivalent.md) — ⚫ non-goal
- Design gap [Synthetic ARNs use a proxy-specific namespace](design-gaps/secretsmanager/synthetic-arns-use-a-proxy-specific-namespace.md) — 🔵 by design
- Design gap [Versioning and staging modelled on Key Vault version tags](design-gaps/secretsmanager/versioning-and-staging-modelled-on-key-vault-version-tags.md) — 🔵 by design
- Operation [PutSecretValue](operations/secretsmanager/putsecretvalue.md) — 🔵 by design
- Operation [RotateSecret](operations/secretsmanager/rotatesecret.md) — ⚫ non-goal
- Operation [TagResource](operations/secretsmanager/tagresource.md) — 🔵 by design
- Operation [UntagResource](operations/secretsmanager/untagresource.md) — 🔵 by design
- Operation [UpdateSecretVersionStage](operations/secretsmanager/updatesecretversionstage.md) — 🔵 by design
- Sub-feature [PutSecretValue](operations/secretsmanager/putsecretvalue.md#sub-feature-clientrequesttoken-idempotency) / ClientRequestToken idempotency — 🔵 by design
- Sub-feature [PutSecretValue](operations/secretsmanager/putsecretvalue.md#sub-feature-versionstages-request-labels) / VersionStages request labels — 🔵 by design
- Sub-feature [RotateSecret](operations/secretsmanager/rotatesecret.md#sub-feature-rotateimmediately---rotationrules---rotationlambdaarn) / RotateImmediately / RotationRules / RotationLambdaARN — ⚫ non-goal
- Sub-feature [RotateSecret](operations/secretsmanager/rotatesecret.md#sub-feature-rotation-lambda-orchestration) / Rotation Lambda orchestration — ⚫ non-goal
- Sub-feature [UpdateSecretVersionStage](operations/secretsmanager/updatesecretversionstage.md#sub-feature-standalone-stage-label-mutation-api) / Standalone stage-label mutation API — 🔵 by design
- Sub-feature [UpdateSecret](operations/secretsmanager/updatesecret.md#sub-feature-version-durability-and-clientrequesttoken-replay) / Version durability and ClientRequestToken replay — 🔵 by design

## sns

| Status | Feasible backlog | By design | Non-goal |
|---|---:|---:|---:|
| ✅ implemented | 0 | 0 | 0 |
| 🟡 partial | 2 | 9 | 1 |
| ⚪ stub | 0 | 0 | 0 |
| ⛔ unsupported | 0 | 0 | 0 |

### Feasible backlog

- Operation [CreateTopic](operations/sns/createtopic.md) — [#800](https://github.com/pedrosakuma/aws2azure/issues/800)
- Operation [SetSubscriptionAttributes](operations/sns/setsubscriptionattributes.md) — [#800](https://github.com/pedrosakuma/aws2azure/issues/800)
- Sub-feature [SetSubscriptionAttributes](operations/sns/setsubscriptionattributes.md#sub-feature-service-bus-rule-translation-for-supported-filter-policies) / Service Bus rule translation for supported filter policies — [#800](https://github.com/pedrosakuma/aws2azure/issues/800)

### Workload maturity

4 workload pattern(s) are documented for this service. See [workload-compatibility](workload-compatibility.md#sns) and [workload-ga](workload-ga.md).

### Structural boundaries

- Design gap [Event Grid subscription management is excluded](design-gaps/sns/event-grid-subscription-management-is-excluded.md) — 🔵 by design
- Design gap [FIFO topics are deferred](design-gaps/sns/fifo-topics-are-deferred.md) — 🔵 by design
- Design gap [No AWS region / account namespace](design-gaps/sns/no-aws-region---account-namespace.md) — 🔵 by design
- Design gap [No IAM-backed policy surface](design-gaps/sns/no-iam-backed-policy-surface.md) — 🔵 by design
- Design gap [Two backends with different fidelity](design-gaps/sns/two-backends-with-different-fidelity.md) — 🔵 by design
- Operation [ConfirmSubscription](operations/sns/confirmsubscription.md) — 🔵 by design
- Operation [GetSubscriptionAttributes](operations/sns/getsubscriptionattributes.md) — 🔵 by design
- Operation [GetTopicAttributes](operations/sns/gettopicattributes.md) — 🔵 by design
- Operation [ListSubscriptionsByTopic](operations/sns/listsubscriptionsbytopic.md) — 🔵 by design
- Operation [ListSubscriptions](operations/sns/listsubscriptions.md) — 🔵 by design
- Operation [PublishBatch](operations/sns/publishbatch.md) — 🔵 by design
- Operation [Publish](operations/sns/publish.md) — 🔵 by design
- Operation [SetTopicAttributes](operations/sns/settopicattributes.md) — 🔵 by design
- Operation [Subscribe](operations/sns/subscribe.md) — ⚫ non-goal
- Operation [Unsubscribe](operations/sns/unsubscribe.md) — 🔵 by design
- Sub-feature [CreateTopic](operations/sns/createtopic.md#sub-feature-azure-service-bus-topic-path-naming-restriction-surfaced-separately-from-aws-side-validation) / Azure Service Bus topic-path naming restriction surfaced separately from AWS-side validation — 🔵 by design
- Sub-feature [Subscribe](operations/sns/subscribe.md#sub-feature-subscriber-delivery-forwarder) / Subscriber delivery forwarder — ⚫ non-goal

## sqs

| Status | Feasible backlog | By design | Non-goal |
|---|---:|---:|---:|
| ✅ implemented | 0 | 0 | 0 |
| 🟡 partial | 3 | 5 | 0 |
| ⚪ stub | 0 | 2 | 0 |
| ⛔ unsupported | 0 | 0 | 0 |

### Feasible backlog

- Operation [ListQueueTags](operations/sqs/listqueuetags.md) — [#801](https://github.com/pedrosakuma/aws2azure/issues/801)
- Operation [TagQueue](operations/sqs/tagqueue.md) — [#801](https://github.com/pedrosakuma/aws2azure/issues/801)
- Operation [UntagQueue](operations/sqs/untagqueue.md) — [#801](https://github.com/pedrosakuma/aws2azure/issues/801)
- Sub-feature [PurgeQueue](operations/sqs/purgequeue.md#sub-feature-60s-cool-down--purgequeueinprogress) / 60s cool-down (PurgeQueueInProgress) — [#801](https://github.com/pedrosakuma/aws2azure/issues/801)

### Workload maturity

6 workload pattern(s) are documented for this service. See [workload-compatibility](workload-compatibility.md#sqs) and [workload-ga](workload-ga.md).

### Structural boundaries

- Design gap [FIFO ordering requires the AMQP transport](design-gaps/sqs/fifo-ordering-requires-the-amqp-transport.md) — 🔵 by design
- Design gap [No AWS region / account namespace](design-gaps/sqs/no-aws-region---account-namespace.md) — 🔵 by design
- Design gap [PurgeQueue is best-effort emulation](design-gaps/sqs/purgequeue-is-best-effort-emulation.md) — 🔵 by design
- Design gap [Queue lifecycle eventual-consistency](design-gaps/sqs/queue-lifecycle-eventual-consistency.md) — 🔵 by design
- Design gap [Transport-dependent capability differences](design-gaps/sqs/transport-dependent-capability-differences.md) — 🔵 by design
- Operation [AddPermission](operations/sqs/addpermission.md) — 🔵 by design
- Operation [ChangeMessageVisibilityBatch](operations/sqs/changemessagevisibilitybatch.md) — 🔵 by design
- Operation [ChangeMessageVisibility](operations/sqs/changemessagevisibility.md) — 🔵 by design
- Operation [GetQueueAttributes](operations/sqs/getqueueattributes.md) — 🔵 by design
- Operation [PurgeQueue](operations/sqs/purgequeue.md) — 🔵 by design
- Operation [RemovePermission](operations/sqs/removepermission.md) — 🔵 by design
- Operation [SetQueueAttributes](operations/sqs/setqueueattributes.md) — 🔵 by design
- Sub-feature [AddPermission](operations/sqs/addpermission.md#sub-feature-cross-account-permission-persistence) / Cross-account permission persistence — 🔵 by design
- Sub-feature [ChangeMessageVisibilityBatch](operations/sqs/changemessagevisibilitybatch.md#sub-feature-renew-semantics) / Renew semantics — 🔵 by design
- Sub-feature [ChangeMessageVisibility](operations/sqs/changemessagevisibility.md#sub-feature-arbitrary-new-visibility-duration) / Arbitrary new visibility duration — 🔵 by design
- Sub-feature [CreateQueue](operations/sqs/createqueue.md#sub-feature-attributefifoqueue---contentbaseddeduplication) / Attribute.FifoQueue / ContentBasedDeduplication — 🔵 by design
- Sub-feature [CreateQueue](operations/sqs/createqueue.md#sub-feature-attributekmsmasterkeyid---kmsdatakeyreuseperiodseconds---sqsmanagedsseenabled) / Attribute.KmsMasterKeyId / KmsDataKeyReusePeriodSeconds / SqsManagedSseEnabled — 🔵 by design
- Sub-feature [CreateQueue](operations/sqs/createqueue.md#sub-feature-attributepolicy) / Attribute.Policy — 🔵 by design
- Sub-feature [CreateQueue](operations/sqs/createqueue.md#sub-feature-attributeredriveallowpolicy) / Attribute.RedriveAllowPolicy — 🔵 by design
- Sub-feature [CreateQueue](operations/sqs/createqueue.md#sub-feature-attributeredrivepolicy) / Attribute.RedrivePolicy — 🔵 by design
- Sub-feature [DeleteMessage](operations/sqs/deletemessage.md#sub-feature-idempotent-behaviour-on-expired-lock---already-deleted-message) / Idempotent behaviour on expired lock / already-deleted message — 🔵 by design
- Sub-feature [GetQueueAttributes](operations/sqs/getqueueattributes.md#sub-feature-attributeredriveallowpolicy) / Attribute.RedriveAllowPolicy — 🔵 by design
- Sub-feature [GetQueueUrl](operations/sqs/getqueueurl.md#sub-feature-queueownerawsaccountid) / QueueOwnerAWSAccountId — 🔵 by design
- Sub-feature [ReceiveMessage](operations/sqs/receivemessage.md#sub-feature-visibilitytimeout-parameter) / VisibilityTimeout parameter — 🔵 by design
- Sub-feature [RemovePermission](operations/sqs/removepermission.md#sub-feature-permission-removal-by-label) / Permission removal by Label — 🔵 by design
- Sub-feature [SendMessage](operations/sqs/sendmessage.md#sub-feature-messagesystemattribute-awstraceheader) / MessageSystemAttribute AWSTraceHeader — ⚫ non-goal
- Sub-feature [SetQueueAttributes](operations/sqs/setqueueattributes.md#sub-feature-policy---kmsmasterkeyid---kmsdatakeyreuseperiodseconds---sqsmanagedsseenabled) / Policy / KmsMasterKeyId / KmsDataKeyReusePeriodSeconds / SqsManagedSseEnabled — 🔵 by design
- Sub-feature [SetQueueAttributes](operations/sqs/setqueueattributes.md#sub-feature-redrivepolicy--forwarddeadletteredmessagesto) / RedrivePolicy → ForwardDeadLetteredMessagesTo — 🔵 by design

