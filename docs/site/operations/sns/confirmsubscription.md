# sns / ConfirmSubscription {#operation-sns-confirmsubscription}

[← sns operation index](../../sns.md) · [Coverage matrix](../../coverage.md)

- **Capability ID:** `operation:sns:confirmsubscription`
- **Status:** 🟡 partial
- **Disposition:** 🔵 by design
- **Azure equivalent:** `Azure Service Bus topic subscriptions`
- **Real-Azure verified:** ✅ 2026-07-22 · [evidence](https://github.com/pedrosakuma/aws2azure/actions/runs/29941293719) · [workflow run](https://github.com/pedrosakuma/aws2azure/actions/runs/29941293719)

## Sub-features

### Auto-confirmed no-op {#sub-feature-auto-confirmed-no-op}

- **Capability ID:** `sub-feature:sns:confirmsubscription:auto-confirmed-no-op`
- **Status:** ✅ implemented

Subscriptions are treated as immediately confirmed when created. ConfirmSubscription accepts either the deterministic 20-hex subscription id or the matching synthetic SubscriptionArn, verifies the live Service Bus subscription and its persisted protocol/endpoint metadata, and returns success without mutating Azure resources.

## Behaviour differences

- SNS confirmation tokens are not validated against an out-of-band challenge flow.
- Arbitrary, cross-topic, missing, and non-deterministic subscription tokens are rejected; the operation does not synthesize a fallback identifier.
- ConfirmSubscription applies to the Service Bus subscription-management profile only and does not confirm Azure Event Grid event subscriptions.

## References

- <https://docs.aws.amazon.com/sns/latest/api/API_ConfirmSubscription.html>
- <https://learn.microsoft.com/azure/service-bus-messaging/service-bus-resource-manager-rest>

