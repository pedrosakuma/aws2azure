# s3 design gap / No IAM / ACL / bucket-policy authorization model {#design-gap-s3-no-iam---acl---bucket-policy-authorization-model}

[← Design-gap index](../../design-gaps.md)

- **Capability ID:** `design-gap:s3:no-iam---acl---bucket-policy-authorization-model`
- **Status:** 🔵 by design

Authorization is the static AWS-key-to-Azure-credential mapping validated by SigV4; there is no server-side IAM. ACLs are synthesised as owner-only and bucket-policy enforcement is not translated.

**Impact.** Fine-grained S3 access control remains outside the proxy.

**Workaround.** Enforce authorization with Azure RBAC, SAS, and network controls.

## References

- <https://docs.aws.amazon.com/AmazonS3/latest/API/API_GetBucketPolicy.html>

