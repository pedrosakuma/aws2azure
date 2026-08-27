# Before you migrate SQS {#before-you-migrate-sqs}

[← Workload compatibility](../workload-compatibility.md#sqs) · [Design gaps](../design-gaps.md#sqs)

Answer each question with **yes** or **no**.
If you answer **yes**, read the linked design gap and confirm its workaround
fits your workload before migrating.

1. **Are you planning to run production FIFO queues on Service Bus Standard instead of Premium?** → [FIFO AMQP production profile expects Service Bus Premium](../design-gaps/sqs/fifo-amqp-production-profile-expects-service-bus-premium.md)
2. **Does your workload require strict FIFO ordering and receive/delete semantics for the same MessageGroupId?** → [FIFO ordering requires the AMQP transport](../design-gaps/sqs/fifo-ordering-requires-the-amqp-transport.md)
3. **Do you expect Service Bus Geo-DR failover to work without configuring the alias hostname up front?** → [Geo-DR failover requires configuring the alias hostname](../design-gaps/sqs/geo-dr-failover-requires-configuring-the-alias-hostname.md)
4. **Does your app parse real AWS account IDs or regions from queue ARNs?** → [No AWS region / account namespace](../design-gaps/sqs/no-aws-region---account-namespace.md)
5. **Do you need PurgeQueue to guarantee an empty queue while producers are still active?** → [PurgeQueue is best-effort emulation](../design-gaps/sqs/purgequeue-is-best-effort-emulation.md)
6. **Does your workflow rely on QueueDeletedRecently or AWS-style delete/recreate timing?** → [Queue lifecycle eventual-consistency](../design-gaps/sqs/queue-lifecycle-eventual-consistency.md)
7. **Must queue semantics, receipt handles, and batch-failure behavior stay identical across REST and AMQP transports?** → [Transport-dependent capability differences](../design-gaps/sqs/transport-dependent-capability-differences.md)
