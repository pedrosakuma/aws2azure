# s3 / PutObjectLockConfiguration {#operation-s3-putobjectlockconfiguration}

[← s3 operation index](../../s3.md) · [Coverage matrix](../../coverage.md)

- **Capability ID:** `operation:s3:putobjectlockconfiguration`
- **Status:** ⛔ unsupported
- **Disposition:** 🔵 by design
- **Azure equivalent:** `(bucket-level WORM is ARM/management-plane only; proxy returns 501 NotImplemented)`

## Sub-features

### configuration storage {#sub-feature-configuration-storage}

- **Capability ID:** `sub-feature:s3:putobjectlockconfiguration:configuration-storage`
- **Status:** ⛔ unsupported
- **Disposition:** 🔵 by design

Per-object retention/legal-hold are supported; bucket-level lock config is ARM-only. See the matching Get* gap doc.

## Behaviour differences

- PUT returns HTTP 501 NotImplemented to make the absence explicit; the matching GET returns the documented 'never configured' shape.

## References

- <https://docs.aws.amazon.com/AmazonS3/latest/API/API_PutObjectLockConfiguration.html>

