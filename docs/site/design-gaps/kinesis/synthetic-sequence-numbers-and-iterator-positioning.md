# kinesis design gap / Synthetic sequence numbers and iterator positioning {#design-gap-kinesis-synthetic-sequence-numbers-and-iterator-positioning}

[← Design-gap index](../../design-gaps.md)

- **Capability ID:** `design-gap:kinesis:synthetic-sequence-numbers-and-iterator-positioning`
- **Status:** 🔵 by design

Kinesis sequence numbers are minted by the proxy as (unixMs << 20) | counter and mapped to an Event Hubs enqueue-time position. AT_SEQUENCE_NUMBER / AFTER_SEQUENCE_NUMBER are therefore best-effort at millisecond granularity, and MillisBehindLatest is derived from the last record's enqueue timestamp versus the proxy clock.

**Impact.** Records sharing a millisecond may be returned together at a sequence-based boundary; exact per-record sequence positioning is not reproducible. ExplicitHashKey and SequenceNumberForOrdering are accepted for wire compatibility but ignored.

**Workaround.** Prefer TRIM_HORIZON / LATEST / AT_TIMESTAMP iterators where exact sequence positioning is not required.

## References

- <https://learn.microsoft.com/rest/api/eventhub/>

