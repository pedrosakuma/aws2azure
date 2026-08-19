# s3 / CopyObject {#operation-s3-copyobject}

[← s3 operation index](../../s3.md) · [Coverage matrix](../../coverage.md)

- **Capability ID:** `operation:s3:copyobject`
- **Status:** ✅ implemented
- **Azure equivalent:** `PUT https://{account}.blob.core.windows.net/{container}/{blob} with x-ms-copy-source`

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

**Gap.** Azure preserves source metadata when no x-ms-meta-* is sent, matching S3 COPY semantics.

### x-amz-metadata-directive=REPLACE {#sub-feature-x-amz-metadata-directivereplace}

- **Capability ID:** `sub-feature:s3:copyobject:x-amz-metadata-directivereplace`
- **Status:** ✅ implemented

**Gap.** Request's metadata + Content-Type override source via standard header forwarding.

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
- ETag in CopyObjectResult is normalised to the same S3-shaped, proxy-translated value HEAD/GET emit for the destination blob (synthetic MD5 of Azure's raw ETag when Content-MD5 is absent), so clients can reuse it across operations without seeing two different ETags for the same object.

## References

- <https://docs.aws.amazon.com/AmazonS3/latest/API/API_CopyObject.html>
- <https://learn.microsoft.com/rest/api/storageservices/copy-blob>

