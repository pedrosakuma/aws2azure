# secretsmanager

## CreateSecret

- **Status:** ✅ implemented
- **Azure equivalent:** `PUT https://{vault}.vault.azure.net/secrets/{name}`

### Behaviour differences

- Initial MVP uses Key Vault AAD auth and translates the core secret CRUD/read paths to AWS Secrets Manager JSON responses.
- Advanced rotation, restore, and policy semantics are not yet modeled; the proxy uses Key Vault secret versions as the AWS version surface.
- Responses use the AWS JSON 1.1 wire shape (Unix-epoch numeric timestamps, Content-Type application/x-amz-json-1.1); validated end-to-end against a real Azure Key Vault through the proxy with the AWS SDK.
- Input Tags are accepted in the AWS Key/Value array shape (as sent by the AWS SDK) and mapped to the Key Vault tags map; an existing-name conflict (including Key Vault 409) maps to ResourceExistsException.
- The aws2azure- tag prefix is reserved for proxy-owned version metadata and is stripped from caller-supplied tags before writing to Key Vault.

### References

- <https://docs.aws.amazon.com/secretsmanager/latest/apireference/API_CreateSecret.html>
- <https://learn.microsoft.com/rest/api/keyvault/secrets/set-secret>

## DeleteSecret

- **Status:** ✅ implemented
- **Azure equivalent:** `DELETE https://{vault}.vault.azure.net/secrets/{name}`

### Behaviour differences

- Initial MVP uses Key Vault AAD auth and translates the core secret CRUD/read paths to AWS Secrets Manager JSON responses.
- Advanced rotation, restore, and policy semantics are not yet modeled; the proxy uses Key Vault secret versions as the AWS version surface.
- Responses use the AWS JSON 1.1 wire shape (Unix-epoch numeric timestamps, Content-Type application/x-amz-json-1.1); validated end-to-end against a real Azure Key Vault through the proxy with the AWS SDK.

### References

- <https://docs.aws.amazon.com/secretsmanager/latest/apireference/API_DeleteSecret.html>
- <https://learn.microsoft.com/rest/api/keyvault/secrets/delete-secret>

## DescribeSecret

- **Status:** ✅ implemented
- **Azure equivalent:** `GET https://{vault}.vault.azure.net/secrets/{name}?api-version=7.4`

### Behaviour differences

- Initial MVP uses Key Vault AAD auth and translates the core secret CRUD/read paths to AWS Secrets Manager JSON responses.
- Advanced rotation, restore, and policy semantics are not yet modeled; the proxy uses Key Vault secret versions as the AWS version surface.
- Responses use the AWS JSON 1.1 wire shape (Unix-epoch numeric timestamps, Content-Type application/x-amz-json-1.1); validated end-to-end against a real Azure Key Vault through the proxy with the AWS SDK.
- Tags are returned as an AWS Key/Value array sourced from the Key Vault secret's tags map.

### References

- <https://docs.aws.amazon.com/secretsmanager/latest/apireference/API_DescribeSecret.html>
- <https://learn.microsoft.com/rest/api/keyvault/secrets/get-secret-properties>

## GetSecretValue

- **Status:** ✅ implemented
- **Azure equivalent:** `GET https://{vault}.vault.azure.net/secrets/{name}/versions/{version?}`

### Behaviour differences

- Initial MVP uses Key Vault AAD auth and translates the core secret CRUD/read paths to AWS Secrets Manager JSON responses.
- Advanced rotation, restore, and policy semantics are not yet modeled; the proxy uses Key Vault secret versions as the AWS version surface.
- Responses use the AWS JSON 1.1 wire shape (Unix-epoch numeric timestamps, Content-Type application/x-amz-json-1.1); validated end-to-end against a real Azure Key Vault through the proxy with the AWS SDK.
- VersionStage lookup scans proxy-owned Key Vault version tags written by PutSecretValue; untagged legacy versions are treated as AWSCURRENT fallback for default reads. VersionId accepts either a raw Key Vault version id or a PutSecretValue ClientRequestToken, and VersionId+VersionStage requests are rejected when they do not refer to the same version. Full AWS rotation workflows such as RotateSecret are not implemented.

### References

- <https://docs.aws.amazon.com/secretsmanager/latest/apireference/API_GetSecretValue.html>
- <https://learn.microsoft.com/rest/api/keyvault/secrets/get-secret>
- <https://learn.microsoft.com/rest/api/keyvault/secrets/get-secret-versions>

## ListSecrets

- **Status:** ✅ implemented
- **Azure equivalent:** `GET https://{vault}.vault.azure.net/secrets?api-version=7.4`

### Behaviour differences

- Initial MVP uses Key Vault AAD auth and translates the core secret CRUD/read paths to AWS Secrets Manager JSON responses.
- Advanced rotation, restore, and policy semantics are not yet modeled; the proxy uses Key Vault secret versions as the AWS version surface.
- Responses use the AWS JSON 1.1 wire shape (Unix-epoch numeric timestamps, Content-Type application/x-amz-json-1.1); validated end-to-end against a real Azure Key Vault through the proxy with the AWS SDK.
- SecretList entries emit Tags as an AWS Key/Value array and resolve the secret Name/ARN from the Key Vault item id; a JSON-map tag shape would desync the AWS SDK list unmarshaller.
- Pagination: AWS NextToken carries the Key Vault $skiptoken continuation value (and MaxResults maps to Key Vault maxresults); the proxy always rebuilds its own vault URI from the token rather than following an inbound URL, so NextToken cannot be used as an SSRF vector.

### References

- <https://docs.aws.amazon.com/secretsmanager/latest/apireference/API_ListSecrets.html>
- <https://learn.microsoft.com/rest/api/keyvault/secrets/get-secret-properties-versions>

## PutSecretValue

- **Status:** 🟡 partial
- **Azure equivalent:** `PUT https://{vault}.vault.azure.net/secrets/{name}`

### Sub-features

| Name | Status | Notes | Gap | Workaround |
|---|---|---|---|---|
| ClientRequestToken idempotency | 🟡 partial | Implemented with proxy-owned Key Vault version tags plus an in-process same-secret lock; pending real-Azure validation. | The lock is per proxy instance, not a durable Key Vault conditional write/lease. Concurrent same-token PutSecretValue calls through different proxy instances can both create versions instead of one call returning ResourceExistsException; versions written directly in Key Vault without aws2azure metadata tags are also not detected. | Route same-secret writes through one proxy instance when strict ClientRequestToken idempotency is required, or add an external single-writer/lease before calling PutSecretValue. |
| VersionStages request labels | 🟡 partial | Persisted as proxy-owned Key Vault version tags and returned by PutSecretValue/GetSecretValue; default PutSecretValue moves AWSCURRENT to the new version and AWSPREVIOUS to the prior current version; explicit VersionStages move only the requested labels. Pending real-Azure validation. | Label transitions are implemented as multiple Key Vault version tag updates, so they are as close to atomic as Key Vault allows but are not a single backend transaction. Full AWS rotation workflow support such as RotateSecret is not implemented. | Use AWSCURRENT-only flows or validate custom labels against Key Vault before marking implemented. |

### Behaviour differences

- Initial MVP uses Key Vault AAD auth and translates PutSecretValue to a Key Vault Set Secret request that creates a new Key Vault secret version.
- The proxy first checks that the secret exists and returns ResourceNotFoundException when it does not, matching AWS PutSecretValue semantics instead of Key Vault's native upsert.
- ClientRequestToken idempotency is modeled with proxy-owned Key Vault version tags and the token is exposed as the AWS VersionId: a repeated token with the same payload replays the existing VersionId, while a repeated token with different payload returns ResourceExistsException. The proxy serializes same-secret token checks within one process, but this is proxy-local metadata and is not a durable cross-instance lock; concurrent same-token writes through different proxy instances can both create versions, and versions written directly in Key Vault without these tags are not detected.
- VersionStages supplied to PutSecretValue are persisted as proxy-owned Key Vault version tags and surfaced by PutSecretValue/GetSecretValue. When VersionStages is omitted, the proxy moves AWSCURRENT to the new Key Vault version, removes AWSCURRENT from the prior current version, assigns AWSPREVIOUS to that prior current version, and removes stale AWSPREVIOUS labels from older versions. When VersionStages is supplied explicitly, only the requested labels are moved off prior holders. These updates are separate Key Vault tag updates rather than one backend transaction, but replaying the same ClientRequestToken re-runs the transitions.
- The Key Vault PUT payload deliberately omits attributes.created so Key Vault preserves the original secret creation timestamp reported by DescribeSecret/GetSecretValue.
- Validated with scripted Key Vault REST fakes and response-writer guardrails only for ClientRequestToken/VersionStages; before promoting this operation to implemented, exercise against a real Azure Key Vault and record any divergences here.

### References

- <https://docs.aws.amazon.com/secretsmanager/latest/apireference/API_PutSecretValue.html>
- <https://learn.microsoft.com/rest/api/keyvault/secrets/set-secret>
- <https://learn.microsoft.com/rest/api/keyvault/secrets/get-secret-versions>

## RotateSecret

- **Status:** ⛔ unsupported
- **Azure equivalent:** `None — Azure Key Vault has no equivalent managed-rotation trigger the proxy can drive`

### Sub-features

| Name | Status | Notes | Gap | Workaround |
|---|---|---|---|---|
| Rotation Lambda orchestration | ⛔ unsupported | AWS RotateSecret invokes a customer-owned Lambda rotation function that generates, sets, tests, and finishes new credential versions (createSecret/setSecret/testSecret/finishSecret steps). aws2azure is a stateless wire-protocol translator: it has no Lambda runtime, no place to execute rotation logic, and no durable state to track a multi-step rotation, so it cannot honour the contract. Translating it to a single Key Vault write would silently break the caller's rotation expectations. |  |  |
| RotateImmediately / RotationRules / RotationLambdaARN | ⛔ unsupported | Not applicable without rotation orchestration; the operation is rejected before any backend call so these parameters are never interpreted. |  |  |

### Behaviour differences

- Returns HTTP 501 with an AWS `NotImplementedException` error shape and a message directing operators to rotate out-of-band and publish the new value via PutSecretValue, or to manage rotation directly in Azure Key Vault. The action is recognised by the wire-protocol router (so it surfaces in metrics) but is rejected before backend credentials are resolved — it is deliberately unsupported, not merely unimplemented.

### References

- <https://docs.aws.amazon.com/secretsmanager/latest/apireference/API_RotateSecret.html>
- <https://docs.aws.amazon.com/secretsmanager/latest/userguide/rotating-secrets.html>
- <https://learn.microsoft.com/azure/key-vault/secrets/tutorial-rotation>

## UpdateSecret

- **Status:** ✅ implemented
- **Azure equivalent:** `PUT https://{vault}.vault.azure.net/secrets/{name}/versions`

### Behaviour differences

- Initial MVP uses Key Vault AAD auth and translates the core secret CRUD/read paths to AWS Secrets Manager JSON responses.
- Advanced rotation, restore, and policy semantics are not yet modeled; the proxy uses Key Vault secret versions as the AWS version surface.
- Responses use the AWS JSON 1.1 wire shape (Unix-epoch numeric timestamps, Content-Type application/x-amz-json-1.1); validated end-to-end against a real Azure Key Vault through the proxy with the AWS SDK.
- Because Key Vault PUT is an upsert, UpdateSecret first checks existence and returns ResourceNotFoundException for a missing secret to match AWS semantics.

### References

- <https://docs.aws.amazon.com/secretsmanager/latest/apireference/API_UpdateSecret.html>
- <https://learn.microsoft.com/rest/api/keyvault/secrets/set-secret>

