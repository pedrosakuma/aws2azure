# s3 design gap / Object-lock-capable storage accounts must be provisioned up front {#design-gap-s3-object-lock-capable-storage-accounts-must-be-provisioned-up-front}

[← Design-gap index](../../design-gaps.md)

- **Capability ID:** `design-gap:s3:object-lock-capable-storage-accounts-must-be-provisioned-up-front`
- **Status:** 🔵 by design
- **Disposition:** 🔵 by design

S3 object-lock translation relies on Azure Blob version-level immutability plus blob versioning, and Azure only allows version-level immutability support to be enabled when the storage account is created.

**Impact.** Pilots started on an existing storage account without that flag cannot add object-lock support later; adopting the workload can require migrating to a newly provisioned account.

**Workaround.** Provision a dedicated storage account with blob versioning and version-level immutability enabled before adopting object-lock workloads, and validate retention/legal-hold flows against real Azure.

