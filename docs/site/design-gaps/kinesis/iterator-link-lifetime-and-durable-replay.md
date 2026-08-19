# kinesis design gap / Iterator link lifetime and durable replay {#design-gap-kinesis-iterator-link-lifetime-and-durable-replay}

[← Design-gap index](../../design-gaps.md)

- **Capability ID:** `design-gap:kinesis:iterator-link-lifetime-and-durable-replay`
- **Status:** 🔵 by design

Each proxy-issued shard iterator has a distinct identity and therefore a distinct pooled AMQP receiver link. Iterator chains advance independently while their links are live. The embedded continuation position recreates a link after failure, idle eviction, or restart, but the supported profile deliberately stops at one consumer loop per partition and consumer group.

**Impact.** Multiple iterator identities on one consumer group are not a certified durable consumer-ownership or replay topology. Recreated links resume from the best available Event Hubs offset/enqueue-time position and inherit the synthetic-sequence and millisecond-boundary differences documented above.

**Workaround.** Use one consumer loop per partition. Assign distinct Event Hubs consumer groups when consumers require independently operated replay/checkpoint lifecycles.

