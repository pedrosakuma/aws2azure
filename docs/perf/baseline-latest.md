# aws2azure — perf baseline

Generated: 2026-05-25T02:13:00.6785724Z

Closed-loop concurrent driver — AWS SDK clients pointing at the proxy
(`Aws2Azure.Proxy`) which fronts local emulators (Azurite, Service Bus,
Cosmos DB, Event Hubs). **Numbers are emulator-bound — they reflect proxy
overhead, not real-Azure throughput.**

| Scenario                         | Cnc | Elap s |  Cmpltd | Failed | Throughput/s |  p50 ms  |  p95 ms  |  p99 ms  |  max ms  | Notes |
|----------------------------------|----:|-------:|--------:|-------:|-------------:|---------:|---------:|---------:|---------:|-------|
| s3.PutObject (4 KiB)             |  16 |   20.0 |     3227 |       0 |        161.3 |      88.3 |     166.4 |     282.1 |     484.4 | S3→Azurite (blob REST) |
| dynamodb.PutItem (small)         |  16 |   20.0 |     1706 |       0 |         85.3 |     175.5 |     253.5 |     491.5 |     723.4 | DynamoDB→Cosmos (REST) emulator |
| azure-sdk.Blob.UploadAsync (4 KiB) |  16 |   20.0 |     3072 |       0 |        153.6 |      98.9 |     151.0 |     305.1 |     463.7 | Azure SDK baseline — direct BlobClient.UploadAsync against Azurite (no proxy) |
| azure-sdk.Cosmos.UpsertItem (small) |  16 |   20.0 |     2127 |       0 |        106.4 |     142.5 |     184.1 |     409.9 |     523.0 | Azure SDK baseline — direct CosmosClient.UpsertItemAsync against Cosmos emulator (no proxy) |
| kinesis.PutRecord (256 B)        |   1 |   60.0 |     4201 |       0 |         70.0 |      12.9 |      22.4 |      31.4 |     152.1 | Kinesis→EventHubs(AMQP) emulator — customHttp=False HTTP=n/a |
| sqs.SendMessage (256 B)          |  16 |   20.0 |     1361 |       0 |         68.0 |     212.8 |     333.5 |     390.2 |     515.1 | SQS→ServiceBus(AMQP) emulator |
| sns.Publish (256 B)              |  16 |   20.0 |     2637 |      33 |        131.8 |      36.0 |     101.9 |     344.1 |    4528.6 | SNS→ServiceBusTopics(AMQP) emulator |
| azure-sdk.ServiceBusTopics.SendMessage (256 B) |  16 |   20.0 |     2017 |       0 |        100.8 |      22.8 |      37.2 |      43.3 |   14186.9 | Azure SDK baseline — direct ServiceBusSender against SB emulator topic (no proxy) |
| sqs.ReceiveMessage+Delete (1)    |  16 |   20.0 |     1759 |       0 |         87.9 |      29.3 |     836.1 |     845.0 |     854.2 | SQS→ServiceBus(AMQP) emulator — receive+delete; empty receives count as no-op calls |
| kinesis.GetRecords (256 B records) |   1 |   30.0 |      101 |       0 |          3.4 |     506.1 |     512.5 |     519.9 |     523.7 | Kinesis←EventHubs(AMQP) emulator — GetRecords drain (limit=100, shard=shardId-000000000003); calls/s metric |
| azure-sdk.ServiceBus.ReceiveMessage+Complete (1) |  16 |   20.0 |     6741 |       0 |        337.0 |      51.7 |      54.1 |      56.2 |      58.1 | Azure SDK baseline — direct ServiceBusReceiver receive+complete against SB emulator queue (no proxy); empty receives count as no-op calls |
| azure-sdk.ServiceBus.SendMessage (256 B, queue) |  16 |   20.0 |     1749 |       0 |         87.4 |      17.0 |      28.1 |    1264.6 |   10018.6 | Azure SDK baseline — direct ServiceBusSender against SB emulator queue (no proxy) |
| azure-sdk.EventHubs.ReceiveBatchAsync (256 B records) |   1 |   30.0 |      132 |       0 |          4.4 |       9.7 |     504.8 |     505.5 |     506.4 | Azure SDK baseline — direct PartitionReceiver.ReceiveBatchAsync against EH emulator (no proxy); records=3400, dataCalls=74, emptyCalls=58; calls/s metric |
| azure-sdk.EventHubs.SendAsync (256 B, c=1) |   1 |   60.0 |     3453 |       0 |         57.5 |       6.7 |      12.6 |      16.9 |   14928.7 | Azure SDK baseline — direct EventHubProducerClient against EH emulator (no proxy) |
