# s3 design gap / Bucket sub-resource configs are not translated {#design-gap-s3-bucket-sub-resource-configs-are-not-translated}

[← Design-gap index](../../design-gaps.md)

- **Capability ID:** `design-gap:s3:bucket-sub-resource-configs-are-not-translated`
- **Status:** ⛔ unsupported
- **Disposition:** 🔵 by design

Lifecycle, replication, website hosting, event notifications, Requester Pays, acceleration, and logging bucket configurations have no Blob-storage equivalent in the wire-protocol path.

**Impact.** Automated tiering/expiry, cross-region replication, static-website hosting, and S3 event automation configured through these APIs have no effect.

**Workaround.** Use Azure Blob lifecycle-management policies, object replication, static website settings, and Event Grid subscriptions configured directly on the storage account.

