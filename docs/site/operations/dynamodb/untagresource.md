# dynamodb / UntagResource {#operation-dynamodb-untagresource}

[← dynamodb operation index](../../dynamodb.md) · [Coverage matrix](../../coverage.md)

- **Capability ID:** `operation:dynamodb:untagresource`
- **Status:** ✅ implemented
- **Azure equivalent:** `Azure Cosmos DB account/resource tags (control plane)`

## Sub-features

### Remove persisted tag keys {#sub-feature-remove-persisted-tag-keys}

- **Capability ID:** `sub-feature:dynamodb:untagresource:remove-persisted-tag-keys`
- **Status:** ✅ implemented

Removes requested keys from the aws2azure TableMetadata sidecar document and invalidates the table metadata cache.

## Behaviour differences

- Tags are stored in the aws2azure TableMetadata sidecar document inside the table's Cosmos container, not as Azure control-plane resource tags.
- Removing tags has no effect on Azure billing, routing, Azure Policy, or Azure-native tag queries.
- Acceptance has unit-test coverage against the Cosmos REST test double; real-Azure validation is pending.

## References

- <https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_UntagResource.html>

