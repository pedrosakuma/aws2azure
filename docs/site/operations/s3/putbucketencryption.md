# s3 / PutBucketEncryption {#operation-s3-putbucketencryption}

[← s3 operation index](../../s3.md) · [Coverage matrix](../../coverage.md)

- **Capability ID:** `operation:s3:putbucketencryption`
- **Status:** 🟡 partial
- **Disposition:** 🔵 by design
- **Azure equivalent:** `Conditional container-metadata update for SSE-S3 intent`

## Sub-features

### SSE-S3 AES256 intent {#sub-feature-sse-s3-aes256-intent}

- **Capability ID:** `sub-feature:s3:putbucketencryption:sse-s3-aes256-intent`
- **Status:** ✅ implemented

Accepts one AES256 rule without KMS key material or BucketKeyEnabled=true and persists it with bounded ETag/If-Match retry.

### SSE-KMS {#sub-feature-sse-kms}

- **Capability ID:** `sub-feature:s3:putbucketencryption:sse-kms`
- **Status:** ⛔ unsupported
- **Disposition:** 🔵 by design

aws:kms, KMSMasterKeyID, and bucket keys return 501 NotImplemented.

### SSE-C {#sub-feature-sse-c}

- **Capability ID:** `sub-feature:s3:putbucketencryption:sse-c`
- **Status:** ⛔ unsupported
- **Disposition:** 🔵 by design

Customer-provided keys remain unsupported on object operations; BlockedEncryptionTypes rules are recognized but return 501 because the proxy cannot enforce them.

## Behaviour differences

- The AES256 document records application intent only and does not alter Azure Storage encryption configuration.

## References

- <https://docs.aws.amazon.com/AmazonS3/latest/API/API_PutBucketEncryption.html>

