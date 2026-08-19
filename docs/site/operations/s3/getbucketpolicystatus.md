# s3 / GetBucketPolicyStatus {#operation-s3-getbucketpolicystatus}

[← s3 operation index](../../s3.md) · [Coverage matrix](../../coverage.md)

- **Capability ID:** `operation:s3:getbucketpolicystatus`
- **Status:** ⛔ unsupported
- **Disposition:** 🔵 by design
- **Azure equivalent:** `(no equivalent — proxy returns 404 NoSuchBucketPolicy)`

## Sub-features

### configuration storage {#sub-feature-configuration-storage}

- **Capability ID:** `sub-feature:s3:getbucketpolicystatus:configuration-storage`
- **Status:** ⛔ unsupported
- **Disposition:** 🔵 by design

Same reason — no policy to summarise.

## Behaviour differences

- GET returns HTTP 404 with code NoSuchBucketPolicy so clients receive the same shape as a never-configured S3 bucket instead of InternalError.

## References

- <https://docs.aws.amazon.com/AmazonS3/latest/API/API_GetBucketPolicyStatus.html>

