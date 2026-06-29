# Coverage matrix

| Service | Operation | Status | Real-Azure | Azure equivalent |
|---|---|---|---|---|
| dynamodb | [BatchGetItem](dynamodb.md#batchgetitem) | 🟡 partial | — | `Azure Cosmos DB (Core SQL API)` |
| dynamodb | [BatchWriteItem](dynamodb.md#batchwriteitem) | 🟡 partial | — | `Azure Cosmos DB (Core SQL API)` |
| dynamodb | [CreateTable](dynamodb.md#createtable) | ✅ implemented | — | `Azure Cosmos DB (Core SQL API) — POST /dbs/{db}/colls` |
| dynamodb | [DeleteItem](dynamodb.md#deleteitem) | 🟡 partial | — | `Azure Cosmos DB (Core SQL API)` |
| dynamodb | [DeleteTable](dynamodb.md#deletetable) | ✅ implemented | — | `Azure Cosmos DB (Core SQL API) — DELETE /dbs/{db}/colls/{name}` |
| dynamodb | [DescribeTable](dynamodb.md#describetable) | ✅ implemented | — | `Azure Cosmos DB (Core SQL API) — GET /dbs/{db}/colls/{name} + sidecar metadata` |
| dynamodb | [DescribeTimeToLive](dynamodb.md#describetimetolive) | 🟡 partial | — | `Azure Cosmos DB container `defaultTtl` / per-item `ttl`` |
| dynamodb | [GetItem](dynamodb.md#getitem) | 🟡 partial | — | `Azure Cosmos DB (Core SQL API)` |
| dynamodb | [ListTables](dynamodb.md#listtables) | ✅ implemented | — | `Azure Cosmos DB (Core SQL API) — GET /dbs/{db}/colls` |
| dynamodb | [ListTagsOfResource](dynamodb.md#listtagsofresource) | ✅ implemented | — | `Azure Cosmos DB account/resource tags (control plane)` |
| dynamodb | [PutItem](dynamodb.md#putitem) | 🟡 partial | — | `Azure Cosmos DB (Core SQL API)` |
| dynamodb | [Query](dynamodb.md#query) | 🟡 partial | — | `Azure Cosmos DB (Core SQL API)` |
| dynamodb | [Scan](dynamodb.md#scan) | 🟡 partial | — | `Azure Cosmos DB (Core SQL API)` |
| dynamodb | [TagResource](dynamodb.md#tagresource) | ✅ implemented | — | `Azure Cosmos DB account/resource tags (control plane)` |
| dynamodb | [TransactGetItems](dynamodb.md#transactgetitems) | 🟡 partial | — | `Azure Cosmos DB (Core SQL API)` |
| dynamodb | [TransactWriteItems](dynamodb.md#transactwriteitems) | 🟡 partial | — | `Azure Cosmos DB (Core SQL API) — single-partition stored-procedure transaction` |
| dynamodb | [UntagResource](dynamodb.md#untagresource) | ✅ implemented | — | `Azure Cosmos DB account/resource tags (control plane)` |
| dynamodb | [UpdateItem](dynamodb.md#updateitem) | 🟡 partial | — | `Azure Cosmos DB (Core SQL API)` |
| dynamodb | [UpdateTimeToLive](dynamodb.md#updatetimetolive) | 🟡 partial | — | `Azure Cosmos DB container `defaultTtl` / per-item `ttl`` |
| kinesis | [DescribeStream](kinesis.md#describestream) | 🟡 partial | — | `Azure Event Hubs Service Bus management REST API` |
| kinesis | [DescribeStreamSummary](kinesis.md#describestreamsummary) | 🟡 partial | — | `Azure Event Hubs Service Bus management REST API` |
| kinesis | [GetRecords](kinesis.md#getrecords) | 🟡 partial | — | `Azure Event Hubs (AMQP 1.0 data plane)` |
| kinesis | [GetShardIterator](kinesis.md#getsharditerator) | 🟡 partial | — | `Azure Event Hubs (AMQP 1.0 data plane)` |
| kinesis | [ListShards](kinesis.md#listshards) | 🟡 partial | — | `Azure Event Hubs Service Bus management REST API` |
| kinesis | [PutRecord](kinesis.md#putrecord) | 🟡 partial | — | `Azure Event Hubs (AMQP 1.0 data plane)` |
| kinesis | [PutRecords](kinesis.md#putrecords) | 🟡 partial | — | `Azure Event Hubs (AMQP 1.0 data plane)` |
| s3 | [AbortMultipartUpload](s3.md#abortmultipartupload) | ✅ implemented | — | `(no-op; uncommitted blocks GC after 7 days)` |
| s3 | [CompleteMultipartUpload](s3.md#completemultipartupload) | ✅ implemented | — | `Put Block List` |
| s3 | [CopyObject](s3.md#copyobject) | ✅ implemented | — | `PUT https://{account}.blob.core.windows.net/{container}/{blob} with x-ms-copy-source` |
| s3 | [CreateBucket](s3.md#createbucket) | ✅ implemented | — | `PUT https://{account}.blob.core.windows.net/{container}?restype=container` |
| s3 | [CreateMultipartUpload](s3.md#createmultipartupload) | ✅ implemented | — | `Stateless UploadId (no Azure call until UploadPart)` |
| s3 | [DeleteBucket](s3.md#deletebucket) | ✅ implemented | — | `DELETE https://{account}.blob.core.windows.net/{container}?restype=container` |
| s3 | [DeleteBucketCors](s3.md#deletebucketcors) | ⚪ stub | — | `(no equivalent — proxy treats it as a no-op)` |
| s3 | [DeleteBucketEncryption](s3.md#deletebucketencryption) | ⚪ stub | — | `(no equivalent — proxy treats it as a no-op)` |
| s3 | [DeleteBucketLifecycle](s3.md#deletebucketlifecycle) | ⚪ stub | — | `(no equivalent — proxy treats it as a no-op)` |
| s3 | [DeleteBucketOwnershipControls](s3.md#deletebucketownershipcontrols) | ⚪ stub | — | `(no equivalent — proxy treats it as a no-op)` |
| s3 | [DeleteBucketPolicy](s3.md#deletebucketpolicy) | ⚪ stub | — | `(no equivalent — proxy treats it as a no-op)` |
| s3 | [DeleteBucketReplication](s3.md#deletebucketreplication) | ⚪ stub | — | `(no equivalent — proxy treats it as a no-op)` |
| s3 | [DeleteBucketTagging](s3.md#deletebuckettagging) | ✅ implemented | — | `PUT {container}?restype=container&comp=metadata with no x-ms-meta-* headers` |
| s3 | [DeleteBucketWebsite](s3.md#deletebucketwebsite) | ⚪ stub | — | `(no equivalent — proxy treats it as a no-op)` |
| s3 | [DeleteObject](s3.md#deleteobject) | ✅ implemented | — | `DELETE https://{account}.blob.core.windows.net/{container}/{blob}` |
| s3 | [DeleteObjectTagging](s3.md#deleteobjecttagging) | ✅ implemented | — | `PUT {blob}?comp=tags with an empty <TagSet/>` |
| s3 | [DeleteObjects](s3.md#deleteobjects) | ✅ implemented | — | `Multiple DELETEs against Blob (no native batch endpoint)` |
| s3 | [DeletePublicAccessBlock](s3.md#deletepublicaccessblock) | ⚪ stub | — | `(no equivalent — proxy treats it as a no-op)` |
| s3 | [GetBucketAccelerateConfiguration](s3.md#getbucketaccelerateconfiguration) | ⚪ stub | — | `(no equivalent — proxy returns an empty <AccelerateConfiguration/> document)` |
| s3 | [GetBucketAcl](s3.md#getbucketacl) | 🟡 partial | — | `(no Azure equivalent — synthetic ownership-only response)` |
| s3 | [GetBucketCors](s3.md#getbucketcors) | ⛔ unsupported | — | `(no equivalent — proxy returns 404 NoSuchCORSConfiguration)` |
| s3 | [GetBucketEncryption](s3.md#getbucketencryption) | ⛔ unsupported | — | `(no equivalent — proxy returns 404 ServerSideEncryptionConfigurationNotFoundError)` |
| s3 | [GetBucketLifecycleConfiguration](s3.md#getbucketlifecycleconfiguration) | ⛔ unsupported | — | `(no equivalent — proxy returns 404 NoSuchLifecycleConfiguration)` |
| s3 | [GetBucketLogging](s3.md#getbucketlogging) | ⚪ stub | — | `(no equivalent — proxy returns an empty <BucketLoggingStatus/> document)` |
| s3 | [GetBucketNotificationConfiguration](s3.md#getbucketnotificationconfiguration) | ⚪ stub | — | `(no equivalent — proxy returns an empty <NotificationConfiguration/> document)` |
| s3 | [GetBucketOwnershipControls](s3.md#getbucketownershipcontrols) | ⛔ unsupported | — | `(no equivalent — proxy returns 404 OwnershipControlsNotFoundError)` |
| s3 | [GetBucketPolicy](s3.md#getbucketpolicy) | ⛔ unsupported | — | `(no equivalent — proxy returns 404 NoSuchBucketPolicy)` |
| s3 | [GetBucketPolicyStatus](s3.md#getbucketpolicystatus) | ⛔ unsupported | — | `(no equivalent — proxy returns 404 NoSuchBucketPolicy)` |
| s3 | [GetBucketReplication](s3.md#getbucketreplication) | ⛔ unsupported | — | `(no equivalent — proxy returns 404 ReplicationConfigurationNotFoundError)` |
| s3 | [GetBucketRequestPayment](s3.md#getbucketrequestpayment) | ⚪ stub | — | `(no equivalent — proxy returns the S3 default body)` |
| s3 | [GetBucketTagging](s3.md#getbuckettagging) | 🟡 partial | — | `GET {container}?restype=container&comp=metadata (single opaque metadata blob)` |
| s3 | [GetBucketVersioning](s3.md#getbucketversioning) | 🟡 partial | — | `Container metadata (per-bucket toggle); reflects stored PutBucketVersioning intent` |
| s3 | [GetBucketWebsite](s3.md#getbucketwebsite) | ⛔ unsupported | — | `(no equivalent — proxy returns 404 NoSuchWebsiteConfiguration)` |
| s3 | [GetObject](s3.md#getobject) | ✅ implemented | — | `GET https://{account}.blob.core.windows.net/{container}/{blob}` |
| s3 | [GetObjectAcl](s3.md#getobjectacl) | 🟡 partial | — | `(no Azure equivalent — synthetic ownership-only response)` |
| s3 | [GetObjectLegalHold](s3.md#getobjectlegalhold) | ✅ implemented | ✅ | `Blob legal hold (HEAD blob: x-ms-legal-hold)` |
| s3 | [GetObjectLockConfiguration](s3.md#getobjectlockconfiguration) | ⛔ unsupported | — | `(bucket-level WORM is ARM/management-plane only; proxy returns 404 ObjectLockConfigurationNotFoundError)` |
| s3 | [GetObjectRetention](s3.md#getobjectretention) | ✅ implemented | ✅ | `Blob immutability policy (HEAD blob: x-ms-immutability-policy-mode/-until-date)` |
| s3 | [GetObjectTagging](s3.md#getobjecttagging) | ✅ implemented | — | `GET {blob}?comp=tags (Azure Blob Index Tags)` |
| s3 | [GetObjectTorrent](s3.md#getobjecttorrent) | ⛔ unsupported | — | `(no equivalent — proxy returns 501 NotImplemented)` |
| s3 | [GetPublicAccessBlock](s3.md#getpublicaccessblock) | ⛔ unsupported | — | `(no equivalent — proxy returns 404 NoSuchPublicAccessBlockConfiguration)` |
| s3 | [HeadBucket](s3.md#headbucket) | ✅ implemented | — | `HEAD https://{account}.blob.core.windows.net/{container}?restype=container` |
| s3 | [HeadObject](s3.md#headobject) | ✅ implemented | — | `HEAD https://{account}.blob.core.windows.net/{container}/{blob}` |
| s3 | [ListBuckets](s3.md#listbuckets) | ✅ implemented | — | `GET https://{account}.blob.core.windows.net/?comp=list` |
| s3 | [ListMultipartUploads](s3.md#listmultipartuploads) | 🟡 partial | — | `(none — Azure Blob has no in-progress-upload enumeration)` |
| s3 | [ListObjectVersions](s3.md#listobjectversions) | 🟡 partial | — | `GET {container}?restype=container&comp=list&include=versions` |
| s3 | [ListObjects](s3.md#listobjects) | ✅ implemented | — | `GET https://{account}.blob.core.windows.net/{container}?restype=container&comp=list` |
| s3 | [ListObjectsV2](s3.md#listobjectsv2) | ✅ implemented | — | `GET https://{account}.blob.core.windows.net/{container}?restype=container&comp=list` |
| s3 | [ListParts](s3.md#listparts) | ✅ implemented | — | `Get Block List (?comp=blocklist&blocklisttype=uncommitted)` |
| s3 | [PresignedUrl](s3.md#presignedurl) | ✅ implemented | — | `(no operation — feature-flag; presigned URLs reuse GetObject / PutObject / HeadObject / DeleteObject paths)` |
| s3 | [PutBucketAccelerateConfiguration](s3.md#putbucketaccelerateconfiguration) | ⛔ unsupported | — | `(no equivalent — proxy returns 501 NotImplemented)` |
| s3 | [PutBucketAcl](s3.md#putbucketacl) | 🟡 partial | — | `(no Azure equivalent — validates owner-only intent and replies 200)` |
| s3 | [PutBucketCors](s3.md#putbucketcors) | ⛔ unsupported | — | `(no equivalent — proxy returns 501 NotImplemented)` |
| s3 | [PutBucketEncryption](s3.md#putbucketencryption) | ⛔ unsupported | — | `(no equivalent — proxy returns 501 NotImplemented)` |
| s3 | [PutBucketLifecycleConfiguration](s3.md#putbucketlifecycleconfiguration) | ⛔ unsupported | — | `(no equivalent — proxy returns 501 NotImplemented)` |
| s3 | [PutBucketLogging](s3.md#putbucketlogging) | ⛔ unsupported | — | `(no equivalent — proxy returns 501 NotImplemented)` |
| s3 | [PutBucketNotificationConfiguration](s3.md#putbucketnotificationconfiguration) | ⛔ unsupported | — | `(no equivalent — proxy returns 501 NotImplemented)` |
| s3 | [PutBucketOwnershipControls](s3.md#putbucketownershipcontrols) | ⛔ unsupported | — | `(no equivalent — proxy returns 501 NotImplemented)` |
| s3 | [PutBucketPolicy](s3.md#putbucketpolicy) | ⛔ unsupported | — | `(no equivalent — proxy returns 501 NotImplemented)` |
| s3 | [PutBucketReplication](s3.md#putbucketreplication) | ⛔ unsupported | — | `(no equivalent — proxy returns 501 NotImplemented)` |
| s3 | [PutBucketRequestPayment](s3.md#putbucketrequestpayment) | ⛔ unsupported | — | `(no equivalent — proxy returns 501 NotImplemented)` |
| s3 | [PutBucketTagging](s3.md#putbuckettagging) | 🟡 partial | — | `PUT {container}?restype=container&comp=metadata` |
| s3 | [PutBucketVersioning](s3.md#putbucketversioning) | 🟡 partial | — | `Container metadata (per-bucket toggle); account-level Blob versioning assumed pre-enabled` |
| s3 | [PutBucketWebsite](s3.md#putbucketwebsite) | ⛔ unsupported | — | `(no equivalent — proxy returns 501 NotImplemented)` |
| s3 | [PutObject](s3.md#putobject) | ✅ implemented | — | `PUT https://{account}.blob.core.windows.net/{container}/{blob}` |
| s3 | [PutObjectAcl](s3.md#putobjectacl) | 🟡 partial | — | `(no Azure equivalent — validates owner-only intent and replies 200)` |
| s3 | [PutObjectLegalHold](s3.md#putobjectlegalhold) | ✅ implemented | ✅ | `Set Blob Legal Hold (PUT blob ?comp=legalhold, x-ms-legal-hold)` |
| s3 | [PutObjectLockConfiguration](s3.md#putobjectlockconfiguration) | ⛔ unsupported | — | `(bucket-level WORM is ARM/management-plane only; proxy returns 501 NotImplemented)` |
| s3 | [PutObjectRetention](s3.md#putobjectretention) | ✅ implemented | ✅ | `Set Blob Immutability Policy (PUT blob ?comp=immutabilityPolicies)` |
| s3 | [PutObjectTagging](s3.md#putobjecttagging) | ✅ implemented | — | `PUT {blob}?comp=tags` |
| s3 | [PutPublicAccessBlock](s3.md#putpublicaccessblock) | ⛔ unsupported | — | `(no equivalent — proxy returns 501 NotImplemented)` |
| s3 | [RestoreObject](s3.md#restoreobject) | ⛔ unsupported | — | `(no equivalent — proxy returns 501 NotImplemented)` |
| s3 | [UploadPart](s3.md#uploadpart) | ✅ implemented | — | `Put Block (?comp=block&blockid=…)` |
| s3 | [UploadPartCopy](s3.md#uploadpartcopy) | ✅ implemented | — | `Put Block From URL (?comp=block&blockid=…)` |
| secretsmanager | [CreateSecret](secretsmanager.md#createsecret) | ✅ implemented | — | `PUT https://{vault}.vault.azure.net/secrets/{name}` |
| secretsmanager | [DeleteSecret](secretsmanager.md#deletesecret) | ✅ implemented | — | `DELETE https://{vault}.vault.azure.net/secrets/{name}` |
| secretsmanager | [DescribeSecret](secretsmanager.md#describesecret) | ✅ implemented | — | `GET https://{vault}.vault.azure.net/secrets/{name}?api-version=7.4` |
| secretsmanager | [GetSecretValue](secretsmanager.md#getsecretvalue) | ✅ implemented | — | `GET https://{vault}.vault.azure.net/secrets/{name}/versions/{version?}` |
| secretsmanager | [ListSecrets](secretsmanager.md#listsecrets) | ✅ implemented | — | `GET https://{vault}.vault.azure.net/secrets?api-version=7.4` |
| secretsmanager | [PutSecretValue](secretsmanager.md#putsecretvalue) | 🟡 partial | — | `PUT https://{vault}.vault.azure.net/secrets/{name}` |
| secretsmanager | [RotateSecret](secretsmanager.md#rotatesecret) | ⛔ unsupported | — | `None — Azure Key Vault has no equivalent managed-rotation trigger the proxy can drive` |
| secretsmanager | [UpdateSecret](secretsmanager.md#updatesecret) | ✅ implemented | — | `PUT https://{vault}.vault.azure.net/secrets/{name}/versions` |
| sns | [ConfirmSubscription](sns.md#confirmsubscription) | 🟡 partial | — | `Azure Service Bus topic subscriptions` |
| sns | [CreateTopic](sns.md#createtopic) | 🟡 partial | — | `Azure Service Bus Topics management REST API` |
| sns | [DeleteTopic](sns.md#deletetopic) | 🟡 partial | — | `Azure Service Bus Topics management REST API` |
| sns | [GetSubscriptionAttributes](sns.md#getsubscriptionattributes) | 🟡 partial | — | `Azure Service Bus subscription description` |
| sns | [GetTopicAttributes](sns.md#gettopicattributes) | 🟡 partial | — | `Azure Service Bus topic description` |
| sns | [ListSubscriptions](sns.md#listsubscriptions) | 🟡 partial | — | `Azure Service Bus topic subscriptions` |
| sns | [ListSubscriptionsByTopic](sns.md#listsubscriptionsbytopic) | 🟡 partial | — | `Azure Service Bus topic subscriptions` |
| sns | [ListTopics](sns.md#listtopics) | 🟡 partial | — | `Azure Service Bus Topics management REST API` |
| sns | [Publish](sns.md#publish) | 🟡 partial | — | `Azure Service Bus Topics / Azure Event Grid` |
| sns | [PublishBatch](sns.md#publishbatch) | 🟡 partial | — | `Azure Service Bus Topics / Azure Event Grid` |
| sns | [SetSubscriptionAttributes](sns.md#setsubscriptionattributes) | 🟡 partial | — | `Azure Service Bus subscription description` |
| sns | [SetTopicAttributes](sns.md#settopicattributes) | 🟡 partial | — | `Azure Service Bus topic description` |
| sns | [Subscribe](sns.md#subscribe) | 🟡 partial | — | `Azure Service Bus topic subscriptions` |
| sns | [Unsubscribe](sns.md#unsubscribe) | 🟡 partial | — | `Azure Service Bus topic subscriptions` |
| sqs | [AddPermission](sqs.md#addpermission) | ⚪ stub | — | `No native Service Bus equivalent — validates queue existence and returns success.` |
| sqs | [ChangeMessageVisibility](sqs.md#changemessagevisibility) | 🟡 partial | — | `Azure Service Bus queue runtime REST API — POST /{queue}/messages/{messageId}/{lockToken}?api-version=2021-05 (renew-lock); AMQP — `com.microsoft:renew-lock` over the queue's `$management` request-response link; visibility=0 maps to AMQP Abandon on the receiver link.` |
| sqs | [ChangeMessageVisibilityBatch](sqs.md#changemessagevisibilitybatch) | 🟡 partial | — | `Azure Service Bus queue runtime REST API — N parallel POST /{queue}/messages/{messageId}/{lockToken}?action=renewlock&api-version=2021-05` |
| sqs | [CreateQueue](sqs.md#createqueue) | ✅ implemented | — | `PUT https://{namespace}.servicebus.windows.net/{queue}?api-version=2021-05 (Atom QueueDescription)` |
| sqs | [DeleteMessage](sqs.md#deletemessage) | ✅ implemented | — | `Azure Service Bus queue runtime REST API — DELETE /{queue}/messages/{messageId}/{lockToken}?api-version=2021-05` |
| sqs | [DeleteMessageBatch](sqs.md#deletemessagebatch) | ✅ implemented | — | `Azure Service Bus queue runtime REST API — N parallel DELETE /{queue}/messages/{messageId}/{lockToken}?api-version=2021-05` |
| sqs | [DeleteQueue](sqs.md#deletequeue) | ✅ implemented | — | `DELETE https://{namespace}.servicebus.windows.net/{queue}?api-version=2021-05` |
| sqs | [GetQueueAttributes](sqs.md#getqueueattributes) | 🟡 partial | — | `GET https://{namespace}.servicebus.windows.net/{queue}?api-version=2021-05 (Atom QueueDescription)` |
| sqs | [GetQueueUrl](sqs.md#getqueueurl) | ✅ implemented | — | `GET https://{namespace}.servicebus.windows.net/{queue}?api-version=2021-05 (existence probe)` |
| sqs | [ListDeadLetterSourceQueues](sqs.md#listdeadlettersourcequeues) | ✅ implemented | — | `Page through SB management GET /$Resources/queues?api-version=2021-05 and filter entries whose ForwardDeadLetteredMessagesTo equals the requested queue.` |
| sqs | [ListQueueTags](sqs.md#listqueuetags) | 🟡 partial | — | `GET QueueDescription and decode aws2azure's base64 tag blob from UserMetadata.` |
| sqs | [ListQueues](sqs.md#listqueues) | ✅ implemented | — | `GET https://{namespace}.servicebus.windows.net/$Resources/queues?api-version=2021-05&$skip=N&$top=M` |
| sqs | [PurgeQueue](sqs.md#purgequeue) | 🟡 partial | — | `Azure Service Bus queue runtime REST API — emulated via drain-loop of POST /{queue}/messages/head + DELETE /{queue}/messages/{id}/{lockToken}` |
| sqs | [ReceiveMessage](sqs.md#receivemessage) | ✅ implemented | — | `Azure Service Bus queue runtime REST API — POST /{queue}/messages/head?timeout={waitSeconds}&api-version=2021-05 (peek-lock semantics)` |
| sqs | [RemovePermission](sqs.md#removepermission) | ⚪ stub | — | `No native Service Bus equivalent — validates queue existence and returns success.` |
| sqs | [SendMessage](sqs.md#sendmessage) | ✅ implemented | — | `Azure Service Bus queue runtime REST API — POST /{queue}/messages?api-version=2021-05` |
| sqs | [SendMessageBatch](sqs.md#sendmessagebatch) | ✅ implemented | — | `Azure Service Bus queue runtime REST API — POST /{queue}/messages with Content-Type: application/vnd.microsoft.servicebus.json` |
| sqs | [SetQueueAttributes](sqs.md#setqueueattributes) | 🟡 partial | — | `Azure Service Bus management REST API — PUT /{queue}?api-version=2021-05 with If-Match: * (whole-entity replace)` |
| sqs | [TagQueue](sqs.md#tagqueue) | 🟡 partial | — | `GET + PUT QueueDescription with aws2azure's base64 tag blob stored in UserMetadata.` |
| sqs | [UntagQueue](sqs.md#untagqueue) | 🟡 partial | — | `GET + PUT QueueDescription with aws2azure's base64 tag blob stored in UserMetadata.` |
