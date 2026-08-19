# dynamodb / TagResource {#operation-dynamodb-tagresource}

[← dynamodb operation index](../../dynamodb.md) · [Coverage matrix](../../coverage.md)

- **Capability ID:** `operation:dynamodb:tagresource`
- **Status:** ✅ implemented
- **Azure equivalent:** `Azure Cosmos DB account/resource tags (control plane)`

## Sub-features

### Tag persistence and round-trip {#sub-feature-tag-persistence-and-round-trip}

- **Capability ID:** `sub-feature:dynamodb:tagresource:tag-persistence-and-round-trip`
- **Status:** ✅ implemented

Persists table tags in the aws2azure TableMetadata sidecar document inside the Cosmos container and returns them from ListTagsOfResource.

### Merge duplicate keys {#sub-feature-merge-duplicate-keys}

- **Capability ID:** `sub-feature:dynamodb:tagresource:merge-duplicate-keys`
- **Status:** ✅ implemented

New values overwrite existing keys while preserving unrelated tags; the final tag set is limited to 50 tags.

## Behaviour differences

- Tags are stored in the aws2azure TableMetadata sidecar document inside the table's Cosmos container, not as Azure control-plane resource tags.
- Persisted tags have no effect on Azure billing, routing, Azure Policy, or Azure-native tag queries.
- Acceptance has unit-test coverage against the Cosmos REST test double; real-Azure validation is pending.

## References

- <https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_TagResource.html>

