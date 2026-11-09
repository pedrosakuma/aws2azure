# s3 / HeadObject {#operation-s3-headobject}

[← s3 operation index](../../s3.md) · [Coverage matrix](../../coverage.md)

- **Capability ID:** `operation:s3:headobject`
- **Status:** ✅ implemented
- **Azure equivalent:** `HEAD https://{account}.blob.core.windows.net/{container}/{blob}`
- **Real-Azure verified:** ✅ 2026-07-17 · [evidence](https://github.com/pedrosakuma/aws2azure/actions/runs/29548639280) · [workflow run](https://github.com/pedrosakuma/aws2azure/actions/runs/29548639280)

## Sub-features

### conditional headers {#sub-feature-conditional-headers}

- **Capability ID:** `sub-feature:s3:headobject:conditional-headers`
- **Status:** ✅ implemented

**Gap.** Concrete-ETag conditionals are evaluated proxy-side (the proxy translates ETags). A failed If-Match yields a bodiless 412 with x-amz-error-code: PreconditionFailed and no leaked object metadata; If-None-Match match yields 304.

### standard response headers (ETag, Last-Modified, Content-Length, Content-Type, …) {#sub-feature-standard-response-headers--etag-last-modified-content-length-content-type}

- **Capability ID:** `sub-feature:s3:headobject:standard-response-headers--etag-last-modified-content-length-content-type`
- **Status:** ✅ implemented

### user metadata (x-amz-meta-*) {#sub-feature-user-metadata--x-amz-meta}

- **Capability ID:** `sub-feature:s3:headobject:user-metadata--x-amz-meta`
- **Status:** ✅ implemented

### flexible checksum readback (x-amz-checksum-mode) {#sub-feature-flexible-checksum-readback--x-amz-checksum-mode}

- **Capability ID:** `sub-feature:s3:headobject:flexible-checksum-readback--x-amz-checksum-mode`
- **Status:** 🟡 partial
- **Disposition:** 🔵 by design

**Gap.** x-amz-checksum-mode=ENABLED exposes a persisted CRC32, CRC32C, CRC64NVME, SHA1, or SHA256 value only when the exact selected blob version carries the proxy's complete validated metadata pair. Existing/unmanaged blobs, multipart objects, and trailer-origin uploads omit checksum headers. Corrupt internal metadata fails closed with a bodiless 500 InternalError. [conformance:Aws2Azure.UnitTests.S3.HeadAndListingHandlersTests.HeadObject_exposes_persisted_checksum_only_when_enabled] [conformance:Aws2Azure.UnitTests.S3.HeadAndListingHandlersTests.HeadObject_fails_closed_for_corrupt_checksum_metadata]

## Behaviour differences

- 404 responses include x-amz-error-code: NoSuchKey and an empty body (HEAD spec).
- 412 PreconditionFailed (failed If-Match) responses include x-amz-error-code: PreconditionFailed and an empty body (HEAD spec).
- Algorithm-specific checksum headers are omitted unless x-amz-checksum-mode=ENABLED and the selected version has complete validated proxy metadata. The HEAD path performs no additional Azure request or checksum computation. [conformance:Aws2Azure.UnitTests.S3.HeadAndListingHandlersTests.HeadObject_exposes_persisted_checksum_only_when_enabled]
- Azure x-ms-content-crc64 is never surfaced as x-amz-checksum-crc64nvme; only the AWS/NVMe value validated and stored by the proxy is eligible.
- Presigned HEAD is accepted (see PresignedUrl.yaml).

## References

- <https://docs.aws.amazon.com/AmazonS3/latest/API/API_HeadObject.html>
- <https://learn.microsoft.com/rest/api/storageservices/get-blob-properties>
- <https://github.com/pedrosakuma/aws2azure/issues/1106>

