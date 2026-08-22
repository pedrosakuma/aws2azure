# s3 / CreateMultipartUpload {#operation-s3-createmultipartupload}

[← s3 operation index](../../s3.md) · [Coverage matrix](../../coverage.md)

- **Capability ID:** `operation:s3:createmultipartupload`
- **Status:** ✅ implemented
- **Azure equivalent:** `HEAD container + proxy-owned durable multipart state record`
- **Real-Azure verified:** ✅ 2026-08-11 · [evidence](https://github.com/pedrosakuma/aws2azure/actions/runs/31447675330) · [workflow run](https://github.com/pedrosakuma/aws2azure/actions/runs/31447675330)

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
- **Tracking issue:** [#799](https://github.com/pedrosakuma/aws2azure/issues/799)

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
- x-amz-server-side-encryption is not emitted on the CreateMultipartUpload response; Azure Blob Storage's HEAD container response reports encryption state via x-ms-server-encrypted rather than a header the proxy currently mirrors onto initiate 200s, and no request-time server-side-encryption headers are honoured (see sub_features). [conformance:multipart-upload-abort-roundtrip::missing-header:x-amz-server-side-encryption] [conformance:multipart-upload-copy-complete-roundtrip::missing-header:x-amz-server-side-encryption]
- Real S3 CreateMultipartUpload 200 responses omit the Content-Type header even though the body is XML; the proxy's XML response writer sets Content-Type application/xml because Kestrel's default XML content negotiation adds it. Clients read the body via the operation-specific unmarshaller regardless of Content-Type. [conformance:multipart-upload-abort-roundtrip::extra-header:content-type] [conformance:multipart-upload-copy-complete-roundtrip::extra-header:content-type]
- Echoed <Bucket> and <UploadId> in the initiate response are the destination container name and the proxy-issued opaque UploadId token; the offline Tier-3 diff compares captures against independently seeded buckets and independently issued upload ids, so those echoed values cannot match byte-for-byte across capture runs. [conformance:multipart-upload-abort-roundtrip::field-value:Bucket] [conformance:multipart-upload-abort-roundtrip::field-value:UploadId] [conformance:multipart-upload-copy-complete-roundtrip::field-value:Bucket] [conformance:multipart-upload-copy-complete-roundtrip::field-value:UploadId]

## References

- <https://docs.aws.amazon.com/AmazonS3/latest/API/API_CreateMultipartUpload.html>
- <https://learn.microsoft.com/rest/api/storageservices/get-container-properties>

