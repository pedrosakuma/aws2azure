# Coverage matrix

For adoption decisions, start with the generated [workload compatibility](workload-compatibility.md) guide.

| Service | Operation | Status | Disposition | Tracking | Real-Azure | Azure equivalent |
|---|---|---|---|---|---|---|
| dynamodb | [BatchGetItem](dynamodb.md#batchgetitem) | 🟡 partial | 🔵 by design | — | ✅ | `Azure Cosmos DB (Core SQL API)` |
| dynamodb | [BatchWriteItem](dynamodb.md#batchwriteitem) | 🟡 partial | 🔵 by design | — | ✅ | `Azure Cosmos DB (Core SQL API)` |
| dynamodb | [CreateTable](dynamodb.md#createtable) | ✅ implemented | — | — | ✅ | `Azure Cosmos DB (Core SQL API) — POST /dbs/{db}/colls` |
| dynamodb | [DeleteItem](dynamodb.md#deleteitem) | 🟡 partial | 🔵 by design | — | — | `Azure Cosmos DB (Core SQL API)` |
| dynamodb | [DeleteTable](dynamodb.md#deletetable) | ✅ implemented | — | — | ✅ | `Azure Cosmos DB (Core SQL API) — DELETE /dbs/{db}/colls/{name}` |
| dynamodb | [DescribeTable](dynamodb.md#describetable) | ✅ implemented | — | — | ✅ | `Azure Cosmos DB (Core SQL API) — GET /dbs/{db}/colls/{name} + sidecar metadata` |
| dynamodb | [DescribeTimeToLive](dynamodb.md#describetimetolive) | 🟡 partial | 🔵 by design | — | — | `Azure Cosmos DB container `defaultTtl` / per-item `ttl`` |
| dynamodb | [GetItem](dynamodb.md#getitem) | 🟡 partial | 🔵 by design | — | ✅ | `Azure Cosmos DB (Core SQL API)` |
| dynamodb | [ListTables](dynamodb.md#listtables) | ✅ implemented | — | — | — | `Azure Cosmos DB (Core SQL API) — GET /dbs/{db}/colls` |
| dynamodb | [ListTagsOfResource](dynamodb.md#listtagsofresource) | ✅ implemented | — | — | ✅ | `Azure Cosmos DB account/resource tags (control plane)` |
| dynamodb | [PutItem](dynamodb.md#putitem) | 🟡 partial | 🔵 by design | — | — | `Azure Cosmos DB (Core SQL API)` |
| dynamodb | [Query](dynamodb.md#query) | 🟡 partial | 🔵 by design | — | ✅ | `Azure Cosmos DB (Core SQL API)` |
| dynamodb | [Scan](dynamodb.md#scan) | 🟡 partial | 🔵 by design | — | ✅ | `Azure Cosmos DB (Core SQL API)` |
| dynamodb | [TagResource](dynamodb.md#tagresource) | ✅ implemented | — | — | ✅ | `Azure Cosmos DB account/resource tags (control plane)` |
| dynamodb | [TransactGetItems](dynamodb.md#transactgetitems) | 🟡 partial | 🔵 by design | — | ✅ | `Azure Cosmos DB (Core SQL API) — single-partition read-only stored-procedure snapshot` |
| dynamodb | [TransactWriteItems](dynamodb.md#transactwriteitems) | 🟡 partial | 🔵 by design | — | ✅ | `Azure Cosmos DB (Core SQL API) — single-partition stored-procedure transaction` |
| dynamodb | [UntagResource](dynamodb.md#untagresource) | ✅ implemented | — | — | ✅ | `Azure Cosmos DB account/resource tags (control plane)` |
| dynamodb | [UpdateItem](dynamodb.md#updateitem) | 🟡 partial | 🔵 by design | — | ✅ | `Azure Cosmos DB (Core SQL API)` |
| dynamodb | [UpdateTimeToLive](dynamodb.md#updatetimetolive) | 🟡 partial | 🔵 by design | — | — | `Azure Cosmos DB container `defaultTtl` / per-item `ttl`` |
| kinesis | [DescribeStream](kinesis.md#describestream) | 🟡 partial | 🔵 by design | — | ✅ | `Azure Event Hubs Service Bus management REST API` |
| kinesis | [DescribeStreamSummary](kinesis.md#describestreamsummary) | 🟡 partial | 🔵 by design | — | ✅ | `Azure Event Hubs Service Bus management REST API` |
| kinesis | [GetRecords](kinesis.md#getrecords) | 🟡 partial | 🔵 by design | — | ✅ | `Azure Event Hubs (AMQP 1.0 data plane)` |
| kinesis | [GetShardIterator](kinesis.md#getsharditerator) | 🟡 partial | 🔵 by design | — | ✅ | `Azure Event Hubs (AMQP 1.0 data plane)` |
| kinesis | [ListShards](kinesis.md#listshards) | 🟡 partial | 🔵 by design | — | ✅ | `Azure Event Hubs Service Bus management REST API` |
| kinesis | [PutRecord](kinesis.md#putrecord) | 🟡 partial | 🔵 by design | — | ✅ | `Azure Event Hubs (AMQP 1.0 data plane)` |
| kinesis | [PutRecords](kinesis.md#putrecords) | 🟡 partial | 🔵 by design | — | ✅ | `Azure Event Hubs (AMQP 1.0 data plane)` |
| s3 | [AbortMultipartUpload](s3.md#abortmultipartupload) | ✅ implemented | — | — | ✅ | `Lease state record + delete proxy-owned multipart state blob` |
| s3 | [CompleteMultipartUpload](s3.md#completemultipartupload) | ✅ implemented | — | — | ✅ | `Lease state record + Put Block List` |
| s3 | [CopyObject](s3.md#copyobject) | ✅ implemented | — | — | ✅ | `PUT https://{account}.blob.core.windows.net/{container}/{blob} with x-ms-copy-source` |
| s3 | [CreateBucket](s3.md#createbucket) | ✅ implemented | — | — | ✅ | `PUT https://{account}.blob.core.windows.net/{container}?restype=container` |
| s3 | [CreateMultipartUpload](s3.md#createmultipartupload) | ✅ implemented | — | — | ✅ | `HEAD container + proxy-owned durable multipart state record` |
| s3 | [DeleteBucket](s3.md#deletebucket) | ✅ implemented | — | — | ✅ | `DELETE https://{account}.blob.core.windows.net/{container}?restype=container` |
| s3 | [DeleteBucketCors](s3.md#deletebucketcors) | ⚪ stub | 🔵 by design | — | — | `(no equivalent — proxy treats it as a no-op)` |
| s3 | [DeleteBucketEncryption](s3.md#deletebucketencryption) | 🟡 partial | 🔵 by design | — | — | `Conditional container-metadata update` |
| s3 | [DeleteBucketLifecycle](s3.md#deletebucketlifecycle) | ⚪ stub | 🔵 by design | — | — | `(no equivalent — proxy treats it as a no-op)` |
| s3 | [DeleteBucketOwnershipControls](s3.md#deletebucketownershipcontrols) | 🟡 partial | 🔵 by design | — | — | `Conditional container-metadata update` |
| s3 | [DeleteBucketPolicy](s3.md#deletebucketpolicy) | ⚪ stub | 🔵 by design | — | — | `(no equivalent — proxy treats it as a no-op)` |
| s3 | [DeleteBucketReplication](s3.md#deletebucketreplication) | ⚪ stub | 🔵 by design | — | — | `(no equivalent — proxy treats it as a no-op)` |
| s3 | [DeleteBucketTagging](s3.md#deletebuckettagging) | ✅ implemented | — | — | ✅ | `Conditional GET + PUT {container}?restype=container&comp=metadata` |
| s3 | [DeleteBucketWebsite](s3.md#deletebucketwebsite) | ⚪ stub | 🔵 by design | — | — | `(no equivalent — proxy treats it as a no-op)` |
| s3 | [DeleteObject](s3.md#deleteobject) | ✅ implemented | — | — | ✅ | `DELETE https://{account}.blob.core.windows.net/{container}/{blob}` |
| s3 | [DeleteObjectTagging](s3.md#deleteobjecttagging) | ✅ implemented | — | — | ✅ | `PUT {blob}?comp=tags with an empty <TagSet/>` |
| s3 | [DeleteObjects](s3.md#deleteobjects) | ✅ implemented | — | — | ✅ | `Multiple DELETEs against Blob (no native batch endpoint)` |
| s3 | [DeletePublicAccessBlock](s3.md#deletepublicaccessblock) | 🟡 partial | 🔵 by design | — | — | `Conditional container-metadata update` |
| s3 | [GetBucketAccelerateConfiguration](s3.md#getbucketaccelerateconfiguration) | 🟡 partial | 🔵 by design | — | — | `(no equivalent — proxy returns stable Suspended)` |
| s3 | [GetBucketAcl](s3.md#getbucketacl) | 🟡 partial | 🔵 by design | — | — | `(no Azure equivalent — synthetic ownership-only response)` |
| s3 | [GetBucketCors](s3.md#getbucketcors) | ⛔ unsupported | 🔵 by design | — | — | `(no equivalent — proxy returns 404 NoSuchCORSConfiguration)` |
| s3 | [GetBucketEncryption](s3.md#getbucketencryption) | 🟡 partial | 🔵 by design | — | — | `Container metadata for SSE-S3 intent; Azure Storage encryption remains account-managed` |
| s3 | [GetBucketLifecycleConfiguration](s3.md#getbucketlifecycleconfiguration) | ⛔ unsupported | 🔵 by design | — | — | `(no equivalent — proxy returns 404 NoSuchLifecycleConfiguration)` |
| s3 | [GetBucketLogging](s3.md#getbucketlogging) | ⚪ stub | 🔵 by design | — | — | `(no equivalent — proxy returns an empty <BucketLoggingStatus/> document)` |
| s3 | [GetBucketNotificationConfiguration](s3.md#getbucketnotificationconfiguration) | ⚪ stub | 🔵 by design | — | — | `(no equivalent — proxy returns an empty <NotificationConfiguration/> document)` |
| s3 | [GetBucketOwnershipControls](s3.md#getbucketownershipcontrols) | 🟡 partial | 🔵 by design | — | — | `Container metadata (persisted compatibility intent only)` |
| s3 | [GetBucketPolicy](s3.md#getbucketpolicy) | ⛔ unsupported | 🔵 by design | — | — | `(no equivalent — proxy returns 404 NoSuchBucketPolicy)` |
| s3 | [GetBucketPolicyStatus](s3.md#getbucketpolicystatus) | ⛔ unsupported | 🔵 by design | — | — | `(no equivalent — proxy returns 404 NoSuchBucketPolicy)` |
| s3 | [GetBucketReplication](s3.md#getbucketreplication) | ⛔ unsupported | 🔵 by design | — | — | `(no equivalent — proxy returns 404 ReplicationConfigurationNotFoundError)` |
| s3 | [GetBucketRequestPayment](s3.md#getbucketrequestpayment) | 🟡 partial | 🔵 by design | — | — | `(no equivalent — proxy returns the S3 default body)` |
| s3 | [GetBucketTagging](s3.md#getbuckettagging) | 🟡 partial | 🔵 by design | — | — | `GET {container}?restype=container&comp=metadata (single opaque metadata blob)` |
| s3 | [GetBucketVersioning](s3.md#getbucketversioning) | 🟡 partial | 🔵 by design | — | ✅ | `Container metadata (per-bucket toggle); reflects stored PutBucketVersioning intent` |
| s3 | [GetBucketWebsite](s3.md#getbucketwebsite) | ⛔ unsupported | 🔵 by design | — | — | `(no equivalent — proxy returns 404 NoSuchWebsiteConfiguration)` |
| s3 | [GetObject](s3.md#getobject) | ✅ implemented | — | — | ✅ | `GET https://{account}.blob.core.windows.net/{container}/{blob}` |
| s3 | [GetObjectAcl](s3.md#getobjectacl) | 🟡 partial | 🔵 by design | — | — | `(no Azure equivalent — synthetic ownership-only response)` |
| s3 | [GetObjectLegalHold](s3.md#getobjectlegalhold) | ✅ implemented | — | — | ✅ | `Blob legal hold (HEAD blob: x-ms-legal-hold)` |
| s3 | [GetObjectLockConfiguration](s3.md#getobjectlockconfiguration) | ⛔ unsupported | 🔵 by design | — | — | `(bucket-level WORM is ARM/management-plane only; proxy returns 404 ObjectLockConfigurationNotFoundError)` |
| s3 | [GetObjectRetention](s3.md#getobjectretention) | ✅ implemented | — | — | ✅ | `Blob immutability policy (HEAD blob: x-ms-immutability-policy-mode/-until-date)` |
| s3 | [GetObjectTagging](s3.md#getobjecttagging) | ✅ implemented | — | — | ✅ | `GET {blob}?comp=tags (Azure Blob Index Tags)` |
| s3 | [GetObjectTorrent](s3.md#getobjecttorrent) | ⛔ unsupported | ⚫ non-goal | — | — | `(no equivalent — proxy returns 501 NotImplemented)` |
| s3 | [GetPublicAccessBlock](s3.md#getpublicaccessblock) | 🟡 partial | 🔵 by design | — | — | `Container metadata (persisted compatibility intent only)` |
| s3 | [HeadBucket](s3.md#headbucket) | ✅ implemented | — | — | ✅ | `HEAD https://{account}.blob.core.windows.net/{container}?restype=container` |
| s3 | [HeadObject](s3.md#headobject) | ✅ implemented | — | — | ✅ | `HEAD https://{account}.blob.core.windows.net/{container}/{blob}` |
| s3 | [ListBuckets](s3.md#listbuckets) | ✅ implemented | — | — | ✅ | `GET https://{account}.blob.core.windows.net/?comp=list` |
| s3 | [ListMultipartUploads](s3.md#listmultipartuploads) | 🟡 partial | 🔵 by design | — | — | `Proxy-owned multipart state container (Azure has no native cross-blob MPU enumeration primitive)` |
| s3 | [ListObjectVersions](s3.md#listobjectversions) | 🟡 partial | 🔵 by design | — | ✅ | `GET {container}?restype=container&comp=list&include=versions` |
| s3 | [ListObjects](s3.md#listobjects) | ✅ implemented | — | — | ✅ | `GET https://{account}.blob.core.windows.net/{container}?restype=container&comp=list` |
| s3 | [ListObjectsV2](s3.md#listobjectsv2) | ✅ implemented | — | — | ✅ | `GET https://{account}.blob.core.windows.net/{container}?restype=container&comp=list` |
| s3 | [ListParts](s3.md#listparts) | ✅ implemented | — | — | ✅ | `Proxy state HEAD/verification + Get Block List (?comp=blocklist&blocklisttype=uncommitted)` |
| s3 | [PresignedUrl](s3.md#presignedurl) | ✅ implemented | — | — | ✅ | `(no operation — feature-flag; presigned URLs reuse GetObject / PutObject / HeadObject / DeleteObject paths)` |
| s3 | [PutBucketAccelerateConfiguration](s3.md#putbucketaccelerateconfiguration) | 🟡 partial | 🔵 by design | — | — | `(no equivalent — Suspended is an accepted stable no-op)` |
| s3 | [PutBucketAcl](s3.md#putbucketacl) | 🟡 partial | 🔵 by design | — | — | `(no Azure equivalent — validates owner-only intent and replies 200)` |
| s3 | [PutBucketCors](s3.md#putbucketcors) | ⛔ unsupported | 🔵 by design | — | — | `(no equivalent — proxy returns 501 NotImplemented)` |
| s3 | [PutBucketEncryption](s3.md#putbucketencryption) | 🟡 partial | 🔵 by design | — | — | `Conditional container-metadata update for SSE-S3 intent` |
| s3 | [PutBucketLifecycleConfiguration](s3.md#putbucketlifecycleconfiguration) | ⛔ unsupported | 🔵 by design | — | — | `(no equivalent — proxy returns 501 NotImplemented)` |
| s3 | [PutBucketLogging](s3.md#putbucketlogging) | ⛔ unsupported | 🔵 by design | — | — | `(no equivalent — proxy returns 501 NotImplemented)` |
| s3 | [PutBucketNotificationConfiguration](s3.md#putbucketnotificationconfiguration) | ⛔ unsupported | 🔵 by design | — | — | `(no equivalent — proxy returns 501 NotImplemented)` |
| s3 | [PutBucketOwnershipControls](s3.md#putbucketownershipcontrols) | 🟡 partial | 🔵 by design | — | — | `Conditional container-metadata update (persisted compatibility intent only)` |
| s3 | [PutBucketPolicy](s3.md#putbucketpolicy) | ⛔ unsupported | 🔵 by design | — | — | `(no equivalent — proxy returns 501 NotImplemented)` |
| s3 | [PutBucketReplication](s3.md#putbucketreplication) | ⛔ unsupported | 🔵 by design | — | — | `(no equivalent — proxy returns 501 NotImplemented)` |
| s3 | [PutBucketRequestPayment](s3.md#putbucketrequestpayment) | 🟡 partial | 🔵 by design | — | — | `(no equivalent — BucketOwner is an accepted stable no-op)` |
| s3 | [PutBucketTagging](s3.md#putbuckettagging) | 🟡 partial | 🔵 by design | — | — | `PUT {container}?restype=container&comp=metadata` |
| s3 | [PutBucketVersioning](s3.md#putbucketversioning) | 🟡 partial | 🔵 by design | — | ✅ | `Container metadata (per-bucket toggle); account-level Blob versioning assumed pre-enabled` |
| s3 | [PutBucketWebsite](s3.md#putbucketwebsite) | ⛔ unsupported | 🔵 by design | — | — | `(no equivalent — proxy returns 501 NotImplemented)` |
| s3 | [PutObject](s3.md#putobject) | ✅ implemented | — | — | ✅ | `PUT https://{account}.blob.core.windows.net/{container}/{blob}` |
| s3 | [PutObjectAcl](s3.md#putobjectacl) | 🟡 partial | 🔵 by design | — | — | `(no Azure equivalent — validates owner-only intent and replies 200)` |
| s3 | [PutObjectLegalHold](s3.md#putobjectlegalhold) | ✅ implemented | — | — | ✅ | `Set Blob Legal Hold (PUT blob ?comp=legalhold, x-ms-legal-hold)` |
| s3 | [PutObjectLockConfiguration](s3.md#putobjectlockconfiguration) | ⛔ unsupported | 🔵 by design | — | — | `(bucket-level WORM is ARM/management-plane only; proxy returns 501 NotImplemented)` |
| s3 | [PutObjectRetention](s3.md#putobjectretention) | ✅ implemented | — | — | ✅ | `Set Blob Immutability Policy (PUT blob ?comp=immutabilityPolicies)` |
| s3 | [PutObjectTagging](s3.md#putobjecttagging) | ✅ implemented | — | — | ✅ | `PUT {blob}?comp=tags` |
| s3 | [PutPublicAccessBlock](s3.md#putpublicaccessblock) | 🟡 partial | 🔵 by design | — | — | `Conditional container-metadata update (persisted compatibility intent only)` |
| s3 | [RestoreObject](s3.md#restoreobject) | ⛔ unsupported | 🔵 by design | — | — | `(no equivalent — proxy returns 501 NotImplemented)` |
| s3 | [UploadPart](s3.md#uploadpart) | ✅ implemented | — | — | ✅ | `Proxy state HEAD/verification + Put Block (?comp=block&blockid=…)` |
| s3 | [UploadPartCopy](s3.md#uploadpartcopy) | ✅ implemented | — | — | ✅ | `Proxy state HEAD/verification + Put Block From URL (?comp=block&blockid=…)` |
| secretsmanager | [CreateSecret](secretsmanager.md#createsecret) | ✅ implemented | — | — | ✅ | `PUT https://{vault}.vault.azure.net/secrets/{name}` |
| secretsmanager | [DeleteSecret](secretsmanager.md#deletesecret) | ✅ implemented | — | — | ✅ | `DELETE https://{vault}.vault.azure.net/secrets/{name}` |
| secretsmanager | [DescribeSecret](secretsmanager.md#describesecret) | ✅ implemented | — | — | ✅ | `GET https://{vault}.vault.azure.net/secrets/{name}?api-version=7.4` |
| secretsmanager | [GetSecretValue](secretsmanager.md#getsecretvalue) | ✅ implemented | — | — | ✅ | `GET https://{vault}.vault.azure.net/secrets/{name}/versions/{version?}` |
| secretsmanager | [ListSecrets](secretsmanager.md#listsecrets) | ✅ implemented | — | — | ✅ | `GET https://{vault}.vault.azure.net/secrets?api-version=7.4` |
| secretsmanager | [PutSecretValue](secretsmanager.md#putsecretvalue) | 🟡 partial | 🔵 by design | — | ✅ | `PUT https://{vault}.vault.azure.net/secrets/{name}` |
| secretsmanager | [RotateSecret](secretsmanager.md#rotatesecret) | ⛔ unsupported | ⚫ non-goal | — | — | `None — Azure Key Vault has no equivalent managed-rotation trigger the proxy can drive` |
| secretsmanager | [UpdateSecret](secretsmanager.md#updatesecret) | ✅ implemented | — | — | ✅ | `PUT https://{vault}.vault.azure.net/secrets/{name}/versions` |
| sns | [ConfirmSubscription](sns.md#confirmsubscription) | 🟡 partial | 🔵 by design | — | ✅ | `Azure Service Bus topic subscriptions` |
| sns | [CreateTopic](sns.md#createtopic) | 🟡 partial | 🛠️ feasible backlog | [#692](https://github.com/pedrosakuma/aws2azure/issues/692) | ✅ | `Azure Service Bus Topics management REST API` |
| sns | [DeleteTopic](sns.md#deletetopic) | 🟡 partial | 🛠️ feasible backlog | [#692](https://github.com/pedrosakuma/aws2azure/issues/692) | ✅ | `Azure Service Bus Topics management REST API` |
| sns | [GetSubscriptionAttributes](sns.md#getsubscriptionattributes) | 🟡 partial | 🔵 by design | — | ✅ | `Azure Service Bus subscription description` |
| sns | [GetTopicAttributes](sns.md#gettopicattributes) | 🟡 partial | 🔵 by design | — | — | `Azure Service Bus topic description` |
| sns | [ListSubscriptions](sns.md#listsubscriptions) | 🟡 partial | 🔵 by design | — | ✅ | `Azure Service Bus topic subscriptions` |
| sns | [ListSubscriptionsByTopic](sns.md#listsubscriptionsbytopic) | 🟡 partial | 🔵 by design | — | ✅ | `Azure Service Bus topic subscriptions` |
| sns | [ListTopics](sns.md#listtopics) | 🟡 partial | 🛠️ feasible backlog | [#692](https://github.com/pedrosakuma/aws2azure/issues/692) | ✅ | `Azure Service Bus Topics management REST API` |
| sns | [Publish](sns.md#publish) | 🟡 partial | 🔵 by design | — | ✅ | `Azure Service Bus Topics / Azure Event Grid` |
| sns | [PublishBatch](sns.md#publishbatch) | 🟡 partial | 🔵 by design | — | ✅ | `Azure Service Bus Topics / Azure Event Grid` |
| sns | [SetSubscriptionAttributes](sns.md#setsubscriptionattributes) | 🟡 partial | 🛠️ feasible backlog | [#691](https://github.com/pedrosakuma/aws2azure/issues/691) | ✅ | `Azure Service Bus subscription description` |
| sns | [SetTopicAttributes](sns.md#settopicattributes) | 🟡 partial | 🔵 by design | — | — | `Azure Service Bus topic description` |
| sns | [Subscribe](sns.md#subscribe) | 🟡 partial | ⚫ non-goal | — | ✅ | `Azure Service Bus topic subscriptions` |
| sns | [Unsubscribe](sns.md#unsubscribe) | 🟡 partial | 🔵 by design | — | ✅ | `Azure Service Bus topic subscriptions` |
| sqs | [AddPermission](sqs.md#addpermission) | ⚪ stub | 🔵 by design | — | — | `No native Service Bus equivalent — validates queue existence and returns success.` |
| sqs | [ChangeMessageVisibility](sqs.md#changemessagevisibility) | 🟡 partial | 🔵 by design | — | — | `Azure Service Bus queue runtime REST API — PUT /{queue}/messages/{messageId}/{lockToken}?api-version=2021-05 for visibility=0 (unlock), POST to the same path for positive values (renew-lock); AMQP — visibility=0 maps to Abandon and positive values use `com.microsoft:renew-lock`.` |
| sqs | [ChangeMessageVisibilityBatch](sqs.md#changemessagevisibilitybatch) | 🟡 partial | 🔵 by design | — | — | `Azure Service Bus queue runtime REST API — bounded parallel PUT Unlock calls for VisibilityTimeout=0 and POST RenewLock calls for positive values.` |
| sqs | [CreateQueue](sqs.md#createqueue) | ✅ implemented | — | — | ✅ | `PUT https://{namespace}.servicebus.windows.net/{queue}?api-version=2021-05 (Atom QueueDescription)` |
| sqs | [DeleteMessage](sqs.md#deletemessage) | ✅ implemented | — | — | ✅ | `Azure Service Bus queue runtime REST API — DELETE /{queue}/messages/{messageId}/{lockToken}?api-version=2021-05` |
| sqs | [DeleteMessageBatch](sqs.md#deletemessagebatch) | ✅ implemented | — | — | ✅ | `Azure Service Bus queue runtime REST API — N parallel DELETE /{queue}/messages/{messageId}/{lockToken}?api-version=2021-05` |
| sqs | [DeleteQueue](sqs.md#deletequeue) | ✅ implemented | — | — | ✅ | `DELETE https://{namespace}.servicebus.windows.net/{queue}?api-version=2021-05` |
| sqs | [GetQueueAttributes](sqs.md#getqueueattributes) | 🟡 partial | 🔵 by design | — | — | `GET https://{namespace}.servicebus.windows.net/{queue}?api-version=2021-05 (Atom QueueDescription)` |
| sqs | [GetQueueUrl](sqs.md#getqueueurl) | ✅ implemented | — | — | ✅ | `GET https://{namespace}.servicebus.windows.net/{queue}?api-version=2021-05 (existence probe)` |
| sqs | [ListDeadLetterSourceQueues](sqs.md#listdeadlettersourcequeues) | ✅ implemented | — | — | ✅ | `Page through SB management GET /$Resources/queues?api-version=2021-05 and filter entries whose ForwardDeadLetteredMessagesTo equals the requested queue.` |
| sqs | [ListQueueTags](sqs.md#listqueuetags) | 🟡 partial | 🛠️ feasible backlog | [#693](https://github.com/pedrosakuma/aws2azure/issues/693) | — | `GET QueueDescription and decode aws2azure's compact metadata envelope from UserMetadata.` |
| sqs | [ListQueues](sqs.md#listqueues) | ✅ implemented | — | — | ✅ | `GET https://{namespace}.servicebus.windows.net/$Resources/queues?api-version=2021-05&$skip=N&$top=M` |
| sqs | [PurgeQueue](sqs.md#purgequeue) | 🟡 partial | 🔵 by design | — | — | `Azure Service Bus queue runtime REST API — emulated via drain-loop of POST /{queue}/messages/head + DELETE /{queue}/messages/{id}/{lockToken}` |
| sqs | [ReceiveMessage](sqs.md#receivemessage) | ✅ implemented | — | — | ✅ | `Azure Service Bus queue runtime REST API — POST /{queue}/messages/head?timeout={waitSeconds}&api-version=2021-05 (peek-lock semantics)` |
| sqs | [RemovePermission](sqs.md#removepermission) | ⚪ stub | 🔵 by design | — | — | `No native Service Bus equivalent — validates queue existence and returns success.` |
| sqs | [SendMessage](sqs.md#sendmessage) | ✅ implemented | — | — | ✅ | `Azure Service Bus queue runtime REST API — POST /{queue}/messages?api-version=2021-05` |
| sqs | [SendMessageBatch](sqs.md#sendmessagebatch) | ✅ implemented | — | — | ✅ | `Azure Service Bus queue runtime REST API — POST /{queue}/messages with Content-Type: application/vnd.microsoft.servicebus.json` |
| sqs | [SetQueueAttributes](sqs.md#setqueueattributes) | 🟡 partial | 🔵 by design | — | — | `Azure Service Bus management REST API — PUT /{queue}?api-version=2021-05 with If-Match: * (whole-entity replace)` |
| sqs | [TagQueue](sqs.md#tagqueue) | 🟡 partial | 🛠️ feasible backlog | [#693](https://github.com/pedrosakuma/aws2azure/issues/693) | — | `GET + PUT QueueDescription with aws2azure's compact metadata envelope stored in UserMetadata.` |
| sqs | [UntagQueue](sqs.md#untagqueue) | 🟡 partial | 🛠️ feasible backlog | [#693](https://github.com/pedrosakuma/aws2azure/issues/693) | — | `GET + PUT QueueDescription with aws2azure's compact metadata envelope stored in UserMetadata.` |
