# secretsmanager / CreateSecret {#operation-secretsmanager-createsecret}

[← secretsmanager operation index](../../secretsmanager.md) · [Coverage matrix](../../coverage.md)

- **Capability ID:** `operation:secretsmanager:createsecret`
- **Status:** ✅ implemented
- **Azure equivalent:** `PUT https://{vault}.vault.azure.net/secrets/{name}`
- **Real-Azure verified:** ✅ 2026-07-16 · [evidence](https://github.com/pedrosakuma/aws2azure/actions/runs/29473539261) · [workflow run](https://github.com/pedrosakuma/aws2azure/actions/runs/29473539261)

## Behaviour differences

- Initial MVP uses Key Vault AAD auth and translates the core secret CRUD/read paths to AWS Secrets Manager JSON responses.
- Advanced rotation, restore, and policy semantics are not yet modeled; the proxy uses Key Vault secret versions as the AWS version surface.
- Responses use the AWS JSON 1.1 wire shape (Unix-epoch numeric timestamps, Content-Type application/x-amz-json-1.1); validated end-to-end against a real Azure Key Vault through the proxy with the AWS SDK.
- Returned ARNs use the synthetic `arn:aws:secretsmanager:azure:keyvault:secret:{name}` namespace documented in `_design.yaml`; exact real-AWS-vs-real-Azure ARN bytes are therefore intentionally non-comparable in Tier-3 offline diffs. [conformance:field-value:ARN]
- Input Tags are accepted in the AWS Key/Value array shape (as sent by the AWS SDK) and mapped to the Key Vault tags map; an existing-name conflict (including Key Vault 409) maps to ResourceExistsException.
- The aws2azure- tag prefix is reserved for proxy-owned version metadata and is stripped from caller-supplied tags before writing to Key Vault.
- For the current Tier-3 happy-path capture cases, exact `VersionId` bytes are intentionally case-scoped and non-comparable: each real-AWS capture and each real-Azure evidence export generates fresh CreateSecret/UpdateSecret idempotency tokens or Key Vault version ids in an independent run. The gate still compares field presence/shape and same-step semantics, but not cross-run UUID equality. [conformance:create-get-update-delete-secret-roundtrip::field-value:VersionId] [conformance:describe-secret-roundtrip::field-value:VersionId] [conformance:list-secrets-pagination::field-value:VersionId]
- ClientRequestToken is persisted on the first Key Vault version and replayed as the AWS-facing VersionId for same-payload create retries; conflicting same-name creates still return ResourceExistsException.
- Exactly one of SecretString or SecretBinary must be supplied. Unlike the earlier silent-binary-wins behavior, mixed or missing secret-value fields now fail with InvalidParameterException before any Key Vault write.

## References

- <https://docs.aws.amazon.com/secretsmanager/latest/apireference/API_CreateSecret.html>
- <https://learn.microsoft.com/rest/api/keyvault/secrets/set-secret>

