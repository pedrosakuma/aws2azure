# s3 / PutBucketAccelerateConfiguration {#operation-s3-putbucketaccelerateconfiguration}

[← s3 operation index](../../s3.md) · [Coverage matrix](../../coverage.md)

- **Capability ID:** `operation:s3:putbucketaccelerateconfiguration`
- **Status:** 🟡 partial
- **Disposition:** 🔵 by design
- **Azure equivalent:** `(no equivalent — Suspended is an accepted stable no-op)`

## Sub-features

### Suspended {#sub-feature-suspended}

- **Capability ID:** `sub-feature:s3:putbucketaccelerateconfiguration:suspended`
- **Status:** ✅ implemented

Accepted as a 200 no-op; the matching GET remains Suspended.

### Enabled {#sub-feature-enabled}

- **Capability ID:** `sub-feature:s3:putbucketaccelerateconfiguration:enabled`
- **Status:** ⛔ unsupported
- **Disposition:** 🔵 by design

Returns 501 NotImplemented; no Azure acceleration topology is assumed or provisioned.

## Behaviour differences

- Suspended is stable but not persisted because it is the only representable state.

## References

- <https://docs.aws.amazon.com/AmazonS3/latest/API/API_PutBucketAccelerateConfiguration.html>

