# s3 design gap / Account-level recovery and delete-retention features remain operator-provisioned {#design-gap-s3-account-level-recovery-and-delete-retention-features-remain-operator-provisioned}

[← Design-gap index](../../design-gaps.md)

- **Capability ID:** `design-gap:s3:account-level-recovery-and-delete-retention-features-remain-operator-provisioned`
- **Status:** 🔵 by design
- **Disposition:** 🔵 by design

Azure Blob point-in-time restore, change feed, soft delete, and blob versioning are storage-account features configured outside the proxy. DeleteObject/DeleteObjects follow the account's soft-delete policy, so a successful S3 delete can leave recoverable retained bytes until the Azure retention window expires.

**Impact.** Disaster-recovery plans that expect account-level rollback must pre-enable the Azure features before data lands, and delete-heavy workloads can accumulate retained bytes/quota with no extra S3-side signal beyond the normal delete success path.

**Workaround.** Decide the storage-account recovery posture up front; enable blob versioning + soft delete + change feed together when Azure PITR is required, and monitor retention/cost behavior through Azure's control plane rather than the S3 API.

