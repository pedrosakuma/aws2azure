# s3 / PutObject {#operation-s3-putobject}

[← s3 operation index](../../s3.md) · [Coverage matrix](../../coverage.md)

- **Capability ID:** `operation:s3:putobject`
- **Status:** ✅ implemented
- **Azure equivalent:** `PUT https://{account}.blob.core.windows.net/{container}/{blob}`
- **Real-Azure verified:** ✅ 2026-07-16 · [evidence](https://github.com/pedrosakuma/aws2azure/actions/runs/29473539261) · [workflow run](https://github.com/pedrosakuma/aws2azure/actions/runs/29473539261)

## Sub-features

### standard content headers (Content-Type, Content-Encoding, Content-Language, Content-Disposition, Cache-Control, Content-MD5) {#sub-feature-standard-content-headers--content-type-content-encoding-content-language-content-disposition-cache-control-content-md5}

- **Capability ID:** `sub-feature:s3:putobject:standard-content-headers--content-type-content-encoding-content-language-content-disposition-cache-control-content-md5`
- **Status:** ✅ implemented

### user metadata (x-amz-meta-*) {#sub-feature-user-metadata--x-amz-meta}

- **Capability ID:** `sub-feature:s3:putobject:user-metadata--x-amz-meta`
- **Status:** ✅ implemented

**Gap.** Renamed to x-ms-meta-* on the Azure call; Azure restricts header names to ASCII and ≤256 bytes.

### aws-chunked payloads (STREAMING-AWS4-HMAC-SHA256-PAYLOAD) {#sub-feature-aws-chunked-payloads--streaming-aws4-hmac-sha256-payload}

- **Capability ID:** `sub-feature:s3:putobject:aws-chunked-payloads--streaming-aws4-hmac-sha256-payload`
- **Status:** ✅ implemented

**Gap.** Proxy decodes the AWS chunk framing and verifies each chunk signature before forwarding the raw payload to Azure Blob. x-amz-decoded-content-length is used for Content-Length.

### aws-chunked trailer / unsigned payloads (STREAMING-*-PAYLOAD-TRAILER, STREAMING-UNSIGNED-PAYLOAD) {#sub-feature-aws-chunked-trailer---unsigned-payloads--streaming--payload-trailer-streaming-unsigned-payload}

- **Capability ID:** `sub-feature:s3:putobject:aws-chunked-trailer---unsigned-payloads--streaming--payload-trailer-streaming-unsigned-payload`
- **Status:** ✅ implemented

**Gap.** Decodes the signed and unsigned trailer framings emitted by modern AWS SDKs when flexible checksums are enabled (the AWSSDK.S3 4.x / recent boto3 default). The trailing checksum / x-amz-trailer-signature section is consumed and discarded — Azure Blob validates content integrity independently; the proxy does not re-validate the x-amz-checksum-* trailer. The -ECDSA-* (SigV4a) streaming variants are not recognized.

### object-tagging (x-amz-tagging) {#sub-feature-object-tagging--x-amz-tagging}

- **Capability ID:** `sub-feature:s3:putobject:object-tagging--x-amz-tagging`
- **Status:** ✅ implemented

**Gap.** x-amz-tagging is parsed as the same URL-encoded key=value query-string used by CreateMultipartUpload, validated against the PutObjectTagging limits, then applied to the newly written blob via a follow-up Azure Put Blob Tags call.

### storage-class (x-amz-storage-class) {#sub-feature-storage-class--x-amz-storage-class}

- **Capability ID:** `sub-feature:s3:putobject:storage-class--x-amz-storage-class`
- **Status:** ⛔ unsupported
- **Disposition:** 🔵 by design

**Gap.** Azure Blob does not have an AWS storage-class equivalent on Put Blob; the request header is ignored and later listings still report STANDARD.

### flexible checksums (x-amz-sdk-checksum-algorithm / x-amz-checksum-*) {#sub-feature-flexible-checksums--x-amz-sdk-checksum-algorithm---x-amz-checksum}

- **Capability ID:** `sub-feature:s3:putobject:flexible-checksums--x-amz-sdk-checksum-algorithm---x-amz-checksum`
- **Status:** 🟡 partial
- **Disposition:** 🛠️ feasible backlog
- **Tracking issue:** [#894](https://github.com/pedrosakuma/aws2azure/issues/894)

**Gap.** Content-MD5 is forwarded. Algorithm-specific flexible-checksum headers/trailers (CRC32, CRC32C, SHA1, SHA256) are accepted so modern SDK uploads succeed, but the proxy does not re-validate or persist those checksum values beyond the normal Azure integrity checks.

### versioning (x-amz-version-id) {#sub-feature-versioning--x-amz-version-id}

- **Capability ID:** `sub-feature:s3:putobject:versioning--x-amz-version-id`
- **Status:** 🟡 partial
- **Disposition:** 🔵 by design

**Gap.** When account-level Blob versioning is enabled, the created Azure x-ms-version-id is reversibly encoded before being surfaced as x-amz-version-id so the token round-trips through S3 ?versionId without leaking Azure's raw timestamp format.

### server-side encryption (SSE-S3, SSE-KMS, SSE-C) {#sub-feature-server-side-encryption--sse-s3-sse-kms-sse-c}

- **Capability ID:** `sub-feature:s3:putobject:server-side-encryption--sse-s3-sse-kms-sse-c`
- **Status:** ⛔ unsupported
- **Disposition:** 🔵 by design

**Gap.** Azure manages encryption at the storage-account level; SSE headers are ignored.

### Object Lock / Legal Hold / Retention {#sub-feature-object-lock---legal-hold---retention}

- **Capability ID:** `sub-feature:s3:putobject:object-lock---legal-hold---retention`
- **Status:** ⛔ unsupported
- **Disposition:** 🔵 by design

### ACLs (x-amz-acl) {#sub-feature-acls--x-amz-acl}

- **Capability ID:** `sub-feature:s3:putobject:acls--x-amz-acl`
- **Status:** ⛔ unsupported
- **Disposition:** 🔵 by design

## Behaviour differences

- ETag value comes from Azure (hex of MD5 for single-part uploads); shape matches S3 but bytes differ from a re-uploaded object.
- x-amz-version-id is a proxy-encoded Azure version token (azv-...); it preserves presence/round-trip semantics but will not byte-match AWS's opaque version id. [conformance:header-value:x-amz-version-id]
- PUT always overwrites an existing blob, matching S3 default semantics.
- x-amz-server-side-encryption is synthesized as AES256 to reflect Azure Storage's at-rest encryption baseline.
- x-amz-tagging is applied via a second, non-atomic Azure Put Blob Tags call after the blob body is already committed. If the tag call fails, PutObject returns the Azure error even though the object bytes were already written.
- x-amz-storage-class is ignored on write; Azure Blob uses its own account/container tiering model and the proxy does not persist the requested AWS storage class.
- x-amz-checksum-crc32 / x-amz-checksum-crc32c / x-amz-checksum-sha1 / x-amz-checksum-sha256 are not surfaced on the response and are not revalidated proxy-side on upload; only Content-MD5 participates in the classic S3 ETag flow for single-part uploads.
- x-amz-checksum-crc64nvme is not emitted; Azure does not expose AWS's CRC64NVME checksum surface on PutObject responses. [conformance:missing-header:x-amz-checksum-crc64nvme]
- Concrete-ETag preconditions (If-Match / If-None-Match with a value other than '*') return 501 NotImplemented: proxy-translated S3 ETags do not round-trip back to Azure's raw ETag space, and supporting optimistic concurrency would require a HEAD-then-PUT cycle that is not yet implemented. The '*' sentinel is honored (forwarded to Azure).
- Presigned PUT is accepted (see PresignedUrl.yaml). Body integrity is not signature-protected (UNSIGNED-PAYLOAD) — identical to AWS S3 semantics.

## References

- <https://docs.aws.amazon.com/AmazonS3/latest/API/API_PutObject.html>
- <https://docs.aws.amazon.com/AmazonS3/latest/API/sigv4-streaming.html>
- <https://learn.microsoft.com/rest/api/storageservices/put-blob>

