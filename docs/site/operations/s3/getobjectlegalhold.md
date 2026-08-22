# s3 / GetObjectLegalHold {#operation-s3-getobjectlegalhold}

[← s3 operation index](../../s3.md) · [Coverage matrix](../../coverage.md)

- **Capability ID:** `operation:s3:getobjectlegalhold`
- **Status:** ✅ implemented
- **Azure equivalent:** `Blob legal hold (HEAD blob: x-ms-legal-hold)`
- **Real-Azure verified:** ✅ 2026-06-29 · [evidence](https://github.com/pedrosakuma/aws2azure/actions/runs/28346494642) · [workflow run](https://github.com/pedrosakuma/aws2azure/actions/runs/28346494642)

## Sub-features

### status ON/OFF {#sub-feature-status-on-off}

- **Capability ID:** `sub-feature:s3:getobjectlegalhold:status-on-off`
- **Status:** ✅ implemented
- **Real-Azure verified:** ✅ 2026-06-29 · [evidence](https://github.com/pedrosakuma/aws2azure/actions/runs/28346494642) · [workflow run](https://github.com/pedrosakuma/aws2azure/actions/runs/28346494642)

Reads x-ms-legal-hold from HEAD; true->ON, absent/false->OFF.

## Behaviour differences

- Verified against real Azure only - Azurite does not support legal hold.
- Real S3 omits the Content-Type header on GetObjectLegalHold 200 responses despite the XML body; the proxy's XML response writer path sets Content-Type application/xml because Kestrel's default XML content negotiation adds it. AWS SDKs read the body via the operation-specific unmarshaller regardless of Content-Type, so the extra header does not affect deserialization. [conformance:object-legal-hold-roundtrip::extra-header:content-type]

## References

- <https://docs.aws.amazon.com/AmazonS3/latest/API/API_GetObjectLegalHold.html>
- <https://learn.microsoft.com/en-us/rest/api/storageservices/get-blob-properties>

