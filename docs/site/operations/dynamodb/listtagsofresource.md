# dynamodb / ListTagsOfResource {#operation-dynamodb-listtagsofresource}

[← dynamodb operation index](../../dynamodb.md) · [Coverage matrix](../../coverage.md)

- **Capability ID:** `operation:dynamodb:listtagsofresource`
- **Status:** ✅ implemented
- **Azure equivalent:** `Azure Cosmos DB account/resource tags (control plane)`

## Sub-features

### Returns persisted TableMetadata tags {#sub-feature-returns-persisted-tablemetadata-tags}

- **Capability ID:** `sub-feature:dynamodb:listtagsofresource:returns-persisted-tablemetadata-tags`
- **Status:** ✅ implemented

Reads tags from the aws2azure TableMetadata sidecar document written by TagResource.

### Pagination {#sub-feature-pagination}

- **Capability ID:** `sub-feature:dynamodb:listtagsofresource:pagination`
- **Status:** 🟡 partial
- **Disposition:** ⚫ non-goal

The proxy returns the full tag set (DynamoDB allows at most 50 tags) and rejects NextToken instead of paginating.

## Behaviour differences

- Tags are stored in the aws2azure TableMetadata sidecar document inside the table's Cosmos container, not as Azure control-plane resource tags.
- Persisted tags have no effect on Azure billing, routing, Azure Policy, or Azure-native tag queries.
- Acceptance has unit-test coverage against the Cosmos REST test double; real-Azure validation is pending.

## References

- <https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_ListTagsOfResource.html>

