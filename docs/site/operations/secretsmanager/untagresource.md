# secretsmanager / UntagResource {#operation-secretsmanager-untagresource}

[← secretsmanager operation index](../../secretsmanager.md) · [Coverage matrix](../../coverage.md)

- **Capability ID:** `operation:secretsmanager:untagresource`
- **Status:** 🟡 partial
- **Disposition:** 🔵 by design
- **Azure equivalent:** `PATCH https://{vault}.vault.azure.net/secrets/{name}/{version}`

## Behaviour differences

- UntagResource removes the requested user tag keys from the current Key Vault secret's tags map and returns HTTP 200 with an empty body, matching the AWS success shape.
- Untagging is idempotent for absent user tags, but aws2azure- reserved metadata keys are preserved even if a caller names them so version-stage bookkeeping cannot be deleted through the AWS tagging API.
- Because Key Vault stores tags on secret versions, PutSecretValue and UpdateSecret explicitly copy the current caller-visible tags onto the next version so UntagResource changes persist across later writes.
- Acceptance currently relies on unit-test coverage against the Key Vault REST test double; real-Azure verification is still pending, so the operation remains partial.

## References

- <https://docs.aws.amazon.com/secretsmanager/latest/apireference/API_UntagResource.html>
- <https://learn.microsoft.com/rest/api/keyvault/secrets/update-secret/update-secret>

