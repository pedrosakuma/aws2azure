# s3 / ListBuckets {#operation-s3-listbuckets}

[← s3 operation index](../../s3.md) · [Coverage matrix](../../coverage.md)

- **Capability ID:** `operation:s3:listbuckets`
- **Status:** ✅ implemented
- **Azure equivalent:** `GET https://{account}.blob.core.windows.net/?comp=list`

## Sub-features

### pagination {#sub-feature-pagination}

- **Capability ID:** `sub-feature:s3:listbuckets:pagination`
- **Status:** ✅ implemented

The proxy follows Azure's NextMarker chain until exhaustion and returns the complete bucket list in one S3 response.

### owner-identity {#sub-feature-owner-identity}

- **Capability ID:** `sub-feature:s3:listbuckets:owner-identity`
- **Status:** 🟡 partial
- **Disposition:** 🔵 by design

S3 Owner/ID + DisplayName are synthesized from the authenticated AWS access key id (no Azure-side equivalent).

## Behaviour differences

- CreationDate is populated from the container Last-Modified header — close enough for S3 SDKs but not strictly equivalent.
- Single fixed storage account per process (BlobCredentials); cross-account listing is out of scope.

## References

- <https://docs.aws.amazon.com/AmazonS3/latest/API/API_ListBuckets.html>
- <https://learn.microsoft.com/rest/api/storageservices/list-containers2>

