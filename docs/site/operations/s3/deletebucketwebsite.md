# s3 / DeleteBucketWebsite {#operation-s3-deletebucketwebsite}

[← s3 operation index](../../s3.md) · [Coverage matrix](../../coverage.md)

- **Capability ID:** `operation:s3:deletebucketwebsite`
- **Status:** ⚪ stub
- **Disposition:** 🔵 by design
- **Azure equivalent:** `(no equivalent — proxy treats it as a no-op)`

## Sub-features

### idempotent clear {#sub-feature-idempotent-clear}

- **Capability ID:** `sub-feature:s3:deletebucketwebsite:idempotent-clear`
- **Status:** ✅ implemented

Returns 204 No Content because the underlying configuration is never set in the first place.

## References

- <https://docs.aws.amazon.com/AmazonS3/latest/API/API_DeleteBucketWebsite.html>

