# sns design gap / Service Bus Geo-DR requires alias-based failover planning {#design-gap-sns-service-bus-geo-dr-requires-alias-based-failover-planning}

[← Design-gap index](../../design-gaps.md)

- **Capability ID:** `design-gap:sns:service-bus-geo-dr-requires-alias-based-failover-planning`
- **Status:** 🔵 by design
- **Disposition:** 🔵 by design

Service Bus-backed SNS publish and subscription-management flows inherit Service Bus Geo-DR's alias model. The proxy must target the Geo-DR alias hostname/FQDN to follow failover; direct primary-namespace endpoints keep pointing at the old region. Geo-DR replicates topic/subscription metadata, including proxy-managed SNS subscription descriptions, but not queued topic/subscription messages or other live broker state.

**Impact.** Regional failover for Service Bus-backed SNS workloads is not transparent unless callers route through the alias and rehearse failover. Proxy-created subscription entities survive as metadata, but buffered deliveries in those subscriptions do not; workloads that require active/active message replication or preserved in-flight backlog need Service Bus Geo-Replication instead of Geo-DR.

**Workaround.** Configure Service Bus-backed SNS bindings to use the Geo-DR alias for data-plane and management-plane endpoints, use safe failover so pending metadata replications complete, and choose Geo-Replication when the workload must preserve queued topic/subscription data across regions.

## References

- <https://learn.microsoft.com/en-us/azure/service-bus-messaging/service-bus-geo-dr>

