# kinesis design gap / Synthetic sequence numbers and iterator positioning {#design-gap-kinesis-synthetic-sequence-numbers-and-iterator-positioning}

[← Design-gap index](../../design-gaps.md)

- **Capability ID:** `design-gap:kinesis:synthetic-sequence-numbers-and-iterator-positioning`
- **Status:** 🔵 by design

PutRecord/PutRecords sequence numbers are minted by the proxy as (unixMs << 20) | counter and mapped back to Event Hubs enqueue-time positions. GetRecords now returns round-trippable Event Hubs broker sequence tokens as sequence:<x-opt-sequence-number>, but synthetic write sequence numbers remain best-effort only at millisecond granularity.

**Impact.** PutRecord/PutRecords sequence boundaries can include sibling records that share the same millisecond enqueue time; exact per-record replay from synthetic write sequence numbers is not reproducible. ExplicitHashKey and SequenceNumberForOrdering are accepted for wire compatibility but ignored.

**Workaround.** Feed GetRecords-returned sequence:<n> tokens back into AT_SEQUENCE_NUMBER / AFTER_SEQUENCE_NUMBER when callers need exact replay of already-read records; otherwise prefer TRIM_HORIZON, LATEST, or AT_TIMESTAMP.

## References

- <https://learn.microsoft.com/rest/api/eventhub/>

