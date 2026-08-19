# s3 / PutBucketOwnershipControls {#operation-s3-putbucketownershipcontrols}

[← s3 operation index](../../s3.md) · [Coverage matrix](../../coverage.md)

- **Capability ID:** `operation:s3:putbucketownershipcontrols`
- **Status:** 🟡 partial
- **Disposition:** 🔵 by design
- **Azure equivalent:** `Conditional container-metadata update (persisted compatibility intent only)`

## Sub-features

### configuration storage {#sub-feature-configuration-storage}

- **Capability ID:** `sub-feature:s3:putbucketownershipcontrols:configuration-storage`
- **Status:** ✅ implemented

Persists one valid ObjectOwnership rule with bounded ETag/If-Match retry while preserving unrelated container metadata.

### ACL ownership enforcement {#sub-feature-acl-ownership-enforcement}

- **Capability ID:** `sub-feature:s3:putbucketownershipcontrols:acl-ownership-enforcement`
- **Status:** ⛔ unsupported
- **Disposition:** 🔵 by design

BucketOwnerPreferred/ObjectWriter are recorded but do not enable non-owner ACL grants; BucketOwnerEnforced does not alter Azure authorization.

## Behaviour differences

- This is an intent surface, not an enforcement surface. Azure account/container authorization remains operator-managed.

## References

- <https://docs.aws.amazon.com/AmazonS3/latest/API/API_PutBucketOwnershipControls.html>

