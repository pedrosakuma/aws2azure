# aws2azure — perf baseline

Generated: 2026-05-23T23:27:53.8228051Z

Closed-loop concurrent driver — AWS SDK clients pointing at the proxy
(`Aws2Azure.Proxy`) which fronts local emulators (Azurite, Service Bus,
Cosmos DB, Event Hubs). **Numbers are emulator-bound — they reflect proxy
overhead, not real-Azure throughput.**

| Scenario                         | Cnc | Elap s |  Cmpltd | Failed | Throughput/s |  p50 ms  |  p95 ms  |  p99 ms  |  max ms  | Notes |
|----------------------------------|----:|-------:|--------:|-------:|-------------:|---------:|---------:|---------:|---------:|-------|
| s3.PutObject (4 KiB)             |  16 |   20.0 |     3935 |       0 |        196.7 |      69.4 |     139.4 |     330.4 |    1363.0 | S3→Azurite (blob REST) |
| dynamodb.PutItem (small)         |  16 |   20.0 |     2168 |       0 |        108.4 |     132.0 |     243.4 |     462.2 |     559.6 | DynamoDB→Cosmos (REST) emulator |
| kinesis.PutRecord (256 B)        |   1 |   20.0 |      100 |       0 |          5.0 |      18.7 |      25.5 |      39.2 |     123.0 | Kinesis→EventHubs(AMQP) emulator |
| sqs.SendMessage (256 B)          |  16 |   20.0 |     1987 |       2 |         99.3 |     126.7 |     239.6 |    1388.1 |    1500.4 | SQS→ServiceBus(AMQP) emulator |
| sns.Publish (256 B)              |  16 |   20.0 |     2858 |      41 |        142.9 |      22.4 |      49.9 |     129.4 |    4140.9 | SNS→ServiceBusTopics(AMQP) emulator |
