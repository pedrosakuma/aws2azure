# secretsmanager / RotateSecret {#operation-secretsmanager-rotatesecret}

[← secretsmanager operation index](../../secretsmanager.md) · [Coverage matrix](../../coverage.md)

- **Capability ID:** `operation:secretsmanager:rotatesecret`
- **Status:** ⛔ unsupported
- **Disposition:** ⚫ non-goal
- **Azure equivalent:** `None — Azure Key Vault has no equivalent managed-rotation trigger the proxy can drive`

## Sub-features

### Rotation Lambda orchestration {#sub-feature-rotation-lambda-orchestration}

- **Capability ID:** `sub-feature:secretsmanager:rotatesecret:rotation-lambda-orchestration`
- **Status:** ⛔ unsupported
- **Disposition:** ⚫ non-goal

AWS RotateSecret invokes a customer-owned Lambda rotation function that generates, sets, tests, and finishes new credential versions (createSecret/setSecret/testSecret/finishSecret steps). aws2azure is a stateless wire-protocol translator: it has no Lambda runtime, no place to execute rotation logic, and no durable state to track a multi-step rotation, so it cannot honour the contract. Translating it to a single Key Vault write would silently break the caller's rotation expectations.

### RotateImmediately / RotationRules / RotationLambdaARN {#sub-feature-rotateimmediately---rotationrules---rotationlambdaarn}

- **Capability ID:** `sub-feature:secretsmanager:rotatesecret:rotateimmediately---rotationrules---rotationlambdaarn`
- **Status:** ⛔ unsupported
- **Disposition:** ⚫ non-goal

Not applicable without rotation orchestration; the operation is rejected before any backend call so these parameters are never interpreted.

## Behaviour differences

- Returns HTTP 501 with an AWS `NotImplementedException` error shape and a message directing operators to rotate out-of-band and publish the new value via PutSecretValue, or to manage rotation directly in Azure Key Vault. The action is recognised by the wire-protocol router (so it surfaces in metrics) but is rejected before backend credentials are resolved — it is deliberately unsupported, not merely unimplemented.

## References

- <https://docs.aws.amazon.com/secretsmanager/latest/apireference/API_RotateSecret.html>
- <https://docs.aws.amazon.com/secretsmanager/latest/userguide/rotating-secrets.html>
- <https://learn.microsoft.com/azure/key-vault/secrets/tutorial-rotation>

