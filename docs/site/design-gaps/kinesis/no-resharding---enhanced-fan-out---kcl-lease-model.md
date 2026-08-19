# kinesis design gap / No resharding / enhanced fan-out / KCL lease model {#design-gap-kinesis-no-resharding---enhanced-fan-out---kcl-lease-model}

[← Design-gap index](../../design-gaps.md)

- **Capability ID:** `design-gap:kinesis:no-resharding---enhanced-fan-out---kcl-lease-model`
- **Status:** ⛔ unsupported
- **Disposition:** 🔵 by design

Kinesis shards map to Event Hubs partitions, which are fixed at the hub level. Dynamic resharding (SplitShard/MergeShards), enhanced fan-out (SubscribeToShard), and the KCL DynamoDB lease/checkpoint model have no in-scope translation.

**Impact.** Applications that resize streams at runtime or rely on enhanced-fan-out throughput isolation cannot drive those code paths through the proxy.

**Workaround.** Provision Event Hubs partition count for peak load up front; use consumer groups for isolation.

