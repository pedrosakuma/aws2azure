# aws2azure — perf baseline

Generated: 2026-05-23T23:11:00.7795629Z

Closed-loop concurrent driver — AWS SDK clients pointing at the proxy
(`Aws2Azure.Proxy`) which fronts local emulators (Azurite, Service Bus,
Cosmos DB, Event Hubs). **Numbers are emulator-bound — they reflect proxy
overhead, not real-Azure throughput.**

| Scenario                         | Cnc | Elap s |  Cmpltd | Failed | Throughput/s |  p50 ms  |  p95 ms  |  p99 ms  |  max ms  | Notes |
|----------------------------------|----:|-------:|--------:|-------:|-------------:|---------:|---------:|---------:|---------:|-------|
| s3.PutObject (4 KiB)             |  16 |   20.0 |     3874 |       0 |        193.5 |      72.9 |     135.4 |     258.0 |    1023.3 | S3→Azurite (blob REST) |
| dynamodb.PutItem (small)         |  16 |   20.0 |     2142 |       0 |        107.1 |     135.6 |     245.4 |     402.3 |     459.1 | DynamoDB→Cosmos (REST) emulator |
| kinesis.PutRecord (256 B)        |  16 |   20.0 |       99 |       0 |          4.9 |      60.8 |     359.6 |     517.2 |     517.2 | Kinesis→EventHubs(AMQP) emulator |
| sqs.SendMessage (256 B)          |  16 |   20.0 |     2005 |       1 |        100.2 |     134.2 |     266.1 |     302.1 |    1447.0 | SQS→ServiceBus(AMQP) emulator |
| sns.Publish (256 B)              |  16 |   20.0 |     2004 |      40 |        100.2 |      25.3 |      57.2 |     733.3 |    4511.7 | SNS→ServiceBusTopics(AMQP) emulator |
