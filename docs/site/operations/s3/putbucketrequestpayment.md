# s3 / PutBucketRequestPayment {#operation-s3-putbucketrequestpayment}

[← s3 operation index](../../s3.md) · [Coverage matrix](../../coverage.md)

- **Capability ID:** `operation:s3:putbucketrequestpayment`
- **Status:** 🟡 partial
- **Disposition:** 🔵 by design
- **Azure equivalent:** `(no equivalent — BucketOwner is an accepted stable no-op)`

## Sub-features

### BucketOwner {#sub-feature-bucketowner}

- **Capability ID:** `sub-feature:s3:putbucketrequestpayment:bucketowner`
- **Status:** ✅ implemented

Accepted as a 200 no-op; the matching GET remains BucketOwner.

### Requester {#sub-feature-requester}

- **Capability ID:** `sub-feature:s3:putbucketrequestpayment:requester`
- **Status:** ⛔ unsupported
- **Disposition:** 🔵 by design

Returns 501 NotImplemented because Azure cannot implement requester billing.

## Behaviour differences

- BucketOwner is stable but not persisted because it is the only representable state.

## References

- <https://docs.aws.amazon.com/AmazonS3/latest/API/API_PutBucketRequestPayment.html>

