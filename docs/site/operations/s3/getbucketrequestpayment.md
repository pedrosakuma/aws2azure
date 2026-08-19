# s3 / GetBucketRequestPayment {#operation-s3-getbucketrequestpayment}

[← s3 operation index](../../s3.md) · [Coverage matrix](../../coverage.md)

- **Capability ID:** `operation:s3:getbucketrequestpayment`
- **Status:** 🟡 partial
- **Disposition:** 🔵 by design
- **Azure equivalent:** `(no equivalent — proxy returns the S3 default body)`

## Sub-features

### BucketOwner contract {#sub-feature-bucketowner-contract}

- **Capability ID:** `sub-feature:s3:getbucketrequestpayment:bucketowner-contract`
- **Status:** ✅ implemented

Always reports BucketOwner, including after an accepted PutBucketRequestPayment BucketOwner no-op.

### Requester Pays {#sub-feature-requester-pays}

- **Capability ID:** `sub-feature:s3:getbucketrequestpayment:requester-pays`
- **Status:** ⛔ unsupported
- **Disposition:** 🔵 by design

Azure billing is account-scoped and cannot charge the S3 requester.

## Behaviour differences

- GET deterministically returns 200 with <Payer>BucketOwner</Payer>; Requester Pays is never activated.

## References

- <https://docs.aws.amazon.com/AmazonS3/latest/API/API_GetBucketRequestPayment.html>

