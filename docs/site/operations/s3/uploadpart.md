# s3 / UploadPart {#operation-s3-uploadpart}

[← s3 operation index](../../s3.md) · [Coverage matrix](../../coverage.md)

- **Capability ID:** `operation:s3:uploadpart`
- **Status:** ✅ implemented
- **Azure equivalent:** `Proxy state HEAD/verification + Put Block (?comp=block&blockid=…)`
- **Real-Azure verified:** ✅ 2026-08-11 · [evidence](https://github.com/pedrosakuma/aws2azure/actions/runs/31447675330) · [workflow run](https://github.com/pedrosakuma/aws2azure/actions/runs/31447675330)

## Sub-features

### durable-upload-existence-check {#sub-feature-durable-upload-existence-check}

- **Capability ID:** `sub-feature:s3:uploadpart:durable-upload-existence-check`
- **Status:** ✅ implemented

UploadPart first resolves the proxy-owned multipart state record and re-verifies the destination container generation. Missing, expired, aborted, completed, or stale-generation uploadIds return NoSuchUpload before Azure stages a block.

### content-md5 {#sub-feature-content-md5}

- **Capability ID:** `sub-feature:s3:uploadpart:content-md5`
- **Status:** ✅ implemented

ETag returned to clients is hex(MD5) of the part body, computed in-flight as the body streams to Azure.

### aws-chunked-payload {#sub-feature-aws-chunked-payload}

- **Capability ID:** `sub-feature:s3:uploadpart:aws-chunked-payload`
- **Status:** ✅ implemented

All STREAMING-* chunked payload variants already supported by the proxy are decoded before forwarding the part to Azure.

### flexible checksums (x-amz-sdk-checksum-algorithm / x-amz-checksum-*) {#sub-feature-flexible-checksums--x-amz-sdk-checksum-algorithm---x-amz-checksum}

- **Capability ID:** `sub-feature:s3:uploadpart:flexible-checksums--x-amz-sdk-checksum-algorithm---x-amz-checksum`
- **Status:** ✅ implemented

CRC32, CRC32C, CRC64NVME, SHA1, and SHA256 request headers and aws-chunked decoded trailers reuse the shared bounded streaming validator. A mismatched final digest withholds the final body byte from Azure and returns BadDigest. For durable uploads, the validated algorithm/digest is persisted in one bounded hidden metadata record per part; retries overwrite the same part record. The UploadPart response ETag remains the independently streamed MD5 hex of the part body. [conformance:Aws2Azure.UnitTests.S3.MultipartHandlersTests.UploadPart_validates_algorithm_specific_checksum_and_returns_md5_etag] [conformance:Aws2Azure.UnitTests.S3.MultipartHandlersTests.UploadPart_accepts_matching_aws_chunked_checksum_trailer] [conformance:Aws2Azure.UnitTests.S3.MultipartHandlersTests.UploadPart_returns_bad_digest_before_accepting_mismatched_part] [conformance:Aws2Azure.UnitTests.S3.MultipartHandlersTests.UploadPart_retry_replaces_checksum_metadata_for_same_part]

### server-side-encryption-customer {#sub-feature-server-side-encryption-customer}

- **Capability ID:** `sub-feature:s3:uploadpart:server-side-encryption-customer`
- **Status:** ⛔ unsupported
- **Disposition:** 🔵 by design

## Behaviour differences

- Block IDs use the fixed-width layout b{nonce16hex}p{partNumber5d} (base64-encoded) so all parts of a blob share a constant length, satisfying Azure's block-ID uniformity rule.
- Part numbers must be in [1, 10000] (S3 limit). Azure's higher block-count ceiling is intentionally unused.
- x-amz-checksum-crc32 / x-amz-checksum-crc32c / x-amz-checksum-crc64nvme / x-amz-checksum-sha1 / x-amz-checksum-sha256 are validated but not emitted on the UploadPart response; the returned ETag remains the streamed MD5 of the part body. [conformance:Aws2Azure.UnitTests.S3.MultipartHandlersTests.UploadPart_validates_algorithm_specific_checksum_and_returns_md5_etag]
- When CreateMultipartUpload selected a checksum algorithm, every UploadPart must provide that same algorithm. A missing or mixed algorithm is rejected before Azure receives the body.
- Validated per-part checksum metadata is stored separately from the body and remains bounded by digest size. Abort/Complete immediately retire up to 256 checksum records (covering ordinary uploads); larger uploads or transient cleanup failures become unreachable with the main state and fall back to normal seven-day multipart-state cleanup. [conformance:Aws2Azure.UnitTests.S3.MultipartHandlersTests.Abort_checksum_enabled_upload_retires_bounded_part_metadata_immediately]
- x-amz-server-side-encryption is not emitted on the UploadPart response; Azure Blob Storage's Put Block response reports encryption state via x-ms-server-encrypted rather than an equivalent AWS-style header the proxy mirrors onto UploadPart 200s. [conformance:multipart-upload-abort-roundtrip::missing-header:x-amz-server-side-encryption] [conformance:multipart-upload-copy-complete-roundtrip::missing-header:x-amz-server-side-encryption]

## References

- <https://docs.aws.amazon.com/AmazonS3/latest/API/API_UploadPart.html>
- <https://learn.microsoft.com/rest/api/storageservices/put-block>

