# sqs design gap / PurgeQueue is best-effort emulation {#design-gap-sqs-purgequeue-is-best-effort-emulation}

[← Design-gap index](../../design-gaps.md)

- **Capability ID:** `design-gap:sqs:purgequeue-is-best-effort-emulation`
- **Status:** 🔵 by design

Service Bus has no native purge. The proxy emulates it by draining peek-locked messages and deleting them within a bounded 60s budget, so the queue is best-effort empty rather than guaranteed empty at the end of the call.

**Impact.** Under sustained high producer rates the drain may not keep up, so a purge can leave residual messages — unlike the SQS contract, which guarantees all messages enqueued at the time of the call are deleted.

**Workaround.** Pause producers before purging when a hard empty is required.

