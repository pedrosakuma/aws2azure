# sqs design gap / Queue lifecycle eventual-consistency {#design-gap-sqs-queue-lifecycle-eventual-consistency}

[← Design-gap index](../../design-gaps.md)

- **Capability ID:** `design-gap:sqs:queue-lifecycle-eventual-consistency`
- **Status:** 🔵 by design

Service Bus deletes queues synchronously, whereas SQS may take up to 60s of eventual consistency and returns QueueDeletedRecently on immediate re-create. The proxy does not currently synthesise QueueDeletedRecently.

**Impact.** Delete-then-recreate-within-seconds patterns that expect the AWS eventual-consistency error will instead succeed immediately.

**Workaround.** Do not rely on QueueDeletedRecently timing behaviour.

