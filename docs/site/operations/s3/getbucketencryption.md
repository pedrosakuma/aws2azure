# s3 / GetBucketEncryption {#operation-s3-getbucketencryption}

[← s3 operation index](../../s3.md) · [Coverage matrix](../../coverage.md)

- **Capability ID:** `operation:s3:getbucketencryption`
- **Status:** 🟡 partial
- **Disposition:** 🔵 by design
- **Azure equivalent:** `Container metadata for SSE-S3 intent; Azure Storage encryption remains account-managed`

## Sub-features

### SSE-S3 AES256 intent {#sub-feature-sse-s3-aes256-intent}

- **Capability ID:** `sub-feature:s3:getbucketencryption:sse-s3-aes256-intent`
- **Status:** ✅ implemented

Returns a persisted AES256 ServerSideEncryptionConfiguration and survives proxy restart.

### SSE-KMS and SSE-C {#sub-feature-sse-kms-and-sse-c}

- **Capability ID:** `sub-feature:s3:getbucketencryption:sse-kms-and-sse-c`
- **Status:** ⛔ unsupported
- **Disposition:** 🔵 by design

Customer-managed KMS identity and customer-provided key semantics are not representable through this data-plane proxy.

## Behaviour differences

- AES256 is compatibility intent only. Azure encrypts data at rest according to storage-account settings, not this metadata value.
- When no explicit intent is stored, the response reports the AWS default SSE-S3 AES256 configuration.

## References

- <https://docs.aws.amazon.com/AmazonS3/latest/API/API_GetBucketEncryption.html>

