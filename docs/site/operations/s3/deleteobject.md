# s3 / DeleteObject {#operation-s3-deleteobject}

[← s3 operation index](../../s3.md) · [Coverage matrix](../../coverage.md)

- **Capability ID:** `operation:s3:deleteobject`
- **Status:** ✅ implemented
- **Azure equivalent:** `DELETE https://{account}.blob.core.windows.net/{container}/{blob}`
- **Real-Azure verified:** ✅ 2026-07-16 · [evidence](https://github.com/pedrosakuma/aws2azure/actions/runs/29473539261) · [workflow run](https://github.com/pedrosakuma/aws2azure/actions/runs/29473539261)

## Sub-features

### idempotent delete (404 → 204) {#sub-feature-idempotent-delete--404--204}

- **Capability ID:** `sub-feature:s3:deleteobject:idempotent-delete--404--204`
- **Status:** ✅ implemented

**Gap.** Azure returns 404 for a missing blob; the proxy mirrors S3 by returning 204 in that case.

### versioning (versionId query) {#sub-feature-versioning--versionid-query}

- **Capability ID:** `sub-feature:s3:deleteobject:versioning--versionid-query`
- **Status:** 🟡 partial
- **Disposition:** 🔵 by design

**Gap.** ?versionId selector reversibly decodes the client-facing x-amz-version-id token back to Azure ?versionid. Version-scoped deletes round-trip and versionless deletes surface x-amz-delete-marker when Azure reports a soft delete; list-versions delete-marker entries remain unmodelled and require account-level versioning enabled out-of-band.

### MFA delete (x-amz-mfa) {#sub-feature-mfa-delete--x-amz-mfa}

- **Capability ID:** `sub-feature:s3:deleteobject:mfa-delete--x-amz-mfa`
- **Status:** ⛔ unsupported
- **Disposition:** 🔵 by design

### bypass-governance {#sub-feature-bypass-governance}

- **Capability ID:** `sub-feature:s3:deleteobject:bypass-governance`
- **Status:** ⛔ unsupported
- **Disposition:** 🔵 by design

## Behaviour differences

- Soft-delete behavior depends on the configured Azure storage account; the proxy does not toggle it per-request. When blob soft delete is enabled, a successful versionless delete can leave a recoverable soft-deleted blob (and retained billable bytes) until the Azure retention window expires.
- Real S3 returns x-amz-version-id on successful DeleteObject responses, but Azure Blob Delete Blob does not surface an equivalent response header even when blob versioning is enabled; Tier-3 diff allow-lists [conformance:missing-header:x-amz-version-id] for delete-object and delete-object-version teardown steps until Azure exposes one.
- Presigned DELETE is accepted (see PresignedUrl.yaml).

## References

- <https://docs.aws.amazon.com/AmazonS3/latest/API/API_DeleteObject.html>
- <https://learn.microsoft.com/rest/api/storageservices/delete-blob>

