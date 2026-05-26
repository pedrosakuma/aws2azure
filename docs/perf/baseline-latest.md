# aws2azure — perf baseline

Generated: 2026-05-26T20:46:53.4444808Z

Closed-loop concurrent driver — AWS SDK clients pointing at the proxy
(`Aws2Azure.Proxy`) which fronts local emulators (Azurite, Service Bus,
Cosmos DB, Event Hubs). **Numbers are emulator-bound — they reflect proxy
overhead, not real-Azure throughput.**

| Scenario                         | Cnc | Elap s |  Cmpltd | Failed | Throughput/s |  p50 ms  |  p95 ms  |  p99 ms  |  max ms  | Notes |
|----------------------------------|----:|-------:|--------:|-------:|-------------:|---------:|---------:|---------:|---------:|-------|
| kinesis.PutRecord (256 B)        |   1 |  120.0 |    10304 |       0 |         85.9 |       8.8 |      15.2 |      20.8 |    6811.2 | Kinesis→EventHubs(AMQP) emulator — customHttp=False HTTP=n/a |
| kinesis.GetRecords (256 B records) |   1 |   30.0 |       91 |       0 |          3.0 |     502.8 |     506.2 |     512.1 |     512.1 | Kinesis←EventHubs(AMQP) emulator — GetRecords drain (limit=100, shard=shardId-000000000001); calls/s metric |
| azure-sdk.EventHubs.ReceiveBatchAsync (256 B records) |   1 |   30.0 |      111 |       0 |          3.7 |     497.7 |     504.7 |     505.8 |     506.3 | Azure SDK baseline — direct PartitionReceiver.ReceiveBatchAsync against EH emulator (no proxy); records=3400, dataCalls=53, emptyCalls=58; calls/s metric |
| azure-sdk.EventHubs.SendAsync (256 B, c=1) |   1 |   60.0 |     4463 |       0 |         74.4 |       5.4 |       9.3 |      14.1 |   12908.4 | Azure SDK baseline — direct EventHubProducerClient against EH emulator (no proxy) |
| dynamodb.PutItem (small)         |  16 |  120.0 |    16002 |       0 |        133.3 |     113.8 |     153.9 |     181.0 |     536.4 | DynamoDB→Cosmos (REST) emulator |
| dynamodb.Scan (pushable filter)  |   4 |   20.0 |     4895 |       0 |        244.5 |      15.3 |      24.0 |      32.5 |      63.5 | DynamoDB→Cosmos Scan — FilterPushdownVisitor (BETWEEN on score, Limit=100) |
| dynamodb.BatchWriteItem (25 items) |   8 |   20.0 |      121 |       0 |          6.0 |    1226.8 |    1868.1 |    1889.6 |    1928.4 | DynamoDB→Cosmos BatchWriteItem — 25 PutRequest/call |
| dynamodb.Query (pushable filter) |   8 |   20.0 |     7223 |       0 |        361.1 |      21.6 |      26.1 |      30.3 |      61.6 | DynamoDB→Cosmos Query — FilterPushdownVisitor (pushable eq on bucket) |
| s3.GetObject (64 KiB)            |  16 |  120.0 |    75671 |       0 |        630.6 |      24.2 |      35.7 |      44.8 |     106.5 | S3→Azurite GetObject — 64 KiB random reads |
| kinesis.PutRecords (25×256 B)    |   1 |   30.0 |      192 |       0 |          6.4 |     148.8 |     209.1 |     231.6 |     436.2 | Kinesis→EventHubs(AMQP) emulator — PutRecords (25 records/call) |
| s3.ListObjectsV2 (500 keys)      |  16 |  120.0 |     3885 |       0 |         32.4 |     460.3 |     900.2 |     977.2 |    3944.6 | S3→Azurite ListObjectsV2 — 500 keys under a prefix |
| sqs.SendMessage (256 B)          |  16 |  120.0 |    12889 |     191 |        107.4 |      16.0 |      28.5 |    1777.6 |    6261.2 | SQS→ServiceBus(AMQP) emulator |
| sns.Publish (256 B)              |  16 |  120.0 |    12821 |     215 |        106.8 |      21.5 |      36.6 |     281.1 |    4633.1 | SNS→ServiceBusTopics(AMQP) emulator |
