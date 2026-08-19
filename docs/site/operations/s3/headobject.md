# s3 / HeadObject {#operation-s3-headobject}

[← s3 operation index](../../s3.md) · [Coverage matrix](../../coverage.md)

- **Capability ID:** `operation:s3:headobject`
- **Status:** ✅ implemented
- **Azure equivalent:** `HEAD https://{account}.blob.core.windows.net/{container}/{blob}`
- **Real-Azure verified:** ✅ 2026-07-17 · [evidence](https://github.com/pedrosakuma/aws2azure/actions/runs/29548639280) · [workflow run](https://github.com/pedrosakuma/aws2azure/actions/runs/29548639280)

## Sub-features

### conditional headers {#sub-feature-conditional-headers}

- **Capability ID:** `sub-feature:s3:headobject:conditional-headers`
- **Status:** ✅ implemented

**Gap.** Concrete-ETag conditionals are evaluated proxy-side (the proxy translates ETags). A failed If-Match yields a bodiless 412 with x-amz-error-code: PreconditionFailed and no leaked object metadata; If-None-Match match yields 304.

### standard response headers (ETag, Last-Modified, Content-Length, Content-Type, …) {#sub-feature-standard-response-headers--etag-last-modified-content-length-content-type}

- **Capability ID:** `sub-feature:s3:headobject:standard-response-headers--etag-last-modified-content-length-content-type`
- **Status:** ✅ implemented

### user metadata (x-amz-meta-*) {#sub-feature-user-metadata--x-amz-meta}

- **Capability ID:** `sub-feature:s3:headobject:user-metadata--x-amz-meta`
- **Status:** ✅ implemented

## Behaviour differences

- 404 responses include x-amz-error-code: NoSuchKey and an empty body (HEAD spec).
- 412 PreconditionFailed (failed If-Match) responses include x-amz-error-code: PreconditionFailed and an empty body (HEAD spec).
- Presigned HEAD is accepted (see PresignedUrl.yaml).

## References

- <https://docs.aws.amazon.com/AmazonS3/latest/API/API_HeadObject.html>
- <https://learn.microsoft.com/rest/api/storageservices/get-blob-properties>

