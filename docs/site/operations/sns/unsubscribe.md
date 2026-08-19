# sns / Unsubscribe {#operation-sns-unsubscribe}

[← sns operation index](../../sns.md) · [Coverage matrix](../../coverage.md)

- **Capability ID:** `operation:sns:unsubscribe`
- **Status:** 🟡 partial
- **Disposition:** 🔵 by design
- **Azure equivalent:** `Azure Service Bus topic subscriptions`
- **Real-Azure verified:** ✅ 2026-07-16 · [evidence](https://github.com/pedrosakuma/aws2azure/actions/runs/29473539261) · [workflow run](https://github.com/pedrosakuma/aws2azure/actions/runs/29473539261)

## Sub-features

### Service Bus subscription deletion {#sub-feature-service-bus-subscription-deletion}

- **Capability ID:** `sub-feature:sns:unsubscribe:service-bus-subscription-deletion`
- **Status:** ✅ implemented

Deletes the mapped Azure Service Bus topic subscription identified by the SNS SubscriptionArn suffix.

## Behaviour differences

- Unsubscribe is idempotent: HTTP 200/204/404 from Service Bus all return SNS success.
- Only the mapped Azure Service Bus topic subscription is deleted. Azure Event Grid event subscriptions are explicitly outside this profile.

## References

- <https://docs.aws.amazon.com/sns/latest/api/API_Unsubscribe.html>
- <https://learn.microsoft.com/azure/service-bus-messaging/service-bus-resource-manager-rest>

