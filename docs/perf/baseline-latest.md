# aws2azure — perf baseline

Generated: 2026-05-25T16:18:10.6873170Z

Closed-loop concurrent driver — AWS SDK clients pointing at the proxy
(`Aws2Azure.Proxy`) which fronts local emulators (Azurite, Service Bus,
Cosmos DB, Event Hubs). **Numbers are emulator-bound — they reflect proxy
overhead, not real-Azure throughput.**

| Scenario                         | Cnc | Elap s |  Cmpltd | Failed | Throughput/s |  p50 ms  |  p95 ms  |  p99 ms  |  max ms  | Notes |
|----------------------------------|----:|-------:|--------:|-------:|-------------:|---------:|---------:|---------:|---------:|-------|
| kinesis.PutRecord (256 B)        |   1 |   60.0 |     5157 |       0 |         85.9 |       8.8 |      16.6 |      30.6 |    5538.9 | Kinesis→EventHubs(AMQP) emulator — customHttp=False HTTP=n/a |
| kinesis.GetRecords (256 B records) |   1 |   30.0 |       90 |       0 |          3.0 |     502.5 |     507.2 |     528.8 |     528.8 | Kinesis←EventHubs(AMQP) emulator — GetRecords drain (limit=100, shard=shardId-000000000000); calls/s metric |
| azure-sdk.EventHubs.ReceiveBatchAsync (256 B records) |   1 |   30.0 |       86 |       0 |          2.9 |     499.6 |     503.5 |     504.7 |     504.7 | Azure SDK baseline — direct PartitionReceiver.ReceiveBatchAsync against EH emulator (no proxy); records=2200, dataCalls=28, emptyCalls=58; calls/s metric |
| azure-sdk.EventHubs.SendAsync (256 B, c=1) |   1 |   60.0 |     3952 |       0 |         65.9 |       6.2 |      11.5 |      16.4 |   11885.2 | Azure SDK baseline — direct EventHubProducerClient against EH emulator (no proxy) |
