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

MessageAttributes scope translates supported SNS operators onto Service Bus SQL filters over mirrored application properties. MessageBody scope translates supported nested JSON object paths, including flat string-array leaves, onto reserved application properties stamped during Publish / PublishBatch. Beyond the original exact/prefix/exists/anything-but/numeric-range subset, this slice now also translates: (1) suffix matching, via a companion reversed LIKE clause plus the existing array-guard fallback; (2) equals-ignore-case matching, by stamping a lower-invariant companion property at publish time (Service Bus SQL filters have no UPPER/LOWER function, so case-folding happens at publish time rather than in the SQL expression); (3) IPv4 CIDR matching, by stamping a companion 32-bit-integer property for any string attribute or body leaf that parses as a dotted-quad IPv4 address, then translating the CIDR range into a numeric >=/<= comparison (Service Bus SQL has no bitwise operators, so this only works for IPv4 -- IPv6 CIDR is rejected as structurally unsupported); (4) MessageBody array matching for flat string arrays, by extending the same publish-time stamping used for MessageAttributes String.Array values to JSON body array fields (previously such fields were silently dropped and could never match).

**Gap.** Genuinely inexpressible SNS operators remain rejected with InvalidParameter rather than silently accepted as unenforced: the nested anything-but-prefix form ({"anything-but": {"prefix": "..."}}), IPv6 CIDR matching (no 128-bit arithmetic in Service Bus SQL filters), and MessageBody arrays containing non-string elements (numbers, booleans, nested objects, or nested arrays) because there is no portable per-element numeric/boolean SQL comparison for JSON-array membership. Equals-ignore-case and IPv4 CIDR support are new in this slice and have unit-level coverage only; they are not yet verified against real Azure Service Bus (see the emulator caveat in AGENTS.md).

### Compatibility no-ops {#sub-feature-compatibility-no-ops}

- **Capability ID:** `sub-feature:sns:setsubscriptionattributes:compatibility-no-ops`
- **Status:** ✅ implemented

Treats DeliveryPolicy, RedrivePolicy, and SubscriptionRoleArn as successful no-ops because this slice does not translate those SNS attributes onto Azure primitives.

## Behaviour differences

- FilterPolicy is stored in UserMetadata and also translated onto the subscription's default Service Bus rule. MessageAttributes scope matches mirrored application properties; MessageBody scope matches scalar and flat-string-array JSON body fields projected into reserved application properties during Publish / PublishBatch.
- Suffix and equals-ignore-case matchers are translated onto companion reversed-LIKE / lower-invariant properties stamped at publish time; IPv4 CIDR matchers are translated onto a companion 32-bit-integer property and a numeric range comparison. None of these require a native Service Bus SQL function that doesn't exist (there is no UPPER/LOWER or bitwise arithmetic in Service Bus SQL filters), so all three are publish-time projections rather than SQL-native operators.
- Requests using unsupported SNS filter operators or shapes -- the nested anything-but-prefix form, IPv6 CIDR, and MessageBody arrays with non-string elements -- fail fast with InvalidParameter instead of being stored as unenforced metadata.
- DeliveryPolicy, RedrivePolicy, and SubscriptionRoleArn are accepted as no-ops because Service Bus does not expose a matching SNS attribute contract here.
- RawMessageDelivery updates only aws2azure's stored UserMetadata flag. It does not alter publish-time delivery shape on either backend.
- Updates preserve mutable SubscriptionDescription property XML, replace only UserMetadata, and send If-Match: * because Service Bus subscriptions do not expose usable per-entity ETags. Concurrent Azure-side writers are therefore last-write-wins; read-only runtime properties are never replayed.
- Updates that would push the serialized UserMetadata payload beyond Service Bus's 1024-character limit are rejected with InvalidParameter.
- Updates whose translated Service Bus SQL expression would exceed the 1024-character Service Bus rule limit are rejected with InvalidParameter.
- Unknown AWS attribute names return InvalidParameter.
- Only Azure Service Bus subscription descriptions are updated; Azure Event Grid event-subscription properties are explicitly outside this profile.
- Real Azure has been observed (see #691 and the #800 re-investigation on 2026-08-27) to reject the very first write to the reserved `$Default` subscription rule immediately after `Subscribe` in one specific conformance scenario. Historical evidence captured the proxy-surfaced SNS `AuthorizationError` with an empty Microsoft-HTTPAPI response body; the 2026-08-27 reinvestigation added proxy-side management logs and showed the raw failing backend call is `PutSubscriptionRuleAsync` against `.../subscriptions/{id}/rules/$Default?api-version=2021-05`, which returned HTTP 401 through bounded authorization retries before aws2azure translated it back to the SNS 403 `AuthorizationError`. This is distinct from the repo's separate OIDC/Entra token-acquisition 401 flake: Azure login and namespace provisioning had already succeeded, and the failure occurred on the Service Bus Topics management request itself. Root cause is still unconfirmed after investigating retry timing, interleaved warm-up reads, auth path, call ordering, and request shaping, so the affected conformance test (`SnsRealAzureConformanceTests.cs`) still skips rather than fails when this specific quirk reproduces.

## References

- <https://docs.aws.amazon.com/sns/latest/api/API_SetSubscriptionAttributes.html>
- <https://learn.microsoft.com/azure/service-bus-messaging/service-bus-resource-manager-rest>

