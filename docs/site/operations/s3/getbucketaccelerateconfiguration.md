# s3 / GetBucketAccelerateConfiguration {#operation-s3-getbucketaccelerateconfiguration}

[← s3 operation index](../../s3.md) · [Coverage matrix](../../coverage.md)

- **Capability ID:** `operation:s3:getbucketaccelerateconfiguration`
- **Status:** 🟡 partial
- **Disposition:** 🔵 by design
- **Azure equivalent:** `(no equivalent — proxy returns stable Suspended)`

## Sub-features

### Suspended contract {#sub-feature-suspended-contract}

- **Capability ID:** `sub-feature:s3:getbucketaccelerateconfiguration:suspended-contract`
- **Status:** ✅ implemented

Always returns <Status>Suspended</Status>, including after an accepted Suspended PUT.

### Enabled acceleration {#sub-feature-enabled-acceleration}

- **Capability ID:** `sub-feature:s3:getbucketaccelerateconfiguration:enabled-acceleration`
- **Status:** ⛔ unsupported
- **Disposition:** 🔵 by design

No Azure equivalent of S3 Transfer Acceleration is configured by the data-plane proxy.

## Behaviour differences

- GET returns an explicit Suspended state rather than a topology-dependent acceleration claim.

## References

- <https://docs.aws.amazon.com/AmazonS3/latest/API/API_GetBucketAccelerateConfiguration.html>

