# s3 / GetObjectRetention {#operation-s3-getobjectretention}

[← s3 operation index](../../s3.md) · [Coverage matrix](../../coverage.md)

- **Capability ID:** `operation:s3:getobjectretention`
- **Status:** ✅ implemented
- **Azure equivalent:** `Blob immutability policy (HEAD blob: x-ms-immutability-policy-mode/-until-date)`
- **Real-Azure verified:** ✅ 2026-06-29 · [evidence](https://github.com/pedrosakuma/aws2azure/actions/runs/28346494642) · [workflow run](https://github.com/pedrosakuma/aws2azure/actions/runs/28346494642)

## Sub-features

### mode + retain-until {#sub-feature-mode--retain-until}

- **Capability ID:** `sub-feature:s3:getobjectretention:mode--retain-until`
- **Status:** ✅ implemented
- **Real-Azure verified:** ✅ 2026-06-29 · [evidence](https://github.com/pedrosakuma/aws2azure/actions/runs/28346494642) · [workflow run](https://github.com/pedrosakuma/aws2azure/actions/runs/28346494642)

Reads the blob immutability policy via HEAD: x-ms-immutability-policy-mode (locked->COMPLIANCE, unlocked->GOVERNANCE) and x-ms-immutability-policy-until-date. Returns 404 NoSuchObjectLockConfiguration when no policy is set.

## Behaviour differences

- Mode mapping: GOVERNANCE<->unlocked, COMPLIANCE<->locked. Azure locked is irreversible and extend-only, like S3 COMPLIANCE.
- Verified against real Azure only - Azurite does not support immutability policies.
- Real S3 omits the Content-Type header on GetObjectRetention 200 responses despite the XML body; the proxy's XML response writer path sets Content-Type application/xml because Kestrel's default XML content negotiation adds it. AWS SDKs read the body via the operation-specific unmarshaller regardless of Content-Type, so the extra header does not affect deserialization. [conformance:object-retention-roundtrip::extra-header:content-type]
- RetainUntilDate reflects the Azure blob's x-ms-immutability-policy-until-date the proxy read at request time. The offline Tier-3 diff compares captures produced at different wall-clock times whose PutObjectRetention step set 'now + N seconds' as the retain-until value, so the two envelopes carry different absolute timestamps even though the retention policy round-tripped correctly on each backend. [conformance:object-retention-roundtrip::field-value:RetainUntilDate]

## References

- <https://docs.aws.amazon.com/AmazonS3/latest/API/API_GetObjectRetention.html>
- <https://learn.microsoft.com/en-us/rest/api/storageservices/get-blob-properties>

