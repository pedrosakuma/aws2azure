# sns design gap / No IAM-backed policy surface {#design-gap-sns-no-iam-backed-policy-surface}

[← Design-gap index](../../design-gaps.md)

- **Capability ID:** `design-gap:sns:no-iam-backed-policy-surface`
- **Status:** ⛔ unsupported
- **Disposition:** 🔵 by design

DeliveryPolicy, RedrivePolicy, and SubscriptionRoleArn are accepted as no-ops because Service Bus / Event Grid expose no matching SNS attribute contract, and there is no server-side IAM evaluation.

**Impact.** Retry/redrive policy and role-based delivery configured via these attributes have no effect.

**Workaround.** Configure delivery reliability at the Azure backend level; do not rely on SNS policy attributes being enforced.

