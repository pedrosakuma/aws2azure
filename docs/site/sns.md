# sns

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

- **Status:** ⚪ stub
- **Azure equivalent:** `Azure Service Bus Topics / Azure Event Grid`

### Sub-features

| Name | Status | Notes | Gap | Workaround |
|---|---|---|---|---|
| Slice 1 scaffold | ✅ implemented | Parses the AWS Query envelope, validates credentials, and dispatches to a structured SNS-shaped 501 stub for now. Backend translation to Service Bus Topics / Event Grid lands in later slices. |  |  |

### References

- <https://docs.aws.amazon.com/sns/latest/api/API_ListSubscriptions.html>

## ListSubscriptionsByTopic

- **Status:** ⚪ stub
- **Azure equivalent:** `Azure Service Bus Topics / Azure Event Grid`

### Sub-features

| Name | Status | Notes | Gap | Workaround |
|---|---|---|---|---|
| Slice 1 scaffold | ✅ implemented | Parses the AWS Query envelope, validates credentials, and dispatches to a structured SNS-shaped 501 stub for now. Backend translation to Service Bus Topics / Event Grid lands in later slices. |  |  |

### References

- <https://docs.aws.amazon.com/sns/latest/api/API_ListSubscriptionsByTopic.html>

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

- **Status:** ⚪ stub
- **Azure equivalent:** `Azure Service Bus Topics / Azure Event Grid`

### Sub-features

| Name | Status | Notes | Gap | Workaround |
|---|---|---|---|---|
| Slice 1 scaffold | ✅ implemented | Parses the AWS Query envelope, validates credentials, and dispatches to a structured SNS-shaped 501 stub for now. Backend translation to Service Bus Topics / Event Grid lands in later slices. |  |  |

### References

- <https://docs.aws.amazon.com/sns/latest/api/API_Subscribe.html>

## Unsubscribe

- **Status:** ⚪ stub
- **Azure equivalent:** `Azure Service Bus Topics / Azure Event Grid`

### Sub-features

| Name | Status | Notes | Gap | Workaround |
|---|---|---|---|---|
| Slice 1 scaffold | ✅ implemented | Parses the AWS Query envelope, validates credentials, and dispatches to a structured SNS-shaped 501 stub for now. Backend translation to Service Bus Topics / Event Grid lands in later slices. |  |  |

### References

- <https://docs.aws.amazon.com/sns/latest/api/API_Unsubscribe.html>

