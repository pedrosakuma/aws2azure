# sns / SetSubscriptionAttributes {#operation-sns-setsubscriptionattributes}

[← sns operation index](../../sns.md) · [Coverage matrix](../../coverage.md)

- **Capability ID:** `operation:sns:setsubscriptionattributes`
- **Status:** 🟡 partial
- **Disposition:** 🛠️ feasible backlog
- **Tracking issue:** [#800](https://github.com/pedrosakuma/aws2azure/issues/800)
- **Azure equivalent:** `Azure Service Bus subscription description`
- **Real-Azure verified:** ✅ 2026-07-22 · [evidence](https://github.com/pedrosakuma/aws2azure/actions/runs/29941293719) · [workflow run](https://github.com/pedrosakuma/aws2azure/actions/runs/29941293719)

## Sub-features

### UserMetadata attribute updates {#sub-feature-usermetadata-attribute-updates}

- **Capability ID:** `sub-feature:sns:setsubscriptionattributes:usermetadata-attribute-updates`
- **Status:** ✅ implemented

Performs a GET → merge → conditional PUT cycle against the Service Bus subscription description, persists FilterPolicy, FilterPolicyScope, and RawMessageDelivery inside UserMetadata as compact JSON, and upserts the default Service Bus subscription rule so the supported filter subset is enforced natively.

### Service Bus rule translation for supported filter policies {#sub-feature-service-bus-rule-translation-for-supported-filter-policies}

- **Capability ID:** `sub-feature:sns:setsubscriptionattributes:service-bus-rule-translation-for-supported-filter-policies`
- **Status:** 🟡 partial
- **Disposition:** 🛠️ feasible backlog
- **Tracking issue:** [#800](https://github.com/pedrosakuma/aws2azure/issues/800)

MessageAttributes scope translates supported SNS operators onto Service Bus SQL filters over mirrored application properties. MessageBody scope translates supported nested JSON object paths onto reserved application properties stamped during Publish / PublishBatch.

**Gap.** Body-array matching and unsupported SNS operators such as suffix, equals-ignore-case, CIDR, and more complex anything-but forms are rejected with InvalidParameter because this slice only translates the Service Bus SQL-filter subset it can enforce correctly.

### Compatibility no-ops {#sub-feature-compatibility-no-ops}

- **Capability ID:** `sub-feature:sns:setsubscriptionattributes:compatibility-no-ops`
- **Status:** ✅ implemented

Treats DeliveryPolicy, RedrivePolicy, and SubscriptionRoleArn as successful no-ops because this slice does not translate those SNS attributes onto Azure primitives.

## Behaviour differences

- FilterPolicy is stored in UserMetadata and also translated onto the subscription's default Service Bus rule. MessageAttributes scope matches mirrored application properties; MessageBody scope matches scalar JSON body fields projected into reserved application properties during Publish / PublishBatch.
- Requests using unsupported SNS filter operators or shapes fail fast with InvalidParameter instead of being stored as unenforced metadata.
- DeliveryPolicy, RedrivePolicy, and SubscriptionRoleArn are accepted as no-ops because Service Bus does not expose a matching SNS attribute contract here.
- Updates preserve mutable SubscriptionDescription property XML, replace only UserMetadata, and send If-Match: * because Service Bus subscriptions do not expose usable per-entity ETags. Concurrent Azure-side writers are therefore last-write-wins; read-only runtime properties are never replayed.
- Updates that would push the serialized UserMetadata payload beyond Service Bus's 1024-character limit are rejected with InvalidParameter.
- Updates whose translated Service Bus SQL expression would exceed the 1024-character Service Bus rule limit are rejected with InvalidParameter.
- Unknown AWS attribute names return InvalidParameter.
- Only Azure Service Bus subscription descriptions are updated; Azure Event Grid event-subscription properties are explicitly outside this profile.
- Real Azure has been observed (see #691) to reject the very first write to the reserved `$Default` subscription rule immediately after `Subscribe` in one specific conformance scenario, with an authorization-denied response carrying an empty body (`Server: Microsoft-HTTPAPI/2.0`, `Content-Length: 0`, an `ETag` header present). The failure is non-transient: it is immune to a bounded in-process retry, to a 60-second/6-attempt exponential backoff, and to an interleaved warm-up read on the same subscription before the write. Root cause is unconfirmed after extensive investigation (DELETE-vs-PUT sequencing, SAS-vs-AAD auth path, call ordering, propagation delay, an interleaved warm-up read, and the SAS resource-string encoding were all ruled out). The affected conformance test (`SnsRealAzureConformanceTests.cs`) skips rather than fails when this specific quirk reproduces, so it does not block CI while the gap remains open.

## References

- <https://docs.aws.amazon.com/sns/latest/api/API_SetSubscriptionAttributes.html>
- <https://learn.microsoft.com/azure/service-bus-messaging/service-bus-resource-manager-rest>

