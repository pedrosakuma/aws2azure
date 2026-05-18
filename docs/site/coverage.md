# Coverage matrix

| Service | Operation | Status | Azure equivalent |
|---|---|---|---|
| dynamodb | [DeleteItem](dynamodb.md#deleteitem) | ⚪ stub | `Azure Cosmos DB (Core SQL API)` |
| dynamodb | [GetItem](dynamodb.md#getitem) | ⚪ stub | `Azure Cosmos DB (Core SQL API)` |
| dynamodb | [PutItem](dynamodb.md#putitem) | ⚪ stub | `Azure Cosmos DB (Core SQL API)` |
| dynamodb | [Query](dynamodb.md#query) | ⚪ stub | `Azure Cosmos DB (Core SQL API)` |
| dynamodb | [Scan](dynamodb.md#scan) | ⚪ stub | `Azure Cosmos DB (Core SQL API)` |
| dynamodb | [UpdateItem](dynamodb.md#updateitem) | ⚪ stub | `Azure Cosmos DB (Core SQL API)` |
| kinesis | [GetRecords](kinesis.md#getrecords) | ⚪ stub | `Azure Event Hubs Kafka/HTTP surface` |
| kinesis | [GetShardIterator](kinesis.md#getsharditerator) | ⚪ stub | `Azure Event Hubs Kafka/HTTP surface` |
| kinesis | [PutRecord](kinesis.md#putrecord) | ⚪ stub | `Azure Event Hubs Kafka/HTTP surface` |
| kinesis | [PutRecords](kinesis.md#putrecords) | ⚪ stub | `Azure Event Hubs Kafka/HTTP surface` |
| s3 | [CopyObject](s3.md#copyobject) | ⚪ stub | `PUT https://{account}.blob.core.windows.net/{container}/{blob} with x-ms-copy-source` |
| s3 | [CreateBucket](s3.md#createbucket) | ✅ implemented | `PUT https://{account}.blob.core.windows.net/{container}?restype=container` |
| s3 | [DeleteBucket](s3.md#deletebucket) | ✅ implemented | `DELETE https://{account}.blob.core.windows.net/{container}?restype=container` |
| s3 | [DeleteObject](s3.md#deleteobject) | ✅ implemented | `DELETE https://{account}.blob.core.windows.net/{container}/{blob}` |
| s3 | [DeleteObjects](s3.md#deleteobjects) | ⚪ stub | `Multiple DELETEs against Blob (no native batch endpoint)` |
| s3 | [GetObject](s3.md#getobject) | ✅ implemented | `GET https://{account}.blob.core.windows.net/{container}/{blob}` |
| s3 | [HeadBucket](s3.md#headbucket) | ✅ implemented | `HEAD https://{account}.blob.core.windows.net/{container}?restype=container` |
| s3 | [HeadObject](s3.md#headobject) | ✅ implemented | `HEAD https://{account}.blob.core.windows.net/{container}/{blob}` |
| s3 | [ListBuckets](s3.md#listbuckets) | ✅ implemented | `GET https://{account}.blob.core.windows.net/?comp=list` |
| s3 | [ListObjectsV2](s3.md#listobjectsv2) | ⚪ stub | `GET https://{account}.blob.core.windows.net/{container}?restype=container&comp=list` |
| s3 | [PutObject](s3.md#putobject) | ✅ implemented | `PUT https://{account}.blob.core.windows.net/{container}/{blob}` |
| sns | [CreateTopic](sns.md#createtopic) | ⚪ stub | `Azure Service Bus topics / Event Grid (TBD per operation)` |
| sns | [DeleteTopic](sns.md#deletetopic) | ⚪ stub | `Azure Service Bus topics / Event Grid (TBD per operation)` |
| sns | [Publish](sns.md#publish) | ⚪ stub | `Azure Service Bus topics / Event Grid (TBD per operation)` |
| sns | [Subscribe](sns.md#subscribe) | ⚪ stub | `Azure Service Bus topics / Event Grid (TBD per operation)` |
| sns | [Unsubscribe](sns.md#unsubscribe) | ⚪ stub | `Azure Service Bus topics / Event Grid (TBD per operation)` |
| sqs | [CreateQueue](sqs.md#createqueue) | ⚪ stub | `Azure Service Bus (queue) REST API` |
| sqs | [DeleteMessage](sqs.md#deletemessage) | ⚪ stub | `Azure Service Bus (queue) REST API` |
| sqs | [DeleteQueue](sqs.md#deletequeue) | ⚪ stub | `Azure Service Bus (queue) REST API` |
| sqs | [ReceiveMessage](sqs.md#receivemessage) | ⚪ stub | `Azure Service Bus (queue) REST API` |
| sqs | [SendMessage](sqs.md#sendmessage) | ⚪ stub | `Azure Service Bus (queue) REST API` |
