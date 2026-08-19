# s3 / DeleteBucketOwnershipControls {#operation-s3-deletebucketownershipcontrols}

[← s3 operation index](../../s3.md) · [Coverage matrix](../../coverage.md)

- **Capability ID:** `operation:s3:deletebucketownershipcontrols`
- **Status:** 🟡 partial
- **Disposition:** 🔵 by design
- **Azure equivalent:** `Conditional container-metadata update`

## Sub-features

### idempotent intent clear {#sub-feature-idempotent-intent-clear}

- **Capability ID:** `sub-feature:s3:deletebucketownershipcontrols:idempotent-intent-clear`
- **Status:** ✅ implemented

Removes only the proxy-owned ownership-intent key with bounded ETag/If-Match retry and returns 204.

### authorization change {#sub-feature-authorization-change}

- **Capability ID:** `sub-feature:s3:deletebucketownershipcontrols:authorization-change`
- **Status:** ⛔ unsupported
- **Disposition:** 🔵 by design

Deleting the compatibility intent does not alter Azure authorization.

## Behaviour differences

- The operation clears persisted proxy intent only.

## References

- <https://docs.aws.amazon.com/AmazonS3/latest/API/API_DeleteBucketOwnershipControls.html>

