# aws2azure — perf baseline

Generated: 2026-05-24T01:19:31.5227748Z

Closed-loop concurrent driver — AWS SDK clients pointing at the proxy
(`Aws2Azure.Proxy`) which fronts local emulators (Azurite, Service Bus,
Cosmos DB, Event Hubs). **Numbers are emulator-bound — they reflect proxy
overhead, not real-Azure throughput.**

| Scenario                         | Cnc | Elap s |  Cmpltd | Failed | Throughput/s |  p50 ms  |  p95 ms  |  p99 ms  |  max ms  | Notes |
|----------------------------------|----:|-------:|--------:|-------:|-------------:|---------:|---------:|---------:|---------:|-------|
| s3.PutObject (4 KiB)             |  16 |   20.0 |     3867 |       0 |        193.3 |      73.5 |     136.2 |     285.9 |     557.9 | S3→Azurite (blob REST) |
| dynamodb.PutItem (small)         |  16 |   20.0 |     2086 |       0 |        104.3 |     145.7 |     215.2 |     298.1 |     422.2 | DynamoDB→Cosmos (REST) emulator |
| kinesis.PutRecord (256 B)        |   1 |   60.0 |     5731 |       0 |         95.5 |       6.3 |      10.9 |      14.9 |    7284.5 | Kinesis→EventHubs(AMQP) emulator — re-measured 2026-05-24 (replaces a stale 1.7/s entry; see issue #129) |
| sqs.SendMessage (256 B)          |  16 |   20.0 |     1754 |       1 |         87.7 |     150.7 |     262.8 |     423.1 |    1482.8 | SQS→ServiceBus(AMQP) emulator |
| sns.Publish (256 B)              |  16 |   20.0 |     2902 |      37 |        145.1 |      24.0 |      66.6 |     323.3 |    4559.4 | SNS→ServiceBusTopics(AMQP) emulator |
