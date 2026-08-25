# kinesis / PutRecord {#operation-kinesis-putrecord}

[← kinesis operation index](../../kinesis.md) · [Coverage matrix](../../coverage.md)

- **Capability ID:** `operation:kinesis:putrecord`
- **Status:** 🟡 partial
- **Disposition:** 🔵 by design
- **Azure equivalent:** `Azure Event Hubs (AMQP 1.0 data plane)`
- **Real-Azure verified:** ✅ 2026-07-16 · [evidence](https://github.com/pedrosakuma/aws2azure/actions/runs/29473539261) · [workflow run](https://github.com/pedrosakuma/aws2azure/actions/runs/29473539261)

## Behaviour differences

- SequenceNumber is synthetic and proxy-generated from a per-process monotonic counter; it is not the Event Hubs broker-assigned sequence number or offset. [conformance:field-value:SequenceNumber]
- ShardId is derived client-side by hashing PartitionKey with MD5 and routing to {eventHub}/Partitions/{id}. This matches Event Hubs' historical partitioning algorithm, but the broker may diverge if Azure changes its internal hashing in the future. [conformance:field-value:ShardId]
- DescribeStream/ListShards HashKeyRange values are synthetic even splits of the 128-bit Kinesis hash space; they do not describe the modulo-based write routing the proxy actually applies for PutRecord.
- ExplicitHashKey and SequenceNumberForOrdering are accepted for wire compatibility but ignored.
- EncryptionType is omitted because Event Hubs does not expose AWS-style stream encryption metadata on PutRecord responses.
- The proxy does not emulate Kinesis per-shard write quotas (records/sec or bytes/sec) before sending to Event Hubs; only Azure-originated throttles are mapped back to ProvisionedThroughputExceededException.
- Record publication is validated against production Azure Event Hubs through the real-Azure conformance workflow.

## References

- <https://docs.aws.amazon.com/kinesis/latest/APIReference/API_PutRecord.html>

