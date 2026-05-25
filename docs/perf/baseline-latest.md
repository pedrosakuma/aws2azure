# aws2azure — perf baseline

Generated: 2026-05-25T02:37:27.4873096Z

Closed-loop concurrent driver — AWS SDK clients pointing at the proxy
(`Aws2Azure.Proxy`) which fronts local emulators (Azurite, Service Bus,
Cosmos DB, Event Hubs). **Numbers are emulator-bound — they reflect proxy
overhead, not real-Azure throughput.**

| Scenario                         | Cnc | Elap s |  Cmpltd | Failed | Throughput/s |  p50 ms  |  p95 ms  |  p99 ms  |  max ms  | Notes |
|----------------------------------|----:|-------:|--------:|-------:|-------------:|---------:|---------:|---------:|---------:|-------|
| s3.PutObject (4 KiB)             |  16 |   20.0 |     3617 |       0 |        180.8 |      77.2 |     145.7 |     355.4 |    1171.1 | S3→Azurite (blob REST) |
| dynamodb.PutItem (small)         |  16 |   20.0 |     1845 |       0 |         92.2 |     162.1 |     237.1 |     334.2 |     640.5 | DynamoDB→Cosmos (REST) emulator |
| azure-sdk.Blob.UploadAsync (4 KiB) |  16 |   20.0 |     2684 |       0 |        134.2 |     109.5 |     216.8 |     400.5 |     662.3 | Azure SDK baseline — direct BlobClient.UploadAsync against Azurite (no proxy) |
| azure-sdk.Cosmos.UpsertItem (small) |  16 |   20.0 |     1793 |       0 |         89.6 |     155.9 |     291.8 |     482.6 |     651.3 | Azure SDK baseline — direct CosmosClient.UpsertItemAsync against Cosmos emulator (no proxy) |
| kinesis.PutRecord (256 B)        |   1 |   60.0 |     4045 |       0 |         67.4 |      12.8 |      26.6 |      40.5 |     211.8 | Kinesis→EventHubs(AMQP) emulator — customHttp=False HTTP=n/a |
| sqs.SendMessage (256 B)          |  16 |   20.0 |     1403 |       0 |         70.1 |     218.6 |     318.8 |     367.6 |     495.7 | SQS→ServiceBus(AMQP) emulator |
| sns.Publish (256 B)              |  16 |   20.0 |     2783 |      31 |        139.1 |      34.4 |      79.5 |     337.3 |    4511.3 | SNS→ServiceBusTopics(AMQP) emulator |
| azure-sdk.ServiceBusTopics.SendMessage (256 B) |  16 |   20.0 |     2013 |       0 |        100.6 |      22.2 |      37.7 |      46.5 |   14179.3 | Azure SDK baseline — direct ServiceBusSender against SB emulator topic (no proxy) |
| sqs.ReceiveMessage+Delete (1)    |  16 |   20.0 |     6349 |       0 |        317.4 |      52.3 |      56.3 |      58.8 |      94.6 | SQS→ServiceBus(AMQP) emulator — receive+delete; empty receives count as no-op calls |
| kinesis.GetRecords (256 B records) |   1 |   30.0 |      101 |       0 |          3.4 |     506.3 |     512.8 |     516.7 |     518.2 | Kinesis←EventHubs(AMQP) emulator — GetRecords drain (limit=100, shard=shardId-000000000002); calls/s metric |
| azure-sdk.ServiceBus.ReceiveMessage+Complete (1) |  16 |   20.0 |     6722 |       0 |        336.1 |      51.8 |      54.3 |      56.7 |      83.3 | Azure SDK baseline — direct ServiceBusReceiver receive+complete against SB emulator queue (no proxy); empty receives count as no-op calls |
| azure-sdk.EventHubs.ReceiveBatchAsync (256 B records) |   1 |   30.0 |      122 |       0 |          4.1 |      24.9 |     504.2 |     504.5 |     505.3 | Azure SDK baseline — direct PartitionReceiver.ReceiveBatchAsync against EH emulator (no proxy); records=3300, dataCalls=64, emptyCalls=58; calls/s metric |
| azure-sdk.ServiceBus.SendMessage (256 B, queue) |  16 |   20.0 |     1717 |       0 |         85.9 |      15.6 |      25.7 |      33.4 |   14164.3 | Azure SDK baseline — direct ServiceBusSender against SB emulator queue (no proxy) |
| azure-sdk.EventHubs.SendAsync (256 B, c=1) |   1 |   60.0 |     3088 |       0 |         51.5 |       7.2 |      13.2 |      18.9 |   10909.3 | Azure SDK baseline — direct EventHubProducerClient against EH emulator (no proxy) |
