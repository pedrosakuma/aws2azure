# aws2azure — perf baseline

Generated: 2026-05-25T14:46:29.9400478Z

Closed-loop concurrent driver — AWS SDK clients pointing at the proxy
(`Aws2Azure.Proxy`) which fronts local emulators (Azurite, Service Bus,
Cosmos DB, Event Hubs). **Numbers are emulator-bound — they reflect proxy
overhead, not real-Azure throughput.**

| Scenario                         | Cnc | Elap s |  Cmpltd | Failed | Throughput/s |  p50 ms  |  p95 ms  |  p99 ms  |  max ms  | Notes |
|----------------------------------|----:|-------:|--------:|-------:|-------------:|---------:|---------:|---------:|---------:|-------|
| s3.PutObject (4 KiB)             |  16 |   20.0 |     3490 |       0 |        174.4 |      81.0 |     156.7 |     302.4 |     572.3 | S3→Azurite (blob REST) |
| dynamodb.PutItem (small)         |  16 |   20.0 |     1806 |       0 |         90.3 |     165.0 |     246.8 |     318.4 |     675.7 | DynamoDB→Cosmos (REST) emulator |
| azure-sdk.Blob.UploadAsync (4 KiB) |  16 |   20.0 |     3254 |       0 |        162.6 |      91.8 |     150.9 |     280.9 |     452.0 | Azure SDK baseline — direct BlobClient.UploadAsync against Azurite (no proxy) |
| azure-sdk.Cosmos.UpsertItem (small) |  16 |   20.0 |     2383 |       0 |        119.1 |     130.2 |     157.2 |     236.8 |     456.9 | Azure SDK baseline — direct CosmosClient.UpsertItemAsync against Cosmos emulator (no proxy) |
| kinesis.PutRecord (256 B)        |   1 |   60.0 |     4508 |       0 |         75.1 |      12.3 |      20.6 |      28.4 |     109.4 | Kinesis→EventHubs(AMQP) emulator — customHttp=False HTTP=n/a |
| sqs.SendMessage (256 B)          |  16 |   20.0 |     2911 |      17 |        145.5 |      29.4 |      48.7 |     325.7 |    5279.8 | SQS→ServiceBus(AMQP) emulator |
| sns.Publish (256 B)              |  16 |   20.0 |     2906 |      13 |        145.3 |      31.9 |      60.5 |     857.6 |    3728.2 | SNS→ServiceBusTopics(AMQP) emulator |
| azure-sdk.ServiceBusTopics.SendMessage (256 B) |  16 |   20.0 |     1013 |       0 |         50.7 |      21.0 |      34.8 |   10028.2 |   12934.4 | Azure SDK baseline — direct ServiceBusSender against SB emulator topic (no proxy) |
| sqs.ReceiveMessage+Delete (1)    |  16 |   20.0 |     6058 |       0 |        302.8 |      52.6 |      56.6 |      60.3 |     128.1 | SQS→ServiceBus(AMQP) emulator — receive+delete; empty receives count as no-op calls |
| kinesis.GetRecords (256 B records) |   1 |   30.0 |      101 |       0 |          3.4 |     505.6 |     509.7 |     512.5 |     515.1 | Kinesis←EventHubs(AMQP) emulator — GetRecords drain (limit=100, shard=shardId-000000000001); calls/s metric |
| azure-sdk.ServiceBus.ReceiveMessage+Complete (1) |  16 |   20.0 |     7090 |       0 |        354.4 |      51.5 |      53.9 |      56.0 |      70.8 | Azure SDK baseline — direct ServiceBusReceiver receive+complete against SB emulator queue (no proxy); empty receives count as no-op calls |
| azure-sdk.EventHubs.ReceiveBatchAsync (256 B records) |   1 |   30.0 |      129 |       0 |          4.3 |       9.5 |     502.7 |     504.9 |     504.9 | Azure SDK baseline — direct PartitionReceiver.ReceiveBatchAsync against EH emulator (no proxy); records=3300, dataCalls=71, emptyCalls=58; calls/s metric |
| azure-sdk.ServiceBus.SendMessage (256 B, queue) |  16 |   20.0 |     2095 |       0 |        104.7 |      15.3 |      25.5 |      33.7 |   14169.3 | Azure SDK baseline — direct ServiceBusSender against SB emulator queue (no proxy) |
| azure-sdk.EventHubs.SendAsync (256 B, c=1) |   1 |   60.0 |     3675 |       0 |         61.2 |       7.2 |      11.8 |      15.9 |   13896.8 | Azure SDK baseline — direct EventHubProducerClient against EH emulator (no proxy) |
