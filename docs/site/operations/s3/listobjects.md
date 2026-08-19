# s3 / ListObjects {#operation-s3-listobjects}

[← s3 operation index](../../s3.md) · [Coverage matrix](../../coverage.md)

- **Capability ID:** `operation:s3:listobjects`
- **Status:** ✅ implemented
- **Azure equivalent:** `GET https://{account}.blob.core.windows.net/{container}?restype=container&comp=list`

## Sub-features

### prefix {#sub-feature-prefix}

- **Capability ID:** `sub-feature:s3:listobjects:prefix`
- **Status:** ✅ implemented

### delimiter (CommonPrefixes via Azure BlobPrefix) {#sub-feature-delimiter--commonprefixes-via-azure-blobprefix}

- **Capability ID:** `sub-feature:s3:listobjects:delimiter--commonprefixes-via-azure-blobprefix`
- **Status:** ✅ implemented

### marker / NextMarker {#sub-feature-marker---nextmarker}

- **Capability ID:** `sub-feature:s3:listobjects:marker---nextmarker`
- **Status:** ✅ implemented

**Gap.** S3 V1 only emits NextMarker when a delimiter is set; we follow that contract.

### max-keys (default 1000, cap 1000) {#sub-feature-max-keys--default-1000-cap-1000}

- **Capability ID:** `sub-feature:s3:listobjects:max-keys--default-1000-cap-1000`
- **Status:** ✅ implemented

### encoding-type=url {#sub-feature-encoding-typeurl}

- **Capability ID:** `sub-feature:s3:listobjects:encoding-typeurl`
- **Status:** ✅ implemented

## Behaviour differences

- Legacy V1 listing kept alongside V2 for SDKs that have not migrated.
- Without a delimiter, callers derive the next marker from the last Contents.Key (matches S3 docs).

## References

- <https://docs.aws.amazon.com/AmazonS3/latest/API/API_ListObjects.html>
- <https://learn.microsoft.com/rest/api/storageservices/list-blobs>

