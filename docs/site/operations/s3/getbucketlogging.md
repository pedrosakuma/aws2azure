# s3 / GetBucketLogging {#operation-s3-getbucketlogging}

[← s3 operation index](../../s3.md) · [Coverage matrix](../../coverage.md)

- **Capability ID:** `operation:s3:getbucketlogging`
- **Status:** ⚪ stub
- **Disposition:** 🔵 by design
- **Azure equivalent:** `(no equivalent — proxy returns an empty <BucketLoggingStatus/> document)`

## Sub-features

### configuration storage {#sub-feature-configuration-storage}

- **Capability ID:** `sub-feature:s3:getbucketlogging:configuration-storage`
- **Status:** ⛔ unsupported
- **Disposition:** 🔵 by design

S3 returns an empty <BucketLoggingStatus/> when logging has never been enabled; the proxy mirrors that. Azure Storage Analytics logging is account-scoped and not bridged.

## Behaviour differences

- GET returns 200 with an empty <BucketLoggingStatus/> document, matching the S3 'never configured' wire shape.

## References

- <https://docs.aws.amazon.com/AmazonS3/latest/API/API_GetBucketLogging.html>

