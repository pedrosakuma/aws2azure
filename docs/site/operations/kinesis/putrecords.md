# kinesis / PutRecords {#operation-kinesis-putrecords}

[← kinesis operation index](../../kinesis.md) · [Coverage matrix](../../coverage.md)

- **Capability ID:** `operation:kinesis:putrecords`
- **Status:** 🟡 partial
- **Disposition:** 🔵 by design
- **Azure equivalent:** `Azure Event Hubs (AMQP 1.0 data plane)`
- **Real-Azure verified:** ✅ 2026-07-16 · [evidence](https://github.com/pedrosakuma/aws2azure/actions/runs/29473539261) · [workflow run](https://github.com/pedrosakuma/aws2azure/actions/runs/29473539261)

## Behaviour differences

- Sequence numbers are synthetic proxy-generated values, not Azure Event Hubs offsets. [conformance:field-value:Records[].SequenceNumber]
- ShardId values are derived client-side by hashing PartitionKey with MD5 and mapping the result modulo the Event Hubs partition count. [conformance:field-value:Records[].ShardId]
- Batch sends are still grouped per partition, but broker dispositions are tracked per message; records accepted before a later reject remain successful in the PutRecords response so callers do not retry already-committed messages.
- ExplicitHashKey is ignored; partition routing always follows the PartitionKey hash.
- Batch record publication and per-entry result handling are validated against production Azure Event Hubs through the real-Azure conformance workflow.

## References

- <https://docs.aws.amazon.com/kinesis/latest/APIReference/API_PutRecords.html>

