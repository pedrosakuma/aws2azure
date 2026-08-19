# s3 / ListMultipartUploads {#operation-s3-listmultipartuploads}

[← s3 operation index](../../s3.md) · [Coverage matrix](../../coverage.md)

- **Capability ID:** `operation:s3:listmultipartuploads`
- **Status:** 🟡 partial
- **Disposition:** 🔵 by design
- **Azure equivalent:** `Proxy-owned multipart state container (Azure has no native cross-blob MPU enumeration primitive)`

## Sub-features

### deterministic-enumeration {#sub-feature-deterministic-enumeration}

- **Capability ID:** `sub-feature:s3:listmultipartuploads:deterministic-enumeration`
- **Status:** ✅ implemented

The proxy stores one durable state record per active upload and lists those records to build S3's in-progress upload view. Enumeration survives proxy restart because the index lives in Azure Blob Storage.

### pagination {#sub-feature-pagination}

- **Capability ID:** `sub-feature:s3:listmultipartuploads:pagination`
- **Status:** ✅ implemented

Honours max-uploads (default 1000, hard cap 1000), key-marker, and upload-id-marker. max-uploads=0 is rejected as InvalidArgument.

### prefix-filter {#sub-feature-prefix-filter}

- **Capability ID:** `sub-feature:s3:listmultipartuploads:prefix-filter`
- **Status:** ✅ implemented

### delimiter-common-prefixes {#sub-feature-delimiter-common-prefixes}

- **Capability ID:** `sub-feature:s3:listmultipartuploads:delimiter-common-prefixes`
- **Status:** ✅ implemented

### stale-generation-filtering {#sub-feature-stale-generation-filtering}

- **Capability ID:** `sub-feature:s3:listmultipartuploads:stale-generation-filtering`
- **Status:** ✅ implemented

Only records whose stored destination-container ETag matches the bucket's current ETag are visible; uploads from a deleted/recreated bucket generation are filtered out.

### opportunistic-cleanup {#sub-feature-opportunistic-cleanup}

- **Capability ID:** `sub-feature:s3:listmultipartuploads:opportunistic-cleanup`
- **Status:** ✅ implemented

ListMultipartUploads opportunistically deletes a bounded number of expired records while listing. Record names start with a zero-padded initiation timestamp so Azure's native lexical ordering exposes the oldest uploads first without a full cleanup scan.

## Behaviour differences

- Enumeration is implemented by the proxy's hidden state container, not by an Azure storage primitive; Azure Blob Storage has no cross-blob multipart-upload listing API.
- Expired-record cleanup is bounded per request (small bound on CreateMultipartUpload, larger bound on ListMultipartUploads) so abandoned uploads are eventually reclaimed without unbounded work on the request path.

## References

- <https://docs.aws.amazon.com/AmazonS3/latest/API/API_ListMultipartUploads.html>
- <https://learn.microsoft.com/rest/api/storageservices/list-blobs>

