# s3 / ListParts {#operation-s3-listparts}

[← s3 operation index](../../s3.md) · [Coverage matrix](../../coverage.md)

- **Capability ID:** `operation:s3:listparts`
- **Status:** ✅ implemented
- **Azure equivalent:** `Proxy state HEAD/verification + Get Block List (?comp=blocklist&blocklisttype=uncommitted)`
- **Real-Azure verified:** ✅ 2026-08-11 · [evidence](https://github.com/pedrosakuma/aws2azure/actions/runs/31447675330) · [workflow run](https://github.com/pedrosakuma/aws2azure/actions/runs/31447675330)

## Sub-features

### pagination {#sub-feature-pagination}

- **Capability ID:** `sub-feature:s3:listparts:pagination`
- **Status:** ✅ implemented

Honours max-parts (default 1000, capped at 1000) and part-number-marker.

### upload-existence-check {#sub-feature-upload-existence-check}

- **Capability ID:** `sub-feature:s3:listparts:upload-existence-check`
- **Status:** ✅ implemented

ListParts returns NoSuchUpload when the proxy-owned state record is missing or bound to a stale bucket generation.

### encoding-type {#sub-feature-encoding-type}

- **Capability ID:** `sub-feature:s3:listparts:encoding-type`
- **Status:** ⛔ unsupported
- **Disposition:** 🔵 by design

### requester-pays {#sub-feature-requester-pays}

- **Capability ID:** `sub-feature:s3:listparts:requester-pays`
- **Status:** ⛔ unsupported
- **Disposition:** 🔵 by design

## Behaviour differences

- <ETag> values returned by ListParts are synthetic (derived from the Azure block name) and are NOT equal to the per-part ETags returned by UploadPart. Azure's Get Block List response does not expose per-block MD5s, so this permanent incompatibility remains explicit.
- <LastModified> for every part is the multipart upload's initiation timestamp from the durable state record; Azure exposes no per-block timestamp.
- <Owner> / <Initiator> are omitted because aws2azure does not model IAM principals. [conformance:multipart-upload-copy-complete-roundtrip::missing-field:Initiator] [conformance:multipart-upload-copy-complete-roundtrip::missing-field:Owner]
- NextPartNumberMarker is omitted from the final page of a ListParts response because the proxy's paginator only surfaces the marker when another page follows; real S3 also emits it on the final page when IsTruncated is false, and Azure's Get Block List has no equivalent trailing marker to reproduce. [conformance:multipart-upload-copy-complete-roundtrip::missing-field:NextPartNumberMarker]
- Echoed <Bucket> and <UploadId> in ListParts responses are the destination container name and the proxy-issued opaque UploadId token respectively; the offline Tier-3 diff compares captures against independently seeded buckets and independently issued upload ids, so those echoed values cannot match byte-for-byte across capture runs. [conformance:multipart-upload-copy-complete-roundtrip::field-value:Bucket] [conformance:multipart-upload-copy-complete-roundtrip::field-value:UploadId]
- InvalidBucketName responses emitted by the request-validation stage (before Azure is called) omit the informational <BucketName> element real S3 includes when the offending name is 400-illegal (e.g. shorter than 3 chars) even when routed through the multipart ?uploadId dispatcher; the proxy's shared S3 error envelope helper currently does not carry the offending bucket name into the XML body. [conformance:multipart-invalid-bucket-name::missing-field:BucketName]
- NoSuchBucket responses emitted before the multipart uploadId is examined omit the informational <BucketName> element real S3 includes; the proxy's shared S3 error envelope short-circuits length-legal but Azure-illegal container names into NoSuchBucket without carrying the offending name into the body. [conformance:multipart-azure-illegal-bucket-name-is-nosuchbucket::missing-field:BucketName]
- NoSuchUpload responses returned by ListParts when the durable multipart state record is missing (e.g. after AbortMultipartUpload) omit the informational <UploadId> element real S3 includes in its <Error> envelope; the proxy's shared S3 error envelope does not carry the requested uploadId back into the response body. [conformance:multipart-upload-abort-roundtrip::missing-field:UploadId]

## References

- <https://docs.aws.amazon.com/AmazonS3/latest/API/API_ListParts.html>
- <https://learn.microsoft.com/rest/api/storageservices/get-block-list>

