# aws2azure — perf baseline

Generated: 2026-05-28T22:28:07.3874762Z

Closed-loop concurrent driver — AWS SDK clients pointing at the proxy
(`Aws2Azure.Proxy`) which fronts local emulators (Azurite, Service Bus,
Cosmos DB, Event Hubs). **Numbers are emulator-bound — they reflect proxy
overhead, not real-Azure throughput.**

| Scenario                         | Cnc | Elap s |  Cmpltd | Failed | Throughput/s |  p50 ms  |  p95 ms  |  p99 ms  |  max ms  | Notes |
|----------------------------------|----:|-------:|--------:|-------:|-------------:|---------:|---------:|---------:|---------:|-------|
| kinesis.PutRecord (256 B)        |   1 |   60.0 |        0 |       0 |          0.0 |       0.0 |       0.0 |       0.0 |       0.0 | Kinesis→EventHubs(AMQP) emulator — customHttp=False HTTP=n/a |
| kinesis.GetRecords (256 B records) |   1 |   30.0 |       91 |       0 |          3.0 |     502.5 |     506.0 |     515.0 |     515.0 | Kinesis←EventHubs(AMQP) emulator — GetRecords drain (limit=100, shard=shardId-000000000002); calls/s metric |
| azure-sdk.EventHubs.ReceiveBatchAsync (256 B records) |   1 |   30.0 |      122 |       0 |          4.1 |      22.9 |     504.2 |     505.8 |     506.3 | Azure SDK baseline — direct PartitionReceiver.ReceiveBatchAsync against EH emulator (no proxy); records=3300, dataCalls=64, emptyCalls=58; calls/s metric |
| azure-sdk.EventHubs.SendAsync (256 B, c=1) |   1 |   60.0 |     4947 |       0 |         82.4 |       5.4 |       9.0 |      12.2 |   11916.5 | Azure SDK baseline — direct EventHubProducerClient against EH emulator (no proxy) |
| dynamodb.PutItem (small)         |  16 |   20.0 |     2622 |       0 |        131.1 |     113.4 |     184.7 |     220.8 |     260.3 | DynamoDB→Cosmos (REST) emulator |
| dynamodb.Scan (pushable filter)  |   4 |   20.0 |     4629 |       0 |        231.4 |      15.6 |      27.0 |      41.8 |     245.8 | DynamoDB→Cosmos Scan — FilterPushdownVisitor (BETWEEN on score, Limit=100) |
| dynamodb.BatchWriteItem (25 items) |   8 |   20.0 |      104 |       0 |          5.2 |    1283.4 |    3272.7 |    3578.3 |    3600.0 | DynamoDB→Cosmos BatchWriteItem — 25 PutRequest/call |
| dynamodb.Query (pushable filter) |   8 |   20.0 |    10356 |       0 |        517.7 |      15.0 |      19.7 |      22.9 |      52.8 | DynamoDB→Cosmos Query — FilterPushdownVisitor (pushable eq on bucket) |
| s3.GetObject (64 KiB)            |  16 |   20.0 |     5873 |       0 |        293.6 |      52.9 |      70.2 |      92.0 |     167.6 | S3→Azurite GetObject — 64 KiB random reads |
| kinesis.PutRecords (25×256 B)    |   1 |   30.0 |      109 |       0 |          3.6 |     247.1 |     428.3 |    1023.8 |    1677.3 | Kinesis→EventHubs(AMQP) emulator — PutRecords (25 records/call) |
| s3.ListObjectsV2 (500 keys)      |  16 |   20.0 |      479 |       0 |         23.9 |     536.2 |    1048.6 |    4568.7 |    6593.7 | S3→Azurite ListObjectsV2 — 500 keys under a prefix |
| sqs.SendMessage (256 B)          |  16 |  120.0 |    12889 |     191 |        107.4 |      16.0 |      28.5 |    1777.6 |    6261.2 | SQS→ServiceBus(AMQP) emulator |
| sns.Publish (256 B)              |  16 |  120.0 |    12821 |     215 |        106.8 |      21.5 |      36.6 |     281.1 |    4633.1 | SNS→ServiceBusTopics(AMQP) emulator |
| s3.PutObject (4 KiB)             |  16 |   20.0 |     3995 |       0 |        199.7 |      75.1 |     118.2 |     189.6 |     400.9 | S3→Azurite (blob REST) |
| azure-sdk.Blob.UploadAsync (4 KiB) |  16 |   20.0 |     3639 |       0 |        181.9 |      75.8 |     188.7 |     231.2 |     436.5 | Azure SDK baseline — direct BlobClient.UploadAsync against Azurite (no proxy) |
| azure-sdk.Cosmos.UpsertItem (small) |  16 |   20.0 |     3311 |       0 |        165.5 |      95.4 |     113.1 |     131.5 |     148.0 | Azure SDK baseline — direct CosmosClient.UpsertItemAsync against Cosmos emulator (no proxy) |
| dynamodb.BatchGetItem (25 items) |   8 |   20.0 |      508 |       0 |         25.4 |     303.2 |     368.0 |     395.8 |     670.1 | DynamoDB→Cosmos BatchGetItem — 25 keys, single partition |
| dynamodb.GetItem (small)         |  16 |   20.0 |    13861 |       0 |        692.9 |      22.1 |      29.2 |      34.6 |     263.3 | DynamoDB→Cosmos GetItem — point read against seeded QueryTable |
| azure-sdk.Cosmos.ReadItem (small) |  16 |   20.0 |    13490 |       0 |        674.4 |      23.2 |      29.4 |      32.8 |      41.5 | Azure SDK baseline — direct CosmosClient.ReadItemAsync against Cosmos emulator (no proxy) |
| azure-sdk.Cosmos.ReadManyItems (25 keys) |   8 |   20.0 |     6338 |       0 |        316.7 |      24.5 |      31.8 |      35.1 |      51.6 | Azure SDK baseline — direct CosmosClient.ReadManyItemsAsync against Cosmos emulator (no proxy) |
