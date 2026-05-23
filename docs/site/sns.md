# sns

## ConfirmSubscription

- **Status:** 🟡 partial
- **Azure equivalent:** `Azure Service Bus topic subscriptions`

### Sub-features

| Name | Status | Notes | Gap | Workaround |
|---|---|---|---|---|
| Auto-confirmed no-op | ✅ implemented | Subscriptions are treated as immediately confirmed when created. ConfirmSubscription returns success and a derived SubscriptionArn without mutating Azure resources. |  |  |

### Behaviour differences

- SNS confirmation tokens are not validated against an out-of-band challenge flow.
- If the token cannot be mapped back to a known subscription identifier, aws2azure returns a synthetic auto-confirmed subscription ARN for the topic.

### References

- <https://docs.aws.amazon.com/sns/latest/api/API_ConfirmSubscription.html>
- <https://learn.microsoft.com/azure/service-bus-messaging/service-bus-resource-manager-rest>

## CreateTopic

- **Status:** 🟡 partial
- **Azure equivalent:** `Azure Service Bus Topics management REST API`

### Sub-features

| Name | Status | Notes | Gap | Workaround |
|---|---|---|---|---|
| Basic topic create over Service Bus Topics REST | ✅ implemented | Maps CreateTopic(Name) to PUT https://{namespace}.servicebus.windows.net/{topic}?api-version=2021-05 with an empty TopicDescription Atom entry. 200/201 both succeed so create remains idempotent from the SNS caller's perspective. |  |  |
| Attribute translation | 🟡 partial | CreateTopic attributes are parsed by AWS clients but ignored in this slice. Topic property translation lands with SetTopicAttributes / GetTopicAttributes. |  |  |

### Behaviour differences

- TopicArn is proxy-synthesised as arn:aws:sns:{sigv4-region}:000000000000:{topicName}. The account id is a stable placeholder because the proxy is not backed by an AWS account namespace.
- Only the basic topic name is honoured on create. Topic attributes supplied in the CreateTopic request are ignored for now; Azure Service Bus topic properties stay at service defaults until a later slice implements attribute mapping.
- FIFO topic semantics are deferred. This slice only accepts the non-FIFO name subset [A-Za-z0-9_-]{1,256}; .fifo names are rejected instead of creating a FIFO-equivalent Azure entity.
- Service Bus topic names are further constrained by Azure. The proxy currently validates the AWS-side subset above and does not yet surface Azure's narrower length/character restrictions separately.

### References

- <https://docs.aws.amazon.com/sns/latest/api/API_CreateTopic.html>
- <https://learn.microsoft.com/en-us/rest/api/servicebus/create-topic>

## DeleteTopic

- **Status:** 🟡 partial
- **Azure equivalent:** `Azure Service Bus Topics management REST API`

### Sub-features

| Name | Status | Notes | Gap | Workaround |
|---|---|---|---|---|
| Idempotent topic delete over Service Bus Topics REST | ✅ implemented | Parses TopicArn, extracts the topic name, and issues DELETE https://{namespace}.servicebus.windows.net/{topic}?api-version=2021-05. Azure 404 is treated as success to preserve SNS idempotency. |  |  |

### Behaviour differences

- DeleteTopic accepts only proxy-shaped ARNs of the form arn:aws:sns:{region}:{accountId}:{topicName}. The proxy currently synthesises accountId as 000000000000, but delete only uses the topic-name suffix when translating to Azure.
- The same FIFO gap as CreateTopic applies: .fifo ARNs are rejected because FIFO semantics are deferred to a later slice.
- Azure deletes are asynchronous underneath Service Bus. A successful DeleteTopic response means the topic was accepted for deletion, not necessarily that every broker-side artifact is already gone.

### References

- <https://docs.aws.amazon.com/sns/latest/api/API_DeleteTopic.html>
- <https://learn.microsoft.com/en-us/rest/api/servicebus/delete-topic>

## GetTopicAttributes

- **Status:** ⚪ stub
- **Azure equivalent:** `Azure Service Bus Topics / Azure Event Grid`

### Sub-features

| Name | Status | Notes | Gap | Workaround |
|---|---|---|---|---|
| Slice 1 scaffold | ✅ implemented | Parses the AWS Query envelope, validates credentials, and dispatches to a structured SNS-shaped 501 stub for now. Backend translation to Service Bus Topics / Event Grid lands in later slices. |  |  |

### References

- <https://docs.aws.amazon.com/sns/latest/api/API_GetTopicAttributes.html>

## ListSubscriptions

- **Status:** 🟡 partial
- **Azure equivalent:** `Azure Service Bus topic subscriptions`

### Sub-features

| Name | Status | Notes | Gap | Workaround |
|---|---|---|---|---|
| Cross-topic subscription enumeration | ✅ implemented | Enumerates Service Bus topics first, then pages each topic's subscriptions and flattens the results into SNS member entries. |  |  |

### Behaviour differences

- NextToken is an opaque base64-encoded JSON cursor containing the current topic offset and subscription offset within that topic.
- Listing all subscriptions requires cross-topic enumeration over the Service Bus management plane and can be more expensive than native SNS ListSubscriptions.

### References

- <https://docs.aws.amazon.com/sns/latest/api/API_ListSubscriptions.html>
- <https://learn.microsoft.com/azure/service-bus-messaging/service-bus-resource-manager-rest>

## ListSubscriptionsByTopic

- **Status:** 🟡 partial
- **Azure equivalent:** `Azure Service Bus topic subscriptions`

### Sub-features

| Name | Status | Notes | Gap | Workaround |
|---|---|---|---|---|
| Per-topic subscription enumeration | ✅ implemented | Lists Azure Service Bus subscriptions for a single topic and projects stored UserMetadata back into SNS protocol/endpoint fields. |  |  |

### Behaviour differences

- NextToken is an opaque base64-encoded JSON cursor containing the subscription offset within the topic.

### References

- <https://docs.aws.amazon.com/sns/latest/api/API_ListSubscriptionsByTopic.html>
- <https://learn.microsoft.com/azure/service-bus-messaging/service-bus-resource-manager-rest>

## ListTopics

- **Status:** 🟡 partial
- **Azure equivalent:** `Azure Service Bus Topics management REST API`

### Sub-features

| Name | Status | Notes | Gap | Workaround |
|---|---|---|---|---|
| Topic enumeration over Service Bus Topics REST | ✅ implemented | Maps ListTopics to GET https://{namespace}.servicebus.windows.net/$Resources/topics?api-version=2021-05&$skip={N}&$top=100, parses the Atom feed entry titles, and emits SNS XML members with synthetic TopicArns. |  |  |

### Behaviour differences

- TopicArn values are proxy-synthesised as arn:aws:sns:{sigv4-region}:000000000000:{topicName}. The account id is a stable placeholder, not an AWS account namespace.
- NextToken is an opaque base64-encoded Service Bus skip counter, not an AWS-compatible cursor. Tokens only preserve the next $skip offset and do not encode any other AWS pagination semantics.
- Pagination is fixed to Azure's $top=100 management page size for this slice. When Azure returns exactly 100 topics the proxy emits NextToken=base64(skip+100); otherwise NextToken is omitted.
- FIFO topic distinction is deferred. Topics created out of band with .fifo suffixes are not specially modelled and the slice does not surface FIFO-only metadata.

### References

- <https://docs.aws.amazon.com/sns/latest/api/API_ListTopics.html>
- <https://learn.microsoft.com/en-us/rest/api/servicebus/list-topics>

## Publish

- **Status:** 🟡 partial
- **Azure equivalent:** `Azure Service Bus Topics`

### Sub-features

| Name | Status | Notes | Gap | Workaround |
|---|---|---|---|---|
| AMQP publish path | ✅ implemented | Sends SNS Publish requests to Azure Service Bus Topics over AMQP 1.0 using SAS or Entra ID CBS authentication. |  |  |

### Behaviour differences

- MessageId is a proxy-generated GUID, not an AWS-generated SNS identifier.
- SequenceNumber is returned empty because Azure Service Bus assigns it broker-side and the proxy does not read it after send.
- MessageStructure=json is passed through as-is; the proxy does not filter per-protocol payloads yet.
- MessageAttributes encode DataType in a parallel application property named '{Name}.DataType' so AWS-style attributes can be reconstructed by downstream consumers.
- Subject is exposed both as the AMQP subject property and as the 'aws.sns.Subject' application property.
- MessageDeduplicationId is forwarded as the 'x-opt-deduplication-id' application property rather than a broker-native send-side field.
- Azure Service Bus message size limits differ from SNS: 256 KB on Standard and up to 100 MB on Premium.

### References

- <https://docs.aws.amazon.com/sns/latest/api/API_Publish.html>
- <https://learn.microsoft.com/azure/service-bus-messaging/service-bus-amqp-protocol-guide>

## PublishBatch

- **Status:** 🟡 partial
- **Azure equivalent:** `Azure Service Bus Topics`

### Sub-features

| Name | Status | Notes | Gap | Workaround |
|---|---|---|---|---|
| AMQP batch publish path | ✅ implemented | Sends PublishBatch entries to Azure Service Bus Topics over AMQP 1.0 and reports per-entry success or failure. |  |  |

### Behaviour differences

- MessageId values are proxy-generated GUIDs, not AWS-generated SNS identifiers.
- SequenceNumber is returned empty because Azure Service Bus assigns it broker-side and the proxy does not read it after send.
- MessageStructure=json is passed through as-is; the proxy does not filter per-protocol payloads yet.
- MessageAttributes encode DataType in a parallel application property named '{Name}.DataType' so AWS-style attributes can be reconstructed by downstream consumers.
- Subject is exposed both as the AMQP subject property and as the 'aws.sns.Subject' application property.
- MessageDeduplicationId is forwarded as the 'x-opt-deduplication-id' application property rather than a broker-native send-side field.
- PublishBatch uses best-effort per-entry outcomes over AMQP; partial-failure behavior can differ from AWS SNS semantics.
- Azure Service Bus message size limits differ from SNS: 256 KB on Standard and up to 100 MB on Premium.

### References

- <https://docs.aws.amazon.com/sns/latest/api/API_PublishBatch.html>
- <https://learn.microsoft.com/azure/service-bus-messaging/service-bus-amqp-protocol-guide>

## SetTopicAttributes

- **Status:** ⚪ stub
- **Azure equivalent:** `Azure Service Bus Topics / Azure Event Grid`

### Sub-features

| Name | Status | Notes | Gap | Workaround |
|---|---|---|---|---|
| Slice 1 scaffold | ✅ implemented | Parses the AWS Query envelope, validates credentials, and dispatches to a structured SNS-shaped 501 stub for now. Backend translation to Service Bus Topics / Event Grid lands in later slices. |  |  |

### References

- <https://docs.aws.amazon.com/sns/latest/api/API_SetTopicAttributes.html>

## Subscribe

- **Status:** 🟡 partial
- **Azure equivalent:** `Azure Service Bus topic subscriptions`

### Sub-features

| Name | Status | Notes | Gap | Workaround |
|---|---|---|---|---|
| Service Bus subscription provisioning | ✅ implemented | Creates an Azure Service Bus topic subscription with deterministic subscription IDs derived from TopicArn + Protocol + Endpoint so repeat Subscribe calls return the same ARN. Supported protocols in this slice: sqs, https, http. |  |  |
| Subscription metadata projection | ✅ implemented | Stores protocol, endpoint, compact filter policy JSON, and RawMessageDelivery in SubscriptionDescription.UserMetadata. Metadata longer than 1024 chars is truncated to fit the Service Bus limit. |  |  |
| Subscriber delivery forwarder | ⛔ unsupported | This slice only manages subscription metadata. Messages accumulate in the Service Bus subscription until a later slice wires forwarding to HTTPS endpoints or SQS-backed queues. |  |  |

### Behaviour differences

- HTTPS / HTTP subscriptions are auto-confirmed immediately. SNS token-based confirmation is not implemented in this slice.
- When a deterministic subscription already exists but its stored metadata differs from the new Subscribe request, aws2azure returns the existing ARN and logs a warning instead of replacing the subscription.
- Only sqs, https, and http protocols are accepted. email, email-json, sms, lambda, application, and firehose are rejected with InvalidParameter.

### References

- <https://docs.aws.amazon.com/sns/latest/api/API_Subscribe.html>
- <https://docs.aws.amazon.com/sns/latest/dg/sns-send-message-to-sqs-cross-account.html>
- <https://learn.microsoft.com/azure/service-bus-messaging/service-bus-resource-manager-rest>

## Unsubscribe

- **Status:** 🟡 partial
- **Azure equivalent:** `Azure Service Bus topic subscriptions`

### Sub-features

| Name | Status | Notes | Gap | Workaround |
|---|---|---|---|---|
| Service Bus subscription deletion | ✅ implemented | Deletes the mapped Azure Service Bus topic subscription identified by the SNS SubscriptionArn suffix. |  |  |

### Behaviour differences

- Unsubscribe is idempotent: HTTP 200/204/404 from Service Bus all return SNS success.

### References

- <https://docs.aws.amazon.com/sns/latest/api/API_Unsubscribe.html>
- <https://learn.microsoft.com/azure/service-bus-messaging/service-bus-resource-manager-rest>

