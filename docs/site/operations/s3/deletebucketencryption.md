# s3 / DeleteBucketEncryption {#operation-s3-deletebucketencryption}

[← s3 operation index](../../s3.md) · [Coverage matrix](../../coverage.md)

- **Capability ID:** `operation:s3:deletebucketencryption`
- **Status:** 🟡 partial
- **Disposition:** 🔵 by design
- **Azure equivalent:** `Conditional container-metadata update`

## Sub-features

### idempotent AES256-intent clear {#sub-feature-idempotent-aes256-intent-clear}

- **Capability ID:** `sub-feature:s3:deletebucketencryption:idempotent-aes256-intent-clear`
- **Status:** ✅ implemented

Removes only the proxy-owned encryption-intent key, resets the compatibility response to default SSE-S3 AES256, and returns 204.

### Azure encryption change {#sub-feature-azure-encryption-change}

- **Capability ID:** `sub-feature:s3:deletebucketencryption:azure-encryption-change`
- **Status:** ⛔ unsupported
- **Disposition:** 🔵 by design

Azure Storage encryption remains enabled and account-managed.

## Behaviour differences

- Deleting the intent does not disable or reconfigure Azure encryption.
- A subsequent GetBucketEncryption returns the synthetic default SSE-S3 AES256 configuration.

## References

- <https://docs.aws.amazon.com/AmazonS3/latest/API/API_DeleteBucketEncryption.html>

