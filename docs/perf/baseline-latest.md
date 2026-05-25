# aws2azure — perf baseline

Generated: 2026-05-25T01:56:22.9566397Z

Closed-loop concurrent driver — AWS SDK clients pointing at the proxy
(`Aws2Azure.Proxy`) which fronts local emulators (Azurite, Service Bus,
Cosmos DB, Event Hubs). **Numbers are emulator-bound — they reflect proxy
overhead, not real-Azure throughput.**

| Scenario                         | Cnc | Elap s |  Cmpltd | Failed | Throughput/s |  p50 ms  |  p95 ms  |  p99 ms  |  max ms  | Notes |
|----------------------------------|----:|-------:|--------:|-------:|-------------:|---------:|---------:|---------:|---------:|-------|
| s3.PutObject (4 KiB)             |  16 |   20.0 |     3282 |       0 |        164.0 |      89.3 |     165.3 |     262.7 |     550.8 | S3→Azurite (blob REST) |
| dynamodb.PutItem (small)         |  16 |   20.0 |     1439 |       0 |         71.9 |     202.3 |     371.1 |     508.9 |     611.6 | DynamoDB→Cosmos (REST) emulator |
| azure-sdk.Blob.UploadAsync (4 KiB) |  16 |   20.0 |     2722 |       0 |        136.0 |     111.6 |     194.1 |     292.5 |     470.8 | Azure SDK baseline — direct BlobClient.UploadAsync against Azurite (no proxy) |
| azure-sdk.Cosmos.UpsertItem (small) |  16 |   20.0 |     1920 |       0 |         96.0 |     144.6 |     311.0 |     452.7 |     590.5 | Azure SDK baseline — direct CosmosClient.UpsertItemAsync against Cosmos emulator (no proxy) |
| kinesis.PutRecord (256 B)        |   1 |   60.0 |     3752 |       0 |         62.5 |      13.5 |      30.3 |      57.7 |     175.1 | Kinesis→EventHubs(AMQP) emulator — customHttp=False HTTP=n/a |
| sqs.SendMessage (256 B)          |  16 |   20.0 |     1068 |       0 |         53.4 |     282.6 |     403.1 |     456.0 |     673.6 | SQS→ServiceBus(AMQP) emulator |
| sns.Publish (256 B)              |  16 |   20.0 |     2150 |      32 |        107.4 |      42.9 |      93.6 |     538.3 |    3419.0 | SNS→ServiceBusTopics(AMQP) emulator |
| azure-sdk.ServiceBusTopics.SendMessage (256 B) |  16 |   20.0 |     2484 |       3 |        124.2 |      26.7 |      55.2 |     107.2 |   14124.6 | Azure SDK baseline — direct ServiceBusSender against SB emulator topic (no proxy) |
| sqs.ReceiveMessage+Delete (1)    |  16 |   20.0 |     1843 |       0 |         92.1 |      28.6 |     834.7 |     842.2 |     848.2 | SQS→ServiceBus(AMQP) emulator — receive+delete; empty receives count as no-op calls |
| kinesis.GetRecords (256 B records) |   1 |   30.0 |      101 |       0 |          3.4 |     505.7 |     513.7 |     515.9 |     518.7 | Kinesis←EventHubs(AMQP) emulator — GetRecords drain (limit=100, shard=shardId-000000000001); calls/s metric |
| azure-sdk.ServiceBus.ReceiveMessage+Complete (1) |  16 |   20.0 |     6755 |       0 |        337.7 |      51.7 |      54.0 |      56.7 |      79.8 | Azure SDK baseline — direct ServiceBusReceiver receive+complete against SB emulator queue (no proxy); empty receives count as no-op calls |
| azure-sdk.EventHubs.ReadEventsFromPartition (256 B records) |   1 |   30.0 |       67 |       0 |          2.2 |     506.6 |     512.4 |     520.2 |     520.2 | Azure SDK baseline — direct EventHubConsumerClient.ReadEventsFromPartitionAsync against EH emulator (no proxy); records=900, dataCalls=9, emptyCalls=58; calls/s metric |
| azure-sdk.ServiceBus.SendMessage (256 B, queue) |  16 |   20.0 |     1720 |       0 |         86.0 |      19.9 |      34.5 |    1259.5 |   12930.1 | Azure SDK baseline — direct ServiceBusSender against SB emulator queue (no proxy) |
| azure-sdk.EventHubs.SendAsync (256 B, c=1) |   1 |   60.0 |     4353 |       0 |         72.5 |       9.0 |      15.7 |      24.6 |    9870.3 | Azure SDK baseline — direct EventHubProducerClient against EH emulator (no proxy) |
