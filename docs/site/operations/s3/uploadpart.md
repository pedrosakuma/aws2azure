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

### server-side-encryption-customer {#sub-feature-server-side-encryption-customer}

- **Capability ID:** `sub-feature:s3:uploadpart:server-side-encryption-customer`
- **Status:** ⛔ unsupported
- **Disposition:** 🔵 by design

## Behaviour differences

- Block IDs use the fixed-width layout b{nonce16hex}p{partNumber5d} (base64-encoded) so all parts of a blob share a constant length, satisfying Azure's block-ID uniformity rule.
- Part numbers must be in [1, 10000] (S3 limit). Azure's higher block-count ceiling is intentionally unused.

## References

- <https://docs.aws.amazon.com/AmazonS3/latest/API/API_UploadPart.html>
- <https://learn.microsoft.com/rest/api/storageservices/put-block>

