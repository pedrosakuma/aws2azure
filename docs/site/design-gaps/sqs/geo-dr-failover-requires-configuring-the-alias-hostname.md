# sqs design gap / Geo-DR failover requires configuring the alias hostname {#design-gap-sqs-geo-dr-failover-requires-configuring-the-alias-hostname}

[← Design-gap index](../../design-gaps.md)

- **Capability ID:** `design-gap:sqs:geo-dr-failover-requires-configuring-the-alias-hostname`
- **Status:** 🔵 by design

Azure Service Bus Premium Geo-DR fails over by re-pointing an alias hostname, but the SQS backend configuration exposes only a single namespace/host input (`ServiceBusCredentials.Namespace` in `src/Aws2Azure.Core/Configuration/ProxyConfig.cs`) that the proxy reuses for REST and AMQP endpoint resolution. If operators configure the primary namespace instead of the alias, the proxy stays pinned to the old hostname and failover does not redirect traffic.

**Impact.** Premium Geo-DR deployments lose transparent failover unless the alias hostname is configured up front. Geo-DR replicates metadata only, not in-flight or locked messages; Geo-Replication is a separate feature and is unvalidated by this proxy.

**Workaround.** Configure `azure.sqs.target.namespace` with the Geo-DR alias short name/FQDN (and `managementEndpoint` too when using a custom REST host), then rehearse failover against the alias rather than the primary namespace hostname.

## References

- <https://learn.microsoft.com/en-us/azure/service-bus-messaging/service-bus-geo-dr>
- <https://learn.microsoft.com/en-us/azure/service-bus-messaging/service-bus-geo-replication>

