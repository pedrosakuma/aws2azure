# s3 / GetBucketCors {#operation-s3-getbucketcors}

[← s3 operation index](../../s3.md) · [Coverage matrix](../../coverage.md)

- **Capability ID:** `operation:s3:getbucketcors`
- **Status:** ⛔ unsupported
- **Disposition:** 🔵 by design
- **Azure equivalent:** `(no equivalent — proxy returns 404 NoSuchCORSConfiguration)`

## Sub-features

### configuration storage {#sub-feature-configuration-storage}

- **Capability ID:** `sub-feature:s3:getbucketcors:configuration-storage`
- **Status:** ⛔ unsupported
- **Disposition:** 🔵 by design

Azure has container-level CORS only via the Blob service properties (account-wide). The proxy does not yet bridge these.

## Behaviour differences

- GET returns HTTP 404 with code NoSuchCORSConfiguration so clients receive the same shape as a never-configured S3 bucket instead of InternalError.

## References

- <https://docs.aws.amazon.com/AmazonS3/latest/API/API_GetBucketCors.html>

