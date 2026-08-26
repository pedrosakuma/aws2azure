# sqs design gap / FIFO AMQP production profile expects Service Bus Premium {#design-gap-sqs-fifo-amqp-production-profile-expects-service-bus-premium}

[← Design-gap index](../../design-gaps.md)

- **Capability ID:** `design-gap:sqs:fifo-amqp-production-profile-expects-service-bus-premium`
- **Status:** 🔵 by design

Service Bus Standard can speak AMQP, but aws2azure's `sqs_fifo_amqp` workload profile (sessions, ordered receive, connection-affine settlement, and DLQ/redrive qualification) is documented and sealed against Premium namespaces. `transport: Amqp` alone is therefore not a claim that Standard tier is production-equivalent for FIFO workloads.

**Impact.** Operators can misread the config surface as "AMQP implies FIFO parity" and deploy on Standard, where session-backed ordering, lock behavior, capacity, and SLA expectations have not been qualified as the same production lane.

**Workaround.** Use Service Bus Premium for production FIFO queues. Treat Standard AMQP as a dev/test lane unless you run separate workload-specific qualification and accept degraded guarantees.

## References

- <https://learn.microsoft.com/en-us/azure/service-bus-messaging/service-bus-premium-messaging>

