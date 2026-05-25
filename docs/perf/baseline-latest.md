# aws2azure — perf baseline

Generated: 2026-05-25T16:39:58.8125114Z

Closed-loop concurrent driver — AWS SDK clients pointing at the proxy
(`Aws2Azure.Proxy`) which fronts local emulators (Azurite, Service Bus,
Cosmos DB, Event Hubs). **Numbers are emulator-bound — they reflect proxy
overhead, not real-Azure throughput.**

| Scenario                         | Cnc | Elap s |  Cmpltd | Failed | Throughput/s |  p50 ms  |  p95 ms  |  p99 ms  |  max ms  | Notes |
|----------------------------------|----:|-------:|--------:|-------:|-------------:|---------:|---------:|---------:|---------:|-------|
| kinesis.PutRecord (256 B)        |   1 |   60.0 |     5493 |       0 |         91.5 |       6.6 |      10.9 |      14.7 |    5016.8 | Kinesis→EventHubs(AMQP) emulator — customHttp=False HTTP=n/a |
| kinesis.GetRecords (256 B records) |   1 |   30.0 |       91 |       0 |          3.0 |     502.8 |     506.2 |     512.1 |     512.1 | Kinesis←EventHubs(AMQP) emulator — GetRecords drain (limit=100, shard=shardId-000000000001); calls/s metric |
| azure-sdk.EventHubs.ReceiveBatchAsync (256 B records) |   1 |   30.0 |      111 |       0 |          3.7 |     497.7 |     504.7 |     505.8 |     506.3 | Azure SDK baseline — direct PartitionReceiver.ReceiveBatchAsync against EH emulator (no proxy); records=3400, dataCalls=53, emptyCalls=58; calls/s metric |
| azure-sdk.EventHubs.SendAsync (256 B, c=1) |   1 |   60.0 |     4463 |       0 |         74.4 |       5.4 |       9.3 |      14.1 |   12908.4 | Azure SDK baseline — direct EventHubProducerClient against EH emulator (no proxy) |
| dynamodb.PutItem (small)         |  16 |   20.0 |     2450 |       0 |        122.4 |     121.8 |     169.5 |     286.0 |     448.1 | DynamoDB→Cosmos (REST) emulator |
