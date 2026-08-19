# s3 / PutBucketTagging {#operation-s3-putbuckettagging}

[← s3 operation index](../../s3.md) · [Coverage matrix](../../coverage.md)

- **Capability ID:** `operation:s3:putbuckettagging`
- **Status:** 🟡 partial
- **Disposition:** 🔵 by design
- **Azure equivalent:** `PUT {container}?restype=container&comp=metadata`

## Sub-features

### replace whole tag set {#sub-feature-replace-whole-tag-set}

- **Capability ID:** `sub-feature:s3:putbuckettagging:replace-whole-tag-set`
- **Status:** ✅ implemented

Single atomic Azure metadata write.

### 50-tag limit per bucket {#sub-feature-50-tag-limit-per-bucket}

- **Capability ID:** `sub-feature:s3:putbuckettagging:50-tag-limit-per-bucket`
- **Status:** ✅ implemented

Enforced before the Azure call.

### 128-char key / 256-char value limits {#sub-feature-128-char-key---256-char-value-limits}

- **Capability ID:** `sub-feature:s3:putbuckettagging:128-char-key---256-char-value-limits`
- **Status:** ✅ implemented

Enforced before the Azure call.

## Behaviour differences

- Azure replaces the full metadata bag; the proxy uses bounded ETag/If-Match retry and re-merges every attempt so unrelated Azure metadata and other proxy intents are preserved.

## References

- <https://docs.aws.amazon.com/AmazonS3/latest/API/API_PutBucketTagging.html>

