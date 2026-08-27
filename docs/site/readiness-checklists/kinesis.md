# Before you migrate Kinesis {#before-you-migrate-kinesis}

[← Workload compatibility](../workload-compatibility.md#kinesis) · [Design gaps](../design-gaps.md#kinesis)

Answer each question with **yes** or **no**.
If you answer **yes**, read the linked design gap and confirm its workaround
fits your workload before migrating.

1. **Do downstream consumers need Event Hubs Capture to archive raw Kinesis record bytes instead of Event Hubs envelopes?** → [Event Hubs Capture archives Event Hubs envelopes, not raw Kinesis bytes](../design-gaps/kinesis/event-hubs-capture-archives-event-hubs-envelopes-not-raw-kinesis-bytes.md)
2. **Do you expect regional failover to preserve iterator continuity without alias planning and checkpoint reacquisition?** → [Geo-DR / Geo-Replication failover requires alias planning and breaks iterator continuity](../design-gaps/kinesis/geo-dr---geo-replication-failover-requires-alias-planning-and-breaks-iterator-continuity.md)
3. **Do your target Event Hubs require idempotent or exclusive producers?** → [Idempotent or exclusive Event Hubs producers are unsupported](../design-gaps/kinesis/idempotent-or-exclusive-event-hubs-producers-are-unsupported.md)
4. **Do you need multiple independently replaying consumers to share one consumer group durably?** → [Iterator link lifetime and durable replay](../design-gaps/kinesis/iterator-link-lifetime-and-durable-replay.md)
5. **Does your workload require resharding, enhanced fan-out, or the KCL lease model?** → [No resharding / enhanced fan-out / KCL lease model](../design-gaps/kinesis/no-resharding---enhanced-fan-out---kcl-lease-model.md)
6. **Do you rely on AWS Kinesis shard-level read/write throttling being enforced locally?** → [No shard-level throughput emulation](../design-gaps/kinesis/no-shard-level-throughput-emulation.md)
7. **Do you require exact AWS sequence-number semantics or replay boundaries from PutRecord/PutRecords responses?** → [Synthetic sequence numbers and iterator positioning](../design-gaps/kinesis/synthetic-sequence-numbers-and-iterator-positioning.md)
