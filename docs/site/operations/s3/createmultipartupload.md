# s3 / CreateMultipartUpload {#operation-s3-createmultipartupload}

[← s3 operation index](../../s3.md) · [Coverage matrix](../../coverage.md)

- **Capability ID:** `operation:s3:createmultipartupload`
- **Status:** ✅ implemented
- **Azure equivalent:** `HEAD container + proxy-owned durable multipart state record`

## Sub-features

### durable-state-record {#sub-feature-durable-state-record}

- **Capability ID:** `sub-feature:s3:createmultipartupload:durable-state-record`
- **Status:** ✅ implemented

The proxy writes one bounded state blob per active upload into its hidden aws2azure-mpu-<account-hash> container. The record captures Content-Type/Content-Encoding/Content-Language/Content-Disposition/Cache-Control and x-amz-meta-* headers (16 KiB cap) so CompleteMultipartUpload can apply them to the committed blob.

### bucket-generation-binding {#sub-feature-bucket-generation-binding}

- **Capability ID:** `sub-feature:s3:createmultipartupload:bucket-generation-binding`
- **Status:** ✅ implemented

CreateMultipartUpload captures the destination container ETag before writing the state record, stores it in record metadata, then re-verifies the ETag after the write. If the bucket was deleted/recreated mid-request the proxy deletes the just-written record and returns 409 OperationAborted.

### server-side-encryption {#sub-feature-server-side-encryption}

- **Capability ID:** `sub-feature:s3:createmultipartupload:server-side-encryption`
- **Status:** ⛔ unsupported
- **Disposition:** 🔵 by design

x-amz-server-side-encryption* request headers are ignored. Encryption remains governed by the destination storage account.

### object-tagging {#sub-feature-object-tagging}

- **Capability ID:** `sub-feature:s3:createmultipartupload:object-tagging`
- **Status:** ⛔ unsupported
- **Disposition:** 🛠️ feasible backlog
- **Tracking issue:** [#690](https://github.com/pedrosakuma/aws2azure/issues/690)

x-amz-tagging is ignored on initiate.

### object-lock {#sub-feature-object-lock}

- **Capability ID:** `sub-feature:s3:createmultipartupload:object-lock`
- **Status:** ⛔ unsupported
- **Disposition:** 🔵 by design

### storage-class {#sub-feature-storage-class}

- **Capability ID:** `sub-feature:s3:createmultipartupload:storage-class`
- **Status:** ⛔ unsupported
- **Disposition:** 🔵 by design

x-amz-storage-class is ignored; Azure uses the account/container defaults.

## Behaviour differences

- UploadId remains a 32-byte base64url token HMAC-bound to (account, bucket, key) and expiring after 7 days, but multipart is no longer purely stateless: a proxy-owned durable index record is also created so in-progress uploads can be enumerated and later completed with the original metadata.
- Initiation fails with InvalidArgument when the captured metadata/property headers would exceed the 16 KiB durable-state cap.
- The hidden multipart-state container is internal-only and not reachable through S3 bucket routes or copy-source headers.

## References

- <https://docs.aws.amazon.com/AmazonS3/latest/API/API_CreateMultipartUpload.html>
- <https://learn.microsoft.com/rest/api/storageservices/get-container-properties>

