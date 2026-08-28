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
- AWS secret names may contain `/_+=.@-` (hierarchical, slash-separated names are the default convention for tools such as Airflow's `SecretsManagerBackend`), but Key Vault secret names must match `^[0-9a-zA-Z-]+$` and be at most 127 characters. Names that are not already Key-Vault-legal are deterministically translated to a sanitized-prefix-plus-hash Key Vault name before every Key Vault REST call; the original AWS name is preserved verbatim in the `aws2azure-secret-name` internal tag (stripped from caller-visible tags) so ListSecrets, DescribeSecret, GetSecretValue, and all other operations still expose the exact AWS name the caller used. [conformance:Aws2Azure.UnitTests.SecretsManager.KeyVaultSecretNameEncodingTests.HandleAsync_CreateSecret_with_slash_name_targets_encoded_key_vault_path_and_tags_raw_name] [conformance:Aws2Azure.UnitTests.SecretsManager.KeyVaultSecretNameEncodingTests.HandleAsync_GetSecretValue_with_slash_name_resolves_via_encoded_key_vault_path]
- Key Vault tag values are capped at 256 characters, while AWS Secrets Manager names allow up to 512. For a name in that gap, the `aws2azure-secret-name` tag is intentionally omitted (rather than silently truncated); CreateSecret still succeeds and echoes the exact AWS name in its own response, but ListSecrets recovers only the Key-Vault-legal encoded name for that secret until it is renamed to 256 characters or fewer. [conformance:Aws2Azure.UnitTests.SecretsManager.KeyVaultSecretNameEncodingTests.HandleAsync_CreateSecret_omits_raw_name_tag_when_name_exceeds_key_vault_tag_value_limit]

## References

- <https://docs.aws.amazon.com/secretsmanager/latest/apireference/API_CreateSecret.html>
- <https://learn.microsoft.com/rest/api/keyvault/secrets/set-secret>

