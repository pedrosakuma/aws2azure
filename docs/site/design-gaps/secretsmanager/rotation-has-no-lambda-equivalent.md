# secretsmanager design gap / Rotation has no Lambda equivalent {#design-gap-secretsmanager-rotation-has-no-lambda-equivalent}

[← Design-gap index](../../design-gaps.md)

- **Capability ID:** `design-gap:secretsmanager:rotation-has-no-lambda-equivalent`
- **Status:** 🟡 partial
- **Disposition:** ⚫ non-goal

Secrets Manager rotation is driven by a customer Lambda function; Azure Key Vault has no equivalent in-line rotation-function contract, so RotateSecret cannot execute an arbitrary rotation workflow.

**Impact.** Automatic, function-driven credential rotation as configured in AWS is not reproduced end-to-end.

**Workaround.** Rotate secrets via an external process (e.g. an Azure Function / pipeline) that calls PutSecretValue through the proxy.

