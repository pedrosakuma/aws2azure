# sns

## ConfirmSubscription

- **Status:** 🟡 partial
- **Disposition:** 🔵 by design
- **Azure equivalent:** `Azure Service Bus topic subscriptions`
- **Real-Azure verified:** ✅ 2026-07-22 · [evidence](https://github.com/pedrosakuma/aws2azure/actions/runs/29941293719) · [workflow run](https://github.com/pedrosakuma/aws2azure/actions/runs/29941293719)

### Sub-features

| Name | Status | Disposition | Tracking | Real-Azure | Notes | Gap | Workaround |
|---|---|---|---|---|---|---|---|
| Auto-confirmed no-op | ✅ implemented | — | — | — | Subscriptions are treated as immediately confirmed when created. ConfirmSubscription accepts either the deterministic 20-hex subscription id or the matching synthetic SubscriptionArn, verifies the live Service Bus subscription and its persisted protocol/endpoint metadata, and returns success without mutating Azure resources. |  |  |

### Behaviour differences

- SNS confirmation tokens are not validated against an out-of-band challenge flow.
- Arbitrary, cross-topic, missing, and non-deterministic subscription tokens are rejected; the operation does not synthesize a fallback identifier.
- ConfirmSubscription applies to the Service Bus subscription-management profile only and does not confirm Azure Event Grid event subscriptions.

### References

- <https://docs.aws.amazon.com/sns/latest/api/API_ConfirmSubscription.html>
- <https://learn.microsoft.com/azure/service-bus-messaging/service-bus-resource-manager-rest>

## CreateTopic

- **Status:** 🟡 partial
- **Disposition:** 🛠️ feasible backlog
- **Tracking issue:** [#692](https://github.com/pedrosakuma/aws2azure/issues/692)
- **Azure equivalent:** `Azure Service Bus Topics management REST API`
- **Real-Azure verified:** ✅ 2026-07-16 · [evidence](https://github.com/pedrosakuma/aws2azure/actions/runs/29473539261) · [workflow run](https://github.com/pedrosakuma/aws2azure/actions/runs/29473539261)

### Sub-features

| Name | Status | Disposition | Tracking | Real-Azure | Notes | Gap | Workaround |
|---|---|---|---|---|---|---|---|
| Basic topic create over Service Bus Topics REST | ✅ implemented | — | — | — | Maps CreateTopic(Name) to PUT https://{namespace}.servicebus.windows.net/{topic}?api-version=2021-05 with an empty TopicDescription Atom entry. 200/201 both succeed so create remains idempotent from the SNS caller's perspective. |  |  |
| Attribute translation | ✅ implemented | — | — | — | CreateTopic persists DisplayName, Policy, DeliveryPolicy, and the FIFO-only ContentBasedDeduplication flag inside TopicDescription.UserMetadata for later GetTopicAttributes / Publish projection. |  |  |
| Service Bus-backed FIFO topic provisioning | ✅ implemented | — | — | — | Names ending in .fifo are accepted on the Service Bus backend. aws2azure enables Service Bus duplicate detection, sets DuplicateDetectionHistoryTimeWindow=PT5M to match SNS's 5-minute dedup window, and uses the stored ContentBasedDeduplication flag later during Publish / PublishBatch when MessageDeduplicationId is omitted. |  |  |

### Behaviour differences

- TopicArn is proxy-synthesised as arn:aws:sns:{sigv4-region}:000000000000:{topicName}. The account id is a stable placeholder because the proxy is not backed by an AWS account namespace.
- DisplayName, Policy, and DeliveryPolicy are stored in Service Bus TopicDescription.UserMetadata for round-tripping. Azure does not evaluate SNS IAM-style topic policies or SNS delivery retry JSON, so those attributes are metadata-only compatibility state rather than native enforcement.
- FIFO topics are recognised only when the SNS name ends in .fifo and the request explicitly sets FifoTopic=true. FifoTopic=true without a .fifo suffix, omitting FifoTopic on a .fifo name, FifoTopic=false on a .fifo name, and ContentBasedDeduplication on a non-FIFO name are rejected with InvalidParameter.
- For FIFO topics aws2azure always enables Service Bus duplicate detection because the supported subset maps SNS MessageDeduplicationId or content-based deduplication onto the broker MessageId within Service Bus's duplicate-detection window.
- ContentBasedDeduplication controls publish-time fallback when MessageDeduplicationId is omitted; it does not imply full SNS FIFO parity beyond the Service Bus-backed subset documented in Publish / PublishBatch / _design.
- FIFO CreateTopic requests are rejected when the resolved SNS backend is Event Grid because that backend cannot honor SNS FIFO ordering or deduplication semantics. Non-FIFO topics still create the backing Service Bus topic because subscription metadata continues to live there while Event Grid handles Publish / PublishBatch.
- Topic metadata is constrained by the Azure Service Bus UserMetadata 1024-character limit. Requests whose serialized DisplayName/Policy/DeliveryPolicy payload would exceed that ceiling are rejected with InvalidParameter.
- Service Bus duplicate detection remains time-windowed. aws2azure creates FIFO topics with the SNS-sized 5-minute window, but sends retried after that window expire are accepted as new messages.
- Service Bus topic names are further constrained by Azure. The proxy currently validates the AWS-side subset above and does not yet surface Azure's narrower length/character restrictions separately.

### References

- <https://docs.aws.amazon.com/sns/latest/api/API_CreateTopic.html>
- <https://learn.microsoft.com/en-us/rest/api/servicebus/create-topic>
- <https://learn.microsoft.com/en-us/azure/service-bus-messaging/duplicate-detection>

## DeleteTopic

- **Status:** 🟡 partial
- **Disposition:** 🛠️ feasible backlog
- **Tracking issue:** [#692](https://github.com/pedrosakuma/aws2azure/issues/692)
- **Azure equivalent:** `Azure Service Bus Topics management REST API`
- **Real-Azure verified:** ✅ 2026-07-16 · [evidence](https://github.com/pedrosakuma/aws2azure/actions/runs/29473539261) · [workflow run](https://github.com/pedrosakuma/aws2azure/actions/runs/29473539261)

### Sub-features

| Name | Status | Disposition | Tracking | Real-Azure | Notes | Gap | Workaround |
|---|---|---|---|---|---|---|---|
| Idempotent topic delete over Service Bus Topics REST | ✅ implemented | — | — | — | Parses TopicArn, extracts the topic name, and issues DELETE https://{namespace}.servicebus.windows.net/{topic}?api-version=2021-05. The delete is preceded by a GET probe so that a missing-entity 404 short-circuits cleanly without depending on the DELETE status code (the SB emulator returns HTTP 400 with no distinguishing body for DELETE on a missing entity; real Azure returns 404 for both). |  |  |

### Behaviour differences

- DeleteTopic accepts only proxy-shaped ARNs of the form arn:aws:sns:{region}:{accountId}:{topicName}. The proxy currently synthesises accountId as 000000000000, but delete only uses the topic-name suffix when translating to Azure.
- FIFO topics can be deleted by their .fifo ARN names once they have been provisioned on the Service Bus-backed subset described in CreateTopic / Publish / PublishBatch.
- Azure deletes are asynchronous underneath Service Bus. A successful DeleteTopic response means the topic was accepted for deletion, not necessarily that every broker-side artifact is already gone.

### References

- <https://docs.aws.amazon.com/sns/latest/api/API_DeleteTopic.html>
- <https://learn.microsoft.com/en-us/rest/api/servicebus/delete-topic>

## GetSubscriptionAttributes

- **Status:** 🟡 partial
- **Disposition:** 🔵 by design
- **Azure equivalent:** `Azure Service Bus subscription description`
- **Real-Azure verified:** ✅ 2026-07-22 · [evidence](https://github.com/pedrosakuma/aws2azure/actions/runs/29941293719) · [workflow run](https://github.com/pedrosakuma/aws2azure/actions/runs/29941293719)

### Sub-features

| Name | Status | Disposition | Tracking | Real-Azure | Notes | Gap | Workaround |
|---|---|---|---|---|---|---|---|
| Subscription metadata projection | ✅ implemented | — | — | — | Fetches the Service Bus subscription Atom entry, parses SubscriptionDescription with XmlReader, and projects aws2azure's UserMetadata JSON back into SNS protocol, endpoint, filter, and raw-delivery attributes. |  |  |

### Behaviour differences

- Protocol and Endpoint come from aws2azure's UserMetadata blob rather than native Service Bus subscription fields. Missing or invalid UserMetadata falls back to empty strings and RawMessageDelivery=false.
- ConfirmationWasAuthenticated is always true and PendingConfirmation is always false because this slice auto-confirms subscriptions.
- FilterPolicy and FilterPolicyScope are returned from aws2azure's stored UserMetadata and correspond to the Service Bus rule currently programmed for the subscription. FilterPolicyScope defaults to MessageAttributes when legacy stored metadata has no explicit scope.
- MessageBody-scope filters are enforced by projecting scalar JSON body fields into reserved Service Bus application properties during Publish / PublishBatch. Non-JSON bodies, array-valued fields, and unsupported SNS operators do not match those rules.
- DeliveryPolicy, EffectiveDeliveryPolicy, and RedrivePolicy are omitted because Service Bus delivery and dead-letter settings do not match the SNS attribute shapes exposed by this API.
- Attributes are read from Azure Service Bus subscriptions only; Azure Event Grid event-subscription properties are explicitly outside this profile.

### References

- <https://docs.aws.amazon.com/sns/latest/api/API_GetSubscriptionAttributes.html>
- <https://learn.microsoft.com/azure/service-bus-messaging/service-bus-resource-manager-rest>

## GetTopicAttributes

- **Status:** 🟡 partial
- **Disposition:** 🔵 by design
- **Azure equivalent:** `Azure Service Bus topic description`

### Sub-features

| Name | Status | Disposition | Tracking | Real-Azure | Notes | Gap | Workaround |
|---|---|---|---|---|---|---|---|
| Topic property projection | ✅ implemented | — | — | — | Fetches the Service Bus topic Atom entry, parses TopicDescription with XmlReader, maps SubscriptionCount / RequiresDuplicateDetection into the closest SNS attribute surface, and projects DisplayName / Policy / DeliveryPolicy from TopicDescription.UserMetadata. |  |  |

### Behaviour differences

- DisplayName, Policy, DeliveryPolicy, and EffectiveDeliveryPolicy come from aws2azure metadata stored in Service Bus TopicDescription.UserMetadata rather than native Service Bus topic fields.
- Policy and DeliveryPolicy remain metadata-only compatibility state. Azure Service Bus does not evaluate SNS IAM-style topic policies or SNS delivery retry policies, so GetTopicAttributes surfaces what aws2azure stored rather than a Service Bus-native enforcement model.
- SubscriptionsConfirmed is populated from Service Bus SubscriptionCount. Pending and deleted counts are always reported as 0 because aws2azure auto-confirms subscriptions and Service Bus does not expose the SNS lifecycle split.
- KmsMasterKeyId is returned empty because Service Bus encryption is configured at the namespace level, not per topic.
- FifoTopic is surfaced only for SNS topic names ending in .fifo. ContentBasedDeduplication is read from aws2azure metadata stored at create time; legacy FIFO topics without that metadata fall back to the raw Service Bus RequiresDuplicateDetection flag for backward compatibility.
- AWS-only attributes such as SignatureVersion and TracingConfig are omitted.

### References

- <https://docs.aws.amazon.com/sns/latest/api/API_GetTopicAttributes.html>
- <https://learn.microsoft.com/azure/service-bus-messaging/service-bus-resource-manager-rest>

## ListSubscriptions

- **Status:** 🟡 partial
- **Disposition:** 🔵 by design
- **Azure equivalent:** `Azure Service Bus topic subscriptions`
- **Real-Azure verified:** ✅ 2026-07-16 · [evidence](https://github.com/pedrosakuma/aws2azure/actions/runs/29473539261) · [workflow run](https://github.com/pedrosakuma/aws2azure/actions/runs/29473539261)

### Sub-features

| Name | Status | Disposition | Tracking | Real-Azure | Notes | Gap | Workaround |
|---|---|---|---|---|---|---|---|
| Cross-topic subscription enumeration | ✅ implemented | — | — | — | Enumerates Service Bus topics first, then pages each topic's subscriptions and flattens the results into SNS member entries. |  |  |

### Behaviour differences

- NextToken is a versioned, HMAC-SHA256-signed opaque cursor containing the current topic and subscription offsets. The AWS binding secret supplies the stable signing key, so tokens survive proxy restart while forged, tampered, and wrong-operation tokens are rejected.
- Listing all subscriptions requires cross-topic enumeration over the Service Bus management plane and can be more expensive than native SNS ListSubscriptions.
- Only Azure Service Bus topic subscriptions are enumerated. Azure Event Grid event subscriptions are explicitly excluded.

### References

- <https://docs.aws.amazon.com/sns/latest/api/API_ListSubscriptions.html>
- <https://learn.microsoft.com/azure/service-bus-messaging/service-bus-resource-manager-rest>

## ListSubscriptionsByTopic

- **Status:** 🟡 partial
- **Disposition:** 🔵 by design
- **Azure equivalent:** `Azure Service Bus topic subscriptions`
- **Real-Azure verified:** ✅ 2026-07-22 · [evidence](https://github.com/pedrosakuma/aws2azure/actions/runs/29941293719) · [workflow run](https://github.com/pedrosakuma/aws2azure/actions/runs/29941293719)

### Sub-features

| Name | Status | Disposition | Tracking | Real-Azure | Notes | Gap | Workaround |
|---|---|---|---|---|---|---|---|
| Per-topic subscription enumeration | ✅ implemented | — | — | — | Lists Azure Service Bus subscriptions for a single topic and projects stored UserMetadata back into SNS protocol/endpoint fields. |  |  |

### Behaviour differences

- NextToken is a versioned, HMAC-SHA256-signed opaque cursor bound to ListSubscriptionsByTopic and the exact topic name. Tokens survive restart with the same AWS binding secret; tampering, cross-operation use, and reuse for another topic are rejected.
- Only Azure Service Bus topic subscriptions are enumerated. Azure Event Grid event subscriptions are explicitly excluded.

### References

- <https://docs.aws.amazon.com/sns/latest/api/API_ListSubscriptionsByTopic.html>
- <https://learn.microsoft.com/azure/service-bus-messaging/service-bus-resource-manager-rest>

## ListTopics

- **Status:** 🟡 partial
- **Disposition:** 🛠️ feasible backlog
- **Tracking issue:** [#692](https://github.com/pedrosakuma/aws2azure/issues/692)
- **Azure equivalent:** `Azure Service Bus Topics management REST API`
- **Real-Azure verified:** ✅ 2026-07-16 · [evidence](https://github.com/pedrosakuma/aws2azure/actions/runs/29473539261) · [workflow run](https://github.com/pedrosakuma/aws2azure/actions/runs/29473539261)

### Sub-features

| Name | Status | Disposition | Tracking | Real-Azure | Notes | Gap | Workaround |
|---|---|---|---|---|---|---|---|
| Topic enumeration over Service Bus Topics REST | ✅ implemented | — | — | — | Maps ListTopics to GET https://{namespace}.servicebus.windows.net/$Resources/topics?api-version=2021-05&$skip={N}&$top=100, parses the Atom feed entry titles, and emits SNS XML members with synthetic TopicArns. |  |  |

### Behaviour differences

- TopicArn values are proxy-synthesised as arn:aws:sns:{sigv4-region}:000000000000:{topicName}. The account id is a stable placeholder, not an AWS account namespace.
- NextToken is an opaque base64-encoded Service Bus skip counter, not an AWS-compatible cursor. Tokens only preserve the next $skip offset and do not encode any other AWS pagination semantics.
- Pagination is fixed to Azure's $top=100 management page size for this slice. When Azure returns exactly 100 topics the proxy emits NextToken=base64(skip+100); otherwise NextToken is omitted.
- FIFO topics are distinguished only by their .fifo names in list output. ListTopics does not surface any additional FIFO-only attributes beyond the ARN/name itself.

### References

- <https://docs.aws.amazon.com/sns/latest/api/API_ListTopics.html>
- <https://learn.microsoft.com/en-us/rest/api/servicebus/list-topics>

## Publish

- **Status:** 🟡 partial
- **Disposition:** 🔵 by design
- **Azure equivalent:** `Azure Service Bus Topics / Azure Event Grid`
- **Real-Azure verified:** ✅ 2026-07-16 · [evidence](https://github.com/pedrosakuma/aws2azure/actions/runs/29473539261) · [workflow run](https://github.com/pedrosakuma/aws2azure/actions/runs/29473539261)

### Sub-features

| Name | Status | Disposition | Tracking | Real-Azure | Notes | Gap | Workaround |
|---|---|---|---|---|---|---|---|
| AMQP publish path | ✅ implemented | — | — | ✅ | Sends SNS Publish requests to Azure Service Bus Topics over AMQP 1.0 using SAS or Entra ID CBS authentication. |  |  |
| Event Grid publish path | ✅ implemented | — | — | ✅ | Sends SNS Publish requests to Azure Event Grid custom topics over the classic Event Grid schema using a per-topic backend switch. |  |  |
| Service Bus FIFO subset | ✅ implemented | — | — | — | For Service Bus-backed topics whose SNS names end in .fifo, Publish requires MessageGroupId, maps it to the AMQP group-id/Service Bus SessionId, maps MessageDeduplicationId to the broker MessageId, and falls back to a SHA-256-of-message-body broker MessageId when the topic was created with ContentBasedDeduplication=true. The underlying Service Bus topic must have duplicate detection enabled. |  |  |

### Behaviour differences

- MessageId is a proxy-generated GUID, not an AWS-generated SNS identifier.
- SequenceNumber is returned empty because neither Azure Service Bus nor Azure Event Grid exposes an SNS-compatible sequence number on publish.
- MessageStructure=json is passed through as-is; the proxy does not filter per-protocol payloads yet.
- On the Service Bus Topics backend, MessageAttributes encode DataType in a parallel application property named '{Name}.DataType' so AWS-style attributes can be reconstructed by downstream consumers.
- On the Event Grid backend, the proxy emits the classic Event Grid schema with eventType=aws.sns.Message; CloudEvents-formatted Event Grid topics are not supported in this slice.
- On the Event Grid backend, MessageAttributes are emitted inside data.MessageAttributes as { Type, Value } objects.
- On the Event Grid backend, the Event Grid envelope subject is always the SNS TopicArn; the AWS Subject parameter is copied into data.Subject.
- On the Event Grid backend, HTTP-level publish failures are mapped to SNS per-message failure semantics by the proxy; Publish returns an SNS error while PublishBatch marks each affected entry failed.
- Subject is exposed both as the AMQP subject property and as the 'aws.sns.Subject' application property on the Service Bus Topics backend.
- For Service Bus-backed FIFO topics, broker-side duplicate detection is limited to Service Bus's duplicate-detection window. aws2azure provisions new FIFO topics with a 5-minute window, but out-of-band topic changes or publishes outside that window are treated as new messages.
- For Service Bus-backed FIFO topics, the proxy does not synthesize or return an SNS FIFO SequenceNumber because Service Bus does not expose an SNS-compatible publish sequence identifier on send.
- For standard (non-.fifo) SNS topic names, MessageGroupId and MessageDeduplicationId are rejected with InvalidParameter instead of being silently approximated.
- FIFO topics are unsupported on the Event Grid backend. Publish rejects .fifo topics and FIFO-only request parameters there with InvalidParameter instead of dropping them.
- aws2azure sets Service Bus SessionId on published FIFO messages, but the current SNS subscription-management APIs still create regular Service Bus subscriptions. Guaranteed ordered processing therefore requires consumers to use Service Bus-native session-aware subscriptions provisioned outside the SNS compatibility APIs.
- Azure Service Bus and Event Grid message size limits differ from SNS; Event Grid classic schema also enforces 1 MB per event and 1 MB per HTTP batch.

### References

- <https://docs.aws.amazon.com/sns/latest/api/API_Publish.html>
- <https://learn.microsoft.com/azure/service-bus-messaging/service-bus-amqp-protocol-guide>
- <https://learn.microsoft.com/en-us/azure/service-bus-messaging/message-sessions>
- <https://learn.microsoft.com/en-us/azure/service-bus-messaging/duplicate-detection>
- <https://learn.microsoft.com/azure/event-grid/post-to-custom-topic>

## PublishBatch

- **Status:** 🟡 partial
- **Disposition:** 🔵 by design
- **Azure equivalent:** `Azure Service Bus Topics / Azure Event Grid`
- **Real-Azure verified:** ✅ 2026-07-16 · [evidence](https://github.com/pedrosakuma/aws2azure/actions/runs/29473539261) · [workflow run](https://github.com/pedrosakuma/aws2azure/actions/runs/29473539261)

### Sub-features

| Name | Status | Disposition | Tracking | Real-Azure | Notes | Gap | Workaround |
|---|---|---|---|---|---|---|---|
| AMQP batch publish path | ✅ implemented | — | — | ✅ | Sends PublishBatch entries to Azure Service Bus Topics over AMQP 1.0 and reports per-entry success or failure. |  |  |
| Event Grid batch publish path | ✅ implemented | — | — | ✅ | Sends PublishBatch entries to Azure Event Grid custom topics in classic-schema JSON batches, splitting oversized batches when required. |  |  |
| Service Bus FIFO batch subset | ✅ implemented | — | — | — | For Service Bus-backed .fifo topics, each entry requires MessageGroupId and uses that value as AMQP group-id/Service Bus SessionId. MessageDeduplicationId is mapped to the broker MessageId per entry, and ContentBasedDeduplication=true falls back to SHA-256(message body) when an entry omits MessageDeduplicationId. |  |  |

### Behaviour differences

- MessageId values are proxy-generated GUIDs, not AWS-generated SNS identifiers.
- SequenceNumber is returned empty because neither Azure Service Bus nor Azure Event Grid exposes an SNS-compatible sequence number on publish.
- MessageStructure=json is passed through as-is; the proxy does not filter per-protocol payloads yet.
- On the Service Bus Topics backend, MessageAttributes encode DataType in a parallel application property named '{Name}.DataType' so AWS-style attributes can be reconstructed by downstream consumers.
- On the Event Grid backend, the proxy emits the classic Event Grid schema with eventType=aws.sns.Message; CloudEvents-formatted Event Grid topics are not supported in this slice.
- On the Event Grid backend, MessageAttributes are emitted inside data.MessageAttributes as { Type, Value } objects.
- On the Event Grid backend, returned MessageId values are the proxy-generated GUIDs used as the Event Grid envelope id fields.
- On the Event Grid backend, HTTP-level failures are mapped to per-entry Failed results for every message in the affected HTTP batch, even though Event Grid itself accepts or rejects each POST atomically.
- For Service Bus-backed FIFO topics, broker-side duplicate detection is still limited to the Service Bus duplicate-detection window; aws2azure provisions new FIFO topics with a 5-minute window but cannot enforce dedup forever or outside that broker window.
- For Service Bus-backed FIFO topics, SequenceNumber remains empty even though SessionId ordering metadata is set, because Service Bus does not return an SNS-compatible batch publish sequence identifier.
- For standard (non-.fifo) SNS topic names, MessageGroupId and MessageDeduplicationId are rejected with InvalidParameter instead of being silently approximated.
- FIFO topics are unsupported on the Event Grid backend. PublishBatch rejects .fifo topics and FIFO-only entry parameters there with InvalidParameter instead of dropping them.
- aws2azure sets Service Bus SessionId on published FIFO messages, but the current SNS subscription-management APIs still create regular Service Bus subscriptions. Guaranteed ordered processing therefore requires consumers to use Service Bus-native session-aware subscriptions provisioned outside the SNS compatibility APIs.
- PublishBatch uses best-effort per-entry outcomes over AMQP and proxied per-entry outcomes over Event Grid; partial-failure behavior can differ from AWS SNS semantics.
- Azure Service Bus and Event Grid message size limits differ from SNS; Event Grid classic schema also enforces 1 MB per event, 1 MB per HTTP batch, and 5000 events per POST.

### References

- <https://docs.aws.amazon.com/sns/latest/api/API_PublishBatch.html>
- <https://learn.microsoft.com/azure/service-bus-messaging/service-bus-amqp-protocol-guide>
- <https://learn.microsoft.com/en-us/azure/service-bus-messaging/message-sessions>
- <https://learn.microsoft.com/en-us/azure/service-bus-messaging/duplicate-detection>
- <https://learn.microsoft.com/azure/event-grid/post-to-custom-topic>

## SetSubscriptionAttributes

- **Status:** 🟡 partial
- **Disposition:** 🛠️ feasible backlog
- **Tracking issue:** [#691](https://github.com/pedrosakuma/aws2azure/issues/691)
- **Azure equivalent:** `Azure Service Bus subscription description`
- **Real-Azure verified:** ✅ 2026-07-22 · [evidence](https://github.com/pedrosakuma/aws2azure/actions/runs/29941293719) · [workflow run](https://github.com/pedrosakuma/aws2azure/actions/runs/29941293719)

### Sub-features

| Name | Status | Disposition | Tracking | Real-Azure | Notes | Gap | Workaround |
|---|---|---|---|---|---|---|---|
| UserMetadata attribute updates | ✅ implemented | — | — | — | Performs a GET → merge → conditional PUT cycle against the Service Bus subscription description, persists FilterPolicy, FilterPolicyScope, and RawMessageDelivery inside UserMetadata as compact JSON, and upserts the default Service Bus subscription rule so the supported filter subset is enforced natively. |  |  |
| Service Bus rule translation for supported filter policies | 🟡 partial | 🛠️ feasible backlog | [#691](https://github.com/pedrosakuma/aws2azure/issues/691) | — | MessageAttributes scope translates supported SNS operators onto Service Bus SQL filters over mirrored application properties. MessageBody scope translates supported nested JSON object paths onto reserved application properties stamped during Publish / PublishBatch. | Body-array matching and unsupported SNS operators such as suffix, equals-ignore-case, CIDR, and more complex anything-but forms are rejected with InvalidParameter because this slice only translates the Service Bus SQL-filter subset it can enforce correctly. |  |
| Compatibility no-ops | ✅ implemented | — | — | — | Treats DeliveryPolicy, RedrivePolicy, and SubscriptionRoleArn as successful no-ops because this slice does not translate those SNS attributes onto Azure primitives. |  |  |

### Behaviour differences

- FilterPolicy is stored in UserMetadata and also translated onto the subscription's default Service Bus rule. MessageAttributes scope matches mirrored application properties; MessageBody scope matches scalar JSON body fields projected into reserved application properties during Publish / PublishBatch.
- Requests using unsupported SNS filter operators or shapes fail fast with InvalidParameter instead of being stored as unenforced metadata.
- DeliveryPolicy, RedrivePolicy, and SubscriptionRoleArn are accepted as no-ops because Service Bus does not expose a matching SNS attribute contract here.
- Updates preserve mutable SubscriptionDescription property XML, replace only UserMetadata, and send If-Match: * because Service Bus subscriptions do not expose usable per-entity ETags. Concurrent Azure-side writers are therefore last-write-wins; read-only runtime properties are never replayed.
- Updates that would push the serialized UserMetadata payload beyond Service Bus's 1024-character limit are rejected with InvalidParameter.
- Updates whose translated Service Bus SQL expression would exceed the 1024-character Service Bus rule limit are rejected with InvalidParameter.
- Unknown AWS attribute names return InvalidParameter.
- Only Azure Service Bus subscription descriptions are updated; Azure Event Grid event-subscription properties are explicitly outside this profile.
- Real Azure has been observed (see #691) to reject the very first write to the reserved `$Default` subscription rule immediately after `Subscribe` in one specific conformance scenario, with an authorization-denied response carrying an empty body (`Server: Microsoft-HTTPAPI/2.0`, `Content-Length: 0`, an `ETag` header present). The failure is non-transient: it is immune to a bounded in-process retry, to a 60-second/6-attempt exponential backoff, and to an interleaved warm-up read on the same subscription before the write. Root cause is unconfirmed after extensive investigation (DELETE-vs-PUT sequencing, SAS-vs-AAD auth path, call ordering, propagation delay, an interleaved warm-up read, and the SAS resource-string encoding were all ruled out). The affected conformance test (`SnsRealAzureConformanceTests.cs`) skips rather than fails when this specific quirk reproduces, so it does not block CI while the gap remains open.

### References

- <https://docs.aws.amazon.com/sns/latest/api/API_SetSubscriptionAttributes.html>
- <https://learn.microsoft.com/azure/service-bus-messaging/service-bus-resource-manager-rest>

## SetTopicAttributes

- **Status:** 🟡 partial
- **Disposition:** 🔵 by design
- **Azure equivalent:** `Azure Service Bus topic description`

### Sub-features

| Name | Status | Disposition | Tracking | Real-Azure | Notes | Gap | Workaround |
|---|---|---|---|---|---|---|---|
| Topic metadata-backed attribute updates | ✅ implemented | — | — | — | Performs a GET → conditional PUT cycle against the Service Bus topic description and persists DisplayName, Policy, and DeliveryPolicy inside TopicDescription.UserMetadata for later GetTopicAttributes projection. |  |  |
| Compatibility no-ops | ✅ implemented | — | — | — | Treats EffectiveDeliveryPolicy, KmsMasterKeyId, SignatureVersion, and TracingConfig as successful no-ops because this slice has no faithful Service Bus topic equivalent for those AWS attributes. |  |  |
| Content-based deduplication validation | ✅ implemented | — | — | — | Reads the current Service Bus topic description and rejects attempts to change RequiresDuplicateDetection after topic creation. Re-applying the existing value returns success. |  |  |

### Behaviour differences

- DisplayName, Policy, and DeliveryPolicy are stored in TopicDescription.UserMetadata for round-tripping. Azure Service Bus does not evaluate SNS IAM-style topic policies or SNS delivery retry JSON, so those attributes remain metadata-only compatibility state rather than native enforcement.
- ContentBasedDeduplication is backed by RequiresDuplicateDetection, but Service Bus does not allow changing that property after topic creation. aws2azure returns InvalidParameter instead of attempting an in-place update.
- EffectiveDeliveryPolicy, KmsMasterKeyId, SignatureVersion, and TracingConfig remain no-ops because the Service Bus-backed profile has no faithful equivalent AWS topic-level behavior to apply.
- Updates whose serialized topic metadata would exceed the Azure Service Bus UserMetadata 1024-character limit are rejected with InvalidParameter.
- Unknown AWS attribute names return InvalidParameter.

### References

- <https://docs.aws.amazon.com/sns/latest/api/API_SetTopicAttributes.html>
- <https://learn.microsoft.com/azure/service-bus-messaging/service-bus-resource-manager-rest>

## Subscribe

- **Status:** 🟡 partial
- **Disposition:** ⚫ non-goal
- **Azure equivalent:** `Azure Service Bus topic subscriptions`
- **Real-Azure verified:** ✅ 2026-07-16 · [evidence](https://github.com/pedrosakuma/aws2azure/actions/runs/29473539261) · [workflow run](https://github.com/pedrosakuma/aws2azure/actions/runs/29473539261)

### Sub-features

| Name | Status | Disposition | Tracking | Real-Azure | Notes | Gap | Workaround |
|---|---|---|---|---|---|---|---|
| Service Bus subscription provisioning | ✅ implemented | — | — | — | Creates an Azure Service Bus topic subscription with deterministic 20-hex subscription IDs derived from TopicArn + Protocol + Endpoint so repeat Subscribe calls return the same ARN. Supported protocols in this slice: sqs, https, http. |  |  |
| Subscription metadata projection | ✅ implemented | — | — | — | Stores protocol, endpoint, compact filter policy JSON, and RawMessageDelivery in SubscriptionDescription.UserMetadata. Requests that would exceed the 1024-character Service Bus UserMetadata limit are rejected with InvalidParameter. |  |  |
| Subscriber delivery forwarder | ⛔ unsupported | ⚫ non-goal | — | — | WON'T IMPLEMENT (out of scope by design). aws2azure provides the SNS *publish* side: Subscribe records subscription metadata and published messages land in the backing Azure Service Bus topic subscription, where any Azure-native consumer can read them. It does NOT implement the SNS *delivery* side (pushing each message out to an HTTPS/HTTP endpoint or into an SQS-backed queue). Active push delivery requires a stateful, always-on dispatcher with retry/backoff, dead-letter, and signed delivery — i.e. a callback service (Azure Function / hosted worker) that lives entirely outside this stateless request/response proxy. Use a native Azure subscriber (Service Bus consumer, or an Event Grid event subscription with its own webhook/handler) instead. |  |  |

### Behaviour differences

- HTTPS / HTTP subscriptions are auto-confirmed immediately. SNS token-based confirmation is not implemented in this slice.
- When a deterministic subscription already exists but its stored metadata differs from the new Subscribe request, aws2azure returns the existing ARN and logs a warning instead of replacing the subscription.
- Only sqs, https, and http protocols are accepted. email, email-json, sms, lambda, application, and firehose are rejected with InvalidParameter.
- Subscribe manages Azure Service Bus topic subscriptions only. It never creates or updates an Azure Event Grid event subscription; Event Grid subscription semantics are explicitly outside this profile.
- Subscribers do not receive actively-pushed deliveries: aws2azure is publish-only and never forwards messages out to HTTPS/HTTP endpoints or SQS-backed queues (see the 'Subscriber delivery forwarder' sub-feature — won't implement, out of scope). Messages are readable from the backing Service Bus subscription by a native Azure consumer. Event Grid-backed SNS topics do not fan out to the Service Bus subscriptions created here.
- The Microsoft Service Bus emulator does not persist or echo subscription UserMetadata, where this proxy stores Protocol/Endpoint/FilterPolicy/RawMessageDelivery. Emulator-backed integration tests therefore skip the subscription lifecycle assertions; correctness is validated against real Azure Service Bus.

### References

- <https://docs.aws.amazon.com/sns/latest/api/API_Subscribe.html>
- <https://docs.aws.amazon.com/sns/latest/dg/sns-send-message-to-sqs-cross-account.html>
- <https://learn.microsoft.com/azure/service-bus-messaging/service-bus-resource-manager-rest>

## Unsubscribe

- **Status:** 🟡 partial
- **Disposition:** 🔵 by design
- **Azure equivalent:** `Azure Service Bus topic subscriptions`
- **Real-Azure verified:** ✅ 2026-07-16 · [evidence](https://github.com/pedrosakuma/aws2azure/actions/runs/29473539261) · [workflow run](https://github.com/pedrosakuma/aws2azure/actions/runs/29473539261)

### Sub-features

| Name | Status | Disposition | Tracking | Real-Azure | Notes | Gap | Workaround |
|---|---|---|---|---|---|---|---|
| Service Bus subscription deletion | ✅ implemented | — | — | — | Deletes the mapped Azure Service Bus topic subscription identified by the SNS SubscriptionArn suffix. |  |  |

### Behaviour differences

- Unsubscribe is idempotent: HTTP 200/204/404 from Service Bus all return SNS success.
- Only the mapped Azure Service Bus topic subscription is deleted. Azure Event Grid event subscriptions are explicitly outside this profile.

### References

- <https://docs.aws.amazon.com/sns/latest/api/API_Unsubscribe.html>
- <https://learn.microsoft.com/azure/service-bus-messaging/service-bus-resource-manager-rest>

