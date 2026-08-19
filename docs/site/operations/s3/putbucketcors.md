# s3 / PutBucketCors {#operation-s3-putbucketcors}

[← s3 operation index](../../s3.md) · [Coverage matrix](../../coverage.md)

- **Capability ID:** `operation:s3:putbucketcors`
- **Status:** ⛔ unsupported
- **Disposition:** 🔵 by design
- **Azure equivalent:** `(no equivalent — proxy returns 501 NotImplemented)`

## Sub-features

### configuration storage {#sub-feature-configuration-storage}

- **Capability ID:** `sub-feature:s3:putbucketcors:configuration-storage`
- **Status:** ⛔ unsupported
- **Disposition:** 🔵 by design

See the matching Get* gap doc for the Azure-side reason.

## Behaviour differences

- PUT returns HTTP 501 NotImplemented to make the absence explicit; the matching GET returns the documented 'never configured' shape.

## References

- <https://docs.aws.amazon.com/AmazonS3/latest/API/API_PutBucketCors.html>

