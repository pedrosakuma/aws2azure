# S3 basic object CRUD profile

This version 1 profile covers `CreateBucket`, `PutObject`, `GetObject`,
`HeadObject`, `ListObjectsV2`, `DeleteObject`, and `DeleteBucket` against Azure
Blob Storage.

Its current generated verdict is `candidate` because the previously reviewed
real-Azure qualification evidence is stale. Check
[workload GA certification](../site/workload-ga.md) before adoption; historical
qualification and approved-runtime records do not override the live verdict.

## Required deployment contract

- Use a dedicated storage account or binding boundary whose capacity, network,
  redundancy, and lifecycle settings match the workload.
- Enforce authorization with Azure RBAC, SAS, network controls, and binding
  isolation. The proxy does not reproduce S3 IAM, bucket-policy, or non-owner
  ACL authorization.
- Configure encryption and customer-managed keys on the Azure Storage account.
  Azure encrypts data at rest, but SSE-C and SSE-KMS request semantics are not
  reproduced.
- Size client retries for the documented retryable Azure throttling, timeout,
  and service-unavailable mappings.

## Qualified operation boundary

The profile is ordinary object CRUD only. Validate object bodies and metadata,
conditional requests used by the application, empty and large objects, listing
pagination, proxy restart, cancellation, throttling, retry exhaustion, and
rollback against the exact storage account topology.

Multipart upload, object versioning and lock, bucket sub-resource
administration, metadata compatibility controls, and presigned URL constraints
have separate contracts and are not implied by this profile.

Container and blob state survive proxy restart because Azure Blob Storage is
authoritative. Deleting a non-empty bucket follows the documented S3 error
mapping rather than silently removing its contents.

## Adoption decision

Adopt only when the generated profile verdict is acceptable and fresh
real-Azure evidence exists for the release and topology being deployed. See
[S3 metadata and compatibility controls](s3-metadata-compatibility.md) for
configuration documents that round-trip as compatibility intent rather than
Azure enforcement.
