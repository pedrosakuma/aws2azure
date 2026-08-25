# secretsmanager / DescribeSecret {#operation-secretsmanager-describesecret}

[← secretsmanager operation index](../../secretsmanager.md) · [Coverage matrix](../../coverage.md)

- **Capability ID:** `operation:secretsmanager:describesecret`
- **Status:** ✅ implemented
- **Azure equivalent:** `GET https://{vault}.vault.azure.net/secrets/{name}?api-version=7.6`
- **Real-Azure verified:** ✅ 2026-07-16 · [evidence](https://github.com/pedrosakuma/aws2azure/actions/runs/29473539261) · [workflow run](https://github.com/pedrosakuma/aws2azure/actions/runs/29473539261)

## Behaviour differences

- Initial MVP uses Key Vault AAD auth and translates the core secret CRUD/read paths to AWS Secrets Manager JSON responses.
- Advanced rotation, restore, and policy semantics are not yet modeled; the proxy uses Key Vault secret versions as the AWS version surface.
- Responses use the AWS JSON 1.1 wire shape (Unix-epoch numeric timestamps, Content-Type application/x-amz-json-1.1); validated end-to-end against a real Azure Key Vault through the proxy with the AWS SDK.
- Tags are returned as an AWS Key/Value array sourced from the Key Vault secret's tags map.
- TagResource/UntagResource mutate the current Key Vault secret's tags map, and PutSecretValue/UpdateSecret carry those caller-visible tags forward when creating the next version.
- VersionIdsToStages is built from the complete paginated Key Vault version inventory. Deterministic same-token duplicates are collapsed to one logical AWS VersionId; conflicting token payload metadata returns ResourceExistsException.

## References

- <https://docs.aws.amazon.com/secretsmanager/latest/apireference/API_DescribeSecret.html>
- <https://learn.microsoft.com/rest/api/keyvault/secrets/get-secret-properties>

