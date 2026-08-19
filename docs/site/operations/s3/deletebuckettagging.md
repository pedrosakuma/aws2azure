# s3 / DeleteBucketTagging {#operation-s3-deletebuckettagging}

[← s3 operation index](../../s3.md) · [Coverage matrix](../../coverage.md)

- **Capability ID:** `operation:s3:deletebuckettagging`
- **Status:** ✅ implemented
- **Azure equivalent:** `Conditional GET + PUT {container}?restype=container&comp=metadata`

## Sub-features

### idempotent clear {#sub-feature-idempotent-clear}

- **Capability ID:** `sub-feature:s3:deletebuckettagging:idempotent-clear`
- **Status:** ✅ implemented

Removes only the proxy-owned bucket-tagging key and preserves unrelated metadata.

## Behaviour differences

- Azure replaces the full metadata bag; the proxy uses bounded ETag/If-Match retry and removes only its bucket-tagging key.

## References

- <https://docs.aws.amazon.com/AmazonS3/latest/API/API_DeleteBucketTagging.html>

