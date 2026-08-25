# kinesis design gap / No shard-level throughput emulation {#design-gap-kinesis-no-shard-level-throughput-emulation}

[← Design-gap index](../../design-gaps.md)

- **Capability ID:** `design-gap:kinesis:no-shard-level-throughput-emulation`
- **Status:** 🔵 by design

The proxy does not maintain a local Kinesis-style per-shard byte, record, or TPS budget for reads or writes. Requests flow through to Event Hubs and only Azure-originated throttles are mapped back to ProvisionedThroughputExceededException.

**Impact.** A workload that AWS Kinesis would throttle at the shard boundary can still succeed locally until Azure Event Hubs applies its own namespace, partition, or consumer throttles.

**Workaround.** Capacity-plan the backing Event Hub for the real workload and treat Kinesis shard throughput limits as documentation-only rather than enforced simulation.

## References

- <https://docs.aws.amazon.com/kinesis/latest/dev/service-sizes-and-limits.html>

