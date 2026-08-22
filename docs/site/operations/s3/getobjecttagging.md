# s3 / GetObjectTagging {#operation-s3-getobjecttagging}

[← s3 operation index](../../s3.md) · [Coverage matrix](../../coverage.md)

- **Capability ID:** `operation:s3:getobjecttagging`
- **Status:** ✅ implemented
- **Azure equivalent:** `GET {blob}?comp=tags (Azure Blob Index Tags)`
- **Real-Azure verified:** ✅ 2026-08-11 · [evidence](https://github.com/pedrosakuma/aws2azure/actions/runs/31447675330) · [workflow run](https://github.com/pedrosakuma/aws2azure/actions/runs/31447675330)

## Sub-features

### tag listing {#sub-feature-tag-listing}

- **Capability ID:** `sub-feature:s3:getobjecttagging:tag-listing`
- **Status:** ✅ implemented

Azure response's <Tags><TagSet> is rewrapped as S3 <Tagging><TagSet>.

### versionId qualifier {#sub-feature-versionid-qualifier}

- **Capability ID:** `sub-feature:s3:getobjecttagging:versionid-qualifier`
- **Status:** ✅ implemented

Maps the opaque S3 versionId to Azure versionid. Unqualified requests HEAD and pin the current Azure version before Get Blob Tags so x-amz-version-id identifies the returned tag set.

## Behaviour differences

- Returns an empty TagSet (200) when no tags are set, matching Azure's behaviour. Azure surfaces 'no tags' as an empty set rather than a NoSuchTagSet error.
- Version selection depends on Azure Blob versioning being enabled by the operator.
- Azure BlobVersionNotFound maps to S3 NoSuchVersion.

## References

- <https://docs.aws.amazon.com/AmazonS3/latest/API/API_GetObjectTagging.html>
- <https://learn.microsoft.com/rest/api/storageservices/get-blob-tags>

