# Maximum-viable completeness

This page separates feasible backlog from permanent AWS/Azure boundaries and explicit project non-goals.
It complements the raw [coverage matrix](coverage.md): status alone is **not** an AWS-parity claim.

Workload/profile maturity lives in [workload-compatibility](workload-compatibility.md) and [workload-ga](workload-ga.md).

## Service summary

| Service | Implemented | Partial | Stub | Unsupported | Feasible ops | By-design ops | Non-goal ops | Feasible sub-features | Feasible design gaps | Structural boundaries |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| [dynamodb](dynamodb.md) | 7 | 12 | 0 | 0 | 0 | 12 | 0 | 8 | 0 | 46 |
| [kinesis](kinesis.md) | 0 | 7 | 0 | 0 | 0 | 7 | 0 | 0 | 0 | 12 |
| [s3](s3.md) | 27 | 23 | 7 | 17 | 0 | 46 | 1 | 1 | 0 | 122 |
| [secretsmanager](secretsmanager.md) | 6 | 1 | 0 | 1 | 0 | 1 | 1 | 0 | 0 | 11 |
| [sns](sns.md) | 0 | 14 | 0 | 0 | 4 | 9 | 1 | 1 | 1 | 15 |
| [sqs](sqs.md) | 10 | 8 | 2 | 0 | 0 | 10 | 0 | 14 | 0 | 31 |

## dynamodb

| Status | Feasible backlog | By design | Non-goal |
|---|---:|---:|---:|
| ✅ implemented | 0 | 0 | 0 |
| 🟡 partial | 0 | 12 | 0 |
| ⚪ stub | 0 | 0 | 0 |
| ⛔ unsupported | 0 | 0 | 0 |

### Feasible backlog

- Sub-feature [DeleteItem](dynamodb.md#deleteitem) / ExpressionAttributeNames / ExpressionAttributeValues — [#687](https://github.com/pedrosakuma/aws2azure/issues/687)
- Sub-feature [DeleteItem](dynamodb.md#deleteitem) / ReturnValues — [#687](https://github.com/pedrosakuma/aws2azure/issues/687)
- Sub-feature [DescribeTable](dynamodb.md#describetable) / ItemCount / TableSizeBytes (live metrics) — [#688](https://github.com/pedrosakuma/aws2azure/issues/688)
- Sub-feature [DescribeTable](dynamodb.md#describetable) / GSI/LSI description — [#688](https://github.com/pedrosakuma/aws2azure/issues/688)
- Sub-feature [PutItem](dynamodb.md#putitem) / ExpressionAttributeNames / ExpressionAttributeValues — [#687](https://github.com/pedrosakuma/aws2azure/issues/687)
- Sub-feature [PutItem](dynamodb.md#putitem) / ReturnValues — [#687](https://github.com/pedrosakuma/aws2azure/issues/687)
- Sub-feature [TransactWriteItems](dynamodb.md#transactwriteitems) / Update — [#687](https://github.com/pedrosakuma/aws2azure/issues/687)
- Sub-feature [TransactWriteItems](dynamodb.md#transactwriteitems) / ReturnValuesOnConditionCheckFailure — [#687](https://github.com/pedrosakuma/aws2azure/issues/687)

### Workload maturity

5 workload pattern(s) are documented for this service. See [workload-compatibility](workload-compatibility.md#dynamodb) and [workload-ga](workload-ga.md).

### Structural boundaries

- Design gap [Absent DynamoDB features](design-gaps.md#dynamodb-absent-dynamodb-features) — ⚫ non-goal
- Design gap [Consistency and read-your-writes](design-gaps.md#dynamodb-consistency-and-read-your-writes) — 🔵 by design
- Design gap [Key encoding and on-disk storage format](design-gaps.md#dynamodb-key-encoding-and-on-disk-storage-format) — 🔵 by design
- Design gap [Secondary indexes (GSI / LSI)](design-gaps.md#dynamodb-secondary-indexes--gsi---lsi) — 🔵 by design
- Design gap [Throughput and throttling model](design-gaps.md#dynamodb-throughput-and-throttling-model) — 🔵 by design
- Design gap [Transaction execution has one configured Cosmos authority](design-gaps.md#dynamodb-transaction-execution-has-one-configured-cosmos-authority) — 🔵 by design
- Design gap [Transaction scope is single-partition, single-table](design-gaps.md#dynamodb-transaction-scope-is-single-partition-single-table) — 🔵 by design
- Operation [BatchGetItem](dynamodb.md#batchgetitem) — 🔵 by design
- Operation [BatchWriteItem](dynamodb.md#batchwriteitem) — 🔵 by design
- Operation [DeleteItem](dynamodb.md#deleteitem) — 🔵 by design
- Operation [DescribeTimeToLive](dynamodb.md#describetimetolive) — 🔵 by design
- Operation [GetItem](dynamodb.md#getitem) — 🔵 by design
- Operation [PutItem](dynamodb.md#putitem) — 🔵 by design
- Operation [Query](dynamodb.md#query) — 🔵 by design
- Operation [Scan](dynamodb.md#scan) — 🔵 by design
- Operation [TransactGetItems](dynamodb.md#transactgetitems) — 🔵 by design
- Operation [TransactWriteItems](dynamodb.md#transactwriteitems) — 🔵 by design
- Operation [UpdateItem](dynamodb.md#updateitem) — 🔵 by design
- Operation [UpdateTimeToLive](dynamodb.md#updatetimetolive) — 🔵 by design
- Sub-feature [BatchGetItem](dynamodb.md#batchgetitem) / Legacy AttributesToGet — ⚫ non-goal
- Sub-feature [BatchGetItem](dynamodb.md#batchgetitem) / ReturnConsumedCapacity — 🔵 by design
- Sub-feature [BatchWriteItem](dynamodb.md#batchwriteitem) / ReturnConsumedCapacity / ReturnItemCollectionMetrics — 🔵 by design
- Sub-feature [CreateTable](dynamodb.md#createtable) / GlobalSecondaryIndexes (schema accepted + persisted) — 🔵 by design
- Sub-feature [CreateTable](dynamodb.md#createtable) / LocalSecondaryIndexes (schema accepted + persisted) — 🔵 by design
- Sub-feature [CreateTable](dynamodb.md#createtable) / SSESpecification — 🔵 by design
- Sub-feature [CreateTable](dynamodb.md#createtable) / StreamSpecification — ⚫ non-goal
- Sub-feature [CreateTable](dynamodb.md#createtable) / Tags — ⚫ non-goal
- Sub-feature [DeleteItem](dynamodb.md#deleteitem) / ReturnConsumedCapacity / ReturnItemCollectionMetrics — 🔵 by design
- Sub-feature [GetItem](dynamodb.md#getitem) / AttributesToGet — ⚫ non-goal
- Sub-feature [GetItem](dynamodb.md#getitem) / ConsistentRead — 🔵 by design
- Sub-feature [GetItem](dynamodb.md#getitem) / ReturnConsumedCapacity — 🔵 by design
- Sub-feature [ListTagsOfResource](dynamodb.md#listtagsofresource) / Pagination — ⚫ non-goal
- Sub-feature [PutItem](dynamodb.md#putitem) / ReturnConsumedCapacity / ReturnItemCollectionMetrics — 🔵 by design
- Sub-feature [Query](dynamodb.md#query) / IndexName (GSI / LSI) — 🔵 by design
- Sub-feature [Query](dynamodb.md#query) / Legacy KeyConditions / QueryFilter / ConditionalOperator — ⚫ non-goal
- Sub-feature [Query](dynamodb.md#query) / ReturnConsumedCapacity — 🔵 by design
- Sub-feature [Query](dynamodb.md#query) / Select — 🔵 by design
- Sub-feature [Scan](dynamodb.md#scan) / IndexName (GSI / LSI) — 🔵 by design
- Sub-feature [Scan](dynamodb.md#scan) / Legacy ScanFilter / ConditionalOperator / AttributesToGet — ⚫ non-goal
- Sub-feature [Scan](dynamodb.md#scan) / Parallel scan (Segment / TotalSegments) — ⚫ non-goal
- Sub-feature [Scan](dynamodb.md#scan) / ReturnConsumedCapacity — 🔵 by design
- Sub-feature [Scan](dynamodb.md#scan) / Select — 🔵 by design
- Sub-feature [TransactGetItems](dynamodb.md#transactgetitems) / ReturnConsumedCapacity — 🔵 by design
- Sub-feature [TransactWriteItems](dynamodb.md#transactwriteitems) / ReturnConsumedCapacity / ReturnItemCollectionMetrics — 🔵 by design
- Sub-feature [TransactWriteItems](dynamodb.md#transactwriteitems) / Serialized transaction body limit — 🔵 by design
- Sub-feature [UpdateItem](dynamodb.md#updateitem) / ReturnConsumedCapacity / ReturnItemCollectionMetrics — 🔵 by design

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

- Design gap [Iterator link lifetime and durable replay](design-gaps.md#kinesis-iterator-link-lifetime-and-durable-replay) — 🔵 by design
- Design gap [No resharding / enhanced fan-out / KCL lease model](design-gaps.md#kinesis-no-resharding---enhanced-fan-out---kcl-lease-model) — 🔵 by design
- Design gap [Synthetic sequence numbers and iterator positioning](design-gaps.md#kinesis-synthetic-sequence-numbers-and-iterator-positioning) — 🔵 by design
- Operation [DescribeStreamSummary](kinesis.md#describestreamsummary) — 🔵 by design
- Operation [DescribeStream](kinesis.md#describestream) — 🔵 by design
- Operation [GetRecords](kinesis.md#getrecords) — 🔵 by design
- Operation [GetShardIterator](kinesis.md#getsharditerator) — 🔵 by design
- Operation [ListShards](kinesis.md#listshards) — 🔵 by design
- Operation [PutRecord](kinesis.md#putrecord) — 🔵 by design
- Operation [PutRecords](kinesis.md#putrecords) — 🔵 by design
- Sub-feature [ListShards](kinesis.md#listshards) / AT_TIMESTAMP shard filter — 🔵 by design
- Sub-feature [ListShards](kinesis.md#listshards) / AT_TRIM_HORIZON shard filter — 🔵 by design

## s3

| Status | Feasible backlog | By design | Non-goal |
|---|---:|---:|---:|
| ✅ implemented | 0 | 0 | 0 |
| 🟡 partial | 0 | 23 | 0 |
| ⚪ stub | 0 | 7 | 0 |
| ⛔ unsupported | 0 | 16 | 1 |

### Feasible backlog

- Sub-feature [CreateMultipartUpload](s3.md#createmultipartupload) / object-tagging — [#690](https://github.com/pedrosakuma/aws2azure/issues/690)

### Workload maturity

5 workload pattern(s) are documented for this service. See [workload-compatibility](workload-compatibility.md#s3) and [workload-ga](workload-ga.md).

### Structural boundaries

- Design gap [Bucket sub-resource configs are not translated](design-gaps.md#s3-bucket-sub-resource-configs-are-not-translated) — 🔵 by design
- Design gap [Multipart per-part ETag validation cannot be reproduced](design-gaps.md#s3-multipart-per-part-etag-validation-cannot-be-reproduced) — 🔵 by design
- Design gap [Multipart upload keeps bounded durable proxy state](design-gaps.md#s3-multipart-upload-keeps-bounded-durable-proxy-state) — 🔵 by design
- Design gap [No IAM / ACL / bucket-policy authorization model](design-gaps.md#s3-no-iam---acl---bucket-policy-authorization-model) — 🔵 by design
- Design gap [No enforceable server-side-encryption configuration surface](design-gaps.md#s3-no-enforceable-server-side-encryption-configuration-surface) — 🔵 by design
- Operation [DeleteBucketCors](s3.md#deletebucketcors) — 🔵 by design
- Operation [DeleteBucketEncryption](s3.md#deletebucketencryption) — 🔵 by design
- Operation [DeleteBucketLifecycle](s3.md#deletebucketlifecycle) — 🔵 by design
- Operation [DeleteBucketOwnershipControls](s3.md#deletebucketownershipcontrols) — 🔵 by design
- Operation [DeleteBucketPolicy](s3.md#deletebucketpolicy) — 🔵 by design
- Operation [DeleteBucketReplication](s3.md#deletebucketreplication) — 🔵 by design
- Operation [DeleteBucketWebsite](s3.md#deletebucketwebsite) — 🔵 by design
- Operation [DeletePublicAccessBlock](s3.md#deletepublicaccessblock) — 🔵 by design
- Operation [GetBucketAccelerateConfiguration](s3.md#getbucketaccelerateconfiguration) — 🔵 by design
- Operation [GetBucketAcl](s3.md#getbucketacl) — 🔵 by design
- Operation [GetBucketCors](s3.md#getbucketcors) — 🔵 by design
- Operation [GetBucketEncryption](s3.md#getbucketencryption) — 🔵 by design
- Operation [GetBucketLifecycleConfiguration](s3.md#getbucketlifecycleconfiguration) — 🔵 by design
- Operation [GetBucketLogging](s3.md#getbucketlogging) — 🔵 by design
- Operation [GetBucketNotificationConfiguration](s3.md#getbucketnotificationconfiguration) — 🔵 by design
- Operation [GetBucketOwnershipControls](s3.md#getbucketownershipcontrols) — 🔵 by design
- Operation [GetBucketPolicyStatus](s3.md#getbucketpolicystatus) — 🔵 by design
- Operation [GetBucketPolicy](s3.md#getbucketpolicy) — 🔵 by design
- Operation [GetBucketReplication](s3.md#getbucketreplication) — 🔵 by design
- Operation [GetBucketRequestPayment](s3.md#getbucketrequestpayment) — 🔵 by design
- Operation [GetBucketTagging](s3.md#getbuckettagging) — 🔵 by design
- Operation [GetBucketVersioning](s3.md#getbucketversioning) — 🔵 by design
- Operation [GetBucketWebsite](s3.md#getbucketwebsite) — 🔵 by design
- Operation [GetObjectAcl](s3.md#getobjectacl) — 🔵 by design
- Operation [GetObjectLockConfiguration](s3.md#getobjectlockconfiguration) — 🔵 by design
- Operation [GetObjectTorrent](s3.md#getobjecttorrent) — ⚫ non-goal
- Operation [GetPublicAccessBlock](s3.md#getpublicaccessblock) — 🔵 by design
- Operation [ListMultipartUploads](s3.md#listmultipartuploads) — 🔵 by design
- Operation [ListObjectVersions](s3.md#listobjectversions) — 🔵 by design
- Operation [PutBucketAccelerateConfiguration](s3.md#putbucketaccelerateconfiguration) — 🔵 by design
- Operation [PutBucketAcl](s3.md#putbucketacl) — 🔵 by design
- Operation [PutBucketCors](s3.md#putbucketcors) — 🔵 by design
- Operation [PutBucketEncryption](s3.md#putbucketencryption) — 🔵 by design
- Operation [PutBucketLifecycleConfiguration](s3.md#putbucketlifecycleconfiguration) — 🔵 by design
- Operation [PutBucketLogging](s3.md#putbucketlogging) — 🔵 by design
- Operation [PutBucketNotificationConfiguration](s3.md#putbucketnotificationconfiguration) — 🔵 by design
- Operation [PutBucketOwnershipControls](s3.md#putbucketownershipcontrols) — 🔵 by design
- Operation [PutBucketPolicy](s3.md#putbucketpolicy) — 🔵 by design
- Operation [PutBucketReplication](s3.md#putbucketreplication) — 🔵 by design
- Operation [PutBucketRequestPayment](s3.md#putbucketrequestpayment) — 🔵 by design
- Operation [PutBucketTagging](s3.md#putbuckettagging) — 🔵 by design
- Operation [PutBucketVersioning](s3.md#putbucketversioning) — 🔵 by design
- Operation [PutBucketWebsite](s3.md#putbucketwebsite) — 🔵 by design
- Operation [PutObjectAcl](s3.md#putobjectacl) — 🔵 by design
- Operation [PutObjectLockConfiguration](s3.md#putobjectlockconfiguration) — 🔵 by design
- Operation [PutPublicAccessBlock](s3.md#putpublicaccessblock) — 🔵 by design
- Operation [RestoreObject](s3.md#restoreobject) — 🔵 by design
- Sub-feature [CompleteMultipartUpload](s3.md#completemultipartupload) / per-part-etag-validation — 🔵 by design
- Sub-feature [CopyObject](s3.md#copyobject) / ARN copy-source (S3-on-Outposts) — ⚫ non-goal
- Sub-feature [CopyObject](s3.md#copyobject) / cross-account copy (source in a different Azure storage account) — 🔵 by design
- Sub-feature [CreateBucket](s3.md#createbucket) / CreateBucketConfiguration.LocationConstraint — 🔵 by design
- Sub-feature [CreateBucket](s3.md#createbucket) / ObjectLock / ObjectOwnership headers — 🔵 by design
- Sub-feature [CreateBucket](s3.md#createbucket) / x-amz-acl / x-amz-grant-* — 🔵 by design
- Sub-feature [CreateMultipartUpload](s3.md#createmultipartupload) / object-lock — 🔵 by design
- Sub-feature [CreateMultipartUpload](s3.md#createmultipartupload) / server-side-encryption — 🔵 by design
- Sub-feature [CreateMultipartUpload](s3.md#createmultipartupload) / storage-class — 🔵 by design
- Sub-feature [DeleteBucketEncryption](s3.md#deletebucketencryption) / Azure encryption change — 🔵 by design
- Sub-feature [DeleteBucketOwnershipControls](s3.md#deletebucketownershipcontrols) / authorization change — 🔵 by design
- Sub-feature [DeleteObject](s3.md#deleteobject) / MFA delete (x-amz-mfa) — 🔵 by design
- Sub-feature [DeleteObject](s3.md#deleteobject) / bypass-governance — 🔵 by design
- Sub-feature [DeleteObject](s3.md#deleteobject) / versioning (versionId query) — 🔵 by design
- Sub-feature [DeleteObjects](s3.md#deleteobjects) / mfa-delete — 🔵 by design
- Sub-feature [DeleteObjects](s3.md#deleteobjects) / versionid — 🔵 by design
- Sub-feature [DeletePublicAccessBlock](s3.md#deletepublicaccessblock) / public-access enforcement — 🔵 by design
- Sub-feature [GetBucketAccelerateConfiguration](s3.md#getbucketaccelerateconfiguration) / Enabled acceleration — 🔵 by design
- Sub-feature [GetBucketAcl](s3.md#getbucketacl) / non-owner grants — 🔵 by design
- Sub-feature [GetBucketCors](s3.md#getbucketcors) / configuration storage — 🔵 by design
- Sub-feature [GetBucketEncryption](s3.md#getbucketencryption) / SSE-KMS and SSE-C — 🔵 by design
- Sub-feature [GetBucketLifecycleConfiguration](s3.md#getbucketlifecycleconfiguration) / configuration storage — 🔵 by design
- Sub-feature [GetBucketLogging](s3.md#getbucketlogging) / configuration storage — 🔵 by design
- Sub-feature [GetBucketNotificationConfiguration](s3.md#getbucketnotificationconfiguration) / configuration storage — 🔵 by design
- Sub-feature [GetBucketOwnershipControls](s3.md#getbucketownershipcontrols) / ACL ownership enforcement — 🔵 by design
- Sub-feature [GetBucketPolicyStatus](s3.md#getbucketpolicystatus) / configuration storage — 🔵 by design
- Sub-feature [GetBucketPolicy](s3.md#getbucketpolicy) / configuration storage — 🔵 by design
- Sub-feature [GetBucketReplication](s3.md#getbucketreplication) / configuration storage — 🔵 by design
- Sub-feature [GetBucketRequestPayment](s3.md#getbucketrequestpayment) / Requester Pays — 🔵 by design
- Sub-feature [GetBucketTagging](s3.md#getbuckettagging) / server-side enforcement (cost allocation, IAM tag conditions) — 🔵 by design
- Sub-feature [GetBucketWebsite](s3.md#getbucketwebsite) / configuration storage — 🔵 by design
- Sub-feature [GetObjectAcl](s3.md#getobjectacl) / per-object grants — 🔵 by design
- Sub-feature [GetObjectLockConfiguration](s3.md#getobjectlockconfiguration) / configuration storage — 🔵 by design
- Sub-feature [GetObjectTorrent](s3.md#getobjecttorrent) / operation — ⚫ non-goal
- Sub-feature [GetObject](s3.md#getobject) / server-side encryption customer keys (SSE-C) — 🔵 by design
- Sub-feature [GetObject](s3.md#getobject) / versioning (versionId query) — 🔵 by design
- Sub-feature [GetPublicAccessBlock](s3.md#getpublicaccessblock) / public-access enforcement — 🔵 by design
- Sub-feature [HeadBucket](s3.md#headbucket) / x-amz-bucket-region — 🔵 by design
- Sub-feature [ListBuckets](s3.md#listbuckets) / owner-identity — 🔵 by design
- Sub-feature [ListObjectVersions](s3.md#listobjectversions) / delete markers — 🔵 by design
- Sub-feature [ListObjectsV2](s3.md#listobjectsv2) / fetch-owner — 🔵 by design
- Sub-feature [ListParts](s3.md#listparts) / encoding-type — 🔵 by design
- Sub-feature [ListParts](s3.md#listparts) / requester-pays — 🔵 by design
- Sub-feature [PresignedUrl](s3.md#presignedurl) / Azure Blob SAS issuance / redirect mode — 🔵 by design
- Sub-feature [PresignedUrl](s3.md#presignedurl) / X-Amz-Security-Token (STS session credentials) — 🔵 by design
- Sub-feature [PutBucketAccelerateConfiguration](s3.md#putbucketaccelerateconfiguration) / Enabled — 🔵 by design
- Sub-feature [PutBucketAcl](s3.md#putbucketacl) / other canned ACLs (public-read, public-read-write, log-delivery-write, …) — 🔵 by design
- Sub-feature [PutBucketAcl](s3.md#putbucketacl) / x-amz-grant-* headers and explicit ACL bodies — 🔵 by design
- Sub-feature [PutBucketCors](s3.md#putbucketcors) / configuration storage — 🔵 by design
- Sub-feature [PutBucketEncryption](s3.md#putbucketencryption) / SSE-C — 🔵 by design
- Sub-feature [PutBucketEncryption](s3.md#putbucketencryption) / SSE-KMS — 🔵 by design
- Sub-feature [PutBucketLifecycleConfiguration](s3.md#putbucketlifecycleconfiguration) / configuration storage — 🔵 by design
- Sub-feature [PutBucketLogging](s3.md#putbucketlogging) / configuration storage — 🔵 by design
- Sub-feature [PutBucketNotificationConfiguration](s3.md#putbucketnotificationconfiguration) / configuration storage — 🔵 by design
- Sub-feature [PutBucketOwnershipControls](s3.md#putbucketownershipcontrols) / ACL ownership enforcement — 🔵 by design
- Sub-feature [PutBucketPolicy](s3.md#putbucketpolicy) / configuration storage — 🔵 by design
- Sub-feature [PutBucketReplication](s3.md#putbucketreplication) / configuration storage — 🔵 by design
- Sub-feature [PutBucketRequestPayment](s3.md#putbucketrequestpayment) / Requester — 🔵 by design
- Sub-feature [PutBucketVersioning](s3.md#putbucketversioning) / MFADelete — 🔵 by design
- Sub-feature [PutBucketWebsite](s3.md#putbucketwebsite) / configuration storage — 🔵 by design
- Sub-feature [PutObjectAcl](s3.md#putobjectacl) / other canned ACLs / x-amz-grant-* headers / non-owner grants — 🔵 by design
- Sub-feature [PutObjectLockConfiguration](s3.md#putobjectlockconfiguration) / configuration storage — 🔵 by design
- Sub-feature [PutObject](s3.md#putobject) / ACLs (x-amz-acl) — 🔵 by design
- Sub-feature [PutObject](s3.md#putobject) / Object Lock / Legal Hold / Retention — 🔵 by design
- Sub-feature [PutObject](s3.md#putobject) / server-side encryption (SSE-S3, SSE-KMS, SSE-C) — 🔵 by design
- Sub-feature [PutObject](s3.md#putobject) / versioning (x-amz-version-id) — 🔵 by design
- Sub-feature [PutPublicAccessBlock](s3.md#putpublicaccessblock) / public-access enforcement — 🔵 by design
- Sub-feature [RestoreObject](s3.md#restoreobject) / operation — 🔵 by design
- Sub-feature [UploadPartCopy](s3.md#uploadpartcopy) / cross-account-copy — 🔵 by design
- Sub-feature [UploadPart](s3.md#uploadpart) / server-side-encryption-customer — 🔵 by design

## secretsmanager

| Status | Feasible backlog | By design | Non-goal |
|---|---:|---:|---:|
| ✅ implemented | 0 | 0 | 0 |
| 🟡 partial | 0 | 1 | 0 |
| ⚪ stub | 0 | 0 | 0 |
| ⛔ unsupported | 0 | 0 | 1 |

### Feasible backlog

_No remaining feasible backlog is documented for this service._

### Workload maturity

4 workload pattern(s) are documented for this service. See [workload-compatibility](workload-compatibility.md#secretsmanager) and [workload-ga](workload-ga.md).

### Structural boundaries

- Design gap [Deletion recovery semantics differ](design-gaps.md#secretsmanager-deletion-recovery-semantics-differ) — 🔵 by design
- Design gap [No resource policies or cross-account access](design-gaps.md#secretsmanager-no-resource-policies-or-cross-account-access) — 🔵 by design
- Design gap [Rotation has no Lambda equivalent](design-gaps.md#secretsmanager-rotation-has-no-lambda-equivalent) — ⚫ non-goal
- Design gap [Versioning and staging modelled on Key Vault version tags](design-gaps.md#secretsmanager-versioning-and-staging-modelled-on-key-vault-version-tags) — 🔵 by design
- Operation [PutSecretValue](secretsmanager.md#putsecretvalue) — 🔵 by design
- Operation [RotateSecret](secretsmanager.md#rotatesecret) — ⚫ non-goal
- Sub-feature [PutSecretValue](secretsmanager.md#putsecretvalue) / ClientRequestToken idempotency — 🔵 by design
- Sub-feature [PutSecretValue](secretsmanager.md#putsecretvalue) / VersionStages request labels — 🔵 by design
- Sub-feature [RotateSecret](secretsmanager.md#rotatesecret) / RotateImmediately / RotationRules / RotationLambdaARN — ⚫ non-goal
- Sub-feature [RotateSecret](secretsmanager.md#rotatesecret) / Rotation Lambda orchestration — ⚫ non-goal
- Sub-feature [UpdateSecret](secretsmanager.md#updatesecret) / Version durability and ClientRequestToken replay — 🔵 by design

## sns

| Status | Feasible backlog | By design | Non-goal |
|---|---:|---:|---:|
| ✅ implemented | 0 | 0 | 0 |
| 🟡 partial | 4 | 9 | 1 |
| ⚪ stub | 0 | 0 | 0 |
| ⛔ unsupported | 0 | 0 | 0 |

### Feasible backlog

- Operation [CreateTopic](sns.md#createtopic) — [#692](https://github.com/pedrosakuma/aws2azure/issues/692)
- Operation [DeleteTopic](sns.md#deletetopic) — [#692](https://github.com/pedrosakuma/aws2azure/issues/692)
- Operation [ListTopics](sns.md#listtopics) — [#692](https://github.com/pedrosakuma/aws2azure/issues/692)
- Operation [SetSubscriptionAttributes](sns.md#setsubscriptionattributes) — [#691](https://github.com/pedrosakuma/aws2azure/issues/691)
- Sub-feature [CreateTopic](sns.md#createtopic) / Attribute translation — [#691](https://github.com/pedrosakuma/aws2azure/issues/691)
- Design gap [FIFO topics are deferred](design-gaps.md#sns-fifo-topics-are-deferred) — [#692](https://github.com/pedrosakuma/aws2azure/issues/692)

### Workload maturity

4 workload pattern(s) are documented for this service. See [workload-compatibility](workload-compatibility.md#sns) and [workload-ga](workload-ga.md).

### Structural boundaries

- Design gap [Event Grid subscription management is excluded](design-gaps.md#sns-event-grid-subscription-management-is-excluded) — 🔵 by design
- Design gap [No AWS region / account namespace](design-gaps.md#sns-no-aws-region---account-namespace) — 🔵 by design
- Design gap [No IAM-backed policy surface](design-gaps.md#sns-no-iam-backed-policy-surface) — 🔵 by design
- Design gap [Two backends with different fidelity](design-gaps.md#sns-two-backends-with-different-fidelity) — 🔵 by design
- Operation [ConfirmSubscription](sns.md#confirmsubscription) — 🔵 by design
- Operation [GetSubscriptionAttributes](sns.md#getsubscriptionattributes) — 🔵 by design
- Operation [GetTopicAttributes](sns.md#gettopicattributes) — 🔵 by design
- Operation [ListSubscriptionsByTopic](sns.md#listsubscriptionsbytopic) — 🔵 by design
- Operation [ListSubscriptions](sns.md#listsubscriptions) — 🔵 by design
- Operation [PublishBatch](sns.md#publishbatch) — 🔵 by design
- Operation [Publish](sns.md#publish) — 🔵 by design
- Operation [SetTopicAttributes](sns.md#settopicattributes) — 🔵 by design
- Operation [Subscribe](sns.md#subscribe) — ⚫ non-goal
- Operation [Unsubscribe](sns.md#unsubscribe) — 🔵 by design
- Sub-feature [Subscribe](sns.md#subscribe) / Subscriber delivery forwarder — ⚫ non-goal

## sqs

| Status | Feasible backlog | By design | Non-goal |
|---|---:|---:|---:|
| ✅ implemented | 0 | 0 | 0 |
| 🟡 partial | 0 | 8 | 0 |
| ⚪ stub | 0 | 2 | 0 |
| ⛔ unsupported | 0 | 0 | 0 |

### Feasible backlog

- Sub-feature [CreateQueue](sqs.md#createqueue) / Attribute.DelaySeconds — [#693](https://github.com/pedrosakuma/aws2azure/issues/693)
- Sub-feature [CreateQueue](sqs.md#createqueue) / Attribute.ReceiveMessageWaitTimeSeconds — [#693](https://github.com/pedrosakuma/aws2azure/issues/693)
- Sub-feature [CreateQueue](sqs.md#createqueue) / tags — [#693](https://github.com/pedrosakuma/aws2azure/issues/693)
- Sub-feature [GetQueueAttributes](sqs.md#getqueueattributes) / Attribute.DelaySeconds — [#693](https://github.com/pedrosakuma/aws2azure/issues/693)
- Sub-feature [GetQueueAttributes](sqs.md#getqueueattributes) / Attribute.ReceiveMessageWaitTimeSeconds — [#693](https://github.com/pedrosakuma/aws2azure/issues/693)
- Sub-feature [GetQueueAttributes](sqs.md#getqueueattributes) / Attribute.ApproximateNumberOfMessagesNotVisible / Delayed — [#693](https://github.com/pedrosakuma/aws2azure/issues/693)
- Sub-feature [GetQueueAttributes](sqs.md#getqueueattributes) / Attribute.CreatedTimestamp / LastModifiedTimestamp — [#693](https://github.com/pedrosakuma/aws2azure/issues/693)
- Sub-feature [GetQueueAttributes](sqs.md#getqueueattributes) / Attribute.QueueArn — [#693](https://github.com/pedrosakuma/aws2azure/issues/693)
- Sub-feature [PurgeQueue](sqs.md#purgequeue) / 60s cool-down (PurgeQueueInProgress) — [#693](https://github.com/pedrosakuma/aws2azure/issues/693)
- Sub-feature [ReceiveMessage](sqs.md#receivemessage) / FIFO MessageGroupId session receive — [#694](https://github.com/pedrosakuma/aws2azure/issues/694)
- Sub-feature [SetQueueAttributes](sqs.md#setqueueattributes) / DelaySeconds (queue default) — [#693](https://github.com/pedrosakuma/aws2azure/issues/693)
- Sub-feature [SetQueueAttributes](sqs.md#setqueueattributes) / ReceiveMessageWaitTimeSeconds (queue default for long-poll) — [#693](https://github.com/pedrosakuma/aws2azure/issues/693)
- Sub-feature [TagQueue](sqs.md#tagqueue) / UserMetadata capacity guard — [#693](https://github.com/pedrosakuma/aws2azure/issues/693)
- Sub-feature [UntagQueue](sqs.md#untagqueue) / UserMetadata capacity guard — [#693](https://github.com/pedrosakuma/aws2azure/issues/693)

### Workload maturity

6 workload pattern(s) are documented for this service. See [workload-compatibility](workload-compatibility.md#sqs) and [workload-ga](workload-ga.md).

### Structural boundaries

- Design gap [FIFO ordering requires the AMQP transport](design-gaps.md#sqs-fifo-ordering-requires-the-amqp-transport) — 🔵 by design
- Design gap [No AWS region / account namespace](design-gaps.md#sqs-no-aws-region---account-namespace) — 🔵 by design
- Design gap [PurgeQueue is best-effort emulation](design-gaps.md#sqs-purgequeue-is-best-effort-emulation) — 🔵 by design
- Design gap [Queue lifecycle eventual-consistency](design-gaps.md#sqs-queue-lifecycle-eventual-consistency) — 🔵 by design
- Design gap [Transport-dependent capability differences](design-gaps.md#sqs-transport-dependent-capability-differences) — 🔵 by design
- Operation [AddPermission](sqs.md#addpermission) — 🔵 by design
- Operation [ChangeMessageVisibilityBatch](sqs.md#changemessagevisibilitybatch) — 🔵 by design
- Operation [ChangeMessageVisibility](sqs.md#changemessagevisibility) — 🔵 by design
- Operation [GetQueueAttributes](sqs.md#getqueueattributes) — 🔵 by design
- Operation [ListQueueTags](sqs.md#listqueuetags) — 🔵 by design
- Operation [PurgeQueue](sqs.md#purgequeue) — 🔵 by design
- Operation [RemovePermission](sqs.md#removepermission) — 🔵 by design
- Operation [SetQueueAttributes](sqs.md#setqueueattributes) — 🔵 by design
- Operation [TagQueue](sqs.md#tagqueue) — 🔵 by design
- Operation [UntagQueue](sqs.md#untagqueue) — 🔵 by design
- Sub-feature [AddPermission](sqs.md#addpermission) / Cross-account permission persistence — 🔵 by design
- Sub-feature [ChangeMessageVisibilityBatch](sqs.md#changemessagevisibilitybatch) / Renew semantics — 🔵 by design
- Sub-feature [ChangeMessageVisibility](sqs.md#changemessagevisibility) / Arbitrary new visibility duration — 🔵 by design
- Sub-feature [CreateQueue](sqs.md#createqueue) / Attribute.FifoQueue / ContentBasedDeduplication — 🔵 by design
- Sub-feature [CreateQueue](sqs.md#createqueue) / Attribute.KmsMasterKeyId / KmsDataKeyReusePeriodSeconds / SqsManagedSseEnabled — 🔵 by design
- Sub-feature [CreateQueue](sqs.md#createqueue) / Attribute.Policy — 🔵 by design
- Sub-feature [CreateQueue](sqs.md#createqueue) / Attribute.RedriveAllowPolicy — 🔵 by design
- Sub-feature [CreateQueue](sqs.md#createqueue) / Attribute.RedrivePolicy — 🔵 by design
- Sub-feature [DeleteMessage](sqs.md#deletemessage) / Idempotent behaviour on expired lock / already-deleted message — 🔵 by design
- Sub-feature [GetQueueAttributes](sqs.md#getqueueattributes) / Attribute.RedriveAllowPolicy — 🔵 by design
- Sub-feature [GetQueueUrl](sqs.md#getqueueurl) / QueueOwnerAWSAccountId — 🔵 by design
- Sub-feature [ReceiveMessage](sqs.md#receivemessage) / VisibilityTimeout parameter — 🔵 by design
- Sub-feature [RemovePermission](sqs.md#removepermission) / Permission removal by Label — 🔵 by design
- Sub-feature [SendMessage](sqs.md#sendmessage) / MessageSystemAttribute AWSTraceHeader — ⚫ non-goal
- Sub-feature [SetQueueAttributes](sqs.md#setqueueattributes) / Policy / KmsMasterKeyId / KmsDataKeyReusePeriodSeconds / SqsManagedSseEnabled — 🔵 by design
- Sub-feature [SetQueueAttributes](sqs.md#setqueueattributes) / RedrivePolicy → ForwardDeadLetteredMessagesTo — 🔵 by design

