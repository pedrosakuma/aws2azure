# s3 / PutObjectRetention {#operation-s3-putobjectretention}

[← s3 operation index](../../s3.md) · [Coverage matrix](../../coverage.md)

- **Capability ID:** `operation:s3:putobjectretention`
- **Status:** ✅ implemented
- **Azure equivalent:** `Set Blob Immutability Policy (PUT blob ?comp=immutabilityPolicies)`
- **Real-Azure verified:** ✅ 2026-06-29 · [evidence](https://github.com/pedrosakuma/aws2azure/actions/runs/28346494642) · [workflow run](https://github.com/pedrosakuma/aws2azure/actions/runs/28346494642)

## Sub-features

### mode + retain-until {#sub-feature-mode--retain-until}

- **Capability ID:** `sub-feature:s3:putobjectretention:mode--retain-until`
- **Status:** ✅ implemented
- **Real-Azure verified:** ✅ 2026-06-29 · [evidence](https://github.com/pedrosakuma/aws2azure/actions/runs/28346494642) · [workflow run](https://github.com/pedrosakuma/aws2azure/actions/runs/28346494642)

GOVERNANCE->Unlocked, COMPLIANCE->Locked; RetainUntilDate (ISO8601) -> x-ms-immutability-policy-until-date (RFC1123). Requires the object to exist.

## Behaviour differences

- Azure locked policies are irreversible and extend-only; bypassing/shortening COMPLIANCE is rejected by Azure as in S3.
- Requires the storage account to have version-level immutability + blob versioning enabled (operator-provisioned via ARM); Azure only allows version-level immutability support to be enabled when the storage account is created, so existing accounts without it must be replaced before adopting object-lock workloads.
- Verified against real Azure only - Azurite does not support immutability policies.

## References

- <https://docs.aws.amazon.com/AmazonS3/latest/API/API_PutObjectRetention.html>
- <https://learn.microsoft.com/en-us/rest/api/storageservices/set-blob-immutability-policy>

