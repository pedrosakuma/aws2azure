# s3 design gap / Secondary-region blob reads are not attempted automatically {#design-gap-s3-secondary-region-blob-reads-are-not-attempted-automatically}

[← Design-gap index](../../design-gaps.md)

- **Capability ID:** `design-gap:s3:secondary-region-blob-reads-are-not-attempted-automatically`
- **Status:** 🟡 partial
- **Disposition:** 🔵 by design

RA-GRS and RA-GZRS storage accounts expose a secondary read endpoint, but the proxy currently authenticates GetObject/HeadObject requests only against the configured primary Blob service endpoint and has no secondary-endpoint failover setting.

**Impact.** During a primary endpoint outage, GetObject and HeadObject fail instead of serving potentially stale reads from the Azure secondary region.

**Workaround.** Use Azure account failover, DNS/front-door indirection, or another external read-failover mechanism when the workload requires regional read continuity.

