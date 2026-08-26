# kinesis design gap / Event Hubs Capture archives Event Hubs envelopes, not raw Kinesis bytes {#design-gap-kinesis-event-hubs-capture-archives-event-hubs-envelopes-not-raw-kinesis-bytes}

[← Design-gap index](../../design-gaps.md)

- **Capability ID:** `design-gap:kinesis:event-hubs-capture-archives-event-hubs-envelopes-not-raw-kinesis-bytes`
- **Status:** 🔵 by design

Event Hubs Capture is an Event Hubs control-plane archive feature, not a Kinesis-aware export path. When the proxy publishes PutRecord/PutRecords into Event Hubs, Capture persists the resulting Event Hubs event representation and broker metadata rather than a raw AWS Kinesis record stream.

**Impact.** Operators who enable Capture for downstream analytics can be surprised that the archived payload is Event Hubs-shaped data with Event Hubs/AMQP context, not a clean dump of the original Kinesis record bytes alone.

**Workaround.** Treat Capture as Event Hubs-native archival and plan any downstream decoding around the captured Event Hubs envelope. Use a separate export/ETL path when consumers require raw Kinesis-compatible payload extraction.

## References

- <https://learn.microsoft.com/en-us/azure/event-hubs/event-hubs-capture-overview>

