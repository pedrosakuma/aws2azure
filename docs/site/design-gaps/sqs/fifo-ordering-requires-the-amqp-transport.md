# sqs design gap / FIFO ordering requires the AMQP transport {#design-gap-sqs-fifo-ordering-requires-the-amqp-transport}

[← Design-gap index](../../design-gaps.md)

- **Capability ID:** `design-gap:sqs:fifo-ordering-requires-the-amqp-transport`
- **Status:** 🟡 partial
- **Disposition:** 🔵 by design

Strict per-MessageGroupId ordering is implemented only when a queue is configured with transport: Amqp — the receive path acquires a broker-assigned Service Bus session and holds the session lock so a group's in-flight messages stay pinned to one consumer. The REST transport cannot express session-receive and therefore does not provide strict per-group ordering (it surfaces MessageGroupId but does not block concurrent delivery of the same group).

**Impact.** Workloads that need SQS FIFO guarantees must use the AMQP transport. FIFO settle is connection-affine: an in-flight FIFO message cannot be settled from a different live connection while its session lock is held.

**Workaround.** Set transport: Amqp for .fifo queues; keep the receive-then-delete cycle on the same connection.

## References

- <https://learn.microsoft.com/azure/service-bus-messaging/service-bus-amqp-protocol-guide>

