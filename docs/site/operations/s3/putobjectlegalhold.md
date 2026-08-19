# s3 / PutObjectLegalHold {#operation-s3-putobjectlegalhold}

[← s3 operation index](../../s3.md) · [Coverage matrix](../../coverage.md)

- **Capability ID:** `operation:s3:putobjectlegalhold`
- **Status:** ✅ implemented
- **Azure equivalent:** `Set Blob Legal Hold (PUT blob ?comp=legalhold, x-ms-legal-hold)`
- **Real-Azure verified:** ✅ 2026-06-29 · [evidence](https://github.com/pedrosakuma/aws2azure/actions/runs/28346494642) · [workflow run](https://github.com/pedrosakuma/aws2azure/actions/runs/28346494642)

## Sub-features

### status ON/OFF {#sub-feature-status-on-off}

- **Capability ID:** `sub-feature:s3:putobjectlegalhold:status-on-off`
- **Status:** ✅ implemented
- **Real-Azure verified:** ✅ 2026-06-29 · [evidence](https://github.com/pedrosakuma/aws2azure/actions/runs/28346494642) · [workflow run](https://github.com/pedrosakuma/aws2azure/actions/runs/28346494642)

ON->x-ms-legal-hold:true, OFF->false. Requires the object to exist.

## Behaviour differences

- Verified against real Azure only - Azurite does not support legal hold.

## References

- <https://docs.aws.amazon.com/AmazonS3/latest/API/API_PutObjectLegalHold.html>
- <https://learn.microsoft.com/en-us/rest/api/storageservices/set-blob-legal-hold>

