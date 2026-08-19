# s3 / DeleteBucketLifecycle {#operation-s3-deletebucketlifecycle}

[← s3 operation index](../../s3.md) · [Coverage matrix](../../coverage.md)

- **Capability ID:** `operation:s3:deletebucketlifecycle`
- **Status:** ⚪ stub
- **Disposition:** 🔵 by design
- **Azure equivalent:** `(no equivalent — proxy treats it as a no-op)`

## Sub-features

### idempotent clear {#sub-feature-idempotent-clear}

- **Capability ID:** `sub-feature:s3:deletebucketlifecycle:idempotent-clear`
- **Status:** ✅ implemented

Returns 204 No Content because the underlying configuration is never set in the first place.

## References

- <https://docs.aws.amazon.com/AmazonS3/latest/API/API_DeleteBucketLifecycle.html>

