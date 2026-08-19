# s3 / HeadBucket {#operation-s3-headbucket}

[← s3 operation index](../../s3.md) · [Coverage matrix](../../coverage.md)

- **Capability ID:** `operation:s3:headbucket`
- **Status:** ✅ implemented
- **Azure equivalent:** `HEAD https://{account}.blob.core.windows.net/{container}?restype=container`

## Sub-features

### x-amz-bucket-region {#sub-feature-x-amz-bucket-region}

- **Capability ID:** `sub-feature:s3:headbucket:x-amz-bucket-region`
- **Status:** ✅ implemented

## Behaviour differences

- 404 responses include x-amz-error-code: NoSuchBucket so SDKs can map the error without a body (HEAD has none).

## References

- <https://docs.aws.amazon.com/AmazonS3/latest/API/API_HeadBucket.html>
- <https://learn.microsoft.com/rest/api/storageservices/get-container-properties>

