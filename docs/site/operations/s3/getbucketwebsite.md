# s3 / GetBucketWebsite {#operation-s3-getbucketwebsite}

[← s3 operation index](../../s3.md) · [Coverage matrix](../../coverage.md)

- **Capability ID:** `operation:s3:getbucketwebsite`
- **Status:** ⛔ unsupported
- **Disposition:** 🔵 by design
- **Azure equivalent:** `(no equivalent — proxy returns 404 NoSuchWebsiteConfiguration)`

## Sub-features

### configuration storage {#sub-feature-configuration-storage}

- **Capability ID:** `sub-feature:s3:getbucketwebsite:configuration-storage`
- **Status:** ⛔ unsupported
- **Disposition:** 🔵 by design

Azure Static Websites are account-scoped and configured via the management plane; no per-container equivalent.

## Behaviour differences

- GET returns HTTP 404 with code NoSuchWebsiteConfiguration so clients receive the same shape as a never-configured S3 bucket instead of InternalError.

## References

- <https://docs.aws.amazon.com/AmazonS3/latest/API/API_GetBucketWebsite.html>

