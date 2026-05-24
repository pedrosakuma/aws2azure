# aws2azure — perf baseline

Generated: 2026-05-24T19:42:14.7092038Z

Closed-loop concurrent driver — AWS SDK clients pointing at the proxy
(`Aws2Azure.Proxy`) which fronts local emulators (Azurite, Service Bus,
Cosmos DB, Event Hubs). **Numbers are emulator-bound — they reflect proxy
overhead, not real-Azure throughput.**

| Scenario                         | Cnc | Elap s |  Cmpltd | Failed | Throughput/s |  p50 ms  |  p95 ms  |  p99 ms  |  max ms  | Notes |
|----------------------------------|----:|-------:|--------:|-------:|-------------:|---------:|---------:|---------:|---------:|-------|
| s3.PutObject (4 KiB)             |  16 |   20.0 |     3189 |       0 |        159.4 |      87.6 |     189.6 |     347.0 |     936.3 | S3→Azurite (blob REST) |
| dynamodb.PutItem (small)         |  16 |   20.0 |     1915 |       0 |         95.7 |     156.5 |     226.1 |     315.2 |     550.4 | DynamoDB→Cosmos (REST) emulator |
| kinesis.PutRecord (256 B)        |   1 |   60.0 |     4384 |       0 |         73.1 |      10.9 |      21.1 |      28.5 |    6517.3 | Kinesis→EventHubs(AMQP) emulator — customHttp=False HTTP=n/a |
| sqs.SendMessage (256 B)          |  16 |   20.0 |     1407 |       0 |         70.3 |     216.6 |     309.7 |     356.1 |     383.4 | SQS→ServiceBus(AMQP) emulator |
| sns.Publish (256 B)              |  16 |   20.0 |     2906 |      20 |        145.3 |      32.7 |      89.5 |     913.7 |    4539.3 | SNS→ServiceBusTopics(AMQP) emulator |
| sqs.ReceiveMessage+Delete (1)    |  16 |   20.0 |     1620 |       0 |         81.0 |      22.7 |     836.6 |     844.0 |     846.2 | SQS→ServiceBus(AMQP) emulator — receive+delete; empty receives count as no-op calls |
| kinesis.GetRecords (256 B records) |   1 |   30.0 |       57 |       0 |          1.9 |     510.3 |     530.2 |    1115.0 |    1115.0 | Kinesis←EventHubs(AMQP) emulator — GetRecords drain (limit=100); calls/s metric |
| azure-sdk.EventHubs.SendAsync (256 B, c=1) |   1 |   60.0 |     3196 |       0 |         53.3 |       6.5 |      14.0 |      20.8 |   13890.4 | Azure SDK baseline — direct EventHubProducerClient against EH emulator (no proxy) |
