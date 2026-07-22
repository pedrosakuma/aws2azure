# secretsmanager

## CreateSecret

- **Status:** ✅ implemented
- **Azure equivalent:** `PUT https://{vault}.vault.azure.net/secrets/{name}`
- **Real-Azure verified:** ✅ 2026-07-16 · [evidence](https://github.com/pedrosakuma/aws2azure/actions/runs/29473539261) · [workflow run](https://github.com/pedrosakuma/aws2azure/actions/runs/29473539261)

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
- **Real-Azure verified:** ✅ 2026-07-16 · [evidence](https://github.com/pedrosakuma/aws2azure/actions/runs/29473539261) · [workflow run](https://github.com/pedrosakuma/aws2azure/actions/runs/29473539261)

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
- **Real-Azure verified:** ✅ 2026-07-16 · [evidence](https://github.com/pedrosakuma/aws2azure/actions/runs/29473539261) · [workflow run](https://github.com/pedrosakuma/aws2azure/actions/runs/29473539261)

### Behaviour differences

- Initial MVP uses Key Vault AAD auth and translates the core secret CRUD/read paths to AWS Secrets Manager JSON responses.
- Advanced rotation, restore, and policy semantics are not yet modeled; the proxy uses Key Vault secret versions as the AWS version surface.
- Responses use the AWS JSON 1.1 wire shape (Unix-epoch numeric timestamps, Content-Type application/x-amz-json-1.1); validated end-to-end against a real Azure Key Vault through the proxy with the AWS SDK.
- Tags are returned as an AWS Key/Value array sourced from the Key Vault secret's tags map.
- VersionIdsToStages is built from the complete paginated Key Vault version inventory. Deterministic same-token duplicates are collapsed to one logical AWS VersionId; conflicting token payload metadata returns ResourceExistsException.

### References

- <https://docs.aws.amazon.com/secretsmanager/latest/apireference/API_DescribeSecret.html>
- <https://learn.microsoft.com/rest/api/keyvault/secrets/get-secret-properties>

## GetSecretValue

- **Status:** ✅ implemented
- **Azure equivalent:** `GET https://{vault}.vault.azure.net/secrets/{name}/versions/{version?}`
- **Real-Azure verified:** ✅ 2026-07-16 · [evidence](https://github.com/pedrosakuma/aws2azure/actions/runs/29473539261) · [workflow run](https://github.com/pedrosakuma/aws2azure/actions/runs/29473539261)

### Behaviour differences

- Initial MVP uses Key Vault AAD auth and translates the core secret CRUD/read paths to AWS Secrets Manager JSON responses.
- Advanced rotation, restore, and policy semantics are not yet modeled; the proxy uses Key Vault secret versions as the AWS version surface.
- Responses use the AWS JSON 1.1 wire shape (Unix-epoch numeric timestamps, Content-Type application/x-amz-json-1.1); validated end-to-end against a real Azure Key Vault through the proxy with the AWS SDK.
- VersionStage lookup scans proxy-owned Key Vault version tags written by PutSecretValue; untagged legacy versions are treated as AWSCURRENT fallback for default reads. VersionId accepts either a raw Key Vault version id or a PutSecretValue ClientRequestToken, and VersionId+VersionStage requests are rejected when they do not refer to the same version. Full AWS rotation workflows such as RotateSecret are not implemented.
- Version and token resolution use the same complete paginated inventory and deterministic created-time/version-id ordering as the write reconciler. If multiple physical versions still hold the requested explicit label, GetSecretValue returns ResourceExistsException instead of silently choosing an ambiguous winner.

### References

- <https://docs.aws.amazon.com/secretsmanager/latest/apireference/API_GetSecretValue.html>
- <https://learn.microsoft.com/rest/api/keyvault/secrets/get-secret>
- <https://learn.microsoft.com/rest/api/keyvault/secrets/get-secret-versions>

## ListSecrets

- **Status:** ✅ implemented
- **Azure equivalent:** `GET https://{vault}.vault.azure.net/secrets?api-version=7.4`
- **Real-Azure verified:** ✅ 2026-07-16 · [evidence](https://github.com/pedrosakuma/aws2azure/actions/runs/29473539261) · [workflow run](https://github.com/pedrosakuma/aws2azure/actions/runs/29473539261)

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
- **Real-Azure verified:** ✅ 2026-07-16 · [evidence](https://github.com/pedrosakuma/aws2azure/actions/runs/29473539261) · [workflow run](https://github.com/pedrosakuma/aws2azure/actions/runs/29473539261)

### Sub-features

| Name | Status | Real-Azure | Notes | Gap | Workaround |
|---|---|---|---|---|---|
| ClientRequestToken idempotency | 🟡 partial | — | Proxy-owned Key Vault version metadata now drives a paginated, bounded reconciliation loop. A new version is created without public stages, relisted, duplicate tokens are classified deterministically by created time plus version id, and only the deterministic winner is published. Same-payload replays converge on one AWS VersionId; different payload hashes return AWS ResourceExistsException with HTTP 400. Per-secret process locks are reference-counted and removed after the final waiter. | Key Vault does not provide a transaction spanning version creation, version inventory, and multiple version-tag patches. Two proxy instances can therefore create same-token duplicate physical versions during a race; bounded reconciliation removes labels from deterministic losers, but strict cross-instance atomicity is structurally impossible without an external coordinator. Versions written outside aws2azure without its token/hash metadata cannot participate in token replay detection. | Treat ResourceExistsException from a contended write as an explicit retry/read signal. Route a secret's writers through one instance only when the application requires stronger single-writer behavior than Key Vault can provide without another dependency. |
| VersionStages request labels | 🟡 partial | — | A shared paginated inventory is used by PutSecretValue, UpdateSecret, GetSecretValue, and DescribeSecret. Writers create an empty-stage version, relist it, remove requested labels from every loser before publishing the winner, merge stage metadata into freshly fetched tags, and verify/repair the invariant within a bounded retry budget. DescribeSecret now enumerates the logical version-stage map. | Label changes remain separate Key Vault PATCH requests. Loser-first publication prevents multiple intended AWSCURRENT holders during normal proxy writes, but a crash or independent out-of-band writer can expose a temporary zero-holder or duplicate-holder state. If the unique-label invariant is not observed after bounded repair, the proxy returns ResourceExistsException rather than claiming success. RotateSecret remains unsupported. | Retry a conflicted write or read after Key Vault propagation settles, then inspect DescribeSecret. Applications needing an indivisible cross-instance stage transaction require a coordinator outside this Key Vault-only design. |

### Behaviour differences

- Initial MVP uses Key Vault AAD auth and translates PutSecretValue to a Key Vault Set Secret request that creates a new Key Vault secret version.
- The proxy first checks that the secret exists and returns ResourceNotFoundException when it does not, matching AWS PutSecretValue semantics instead of Key Vault's native upsert.
- ClientRequestToken metadata, payload hashes, intended stages, and transition mode are stored on Key Vault versions. Physical duplicates with the same token and payload are resolved deterministically; conflicting payloads and an invariant that remains unobserved after bounded repair return ResourceExistsException with AWS's HTTP 400 JSON shape.
- New versions are created with an explicit empty stage set. After the version is visible in the complete paginated inventory, requested labels are removed from losers before the winner is published. Every PATCH starts from a fresh version GET so unrelated Key Vault tags are retained, and a bounded verification pass repairs transient visibility or partial-patch failures.
- Strict atomicity across proxy instances remains partial: Key Vault has no transaction covering create/list/multi-version tag updates, and this design intentionally adds no external coordinator or state store.
- The Key Vault PUT payload deliberately omits attributes.created so Key Vault preserves the original secret creation timestamp reported by DescribeSecret/GetSecretValue.
- Source real-Azure scenarios cover rapid cross-instance same-token writes, out-of-band Key Vault tag interference, proxy restart replay, and rollback replay. Evidence regeneration and workflow execution are owned separately; the operation remains partial because strict cross-instance atomicity is structurally unavailable in Key Vault alone.

### References

- <https://docs.aws.amazon.com/secretsmanager/latest/apireference/API_PutSecretValue.html>
- <https://learn.microsoft.com/rest/api/keyvault/secrets/set-secret>
- <https://learn.microsoft.com/rest/api/keyvault/secrets/get-secret-versions>

## RotateSecret

- **Status:** ⛔ unsupported
- **Azure equivalent:** `None — Azure Key Vault has no equivalent managed-rotation trigger the proxy can drive`

### Sub-features

| Name | Status | Real-Azure | Notes | Gap | Workaround |
|---|---|---|---|---|---|
| Rotation Lambda orchestration | ⛔ unsupported | — | AWS RotateSecret invokes a customer-owned Lambda rotation function that generates, sets, tests, and finishes new credential versions (createSecret/setSecret/testSecret/finishSecret steps). aws2azure is a stateless wire-protocol translator: it has no Lambda runtime, no place to execute rotation logic, and no durable state to track a multi-step rotation, so it cannot honour the contract. Translating it to a single Key Vault write would silently break the caller's rotation expectations. |  |  |
| RotateImmediately / RotationRules / RotationLambdaARN | ⛔ unsupported | — | Not applicable without rotation orchestration; the operation is rejected before any backend call so these parameters are never interpreted. |  |  |

### Behaviour differences

- Returns HTTP 501 with an AWS `NotImplementedException` error shape and a message directing operators to rotate out-of-band and publish the new value via PutSecretValue, or to manage rotation directly in Azure Key Vault. The action is recognised by the wire-protocol router (so it surfaces in metrics) but is rejected before backend credentials are resolved — it is deliberately unsupported, not merely unimplemented.

### References

- <https://docs.aws.amazon.com/secretsmanager/latest/apireference/API_RotateSecret.html>
- <https://docs.aws.amazon.com/secretsmanager/latest/userguide/rotating-secrets.html>
- <https://learn.microsoft.com/azure/key-vault/secrets/tutorial-rotation>

## UpdateSecret

- **Status:** ✅ implemented
- **Azure equivalent:** `PUT https://{vault}.vault.azure.net/secrets/{name}/versions`
- **Real-Azure verified:** ✅ 2026-07-16 · [evidence](https://github.com/pedrosakuma/aws2azure/actions/runs/29473539261) · [workflow run](https://github.com/pedrosakuma/aws2azure/actions/runs/29473539261)

### Sub-features

| Name | Status | Real-Azure | Notes | Gap | Workaround |
|---|---|---|---|---|---|
| Version durability and ClientRequestToken replay | 🟡 partial | — | UpdateSecret uses the same empty-stage creation, paginated inventory, deterministic token resolution, loser-first stage publication, fresh tag merge, and bounded verification path as PutSecretValue. | The Key Vault-only reconciliation cannot make version creation plus multi-version label patches atomic across proxy instances. A bounded conflict is returned when the invariant cannot be observed. | Retry ResourceExistsException after propagation settles, or use a single writer when stronger ordering is required. |

### Behaviour differences

- Initial MVP uses Key Vault AAD auth and translates the core secret CRUD/read paths to AWS Secrets Manager JSON responses.
- Advanced rotation, restore, and policy semantics are not yet modeled; the proxy uses Key Vault secret versions as the AWS version surface.
- Responses use the AWS JSON 1.1 wire shape (Unix-epoch numeric timestamps, Content-Type application/x-amz-json-1.1); validated end-to-end against a real Azure Key Vault through the proxy with the AWS SDK.
- Because Key Vault PUT is an upsert, UpdateSecret first checks existence and returns ResourceNotFoundException for a missing secret to match AWS semantics.
- ClientRequestToken is persisted and replayed through the shared version coordinator. ResourceExistsException uses the AWS HTTP 400 JSON shape.
- The operation remains partial because strict cross-instance version/stage atomicity is impossible without an external coordinator, which this Key Vault-only design intentionally avoids.

### References

- <https://docs.aws.amazon.com/secretsmanager/latest/apireference/API_UpdateSecret.html>
- <https://learn.microsoft.com/rest/api/keyvault/secrets/set-secret>

