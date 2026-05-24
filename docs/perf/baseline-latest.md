# aws2azure — perf baseline

Generated: 2026-05-24T19:58:35.7836664Z

Closed-loop concurrent driver — AWS SDK clients pointing at the proxy
(`Aws2Azure.Proxy`) which fronts local emulators (Azurite, Service Bus,
Cosmos DB, Event Hubs). **Numbers are emulator-bound — they reflect proxy
overhead, not real-Azure throughput.**

| Scenario                         | Cnc | Elap s |  Cmpltd | Failed | Throughput/s |  p50 ms  |  p95 ms  |  p99 ms  |  max ms  | Notes |
|----------------------------------|----:|-------:|--------:|-------:|-------------:|---------:|---------:|---------:|---------:|-------|
| s3.PutObject (4 KiB)             |  16 |   20.0 |     3862 |       0 |        193.0 |      73.7 |     135.1 |     286.2 |    1098.4 | S3→Azurite (blob REST) |
| dynamodb.PutItem (small)         |  16 |   20.0 |     1642 |       0 |         81.9 |     174.1 |     302.2 |     356.9 |     526.3 | DynamoDB→Cosmos (REST) emulator |
| kinesis.PutRecord (256 B)        |   1 |   60.0 |     4520 |       0 |         75.3 |      10.8 |      22.5 |      34.2 |    3769.2 | Kinesis→EventHubs(AMQP) emulator — customHttp=False HTTP=n/a |
| sqs.SendMessage (256 B)          |  16 |   20.0 |     1465 |       0 |         73.2 |     211.1 |     293.3 |     327.6 |     380.8 | SQS→ServiceBus(AMQP) emulator |
| sns.Publish (256 B)              |  16 |   20.0 |     2492 |      40 |        124.6 |      28.8 |      53.3 |     250.1 |    4526.8 | SNS→ServiceBusTopics(AMQP) emulator |
| sqs.ReceiveMessage+Delete (1)    |  16 |   20.0 |     2105 |       0 |        105.2 |      19.7 |     835.3 |     839.4 |     849.7 | SQS→ServiceBus(AMQP) emulator — receive+delete; empty receives count as no-op calls |
| kinesis.GetRecords (256 B records) |   1 |   30.0 |      101 |       0 |          3.4 |     506.0 |     515.4 |     524.6 |     526.1 | Kinesis←EventHubs(AMQP) emulator — GetRecords drain (limit=100, shard=shardId-000000000003); calls/s metric |
| azure-sdk.EventHubs.SendAsync (256 B, c=1) |   1 |   60.0 |     3610 |       0 |         60.2 |       6.3 |      11.3 |      16.3 |   15904.2 | Azure SDK baseline — direct EventHubProducerClient against EH emulator (no proxy) |
