# s3 / UploadPartCopy {#operation-s3-uploadpartcopy}

[← s3 operation index](../../s3.md) · [Coverage matrix](../../coverage.md)

- **Capability ID:** `operation:s3:uploadpartcopy`
- **Status:** ✅ implemented
- **Azure equivalent:** `Proxy state HEAD/verification + Put Block From URL (?comp=block&blockid=…)`
- **Real-Azure verified:** ✅ 2026-08-11 · [evidence](https://github.com/pedrosakuma/aws2azure/actions/runs/31447675330) · [workflow run](https://github.com/pedrosakuma/aws2azure/actions/runs/31447675330)

## Sub-features

### same-account-copy {#sub-feature-same-account-copy}

- **Capability ID:** `sub-feature:s3:uploadpartcopy:same-account-copy`
- **Status:** ✅ implemented

Source is referenced via a short-lived read-only blob SAS minted from the same Azure storage account key.

### copy-source-range {#sub-feature-copy-source-range}

- **Capability ID:** `sub-feature:s3:uploadpartcopy:copy-source-range`
- **Status:** ✅ implemented

x-amz-copy-source-range 'bytes=start-end' is canonicalised and forwarded as x-ms-source-range.

### upload-existence-check {#sub-feature-upload-existence-check}

- **Capability ID:** `sub-feature:s3:uploadpartcopy:upload-existence-check`
- **Status:** ✅ implemented

UploadPartCopy requires the durable multipart state record to exist and match the bucket's current generation; stale or aborted uploadIds return NoSuchUpload.

### internal-container-source-guard {#sub-feature-internal-container-source-guard}

- **Capability ID:** `sub-feature:s3:uploadpartcopy:internal-container-source-guard`
- **Status:** ✅ implemented

The proxy's hidden multipart-state container is rejected as a copy source so internal state cannot be reached through x-amz-copy-source.

### cross-account-copy {#sub-feature-cross-account-copy}

- **Capability ID:** `sub-feature:s3:uploadpartcopy:cross-account-copy`
- **Status:** ⛔ unsupported
- **Disposition:** 🔵 by design

### source-conditional-headers {#sub-feature-source-conditional-headers}

- **Capability ID:** `sub-feature:s3:uploadpartcopy:source-conditional-headers`
- **Status:** ✅ implemented

Date-based conditionals forward to Azure. Concrete-ETag if-match is evaluated proxy-side and then guarded on Put Block From URL; concrete if-none-match is supported when the source is version-pinned (explicit versionId or Azure current-version pinning), otherwise the proxy returns NotImplemented rather than risk staging bytes from the wrong source.

### versionId {#sub-feature-versionid}

- **Capability ID:** `sub-feature:s3:uploadpartcopy:versionid`
- **Status:** ✅ implemented

x-amz-copy-source?versionId maps to Azure's ?versionid selector when constructing the source SAS URL.

## Behaviour differences

- Per-part ETag is synthesised from the (uploadId, partNumber) pair when Azure omits Content-MD5. The value is stable for the upload flow but is NOT a content hash and does not equal the UploadPart ETag returned for an equivalent non-copy body.
- UploadPartCopy with source==destination is rejected with InvalidRequest so the eventual CompleteMultipartUpload cannot silently reconcile against the live source blob.
- The hidden multipart-state container is never usable as x-amz-copy-source.

## References

- <https://docs.aws.amazon.com/AmazonS3/latest/API/API_UploadPartCopy.html>
- <https://learn.microsoft.com/rest/api/storageservices/put-block-from-url>

