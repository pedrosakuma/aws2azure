# secretsmanager / UpdateSecretVersionStage {#operation-secretsmanager-updatesecretversionstage}

[← secretsmanager operation index](../../secretsmanager.md) · [Coverage matrix](../../coverage.md)

- **Capability ID:** `operation:secretsmanager:updatesecretversionstage`
- **Status:** ⛔ unsupported
- **Disposition:** 🔵 by design
- **Azure equivalent:** `None — no standalone Key Vault stage-label mutation API`

## Sub-features

### Standalone stage-label mutation API {#sub-feature-standalone-stage-label-mutation-api}

- **Capability ID:** `sub-feature:secretsmanager:updatesecretversionstage:standalone-stage-label-mutation-api`
- **Status:** ⛔ unsupported
- **Disposition:** 🔵 by design

The proxy models AWS staging labels through Key Vault version tags for PutSecretValue, UpdateSecret, GetSecretValue, and DescribeSecret, but it does not expose the standalone UpdateSecretVersionStage contract. Requests are recognised by the router for metrics and rejected before any backend call.

## Behaviour differences

- Returns HTTP 501 with an AWS `NotImplementedException` error shape. The operation is recognised by the wire-protocol router and KnownOperations allowlist, but aws2azure does not implement the standalone label-mutation API.

## References

- <https://docs.aws.amazon.com/secretsmanager/latest/apireference/API_UpdateSecretVersionStage.html>

