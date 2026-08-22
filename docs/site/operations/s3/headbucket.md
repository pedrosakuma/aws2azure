# s3 / HeadBucket {#operation-s3-headbucket}

[← s3 operation index](../../s3.md) · [Coverage matrix](../../coverage.md)

- **Capability ID:** `operation:s3:headbucket`
- **Status:** ✅ implemented
- **Azure equivalent:** `HEAD https://{account}.blob.core.windows.net/{container}?restype=container`
- **Real-Azure verified:** ✅ 2026-08-11 · [evidence](https://github.com/pedrosakuma/aws2azure/actions/runs/31447675330) · [workflow run](https://github.com/pedrosakuma/aws2azure/actions/runs/31447675330)

## Sub-features

### x-amz-bucket-region {#sub-feature-x-amz-bucket-region}

- **Capability ID:** `sub-feature:s3:headbucket:x-amz-bucket-region`
- **Status:** ✅ implemented

## Behaviour differences

- 404 responses include x-amz-error-code: NoSuchBucket so SDKs can map the error without a body (HEAD has none).
- Real S3 HeadBucket 200 responses set Content-Type; application/xml even for an empty body; the Azure Get Container Properties response body is truly empty and Kestrel does not synthesize a Content-Type for a HEAD 200 without a body, so the proxy omits the header. Clients read only the status + x-amz-* headers on HEAD, so this makes no functional difference. [conformance:head-bucket-object-roundtrip::missing-header:content-type]
- x-amz-access-point-alias is not emitted because Azure Blob Storage has no equivalent of S3 Access Points; the proxy does not model access-point aliases, so a "true"/"false" value cannot be surfaced. [conformance:head-bucket-object-roundtrip::missing-header:x-amz-access-point-alias]

## References

- <https://docs.aws.amazon.com/AmazonS3/latest/API/API_HeadBucket.html>
- <https://learn.microsoft.com/rest/api/storageservices/get-container-properties>

