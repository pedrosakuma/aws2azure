# s3 / CopyObject {#operation-s3-copyobject}

[← s3 operation index](../../s3.md) · [Coverage matrix](../../coverage.md)

- **Capability ID:** `operation:s3:copyobject`
- **Status:** ✅ implemented
- **Azure equivalent:** `PUT https://{account}.blob.core.windows.net/{container}/{blob} with x-ms-copy-source`
- **Real-Azure verified:** ✅ 2026-08-11 · [evidence](https://github.com/pedrosakuma/aws2azure/actions/runs/31447675330) · [workflow run](https://github.com/pedrosakuma/aws2azure/actions/runs/31447675330)

## Sub-features

### x-amz-copy-source (path-prefixed + legacy form) {#sub-feature-x-amz-copy-source--path-prefixed--legacy-form}

- **Capability ID:** `sub-feature:s3:copyobject:x-amz-copy-source--path-prefixed--legacy-form`
- **Status:** ✅ implemented

**Gap.** URL-decoded before issuing to Azure. The bucket/key separator is accepted as a literal '/' (hand-built/legacy callers) or as a percent-encoded '%2F' — the official AWS SDKs fully percent-encode the value when marshalling CopyObjectRequest, so '{bucket}%2F{key}' is the default wire form.

### source conditional headers (if-match / if-none-match / if-modified-since / if-unmodified-since) {#sub-feature-source-conditional-headers--if-match---if-none-match---if-modified-since---if-unmodified-since}

- **Capability ID:** `sub-feature:s3:copyobject:source-conditional-headers--if-match---if-none-match---if-modified-since---if-unmodified-since`
- **Status:** ✅ implemented

Date-based conditionals forward to Azure. Concrete-ETag if-match is evaluated proxy-side and then guarded on the Azure copy request; concrete if-none-match is supported when the source is version-pinned (explicit versionId or Azure current-version pinning), otherwise the proxy returns NotImplemented rather than risk a wrong-source copy.

### x-amz-metadata-directive=COPY (default) {#sub-feature-x-amz-metadata-directivecopy--default}

- **Capability ID:** `sub-feature:s3:copyobject:x-amz-metadata-directivecopy--default`
- **Status:** ✅ implemented

**Gap.** Azure preserves source metadata when no x-ms-meta-* is sent, matching S3 COPY semantics. The proxy strips its own hidden multipart part-count marker from the copied metadata so destination HEAD/GET/CopyObjectResult do not inherit a stale multipart-shaped ETag.

### x-amz-metadata-directive=REPLACE {#sub-feature-x-amz-metadata-directivereplace}

- **Capability ID:** `sub-feature:s3:copyobject:x-amz-metadata-directivereplace`
- **Status:** ✅ implemented

**Gap.** Request's metadata + Content-Type override source via standard header forwarding.

### x-amz-tagging-directive=COPY (default) {#sub-feature-x-amz-tagging-directivecopy--default}

- **Capability ID:** `sub-feature:s3:copyobject:x-amz-tagging-directivecopy--default`
- **Status:** ✅ implemented

**Gap.** The proxy reads the source blob tags via Azure Get Blob Tags and reapplies them to the destination with Azure Put Blob Tags after the copy succeeds.

### x-amz-tagging-directive=REPLACE {#sub-feature-x-amz-tagging-directivereplace}

- **Capability ID:** `sub-feature:s3:copyobject:x-amz-tagging-directivereplace`
- **Status:** ✅ implemented

**Gap.** x-amz-tagging is parsed and validated with the same rules as PutObject, then applied to the destination with Azure Put Blob Tags after the copy succeeds.

### versionId source qualifier {#sub-feature-versionid-source-qualifier}

- **Capability ID:** `sub-feature:s3:copyobject:versionid-source-qualifier`
- **Status:** ✅ implemented

?versionId on x-amz-copy-source maps to Azure's ?versionid source selector and round-trips the opaque version id.

### cross-account copy (source in a different Azure storage account) {#sub-feature-cross-account-copy--source-in-a-different-azure-storage-account}

- **Capability ID:** `sub-feature:s3:copyobject:cross-account-copy--source-in-a-different-azure-storage-account`
- **Status:** ⛔ unsupported
- **Disposition:** 🔵 by design

**Gap.** Azure cross-account copy is asynchronous; rejected with NotImplemented to avoid reporting fake success.

### ARN copy-source (S3-on-Outposts) {#sub-feature-arn-copy-source--s3-on-outposts}

- **Capability ID:** `sub-feature:s3:copyobject:arn-copy-source--s3-on-outposts`
- **Status:** ⛔ unsupported
- **Disposition:** ⚫ non-goal

## Behaviour differences

- Intra-account copies are synchronous on Azure; the proxy verifies x-ms-copy-status=success before responding 200.
- Tag COPY/REPLACE semantics require additional Azure tag calls outside the copy itself (Get Blob Tags on the source for COPY, then Put Blob Tags on the destination). Tag application is therefore non-atomic with the data copy: if the post-copy tag write fails, CopyObject returns that error even though the destination bytes were already copied.
- ETag in CopyObjectResult is normalised to the same S3-shaped, proxy-translated value HEAD/GET emit for the destination blob (synthetic MD5 of Azure's raw ETag when Content-MD5 is absent), so clients can reuse it across operations without seeing two different ETags for the same object.
- x-amz-copy-source-version-id is not emitted on the CopyObject response; Azure's Copy Blob response does not surface the source blob's version id (Azure returns x-ms-copy-source but no source-version header), so the proxy has no equivalent value to translate back to S3's per-copy header. [conformance:copy-object-roundtrip::missing-header:x-amz-copy-source-version-id]
- x-amz-server-side-encryption is not emitted on the CopyObject response; Azure's Copy Blob response reports encryption state via x-ms-server-encrypted rather than a header the proxy currently mirrors onto CopyObject 200s. [conformance:copy-object-roundtrip::missing-header:x-amz-server-side-encryption]
- ChecksumCRC64NVME / ChecksumType are omitted from CopyObjectResult; Azure Blob does not compute a CRC64NVME checksum or expose an AWS-shaped ChecksumType (FULL_OBJECT / COMPOSITE) for a copied blob. [conformance:copy-object-roundtrip::missing-field:ChecksumCRC64NVME] [conformance:copy-object-roundtrip::missing-field:ChecksumType]
- CopyObjectResult.LastModified reflects the Azure blob's actual write time on the proxy side. When the offline Tier-3 diff compares captures recorded at different wall-clock times against separately seeded backends (real AWS golden vs real-Azure evidence), the two LastModified values will differ; the field itself round-trips correctly for each backend. [conformance:copy-object-roundtrip::field-value:LastModified]

## References

- <https://docs.aws.amazon.com/AmazonS3/latest/API/API_CopyObject.html>
- <https://learn.microsoft.com/rest/api/storageservices/copy-blob>

