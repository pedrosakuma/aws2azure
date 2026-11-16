# secretsmanager / BatchGetSecretValue {#operation-secretsmanager-batchgetsecretvalue}

[← secretsmanager operation index](../../secretsmanager.md) · [Coverage matrix](../../coverage.md)

- **Capability ID:** `operation:secretsmanager:batchgetsecretvalue`
- **Status:** 🟡 partial
- **Disposition:** 🛠️ feasible backlog
- **Tracking issue:** [#1107](https://github.com/pedrosakuma/aws2azure/issues/1107)
- **Azure equivalent:** `Bounded fan-out of GET https://{vault}.vault.azure.net/secrets/{name} and version inventory requests`

## Sub-features

### SecretIdList retrieval {#sub-feature-secretidlist-retrieval}

- **Capability ID:** `sub-feature:secretsmanager:batchgetsecretvalue:secretidlist-retrieval`
- **Status:** ✅ implemented

Accepts 1 to 20 names or ARNs and reuses the GetSecretValue name, version-stage, binary-value, and response projection path for every entry. Successful values and per-secret errors retain their relative input order. Repeated identifiers remain repeated deterministic results rather than being rejected or silently deduplicated. [conformance:Aws2Azure.UnitTests.SecretsManager.BatchGetSecretValueTests.HandleAsync_returns_mixed_values_and_errors_in_input_order] [conformance:Aws2Azure.UnitTests.SecretsManager.BatchGetSecretValueTests.HandleAsync_preserves_duplicate_secret_ids_deterministically] [conformance:Aws2Azure.UnitTests.SecretsManager.BatchGetSecretValueTests.HandleAsync_supports_single_secret_boundary]

### Bounded Key Vault fan-out {#sub-feature-bounded-key-vault-fan-out}

- **Capability ID:** `sub-feature:secretsmanager:batchgetsecretvalue:bounded-key-vault-fan-out`
- **Status:** ✅ implemented

At most four logical secret retrievals execute concurrently. Each default GetSecretValue resolution can overlap one version-list request with one speculative current-version GET, so physical Key Vault concurrency is capped at eight and caller cancellation drains the fixed worker set. The request creates no task per secret. [conformance:Aws2Azure.UnitTests.SecretsManager.BatchGetSecretValueTests.HandleAsync_bounds_physical_key_vault_fan_out] [conformance:Aws2Azure.UnitTests.SecretsManager.BatchGetSecretValueTests.HandleAsync_cancels_in_flight_key_vault_requests]

### Per-secret partial-success errors {#sub-feature-per-secret-partial-success-errors}

- **Capability ID:** `sub-feature:secretsmanager:batchgetsecretvalue:per-secret-partial-success-errors`
- **Status:** ✅ implemented

Resource, validation, conflict, and per-secret authorization failures are emitted in the AWS Errors array without discarding successful SecretValues. Shared token acquisition failures, transport failures, throttling, timeouts, and backend 5xx responses remain request-level failures so the proxy never returns a success-shaped partial response for a backend-wide outage. [conformance:Aws2Azure.UnitTests.SecretsManager.BatchGetSecretValueTests.HandleAsync_returns_mixed_values_and_errors_in_input_order] [conformance:Aws2Azure.UnitTests.SecretsManager.BatchGetSecretValueTests.HandleAsync_returns_invalid_normalized_secret_id_as_per_secret_error] [conformance:Aws2Azure.UnitTests.SecretsManager.BatchGetSecretValueTests.HandleAsync_returns_backend_wide_failures_at_request_level] [conformance:Aws2Azure.UnitTests.SecretsManager.BatchGetSecretValueTests.HandleAsync_returns_key_vault_timeout_as_request_level_error] [conformance:Aws2Azure.UnitTests.SecretsManager.BatchGetSecretValueTests.HandleAsync_returns_transport_failure_as_request_level_error] [conformance:Aws2Azure.UnitTests.SecretsManager.BatchGetSecretValueTests.HandleAsync_returns_token_failure_before_starting_key_vault_workers]

### Filters pagination {#sub-feature-filters-pagination}

- **Capability ID:** `sub-feature:secretsmanager:batchgetsecretvalue:filters-pagination`
- **Status:** ⛔ unsupported
- **Disposition:** 🛠️ feasible backlog
- **Tracking issue:** [#1107](https://github.com/pedrosakuma/aws2azure/issues/1107)

The Filters + MaxResults + NextToken form is rejected with NotImplementedException before token acquisition or a Key Vault call. It requires composing ListSecrets pagination, filter semantics, and value retrieval under a different permission surface. [conformance:Aws2Azure.UnitTests.SecretsManager.BatchGetSecretValueTests.HandleAsync_reports_filters_as_documented_not_implemented_without_backend_calls]

## Behaviour differences

- Key Vault exposes no batch value API, so aws2azure performs bounded client-side fan-out rather than one backend request. The four-logical/eight-physical ceiling is intentionally conservative for sidecar sockets, memory, Key Vault throttling, and AzureHttpClient retry budgets. [conformance:Aws2Azure.UnitTests.SecretsManager.BatchGetSecretValueTests.HandleAsync_bounds_physical_key_vault_fan_out]
- Duplicate SecretIdList entries are preserved as duplicate ordered response entries and independently retrieved within the same concurrency ceiling; AWS documents the list size but does not define duplicate rejection. [conformance:Aws2Azure.UnitTests.SecretsManager.BatchGetSecretValueTests.HandleAsync_preserves_duplicate_secret_ids_deterministically]
- Only SecretIdList is implemented. Filters, MaxResults, and NextToken remain a feasible backlog because Azure Key Vault has no equivalent batch/filter-value operation. [conformance:Aws2Azure.UnitTests.SecretsManager.BatchGetSecretValueTests.HandleAsync_reports_filters_as_documented_not_implemented_without_backend_calls]
- The implementation has deterministic unit coverage but no fresh real-Azure BatchGetSecretValue seal; the operation remains partial until separately authorized Key Vault evidence is captured.

## References

- <https://docs.aws.amazon.com/secretsmanager/latest/apireference/API_BatchGetSecretValue.html>
- <https://docs.aws.amazon.com/secretsmanager/latest/apireference/API_APIErrorType.html>
- <https://learn.microsoft.com/rest/api/keyvault/secrets/get-secret>
- <https://learn.microsoft.com/rest/api/keyvault/secrets/get-secret-versions>

