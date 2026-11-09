# s3 / CompleteMultipartUpload {#operation-s3-completemultipartupload}

[← s3 operation index](../../s3.md) · [Coverage matrix](../../coverage.md)

- **Capability ID:** `operation:s3:completemultipartupload`
- **Status:** ✅ implemented
- **Azure equivalent:** `Lease state record + Put Block List`
- **Real-Azure verified:** ✅ 2026-08-22 · [evidence](https://github.com/pedrosakuma/aws2azure/actions/runs/32600417556) · [workflow run](https://github.com/pedrosakuma/aws2azure/actions/runs/32600417556)

## Sub-features

### lease-based-convergence {#sub-feature-lease-based-convergence}

- **Capability ID:** `sub-feature:s3:completemultipartupload:lease-based-convergence`
- **Status:** ✅ implemented

Complete acquires the multipart state's 60-second lease before committing the block list and deleting the state record, preventing concurrent Complete/Abort races from committing two outcomes.

### metadata-fidelity {#sub-feature-metadata-fidelity}

- **Capability ID:** `sub-feature:s3:completemultipartupload:metadata-fidelity`
- **Status:** ✅ implemented

Headers captured at CreateMultipartUpload (Content-Type/Encoding/Language/Disposition/Cache-Control and x-amz-meta-*) are replayed onto Put Block List so the completed blob preserves the documented metadata surface. The proxy also writes one reserved hidden metadata key with the committed part count so subsequent GetObject/HeadObject reads can preserve the multipart ETag dash suffix for AWS SDK compatibility; client-supplied metadata using that reserved name is ignored.

### part-ordering {#sub-feature-part-ordering}

- **Capability ID:** `sub-feature:s3:completemultipartupload:part-ordering`
- **Status:** ✅ implemented

Parts must arrive in ascending PartNumber order without duplicates; malformed manifests reuse S3's InvalidPartOrder/MalformedXML semantics.

### per-part-etag-validation {#sub-feature-per-part-etag-validation}

- **Capability ID:** `sub-feature:s3:completemultipartupload:per-part-etag-validation`
- **Status:** ⛔ unsupported
- **Disposition:** 🔵 by design

The proxy still cannot validate client-supplied per-part ETags against Azure's staged blocks because Azure exposes no equivalent per-block MD5 lookup. This remains an explicit permanent gap.

### minimum-part-size {#sub-feature-minimum-part-size}

- **Capability ID:** `sub-feature:s3:completemultipartupload:minimum-part-size`
- **Status:** ✅ implemented

CompleteMultipartUpload resolves the staged Azure block list and rejects any non-final part smaller than 5 MiB with EntityTooSmall before committing.

### per-part-flexible-checksum-validation {#sub-feature-per-part-flexible-checksum-validation}

- **Capability ID:** `sub-feature:s3:completemultipartupload:per-part-flexible-checksum-validation`
- **Status:** ✅ implemented

For checksum-enabled uploads, each manifest Part must provide the algorithm selected at initiation and match the latest validated UploadPart checksum record. Missing metadata, missing manifest values, digest mismatches, and mixed algorithms are rejected before Put Block List. Manifest ordering remains ascending and unique. [conformance:Aws2Azure.UnitTests.S3.MultipartHandlersTests.CompleteMultipartUpload_accepts_matching_persisted_part_checksum] [conformance:Aws2Azure.UnitTests.S3.MultipartHandlersTests.CompleteMultipartUpload_rejects_missing_or_mixed_part_checksums]

### multipart-checksum-result {#sub-feature-multipart-checksum-result}

- **Capability ID:** `sub-feature:s3:completemultipartupload:multipart-checksum-result`
- **Status:** ⛔ unsupported
- **Disposition:** 🔵 by design

CompleteMultipartUploadResult does not claim COMPOSITE, FULL_OBJECT, or CRC64NVME result fields, and the committed blob receives no flexible-checksum metadata. Per-part CRC32, CRC32C, SHA1, and SHA256 values prove each uploaded part but do not by themselves justify an AWS aggregate result; CRC64NVME multipart initiation remains unsupported. The proxy does not reread or hash the committed object because that would add an unbounded full-object transfer, CPU cost, and completion latency to the sidecar.

## Behaviour differences

- Response ETag has the S3 multipart shape "{hash}-{count}" but {hash} is derived from the Azure blob ETag, not from concatenated per-part MD5s. SDKs that only pattern-match the dash-suffix accept it as multipart. [conformance:multipart-upload-copy-complete-roundtrip::field-value:ETag]
- Subsequent GetObject/HeadObject reads reuse the reserved hidden part-count metadata marker to keep the multipart-shaped ETag; without that marker AWSSDK.S3 4.x would treat the object as a single-part MD5 ETag and incorrectly hash-validate the response body.
- Missing/unknown PartNumbers surface as InvalidPart (mapped from Azure's InvalidBlockList).
- Client-supplied per-part <ETag> values are not validated. If a PartNumber was re-uploaded with different bytes, Complete commits the most recently staged block for that number; AWS would reject the stale ETag with InvalidPart.
- Lease-protected Put Block List + state delete are bounded to 45 seconds. On deadline expiry the proxy returns RequestTimeout and does not attempt a best-effort synchronous lease release.
- x-amz-server-side-encryption is not emitted on the CompleteMultipartUpload response; Azure Blob Storage's Put Block List response reports encryption state via x-ms-server-encrypted rather than an AWS-style header the proxy mirrors onto CompleteMultipartUpload 200s. [conformance:multipart-upload-copy-complete-roundtrip::missing-header:x-amz-server-side-encryption]
- ChecksumCRC32 / ChecksumCRC32C / ChecksumCRC64NVME / ChecksumSHA1 / ChecksumSHA256 / ChecksumType are omitted from CompleteMultipartUploadResult. Azure Blob Storage does not expose AWS aggregate checksum results, and this layer intentionally does not synthesize COMPOSITE, FULL_OBJECT, or CRC64NVME claims without executable parity evidence. [conformance:Aws2Azure.UnitTests.S3.MultipartHandlersTests.CompleteMultipartUpload_accepts_matching_persisted_part_checksum] [conformance:multipart-upload-copy-complete-roundtrip::missing-field:ChecksumCRC64NVME] [conformance:multipart-upload-copy-complete-roundtrip::missing-field:ChecksumType]
- Later GetObject/HeadObject checksum-mode requests also omit flexible-checksum headers for multipart objects. No whole-object recomputation is attempted; large-object rereads would violate the sidecar CPU, request-count, latency, and bounded-memory premise.
- Checksum-enabled completion rejects missing per-part checksum metadata (including copied parts), missing manifest checksum values, digest mismatches, and algorithms that differ from the CreateMultipartUpload selection before committing Azure's block list. [conformance:Aws2Azure.UnitTests.S3.MultipartHandlersTests.CompleteMultipartUpload_rejects_missing_or_mixed_part_checksums]
- After the durable upload state is retired, CompleteMultipartUpload best-effort deletes up to 256 hidden per-part checksum records immediately; larger uploads or transient cleanup failures rely on the existing seven-day multipart-state expiry sweep.
- The <Location> element echoes the proxy's own inbound scheme+host+bucket+key (correctly percent-encoded, including internal '/' characters within the key, matching real S3's opaque-key encoding of this field) rather than an "s3.<region>.amazonaws.com"/"<bucket>.s3.<region>.amazonaws.com" domain. Because the proxy is not actually hosted on an AWS-owned domain, byte-identical parity with AWS's own domain is structurally unattainable regardless of deployment topology; only the URL *shape* (scheme, path/virtual-hosted addressing style, encoding) can match. [conformance:multipart-upload-copy-complete-roundtrip::field-value:Location]

## References

- <https://docs.aws.amazon.com/AmazonS3/latest/API/API_CompleteMultipartUpload.html>
- <https://learn.microsoft.com/rest/api/storageservices/put-block-list>
- <https://github.com/pedrosakuma/aws2azure/issues/1106>

