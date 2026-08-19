# sqs design gap / Transport-dependent capability differences {#design-gap-sqs-transport-dependent-capability-differences}

[← Design-gap index](../../design-gaps.md)

- **Capability ID:** `design-gap:sqs:transport-dependent-capability-differences`
- **Status:** 🔵 by design

REST and AMQP transports differ beyond FIFO: receipt-handle formats, VisibilityTimeout=0 immediate release (AMQP only, via Abandon), per-entry partial-failure granularity on batch sends (real on AMQP, coarser on REST), and dead-letter attribution (AMQP only).

**Impact.** The same SQS operation can behave differently depending on the queue's configured transport; receipt handles are not interchangeable across transports.

**Workaround.** Choose the transport per queue based on the semantics required and keep receive/settle on the same transport.

