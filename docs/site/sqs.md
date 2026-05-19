# sqs

## CreateQueue

- **Status:** ✅ implemented
- **Azure equivalent:** `PUT https://{namespace}.servicebus.windows.net/{queue}?api-version=2021-05 (Atom QueueDescription)`

### Sub-features

| Name | Status | Notes | Gap | Workaround |
|---|---|---|---|---|
| Attribute.VisibilityTimeout | ✅ implemented | Maps to Service Bus LockDuration (ISO-8601 duration). |  |  |
| Attribute.MessageRetentionPeriod | ✅ implemented | Maps to DefaultMessageTimeToLive. |  |  |
| Attribute.MaximumMessageSize | ✅ implemented | Recorded as MaxMessageSizeInKilobytes; SB Standard caps at 256 KiB. |  |  |
| Attribute.DelaySeconds | 🟡 partial | Accepted on CreateQueue; honoured per-message via ScheduledEnqueueTimeUtc in Slice 2. |  |  |
| Attribute.ReceiveMessageWaitTimeSeconds | 🟡 partial | Accepted; long-polling emulation lands in Slice 4. |  |  |
| Attribute.FifoQueue / ContentBasedDeduplication | 🟡 partial | Maps to RequiresSession + RequiresDuplicateDetection; full FIFO routing lands in Slice 5. |  |  |
| Attribute.RedrivePolicy | ⛔ unsupported |  | DLQ wiring + auto-provisioning lands in Slice 5. |  |
| Attribute.KmsMasterKeyId / KmsDataKeyReusePeriodSeconds / SqsManagedSseEnabled | ⛔ unsupported |  | Service Bus encryption is namespace-level (Microsoft-managed by default; customer-managed via Key Vault out of band). |  |
| Attribute.Policy | ⛔ unsupported |  | Resource-based access policies are AWS IAM; no Service Bus equivalent on REST. |  |
| tags | ⛔ unsupported |  | Service Bus REST has no per-queue tagging surface; tracked for Slice 5 namespace-metadata workaround. |  |

### Behaviour differences

- Queue name validation enforces SQS rules (1-80 alnum/-_, '.fifo' suffix) before reaching Azure; Azure container names are stricter on some characters.
- Idempotency: an existing queue with matching attributes returns the same QueueUrl; mismatched attributes surface QueueNameExists. We compare LockDuration, TTL, MaxMessageSize, RequiresSession, RequiresDuplicateDetection — a small set vs SQS's full attribute parity.
- Slice-1 verification has only been performed against the Service Bus emulator (mcr.microsoft.com/azure-messaging/servicebus-emulator). Real-Azure validation pending before marking the op stable.

### References

- <https://docs.aws.amazon.com/AWSSimpleQueueService/latest/APIReference/API_CreateQueue.html>
- <https://learn.microsoft.com/rest/api/servicebus/create-queue>

## DeleteMessage

- **Status:** ⚪ stub
- **Azure equivalent:** `Azure Service Bus (queue) REST API`

### Behaviour differences

- AWS SQS visibility timeout maps to Service Bus peek-lock/lock duration

### References

- <https://docs.aws.amazon.com/AWSSimpleQueueService/latest/APIReference/API_DeleteMessage.html>

## DeleteQueue

- **Status:** ✅ implemented
- **Azure equivalent:** `DELETE https://{namespace}.servicebus.windows.net/{queue}?api-version=2021-05`

### Behaviour differences

- Service Bus deletes the queue synchronously; AWS SQS may take up to 60 seconds of eventual consistency before the queue stops accepting messages. Callers that delete-then-recreate within seconds may need to retry on QueueDeletedRecently — not currently surfaced by the proxy.
- Unknown-queue 404 is mapped to AWS.SimpleQueueService.NonExistentQueue (HTTP 400) per SQS spec.
- Verified against Service Bus emulator only; real-Azure validation pending.

### References

- <https://docs.aws.amazon.com/AWSSimpleQueueService/latest/APIReference/API_DeleteQueue.html>
- <https://learn.microsoft.com/rest/api/servicebus/delete-queue>

## GetQueueAttributes

- **Status:** 🟡 partial
- **Azure equivalent:** `GET https://{namespace}.servicebus.windows.net/{queue}?api-version=2021-05 (Atom QueueDescription)`

### Sub-features

| Name | Status | Notes | Gap | Workaround |
|---|---|---|---|---|
| Attribute.VisibilityTimeout | ✅ implemented | Translated from Service Bus LockDuration. |  |  |
| Attribute.MessageRetentionPeriod | ✅ implemented | Translated from DefaultMessageTimeToLive. |  |  |
| Attribute.MaximumMessageSize | ✅ implemented | Derived from MaxMessageSizeInKilobytes; defaults to 256 KiB when absent. |  |  |
| Attribute.DelaySeconds | 🟡 partial | Service Bus has no queue-level default delay; the proxy returns 0. Per-message delay lands in Slice 2. |  |  |
| Attribute.ReceiveMessageWaitTimeSeconds | 🟡 partial | Returned as 0 until long-polling lands in Slice 4. |  |  |
| Attribute.ApproximateNumberOfMessages | ✅ implemented | Mapped from Service Bus MessageCount when the property is present in the Atom response. |  |  |
| Attribute.ApproximateNumberOfMessagesNotVisible / Delayed | ⛔ unsupported |  | Service Bus exposes ActiveMessageCount / DeadLetterMessageCount via CountDetails — requires extra parsing planned for Slice 3+. |  |
| Attribute.CreatedTimestamp / LastModifiedTimestamp | ⛔ unsupported |  | Available in the Atom envelope; the proxy reads them but does not surface them as SQS attributes yet. |  |
| Attribute.QueueArn | ⛔ unsupported |  | aws2azure has no AWS account model; ARN synthesis is intentionally deferred. |  |
| Attribute.RedrivePolicy / RedriveAllowPolicy | ⛔ unsupported |  | DLQ wiring lands in Slice 5. |  |
| AttributeNames=All | ✅ implemented |  |  |  |

### Behaviour differences

- AttributeNames filtering happens proxy-side after the full Atom response is parsed.
- Verified against Service Bus emulator only; real-Azure validation pending.

### References

- <https://docs.aws.amazon.com/AWSSimpleQueueService/latest/APIReference/API_GetQueueAttributes.html>

## GetQueueUrl

- **Status:** ✅ implemented
- **Azure equivalent:** `GET https://{namespace}.servicebus.windows.net/{queue}?api-version=2021-05 (existence probe)`

### Sub-features

| Name | Status | Notes | Gap | Workaround |
|---|---|---|---|---|
| QueueOwnerAWSAccountId | ⛔ unsupported |  | aws2azure does not model AWS accounts; a placeholder 12-zero account id is always returned in the URL path. If a caller supplies a different QueueOwnerAWSAccountId, it is ignored. |  |

### Behaviour differences

- Returned QueueUrl is '{request-scheme}://{request-host}/000000000000/{queue}' so the AWS SDK keeps routing back to the same proxy endpoint the caller reached.
- Existence check uses Service Bus GET; an unknown queue returns AWS.SimpleQueueService.NonExistentQueue.
- Verified against Service Bus emulator only; real-Azure validation pending.

### References

- <https://docs.aws.amazon.com/AWSSimpleQueueService/latest/APIReference/API_GetQueueUrl.html>

## ListQueues

- **Status:** ✅ implemented
- **Azure equivalent:** `GET https://{namespace}.servicebus.windows.net/$Resources/queues?api-version=2021-05&$skip=N&$top=M`

### Sub-features

| Name | Status | Notes | Gap | Workaround |
|---|---|---|---|---|
| QueueNamePrefix | ✅ implemented | Filtered proxy-side after Service Bus returns the page; Service Bus has no native server-side prefix filter. |  |  |
| MaxResults | ✅ implemented | Honoured up to the SQS cap of 1000. Server-side pages are 100 (Service Bus management limit); the proxy concatenates pages until MaxResults or end. |  |  |
| NextToken | ✅ implemented | Opaque base-10 integer encoding the upstream $skip cursor; an end-of-feed probe avoids issuing a token when no more queues remain. |  |  |

### Behaviour differences

- Service Bus iteration is by $skip/$top; the cursor is not stable across queue deletions. AWS SQS tokens are likewise opaque, so no public contract is broken.
- Prefix filtering happens after the page is returned, so the same NextToken may visit a partially-filtered page. This is consistent with AWS-SDK pagination but may surface fewer than MaxResults entries per call.
- Verified against Service Bus emulator only; real-Azure validation pending.

### References

- <https://docs.aws.amazon.com/AWSSimpleQueueService/latest/APIReference/API_ListQueues.html>
- <https://learn.microsoft.com/rest/api/servicebus/list-queues>

## ReceiveMessage

- **Status:** ⚪ stub
- **Azure equivalent:** `Azure Service Bus (queue) REST API`

### Behaviour differences

- AWS SQS visibility timeout maps to Service Bus peek-lock/lock duration

### References

- <https://docs.aws.amazon.com/AWSSimpleQueueService/latest/APIReference/API_ReceiveMessage.html>

## SendMessage

- **Status:** ⚪ stub
- **Azure equivalent:** `Azure Service Bus (queue) REST API`

### Behaviour differences

- AWS SQS visibility timeout maps to Service Bus peek-lock/lock duration

### References

- <https://docs.aws.amazon.com/AWSSimpleQueueService/latest/APIReference/API_SendMessage.html>

