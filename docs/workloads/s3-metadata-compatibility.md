# S3 metadata and compatibility controls

Use these surfaces when an application requires AWS configuration documents to
round-trip through the proxy, not when it requires those documents to enforce
authorization, billing, acceleration, or encryption-key policy.

| Surface | Contract |
|---|---|
| Bucket tagging and versioning | Persisted in Azure container metadata. Updates use bounded ETag/If-Match retry and preserve unrelated metadata. Blob versioning must be enabled out-of-band. |
| Ownership controls | `BucketOwnerEnforced`, `BucketOwnerPreferred`, and `ObjectWriter` persist as intent. ACL responses remain owner-only and non-owner grants remain unsupported. |
| Public access block | All four flags persist as intent. Azure account/container public-access controls remain operator-managed. |
| Bucket encryption | SSE-S3 `AES256` persists as intent. SSE-KMS, SSE-C, KMS key IDs, and bucket keys remain unsupported. |
| Request payment | `BucketOwner` is an accepted stable no-op; `Requester` is unsupported. |
| Acceleration | `Suspended` is an accepted stable no-op; `Enabled` is unsupported. |
| Object tagging and ACL | `versionId` selects the corresponding Azure blob version where Blob versioning supports it; ACL state remains synthetic owner-only. |

Persisted intents survive proxy restart because they live with the Azure
container. They do not survive container replacement unless the metadata is
copied. Treat the Azure resource configuration as the enforcement source of
truth and validate version-specific tagging against real Azure; Azurite does
not provide equivalent Blob-version behavior.
