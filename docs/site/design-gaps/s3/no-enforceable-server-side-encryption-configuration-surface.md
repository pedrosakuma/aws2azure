# s3 design gap / No enforceable server-side-encryption configuration surface {#design-gap-s3-no-enforceable-server-side-encryption-configuration-surface}

[← Design-gap index](../../design-gaps.md)

- **Capability ID:** `design-gap:s3:no-enforceable-server-side-encryption-configuration-surface`
- **Status:** 🔵 by design

Azure Blob Storage encrypts at rest transparently. The proxy can persist some S3 encryption intent, but it does not map SSE request headers to distinct Azure key material or KMS workflows.

**Impact.** Applications that require SSE-C/SSE-KMS semantics cannot preserve them.

**Workaround.** Configure encryption and customer-managed keys at the Azure Storage account level.

