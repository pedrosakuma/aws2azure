# s3 / GetObject {#operation-s3-getobject}

[← s3 operation index](../../s3.md) · [Coverage matrix](../../coverage.md)

- **Capability ID:** `operation:s3:getobject`
- **Status:** ✅ implemented
- **Azure equivalent:** `GET https://{account}.blob.core.windows.net/{container}/{blob}`
- **Real-Azure verified:** ✅ 2026-07-16 · [evidence](https://github.com/pedrosakuma/aws2azure/actions/runs/29473539261) · [workflow run](https://github.com/pedrosakuma/aws2azure/actions/runs/29473539261)

## Sub-features

### Range requests (bytes=X-Y) {#sub-feature-range-requests--bytesx-y}

- **Capability ID:** `sub-feature:s3:getobject:range-requests--bytesx-y`
- **Status:** ✅ implemented

### conditional headers (If-Match / If-None-Match / If-Modified-Since / If-Unmodified-Since) {#sub-feature-conditional-headers--if-match---if-none-match---if-modified-since---if-unmodified-since}

- **Capability ID:** `sub-feature:s3:getobject:conditional-headers--if-match---if-none-match---if-modified-since---if-unmodified-since`
- **Status:** ✅ implemented

**Gap.** The proxy translates ETags, so concrete-ETag conditionals are evaluated proxy-side after Azure returns 200 (Azure cannot recognize the S3-shaped value). A failed If-Match on a read yields 412 PreconditionFailed with a faithful <Error> envelope; If-None-Match match yields 304.

### standard response headers (ETag, Last-Modified, Content-Type, Content-Length, Content-Encoding, Cache-Control, Accept-Ranges, Content-Range) {#sub-feature-standard-response-headers--etag-last-modified-content-type-content-length-content-encoding-cache-control-accept-ranges-content-range}

- **Capability ID:** `sub-feature:s3:getobject:standard-response-headers--etag-last-modified-content-type-content-length-content-encoding-cache-control-accept-ranges-content-range`
- **Status:** ✅ implemented

### user metadata (x-amz-meta-*) {#sub-feature-user-metadata--x-amz-meta}

- **Capability ID:** `sub-feature:s3:getobject:user-metadata--x-amz-meta`
- **Status:** ✅ implemented

**Gap.** Translated from Azure x-ms-meta-*.

### response header overrides (response-content-type, response-content-disposition, …) {#sub-feature-response-header-overrides--response-content-type-response-content-disposition}

- **Capability ID:** `sub-feature:s3:getobject:response-header-overrides--response-content-type-response-content-disposition`
- **Status:** ✅ implemented

response-content-type / response-content-disposition / response-content-encoding / response-content-language / response-cache-control / response-expires override the single response without mutating stored metadata.

### server-side encryption customer keys (SSE-C) {#sub-feature-server-side-encryption-customer-keys--sse-c}

- **Capability ID:** `sub-feature:s3:getobject:server-side-encryption-customer-keys--sse-c`
- **Status:** ⛔ unsupported
- **Disposition:** 🔵 by design

### versioning (versionId query) {#sub-feature-versioning--versionid-query}

- **Capability ID:** `sub-feature:s3:getobject:versioning--versionid-query`
- **Status:** 🟡 partial
- **Disposition:** 🔵 by design

**Gap.** ?versionId selector reversibly decodes the client-facing x-amz-version-id token back to Azure ?versionid; responses re-encode x-ms-version-id before surfacing it to clients. Requires account-level Blob versioning enabled out-of-band; no delete markers in ListObjectVersions yet.

## Behaviour differences

- Streaming end-to-end: response body is forwarded without buffering.
- x-amz-id-2 carries the Azure x-ms-request-id for cross-system tracing.
- The default object Content-Type when Azure has none is binary/octet-stream to match observed S3 behavior.
- x-amz-server-side-encryption is synthesized as AES256 to reflect Azure Storage's at-rest encryption baseline.
- x-amz-checksum-crc64nvme is not emitted; Azure does not expose AWS's CRC64NVME checksum surface on GetObject responses. [conformance:missing-header:x-amz-checksum-crc64nvme]
- Presigned URLs are accepted (see PresignedUrl.yaml); the client must sign against the proxy host.
- Error responses omit the server-side x-amz-id-2 correlation header that real S3 emits. [conformance:missing-header:x-amz-id-2]
- Error envelopes omit the <HostId> element that real S3 emits. [conformance:missing-field:HostId]
- NoSuchBucket envelopes omit the informational <BucketName> element real S3 includes. [conformance:nosuchbucket-get-object::missing-field:BucketName]
- NoSuchKey envelopes omit the informational <Key> element real S3 includes. [conformance:nosuchkey-get-object::missing-field:Key]
- PreconditionFailed envelopes omit the informational <Condition> element (e.g. <Condition>If-Match</Condition>) real S3 includes. [conformance:precondition-failed-get::missing-field:Condition]

## References

- <https://docs.aws.amazon.com/AmazonS3/latest/API/API_GetObject.html>
- <https://learn.microsoft.com/rest/api/storageservices/get-blob>

