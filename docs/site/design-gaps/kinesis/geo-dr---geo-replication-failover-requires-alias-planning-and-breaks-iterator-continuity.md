# kinesis design gap / Geo-DR / Geo-Replication failover requires alias planning and breaks iterator continuity {#design-gap-kinesis-geo-dr---geo-replication-failover-requires-alias-planning-and-breaks-iterator-continuity}

[← Design-gap index](../../design-gaps.md)

- **Capability ID:** `design-gap:kinesis:geo-dr---geo-replication-failover-requires-alias-planning-and-breaks-iterator-continuity`
- **Status:** 🔵 by design

Like the Service Bus-backed SQS/SNS paths, the Event Hubs-backed Kinesis module follows regional failover only when the configured namespace/endpoint already targets the Event Hubs Geo-DR or Geo-Replication alias hostname/FQDN. The proxy otherwise derives a single namespace host from EventHubsCredentials.Namespace or its Endpoint override and stays pinned to that host, while shard iterators encode Event Hubs-local offset/sequence/time checkpoints from the original namespace.

**Impact.** Direct primary-namespace configuration loses transparent regional failover. Even when an alias is configured up front, existing shard iterators are not portable Kinesis leases; after failover the reopened selector runs against the secondary namespace's retained event store and can resume from an earlier retained position without a Kinesis-visible checkpoint-loss signal. Geo-DR replicates metadata only, and Geo-Replication does not make previously issued iterator checkpoints portable across namespaces.

**Workaround.** Configure azure.kinesis.target.namespace or endpoint with the Event Hubs alias hostname/FQDN before failover drills, and treat every regional failover as a consumer restart boundary that must reacquire checkpoints out of band.

## References

- <https://learn.microsoft.com/en-us/azure/event-hubs/event-hubs-geo-dr>
- <https://learn.microsoft.com/en-us/azure/event-hubs/event-hubs-geo-replication>

