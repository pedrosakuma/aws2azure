# s3 / GetBucketLifecycleConfiguration {#operation-s3-getbucketlifecycleconfiguration}

[← s3 operation index](../../s3.md) · [Coverage matrix](../../coverage.md)

- **Capability ID:** `operation:s3:getbucketlifecycleconfiguration`
- **Status:** ⛔ unsupported
- **Disposition:** 🔵 by design
- **Azure equivalent:** `(no equivalent — proxy returns 404 NoSuchLifecycleConfiguration)`

## Sub-features

### configuration storage {#sub-feature-configuration-storage}

- **Capability ID:** `sub-feature:s3:getbucketlifecycleconfiguration:configuration-storage`
- **Status:** ⛔ unsupported
- **Disposition:** 🔵 by design

Azure Storage Management lifecycle policies live on the storage account, not on individual containers; the proxy cannot expose them via the S3 surface today.

## Behaviour differences

- GET returns HTTP 404 with code NoSuchLifecycleConfiguration so clients receive the same shape as a never-configured S3 bucket instead of InternalError.

## References

- <https://docs.aws.amazon.com/AmazonS3/latest/API/API_GetBucketLifecycleConfiguration.html>

