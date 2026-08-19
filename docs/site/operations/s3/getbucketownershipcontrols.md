# s3 / GetBucketOwnershipControls {#operation-s3-getbucketownershipcontrols}

[← s3 operation index](../../s3.md) · [Coverage matrix](../../coverage.md)

- **Capability ID:** `operation:s3:getbucketownershipcontrols`
- **Status:** 🟡 partial
- **Disposition:** 🔵 by design
- **Azure equivalent:** `Container metadata (persisted compatibility intent only)`

## Sub-features

### persisted ownership mode {#sub-feature-persisted-ownership-mode}

- **Capability ID:** `sub-feature:s3:getbucketownershipcontrols:persisted-ownership-mode`
- **Status:** ✅ implemented

Returns the stored BucketOwnerEnforced, BucketOwnerPreferred, or ObjectWriter document and survives proxy restart.

### ACL ownership enforcement {#sub-feature-acl-ownership-enforcement}

- **Capability ID:** `sub-feature:s3:getbucketownershipcontrols:acl-ownership-enforcement`
- **Status:** ⛔ unsupported
- **Disposition:** 🔵 by design

The value is compatibility intent only; Azure RBAC/Shared Key/SAS authorization is unchanged and non-owner ACL grants remain unsupported.

## Behaviour differences

- A missing intent returns 404 OwnershipControlsNotFoundError. A present intent is not an Azure authorization control.

## References

- <https://docs.aws.amazon.com/AmazonS3/latest/API/API_GetBucketOwnershipControls.html>

