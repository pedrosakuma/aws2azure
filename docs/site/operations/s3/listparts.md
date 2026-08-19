# s3 / ListParts {#operation-s3-listparts}

[← s3 operation index](../../s3.md) · [Coverage matrix](../../coverage.md)

- **Capability ID:** `operation:s3:listparts`
- **Status:** ✅ implemented
- **Azure equivalent:** `Proxy state HEAD/verification + Get Block List (?comp=blocklist&blocklisttype=uncommitted)`

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
- <Owner> / <Initiator> are omitted because aws2azure does not model IAM principals.

## References

- <https://docs.aws.amazon.com/AmazonS3/latest/API/API_ListParts.html>
- <https://learn.microsoft.com/rest/api/storageservices/get-block-list>

