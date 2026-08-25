# secretsmanager / TagResource {#operation-secretsmanager-tagresource}

[← secretsmanager operation index](../../secretsmanager.md) · [Coverage matrix](../../coverage.md)

- **Capability ID:** `operation:secretsmanager:tagresource`
- **Status:** 🟡 partial
- **Disposition:** 🔵 by design
- **Azure equivalent:** `PATCH https://{vault}.vault.azure.net/secrets/{name}/{version}`

## Behaviour differences

- TagResource merges the supplied AWS tag array into the current Key Vault secret's tags map and returns HTTP 200 with an empty body, matching the AWS success shape.
- The aws2azure- tag prefix remains reserved for proxy-owned version metadata; caller-supplied reserved keys are ignored so tagging cannot corrupt version-stage bookkeeping.
- Because Key Vault stores tags on secret versions, PutSecretValue and UpdateSecret explicitly copy the current caller-visible tags onto the next version so TagResource changes persist across later writes.
- Acceptance currently relies on unit-test coverage against the Key Vault REST test double; real-Azure verification is still pending, so the operation remains partial.

## References

- <https://docs.aws.amazon.com/secretsmanager/latest/apireference/API_TagResource.html>
- <https://learn.microsoft.com/rest/api/keyvault/secrets/update-secret/update-secret>

