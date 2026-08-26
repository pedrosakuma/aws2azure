# kinesis design gap / Idempotent or exclusive Event Hubs producers are unsupported {#design-gap-kinesis-idempotent-or-exclusive-event-hubs-producers-are-unsupported}

[← Design-gap index](../../design-gaps.md)

- **Capability ID:** `design-gap:kinesis:idempotent-or-exclusive-event-hubs-producers-are-unsupported`
- **Status:** 🔵 by design

PutRecord/PutRecords open an ordinary AMQP sender link and set only standard Event Hubs message annotations. The Kinesis module does not negotiate Event Hubs idempotent-partition producer state or owner-level/exclusive-producer semantics for a partition.

**Impact.** Event Hubs entities that require idempotent publishing, or partitions already held by another exclusive producer, can reject Kinesis writes with Azure-side producer errors that have no AWS Kinesis equivalent.

**Workaround.** Use ordinary non-exclusive Event Hubs producers for streams behind the Kinesis module. Do not enable idempotent or exclusive-producer requirements on those entities.

## References

- <https://learn.microsoft.com/en-us/azure/event-hubs/event-hubs-features#idempotency>

