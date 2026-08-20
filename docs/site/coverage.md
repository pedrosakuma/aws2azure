# Coverage matrix

For adoption decisions, start with the generated [workload compatibility](workload-compatibility.md) guide.

| Service | Operation | Status | Disposition | Tracking | Real-Azure | Azure equivalent |
|---|---|---|---|---|---|---|
| dynamodb | [BatchGetItem](operations/dynamodb/batchgetitem.md) | 🟡 partial | 🔵 by design | — | ✅ | `Azure Cosmos DB (Core SQL API)` |
| dynamodb | [BatchWriteItem](operations/dynamodb/batchwriteitem.md) | 🟡 partial | 🔵 by design | — | ✅ | `Azure Cosmos DB (Core SQL API)` |
| dynamodb | [CreateTable](operations/dynamodb/createtable.md) | ✅ implemented | — | — | ✅ | `Azure Cosmos DB (Core SQL API) — POST /dbs/{db}/colls` |
| dynamodb | [DeleteItem](operations/dynamodb/deleteitem.md) | 🟡 partial | 🔵 by design | — | — | `Azure Cosmos DB (Core SQL API)` |
| dynamodb | [DeleteTable](operations/dynamodb/deletetable.md) | ✅ implemented | — | — | ✅ | `Azure Cosmos DB (Core SQL API) — DELETE /dbs/{db}/colls/{name}` |
| dynamodb | [DescribeTable](operations/dynamodb/describetable.md) | ✅ implemented | — | — | ✅ | `Azure Cosmos DB (Core SQL API) — GET /dbs/{db}/colls/{name} + sidecar metadata` |
| dynamodb | [DescribeTimeToLive](operations/dynamodb/describetimetolive.md) | 🟡 partial | 🔵 by design | — | — | `Azure Cosmos DB container `defaultTtl` / per-item `ttl`` |
| dynamodb | [GetItem](operations/dynamodb/getitem.md) | 🟡 partial | 🔵 by design | — | ✅ | `Azure Cosmos DB (Core SQL API)` |
| dynamodb | [ListTables](operations/dynamodb/listtables.md) | ✅ implemented | — | — | ✅ | `Azure Cosmos DB (Core SQL API) — GET /dbs/{db}/colls` |
| dynamodb | [ListTagsOfResource](operations/dynamodb/listtagsofresource.md) | ✅ implemented | — | — | ✅ | `Azure Cosmos DB account/resource tags (control plane)` |
| dynamodb | [PutItem](operations/dynamodb/putitem.md) | 🟡 partial | 🔵 by design | — | — | `Azure Cosmos DB (Core SQL API)` |
| dynamodb | [Query](operations/dynamodb/query.md) | 🟡 partial | 🔵 by design | — | ✅ | `Azure Cosmos DB (Core SQL API)` |
| dynamodb | [Scan](operations/dynamodb/scan.md) | 🟡 partial | 🔵 by design | — | ✅ | `Azure Cosmos DB (Core SQL API)` |
| dynamodb | [TagResource](operations/dynamodb/tagresource.md) | ✅ implemented | — | — | ✅ | `Azure Cosmos DB account/resource tags (control plane)` |
| dynamodb | [TransactGetItems](operations/dynamodb/transactgetitems.md) | 🟡 partial | 🔵 by design | — | ✅ | `Azure Cosmos DB (Core SQL API) — single-partition read-only stored-procedure snapshot` |
| dynamodb | [TransactWriteItems](operations/dynamodb/transactwriteitems.md) | 🟡 partial | 🔵 by design | — | ✅ | `Azure Cosmos DB (Core SQL API) — single-partition stored-procedure transaction` |
| dynamodb | [UntagResource](operations/dynamodb/untagresource.md) | ✅ implemented | — | — | ✅ | `Azure Cosmos DB account/resource tags (control plane)` |
| dynamodb | [UpdateItem](operations/dynamodb/updateitem.md) | 🟡 partial | 🔵 by design | — | ✅ | `Azure Cosmos DB (Core SQL API)` |
| dynamodb | [UpdateTimeToLive](operations/dynamodb/updatetimetolive.md) | 🟡 partial | 🔵 by design | — | — | `Azure Cosmos DB container `defaultTtl` / per-item `ttl`` |
| kinesis | [DescribeStream](operations/kinesis/describestream.md) | 🟡 partial | 🔵 by design | — | ✅ | `Azure Event Hubs Service Bus management REST API` |
| kinesis | [DescribeStreamSummary](operations/kinesis/describestreamsummary.md) | 🟡 partial | 🔵 by design | — | ✅ | `Azure Event Hubs Service Bus management REST API` |
| kinesis | [GetRecords](operations/kinesis/getrecords.md) | 🟡 partial | 🔵 by design | — | ✅ | `Azure Event Hubs (AMQP 1.0 data plane)` |
| kinesis | [GetShardIterator](operations/kinesis/getsharditerator.md) | 🟡 partial | 🔵 by design | — | ✅ | `Azure Event Hubs (AMQP 1.0 data plane)` |
| kinesis | [ListShards](operations/kinesis/listshards.md) | 🟡 partial | 🔵 by design | — | ✅ | `Azure Event Hubs Service Bus management REST API` |
| kinesis | [PutRecord](operations/kinesis/putrecord.md) | 🟡 partial | 🔵 by design | — | ✅ | `Azure Event Hubs (AMQP 1.0 data plane)` |
| kinesis | [PutRecords](operations/kinesis/putrecords.md) | 🟡 partial | 🔵 by design | — | ✅ | `Azure Event Hubs (AMQP 1.0 data plane)` |
| s3 | [AbortMultipartUpload](operations/s3/abortmultipartupload.md) | ✅ implemented | — | — | ✅ | `Lease state record + delete proxy-owned multipart state blob` |
| s3 | [CompleteMultipartUpload](operations/s3/completemultipartupload.md) | ✅ implemented | — | — | ✅ | `Lease state record + Put Block List` |
| s3 | [CopyObject](operations/s3/copyobject.md) | ✅ implemented | — | — | ✅ | `PUT https://{account}.blob.core.windows.net/{container}/{blob} with x-ms-copy-source` |
| s3 | [CreateBucket](operations/s3/createbucket.md) | ✅ implemented | — | — | ✅ | `PUT https://{account}.blob.core.windows.net/{container}?restype=container` |
| s3 | [CreateMultipartUpload](operations/s3/createmultipartupload.md) | ✅ implemented | — | — | ✅ | `HEAD container + proxy-owned durable multipart state record` |
| s3 | [DeleteBucket](operations/s3/deletebucket.md) | ✅ implemented | — | — | ✅ | `DELETE https://{account}.blob.core.windows.net/{container}?restype=container` |
| s3 | [DeleteBucketCors](operations/s3/deletebucketcors.md) | ⚪ stub | 🔵 by design | — | — | `(no equivalent — proxy treats it as a no-op)` |
| s3 | [DeleteBucketEncryption](operations/s3/deletebucketencryption.md) | 🟡 partial | 🔵 by design | — | — | `Conditional container-metadata update` |
| s3 | [DeleteBucketLifecycle](operations/s3/deletebucketlifecycle.md) | ⚪ stub | 🔵 by design | — | — | `(no equivalent — proxy treats it as a no-op)` |
| s3 | [DeleteBucketOwnershipControls](operations/s3/deletebucketownershipcontrols.md) | 🟡 partial | 🔵 by design | — | — | `Conditional container-metadata update` |
| s3 | [DeleteBucketPolicy](operations/s3/deletebucketpolicy.md) | ⚪ stub | 🔵 by design | — | — | `(no equivalent — proxy treats it as a no-op)` |
| s3 | [DeleteBucketReplication](operations/s3/deletebucketreplication.md) | ⚪ stub | 🔵 by design | — | — | `(no equivalent — proxy treats it as a no-op)` |
| s3 | [DeleteBucketTagging](operations/s3/deletebuckettagging.md) | ✅ implemented | — | — | ✅ | `Conditional GET + PUT {container}?restype=container&comp=metadata` |
| s3 | [DeleteBucketWebsite](operations/s3/deletebucketwebsite.md) | ⚪ stub | 🔵 by design | — | — | `(no equivalent — proxy treats it as a no-op)` |
| s3 | [DeleteObject](operations/s3/deleteobject.md) | ✅ implemented | — | — | ✅ | `DELETE https://{account}.blob.core.windows.net/{container}/{blob}` |
| s3 | [DeleteObjectTagging](operations/s3/deleteobjecttagging.md) | ✅ implemented | — | — | ✅ | `PUT {blob}?comp=tags with an empty <TagSet/>` |
| s3 | [DeleteObjects](operations/s3/deleteobjects.md) | ✅ implemented | — | — | ✅ | `Multiple DELETEs against Blob (no native batch endpoint)` |
| s3 | [DeletePublicAccessBlock](operations/s3/deletepublicaccessblock.md) | 🟡 partial | 🔵 by design | — | — | `Conditional container-metadata update` |
| s3 | [GetBucketAccelerateConfiguration](operations/s3/getbucketaccelerateconfiguration.md) | 🟡 partial | 🔵 by design | — | — | `(no equivalent — proxy returns stable Suspended)` |
| s3 | [GetBucketAcl](operations/s3/getbucketacl.md) | 🟡 partial | 🔵 by design | — | — | `(no Azure equivalent — synthetic ownership-only response)` |
| s3 | [GetBucketCors](operations/s3/getbucketcors.md) | ⛔ unsupported | 🔵 by design | — | — | `(no equivalent — proxy returns 404 NoSuchCORSConfiguration)` |
| s3 | [GetBucketEncryption](operations/s3/getbucketencryption.md) | 🟡 partial | 🔵 by design | — | — | `Container metadata for SSE-S3 intent; Azure Storage encryption remains account-managed` |
| s3 | [GetBucketLifecycleConfiguration](operations/s3/getbucketlifecycleconfiguration.md) | ⛔ unsupported | 🔵 by design | — | — | `(no equivalent — proxy returns 404 NoSuchLifecycleConfiguration)` |
| s3 | [GetBucketLogging](operations/s3/getbucketlogging.md) | ⚪ stub | 🔵 by design | — | — | `(no equivalent — proxy returns an empty <BucketLoggingStatus/> document)` |
| s3 | [GetBucketNotificationConfiguration](operations/s3/getbucketnotificationconfiguration.md) | ⚪ stub | 🔵 by design | — | — | `(no equivalent — proxy returns an empty <NotificationConfiguration/> document)` |
| s3 | [GetBucketOwnershipControls](operations/s3/getbucketownershipcontrols.md) | 🟡 partial | 🔵 by design | — | — | `Container metadata (persisted compatibility intent only)` |
| s3 | [GetBucketPolicy](operations/s3/getbucketpolicy.md) | ⛔ unsupported | 🔵 by design | — | — | `(no equivalent — proxy returns 404 NoSuchBucketPolicy)` |
| s3 | [GetBucketPolicyStatus](operations/s3/getbucketpolicystatus.md) | ⛔ unsupported | 🔵 by design | — | — | `(no equivalent — proxy returns 404 NoSuchBucketPolicy)` |
| s3 | [GetBucketReplication](operations/s3/getbucketreplication.md) | ⛔ unsupported | 🔵 by design | — | — | `(no equivalent — proxy returns 404 ReplicationConfigurationNotFoundError)` |
| s3 | [GetBucketRequestPayment](operations/s3/getbucketrequestpayment.md) | 🟡 partial | 🔵 by design | — | — | `(no equivalent — proxy returns the S3 default body)` |
| s3 | [GetBucketTagging](operations/s3/getbuckettagging.md) | 🟡 partial | 🔵 by design | — | ✅ | `GET {container}?restype=container&comp=metadata (single opaque metadata blob)` |
| s3 | [GetBucketVersioning](operations/s3/getbucketversioning.md) | 🟡 partial | 🔵 by design | — | ✅ | `Container metadata (per-bucket toggle); reflects stored PutBucketVersioning intent` |
| s3 | [GetBucketWebsite](operations/s3/getbucketwebsite.md) | ⛔ unsupported | 🔵 by design | — | — | `(no equivalent — proxy returns 404 NoSuchWebsiteConfiguration)` |
| s3 | [GetObject](operations/s3/getobject.md) | ✅ implemented | — | — | ✅ | `GET https://{account}.blob.core.windows.net/{container}/{blob}` |
| s3 | [GetObjectAcl](operations/s3/getobjectacl.md) | 🟡 partial | 🔵 by design | — | — | `(no Azure equivalent — synthetic ownership-only response)` |
| s3 | [GetObjectLegalHold](operations/s3/getobjectlegalhold.md) | ✅ implemented | — | — | ✅ | `Blob legal hold (HEAD blob: x-ms-legal-hold)` |
| s3 | [GetObjectLockConfiguration](operations/s3/getobjectlockconfiguration.md) | ⛔ unsupported | 🔵 by design | — | — | `(bucket-level WORM is ARM/management-plane only; proxy returns 404 ObjectLockConfigurationNotFoundError)` |
| s3 | [GetObjectRetention](operations/s3/getobjectretention.md) | ✅ implemented | — | — | ✅ | `Blob immutability policy (HEAD blob: x-ms-immutability-policy-mode/-until-date)` |
| s3 | [GetObjectTagging](operations/s3/getobjecttagging.md) | ✅ implemented | — | — | ✅ | `GET {blob}?comp=tags (Azure Blob Index Tags)` |
| s3 | [GetObjectTorrent](operations/s3/getobjecttorrent.md) | ⛔ unsupported | ⚫ non-goal | — | — | `(no equivalent — proxy returns 501 NotImplemented)` |
| s3 | [GetPublicAccessBlock](operations/s3/getpublicaccessblock.md) | 🟡 partial | 🔵 by design | — | — | `Container metadata (persisted compatibility intent only)` |
| s3 | [HeadBucket](operations/s3/headbucket.md) | ✅ implemented | — | — | ✅ | `HEAD https://{account}.blob.core.windows.net/{container}?restype=container` |
| s3 | [HeadObject](operations/s3/headobject.md) | ✅ implemented | — | — | ✅ | `HEAD https://{account}.blob.core.windows.net/{container}/{blob}` |
| s3 | [ListBuckets](operations/s3/listbuckets.md) | ✅ implemented | — | — | ✅ | `GET https://{account}.blob.core.windows.net/?comp=list` |
| s3 | [ListMultipartUploads](operations/s3/listmultipartuploads.md) | 🟡 partial | 🔵 by design | — | ✅ | `Proxy-owned multipart state container (Azure has no native cross-blob MPU enumeration primitive)` |
| s3 | [ListObjectVersions](operations/s3/listobjectversions.md) | 🟡 partial | 🔵 by design | — | ✅ | `GET {container}?restype=container&comp=list&include=versions` |
| s3 | [ListObjects](operations/s3/listobjects.md) | ✅ implemented | — | — | ✅ | `GET https://{account}.blob.core.windows.net/{container}?restype=container&comp=list` |
| s3 | [ListObjectsV2](operations/s3/listobjectsv2.md) | ✅ implemented | — | — | ✅ | `GET https://{account}.blob.core.windows.net/{container}?restype=container&comp=list` |
| s3 | [ListParts](operations/s3/listparts.md) | ✅ implemented | — | — | ✅ | `Proxy state HEAD/verification + Get Block List (?comp=blocklist&blocklisttype=uncommitted)` |
| s3 | [PresignedUrl](operations/s3/presignedurl.md) | ✅ implemented | — | — | ✅ | `(no operation — feature-flag; presigned URLs reuse GetObject / PutObject / HeadObject / DeleteObject paths)` |
| s3 | [PutBucketAccelerateConfiguration](operations/s3/putbucketaccelerateconfiguration.md) | 🟡 partial | 🔵 by design | — | — | `(no equivalent — Suspended is an accepted stable no-op)` |
| s3 | [PutBucketAcl](operations/s3/putbucketacl.md) | 🟡 partial | 🔵 by design | — | — | `(no Azure equivalent — validates owner-only intent and replies 200)` |
| s3 | [PutBucketCors](operations/s3/putbucketcors.md) | ⛔ unsupported | 🔵 by design | — | — | `(no equivalent — proxy returns 501 NotImplemented)` |
| s3 | [PutBucketEncryption](operations/s3/putbucketencryption.md) | 🟡 partial | 🔵 by design | — | — | `Conditional container-metadata update for SSE-S3 intent` |
| s3 | [PutBucketLifecycleConfiguration](operations/s3/putbucketlifecycleconfiguration.md) | ⛔ unsupported | 🔵 by design | — | — | `(no equivalent — proxy returns 501 NotImplemented)` |
| s3 | [PutBucketLogging](operations/s3/putbucketlogging.md) | ⛔ unsupported | 🔵 by design | — | — | `(no equivalent — proxy returns 501 NotImplemented)` |
| s3 | [PutBucketNotificationConfiguration](operations/s3/putbucketnotificationconfiguration.md) | ⛔ unsupported | 🔵 by design | — | — | `(no equivalent — proxy returns 501 NotImplemented)` |
| s3 | [PutBucketOwnershipControls](operations/s3/putbucketownershipcontrols.md) | 🟡 partial | 🔵 by design | — | — | `Conditional container-metadata update (persisted compatibility intent only)` |
| s3 | [PutBucketPolicy](operations/s3/putbucketpolicy.md) | ⛔ unsupported | 🔵 by design | — | — | `(no equivalent — proxy returns 501 NotImplemented)` |
| s3 | [PutBucketReplication](operations/s3/putbucketreplication.md) | ⛔ unsupported | 🔵 by design | — | — | `(no equivalent — proxy returns 501 NotImplemented)` |
| s3 | [PutBucketRequestPayment](operations/s3/putbucketrequestpayment.md) | 🟡 partial | 🔵 by design | — | — | `(no equivalent — BucketOwner is an accepted stable no-op)` |
| s3 | [PutBucketTagging](operations/s3/putbuckettagging.md) | 🟡 partial | 🔵 by design | — | ✅ | `PUT {container}?restype=container&comp=metadata` |
| s3 | [PutBucketVersioning](operations/s3/putbucketversioning.md) | 🟡 partial | 🔵 by design | — | ✅ | `Container metadata (per-bucket toggle); account-level Blob versioning assumed pre-enabled` |
| s3 | [PutBucketWebsite](operations/s3/putbucketwebsite.md) | ⛔ unsupported | 🔵 by design | — | — | `(no equivalent — proxy returns 501 NotImplemented)` |
| s3 | [PutObject](operations/s3/putobject.md) | ✅ implemented | — | — | ✅ | `PUT https://{account}.blob.core.windows.net/{container}/{blob}` |
| s3 | [PutObjectAcl](operations/s3/putobjectacl.md) | 🟡 partial | 🔵 by design | — | — | `(no Azure equivalent — validates owner-only intent and replies 200)` |
| s3 | [PutObjectLegalHold](operations/s3/putobjectlegalhold.md) | ✅ implemented | — | — | ✅ | `Set Blob Legal Hold (PUT blob ?comp=legalhold, x-ms-legal-hold)` |
| s3 | [PutObjectLockConfiguration](operations/s3/putobjectlockconfiguration.md) | ⛔ unsupported | 🔵 by design | — | — | `(bucket-level WORM is ARM/management-plane only; proxy returns 501 NotImplemented)` |
| s3 | [PutObjectRetention](operations/s3/putobjectretention.md) | ✅ implemented | — | — | ✅ | `Set Blob Immutability Policy (PUT blob ?comp=immutabilityPolicies)` |
| s3 | [PutObjectTagging](operations/s3/putobjecttagging.md) | ✅ implemented | — | — | ✅ | `PUT {blob}?comp=tags` |
| s3 | [PutPublicAccessBlock](operations/s3/putpublicaccessblock.md) | 🟡 partial | 🔵 by design | — | — | `Conditional container-metadata update (persisted compatibility intent only)` |
| s3 | [RestoreObject](operations/s3/restoreobject.md) | ⛔ unsupported | 🔵 by design | — | — | `(no equivalent — proxy returns 501 NotImplemented)` |
| s3 | [UploadPart](operations/s3/uploadpart.md) | ✅ implemented | — | — | ✅ | `Proxy state HEAD/verification + Put Block (?comp=block&blockid=…)` |
| s3 | [UploadPartCopy](operations/s3/uploadpartcopy.md) | ✅ implemented | — | — | ✅ | `Proxy state HEAD/verification + Put Block From URL (?comp=block&blockid=…)` |
| secretsmanager | [CreateSecret](operations/secretsmanager/createsecret.md) | ✅ implemented | — | — | ✅ | `PUT https://{vault}.vault.azure.net/secrets/{name}` |
| secretsmanager | [DeleteSecret](operations/secretsmanager/deletesecret.md) | ✅ implemented | — | — | ✅ | `DELETE https://{vault}.vault.azure.net/secrets/{name}` |
| secretsmanager | [DescribeSecret](operations/secretsmanager/describesecret.md) | ✅ implemented | — | — | ✅ | `GET https://{vault}.vault.azure.net/secrets/{name}?api-version=7.4` |
| secretsmanager | [GetSecretValue](operations/secretsmanager/getsecretvalue.md) | ✅ implemented | — | — | ✅ | `GET https://{vault}.vault.azure.net/secrets/{name}/versions/{version?}` |
| secretsmanager | [ListSecrets](operations/secretsmanager/listsecrets.md) | ✅ implemented | — | — | ✅ | `GET https://{vault}.vault.azure.net/secrets?api-version=7.4` |
| secretsmanager | [PutSecretValue](operations/secretsmanager/putsecretvalue.md) | 🟡 partial | 🔵 by design | — | ✅ | `PUT https://{vault}.vault.azure.net/secrets/{name}` |
| secretsmanager | [RotateSecret](operations/secretsmanager/rotatesecret.md) | ⛔ unsupported | ⚫ non-goal | — | — | `None — Azure Key Vault has no equivalent managed-rotation trigger the proxy can drive` |
| secretsmanager | [UpdateSecret](operations/secretsmanager/updatesecret.md) | ✅ implemented | — | — | ✅ | `PUT https://{vault}.vault.azure.net/secrets/{name}/versions` |
| sns | [ConfirmSubscription](operations/sns/confirmsubscription.md) | 🟡 partial | 🔵 by design | — | ✅ | `Azure Service Bus topic subscriptions` |
| sns | [CreateTopic](operations/sns/createtopic.md) | 🟡 partial | 🛠️ feasible backlog | [#692](https://github.com/pedrosakuma/aws2azure/issues/692) | ✅ | `Azure Service Bus Topics management REST API` |
| sns | [DeleteTopic](operations/sns/deletetopic.md) | 🟡 partial | 🛠️ feasible backlog | [#692](https://github.com/pedrosakuma/aws2azure/issues/692) | ✅ | `Azure Service Bus Topics management REST API` |
| sns | [GetSubscriptionAttributes](operations/sns/getsubscriptionattributes.md) | 🟡 partial | 🔵 by design | — | ✅ | `Azure Service Bus subscription description` |
| sns | [GetTopicAttributes](operations/sns/gettopicattributes.md) | 🟡 partial | 🔵 by design | — | ✅ | `Azure Service Bus topic description` |
| sns | [ListSubscriptions](operations/sns/listsubscriptions.md) | 🟡 partial | 🔵 by design | — | ✅ | `Azure Service Bus topic subscriptions` |
| sns | [ListSubscriptionsByTopic](operations/sns/listsubscriptionsbytopic.md) | 🟡 partial | 🔵 by design | — | ✅ | `Azure Service Bus topic subscriptions` |
| sns | [ListTopics](operations/sns/listtopics.md) | 🟡 partial | 🛠️ feasible backlog | [#692](https://github.com/pedrosakuma/aws2azure/issues/692) | ✅ | `Azure Service Bus Topics management REST API` |
| sns | [Publish](operations/sns/publish.md) | 🟡 partial | 🔵 by design | — | ✅ | `Azure Service Bus Topics / Azure Event Grid` |
| sns | [PublishBatch](operations/sns/publishbatch.md) | 🟡 partial | 🔵 by design | — | ✅ | `Azure Service Bus Topics / Azure Event Grid` |
| sns | [SetSubscriptionAttributes](operations/sns/setsubscriptionattributes.md) | 🟡 partial | 🛠️ feasible backlog | [#691](https://github.com/pedrosakuma/aws2azure/issues/691) | ✅ | `Azure Service Bus subscription description` |
| sns | [SetTopicAttributes](operations/sns/settopicattributes.md) | 🟡 partial | 🔵 by design | — | ✅ | `Azure Service Bus topic description` |
| sns | [Subscribe](operations/sns/subscribe.md) | 🟡 partial | ⚫ non-goal | — | ✅ | `Azure Service Bus topic subscriptions` |
| sns | [Unsubscribe](operations/sns/unsubscribe.md) | 🟡 partial | 🔵 by design | — | ✅ | `Azure Service Bus topic subscriptions` |
| sqs | [AddPermission](operations/sqs/addpermission.md) | ⚪ stub | 🔵 by design | — | — | `No native Service Bus equivalent — validates queue existence and returns success.` |
| sqs | [ChangeMessageVisibility](operations/sqs/changemessagevisibility.md) | 🟡 partial | 🔵 by design | — | — | `Azure Service Bus queue runtime REST API — PUT /{queue}/messages/{messageId}/{lockToken}?api-version=2021-05 for visibility=0 (unlock), POST to the same path for positive values (renew-lock); AMQP — visibility=0 maps to Abandon and positive values use `com.microsoft:renew-lock`.` |
| sqs | [ChangeMessageVisibilityBatch](operations/sqs/changemessagevisibilitybatch.md) | 🟡 partial | 🔵 by design | — | — | `Azure Service Bus queue runtime REST API — bounded parallel PUT Unlock calls for VisibilityTimeout=0 and POST RenewLock calls for positive values.` |
| sqs | [CreateQueue](operations/sqs/createqueue.md) | ✅ implemented | — | — | ✅ | `PUT https://{namespace}.servicebus.windows.net/{queue}?api-version=2021-05 (Atom QueueDescription)` |
| sqs | [DeleteMessage](operations/sqs/deletemessage.md) | ✅ implemented | — | — | ✅ | `Azure Service Bus queue runtime REST API — DELETE /{queue}/messages/{messageId}/{lockToken}?api-version=2021-05` |
| sqs | [DeleteMessageBatch](operations/sqs/deletemessagebatch.md) | ✅ implemented | — | — | ✅ | `Azure Service Bus queue runtime REST API — N parallel DELETE /{queue}/messages/{messageId}/{lockToken}?api-version=2021-05` |
| sqs | [DeleteQueue](operations/sqs/deletequeue.md) | ✅ implemented | — | — | ✅ | `DELETE https://{namespace}.servicebus.windows.net/{queue}?api-version=2021-05` |
| sqs | [GetQueueAttributes](operations/sqs/getqueueattributes.md) | 🟡 partial | 🔵 by design | — | — | `GET https://{namespace}.servicebus.windows.net/{queue}?api-version=2021-05 (Atom QueueDescription)` |
| sqs | [GetQueueUrl](operations/sqs/getqueueurl.md) | ✅ implemented | — | — | ✅ | `GET https://{namespace}.servicebus.windows.net/{queue}?api-version=2021-05 (existence probe)` |
| sqs | [ListDeadLetterSourceQueues](operations/sqs/listdeadlettersourcequeues.md) | ✅ implemented | — | — | ✅ | `Page through SB management GET /$Resources/queues?api-version=2021-05 and filter entries whose ForwardDeadLetteredMessagesTo equals the requested queue.` |
| sqs | [ListQueueTags](operations/sqs/listqueuetags.md) | 🟡 partial | 🛠️ feasible backlog | [#693](https://github.com/pedrosakuma/aws2azure/issues/693) | — | `GET QueueDescription and decode aws2azure's compact metadata envelope from UserMetadata.` |
| sqs | [ListQueues](operations/sqs/listqueues.md) | ✅ implemented | — | — | ✅ | `GET https://{namespace}.servicebus.windows.net/$Resources/queues?api-version=2021-05&$skip=N&$top=M` |
| sqs | [PurgeQueue](operations/sqs/purgequeue.md) | 🟡 partial | 🔵 by design | — | — | `Azure Service Bus queue runtime REST API — emulated via drain-loop of POST /{queue}/messages/head + DELETE /{queue}/messages/{id}/{lockToken}` |
| sqs | [ReceiveMessage](operations/sqs/receivemessage.md) | ✅ implemented | — | — | ✅ | `Azure Service Bus queue runtime REST API — POST /{queue}/messages/head?timeout={waitSeconds}&api-version=2021-05 (peek-lock semantics)` |
| sqs | [RemovePermission](operations/sqs/removepermission.md) | ⚪ stub | 🔵 by design | — | — | `No native Service Bus equivalent — validates queue existence and returns success.` |
| sqs | [SendMessage](operations/sqs/sendmessage.md) | ✅ implemented | — | — | ✅ | `Azure Service Bus queue runtime REST API — POST /{queue}/messages?api-version=2021-05` |
| sqs | [SendMessageBatch](operations/sqs/sendmessagebatch.md) | ✅ implemented | — | — | ✅ | `Azure Service Bus queue runtime REST API — POST /{queue}/messages with Content-Type: application/vnd.microsoft.servicebus.json` |
| sqs | [SetQueueAttributes](operations/sqs/setqueueattributes.md) | 🟡 partial | 🔵 by design | — | — | `Azure Service Bus management REST API — PUT /{queue}?api-version=2021-05 with If-Match: * (whole-entity replace)` |
| sqs | [TagQueue](operations/sqs/tagqueue.md) | 🟡 partial | 🛠️ feasible backlog | [#693](https://github.com/pedrosakuma/aws2azure/issues/693) | — | `GET + PUT QueueDescription with aws2azure's compact metadata envelope stored in UserMetadata.` |
| sqs | [UntagQueue](operations/sqs/untagqueue.md) | 🟡 partial | 🛠️ feasible backlog | [#693](https://github.com/pedrosakuma/aws2azure/issues/693) | — | `GET + PUT QueueDescription with aws2azure's compact metadata envelope stored in UserMetadata.` |
