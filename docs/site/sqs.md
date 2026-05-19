# sqs

## ChangeMessageVisibility

- **Status:** 🟡 partial
- **Azure equivalent:** `Azure Service Bus queue runtime REST API — POST /{queue}/messages/{messageId}/{lockToken}?api-version=2021-05 (renew-lock)`

### Sub-features

| Name | Status | Notes | Gap | Workaround |
|---|---|---|---|---|
| ReceiptHandle round-trip | ✅ implemented |  |  |  |
| VisibilityTimeout 0..43200 validation | ✅ implemented |  |  |  |
| Arbitrary new visibility duration | ⛔ unsupported | SB extends the lock by the queue's configured LockDuration only. The proxy still issues the renew and annotates the response with Aws2Azure-VisibilityClamped: <requested-seconds>. |  |  |

### Behaviour differences

- SB renew-lock semantics do not accept a caller-supplied duration — every renew extends by the queue's LockDuration. Callers asking for a longer (or shorter) timeout get the queue-level value instead; the requested value is echoed on the Aws2Azure-VisibilityClamped response header for diagnostics.
- VisibilityTimeout=0 is *not* supported by SB REST (there is no 'unlock immediately' verb on this endpoint). The renew still extends the lock; clients needing immediate release must either DeleteMessage or wait for the lock to expire.
- Verified against in-process fakes; emulator-backed end-to-end validation lands with Slice 4's emulator fixture build-out.

### References

- <https://docs.aws.amazon.com/AWSSimpleQueueService/latest/APIReference/API_ChangeMessageVisibility.html>
- <https://learn.microsoft.com/rest/api/servicebus/renew-lock-for-a-message>

## CreateQueue

- **Status:** ✅ implemented
- **Azure equivalent:** `PUT https://{namespace}.servicebus.windows.net/{queue}?api-version=2021-05 (Atom QueueDescription)`

### Sub-features

| Name | Status | Notes | Gap | Workaround |
|---|---|---|---|---|
| Attribute.VisibilityTimeout | ✅ implemented | Maps to Service Bus LockDuration (ISO-8601 duration). |  |  |
| Attribute.MessageRetentionPeriod | ✅ implemented | Maps to DefaultMessageTimeToLive. |  |  |
| Attribute.MaximumMessageSize | ✅ implemented | Recorded as MaxMessageSizeInKilobytes (1024..1048576 bytes / 1 KiB..1 MiB). SQS raised its hard cap from 256 KiB to 1 MiB in August 2025; the proxy now mirrors that range. Backing Service Bus tier still constrains the *effective* limit: SB Standard rejects anything over 256 KiB, SB Premium honours up to 100 MiB (configurable). Per-queue MaximumMessageSize is set at create time but not re-validated per send — SB itself rejects oversized payloads on the runtime POST. |  |  |
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

- **Status:** ✅ implemented
- **Azure equivalent:** `Azure Service Bus queue runtime REST API — DELETE /{queue}/messages/{messageId}/{lockToken}?api-version=2021-05`

### Sub-features

| Name | Status | Notes | Gap | Workaround |
|---|---|---|---|---|
| ReceiptHandle round-trip | ✅ implemented | Decoded back into (messageId, lockToken) — opaque to the client. |  |  |
| Idempotent behaviour on expired lock / already-deleted message | 🟡 partial | SB 404 surfaces as SQS ReceiptHandleIsInvalid; AWS treats DeleteMessage as idempotent on already-deleted messages but errors on expired locks. The proxy currently surfaces the SB 404 verbatim — see behavior_differences. |  |  |

### Behaviour differences

- If the SB lock has expired before DeleteMessage arrives, the proxy returns ReceiptHandleIsInvalid (404). AWS SQS would return success (idempotent) for already-deleted messages and an error only for genuinely invalid handles — these two cases are indistinguishable on SB REST without an extra round-trip.
- Verified against in-process fakes; emulator-backed end-to-end validation lands with Slice 4's emulator fixture build-out.

### References

- <https://docs.aws.amazon.com/AWSSimpleQueueService/latest/APIReference/API_DeleteMessage.html>
- <https://learn.microsoft.com/rest/api/servicebus/delete-message>

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
| Attribute.MaximumMessageSize | ✅ implemented | Derived from MaxMessageSizeInKilobytes; defaults to 1 MiB (1048576 bytes) when absent — matches the current SQS default (raised from 256 KiB to 1 MiB in August 2025). |  |  |
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

- **Status:** 🟡 partial
- **Azure equivalent:** `Azure Service Bus queue runtime REST API — POST /{queue}/messages/head?timeout=0&api-version=2021-05 (peek-lock semantics)`

### Sub-features

| Name | Status | Notes | Gap | Workaround |
|---|---|---|---|---|
| Short polling (WaitTimeSeconds = 0) | ✅ implemented |  |  |  |
| Long polling (WaitTimeSeconds 1..20) | ⛔ unsupported | Returns NotImplemented; Slice 4 will emulate via short-poll loop. |  |  |
| MaxNumberOfMessages 1..10 | ✅ implemented | SB REST is single-message peek-lock; the proxy loops until count or queue empty, bounded by a 5s aggregate budget. |  |  |
| VisibilityTimeout parameter | 🟡 partial | Accepted and validated (0..43200) but ignored at SB level — see behavior_differences. |  |  |
| AttributeNames / MessageAttributeNames filters | ✅ implemented | Includes 'All' shorthand. Returned system attributes: SentTimestamp, ApproximateReceiveCount, SequenceNumber. |  |  |
| Receipt handle round-trip | ✅ implemented | Opaque base64 of MessageId\|LockToken\|SequenceNumber\|LockedUntilUtc — self-contained for DeleteMessage / ChangeMessageVisibility. |  |  |
| MessageAttributes (String/Number/Binary) round-trip | ✅ implemented | Reconstructed from Aws2Azure-AttrTypes side-channel header emitted by SendMessage. |  |  |
| MD5OfBody / MD5OfMessageAttributes | ✅ implemented |  |  |  |

### Behaviour differences

- VisibilityTimeout on ReceiveMessage cannot be set per-call — SB locks the message for the queue's configured LockDuration. The proxy validates the parameter but does not enforce it; clients needing custom per-message visibility must use ChangeMessageVisibility after receive.
- MaxNumberOfMessages > 1 is emulated by looping POST /messages/head, capped by ReceiveLoopBudget (5s) — the proxy may return fewer messages than requested even when the queue has more if the budget elapses.
- ApproximateFirstReceiveTimestamp and SenderId are not surfaced (SB does not provide them).
- Long polling is intentionally rejected in Slice 3 (NotImplemented) so callers see a deterministic error — Slice 4 will emulate via short-poll loop.
- Verified against in-process fakes; emulator-backed end-to-end validation lands with Slice 4's emulator fixture build-out.

### References

- <https://docs.aws.amazon.com/AWSSimpleQueueService/latest/APIReference/API_ReceiveMessage.html>
- <https://learn.microsoft.com/rest/api/servicebus/peek-lock-message-non-destructive-read>

## SendMessage

- **Status:** ✅ implemented
- **Azure equivalent:** `Azure Service Bus queue runtime REST API — POST /{queue}/messages?api-version=2021-05`

### Sub-features

| Name | Status | Notes | Gap | Workaround |
|---|---|---|---|---|
| MessageBody round-trip (≤1 MiB) | ✅ implemented | 1 MiB cap counts the body and message attributes together, matching SQS's August 2025 quota increase from 256 KiB to 1 MiB. |  |  |
| MessageAttributes (String/Number) | ✅ implemented | Mapped to SB application properties as strings. |  |  |
| MessageAttributes (Binary) | ✅ implemented | Base64-encoded into the side-channel header so receive can rebuild the SQS-shaped attribute. |  |  |
| MessageAttributes (custom .suffix types) | ✅ implemented |  |  |  |
| MD5OfMessageBody / MD5OfMessageAttributes in response | ✅ implemented | Computed locally to match AWS algorithm; clients use them to detect transport corruption. |  |  |
| DelaySeconds (0..900) | ✅ implemented | Translated to BrokerProperties.ScheduledEnqueueTimeUtc (UtcNow + delay). |  |  |
| MessageDeduplicationId (FIFO) | ✅ implemented | Becomes SB MessageId for SB's dedup window. SB default dedup window differs from SQS — see behavior_differences. |  |  |
| MessageGroupId (FIFO) | ✅ implemented | Becomes SB SessionId. |  |  |
| MessageSystemAttribute AWSTraceHeader | ⛔ unsupported |  |  |  |

### Behaviour differences

- MessageId is synthesised proxy-side (SB does not echo the message id on the runtime POST). For FIFO the MessageDeduplicationId is reused as the MessageId; otherwise a fresh Guid is minted.
- SQS attribute data types (String/Number/Binary/'String.Custom') are flattened to SB application-property strings. The proxy emits an Aws2Azure-AttrTypes side-channel header so the receive path can faithfully reconstruct the original SQS shape — without it, all attributes would surface as String on receive.
- SQS's per-message cap is 1 MiB (1,048,576 bytes) — raised from 256 KiB in August 2025 — and includes the body plus every message attribute's name + data type + value bytes. The proxy enforces the same 1 MiB cap. The *effective* cap is also bounded by the backing Service Bus tier: SB Standard rejects anything over 256 KiB regardless, SB Premium honours up to 100 MiB. Per-queue MaximumMessageSize (1024..1048576) is recorded at CreateQueue time but not re-validated per send — SB itself rejects oversized payloads.
- Payloads larger than 1 MiB must use the AWS Extended Client Library, which stores the body in S3 and embeds a JSON pointer in the SQS message. That pointer flows through this proxy unchanged: the receive side returns the same pointer, and the embedded S3 reference resolves against the proxy's S3 → Blob translation, so end-to-end large-message support works as long as the client uses the Extended Client and the same proxy fronts both S3 and SQS.
- ScheduledEnqueueTimeUtc has millisecond resolution in SB; SQS DelaySeconds is integer seconds, so no loss occurs.
- Verified against in-process fakes; end-to-end emulator validation deferred to Slice 3 where the receive path co-validates send. Real-Azure verification pending.

### References

- <https://docs.aws.amazon.com/AWSSimpleQueueService/latest/APIReference/API_SendMessage.html>
- <https://learn.microsoft.com/rest/api/servicebus/send-message-to-queue>

## SendMessageBatch

- **Status:** ✅ implemented
- **Azure equivalent:** `Azure Service Bus queue runtime REST API — POST /{queue}/messages with Content-Type: application/vnd.microsoft.servicebus.json`

### Sub-features

| Name | Status | Notes | Gap | Workaround |
|---|---|---|---|---|
| 1..10 entries per batch | ✅ implemented |  |  |  |
| Aggregate payload cap (≤1 MiB) | ✅ implemented | SQS counts each entry's body + message attributes (name + data type + value bytes) and rejects the batch when the sum exceeds 1 MiB (1,048,576 bytes). The proxy enforces the same rule. |  |  |
| Unique entry Id validation (1..80 alnum/-/_) | ✅ implemented |  |  |  |
| Per-entry MessageAttributes (String/Number/Binary) | ✅ implemented |  |  |  |
| Per-entry DelaySeconds → ScheduledEnqueueTimeUtc | ✅ implemented |  |  |  |
| Per-entry MessageDeduplicationId / MessageGroupId (FIFO) | ✅ implemented |  |  |  |
| Successful / Failed result partitioning | ✅ implemented | See behavior_differences — SB batch is atomic. |  |  |

### Behaviour differences

- SB's runtime batch send is atomic: the whole batch either succeeds or fails together (no AMQP-style per-message ack). The proxy preserves SQS's BatchResultErrorEntry shape: on a batch-level SB error every entry surfaces in Failed[] with the mapped SQS error code (SenderFault=true for client-side rejections, false for server-side). Genuine per-entry partial success (one bad entry mixed with good ones) is not available over SB REST.
- Same attribute-flattening + Aws2Azure-AttrTypes side-channel as SendMessage. The 1 MiB aggregate cap is computed over body + attribute name/type/value bytes per the SQS quota docs (raised from 256 KiB in August 2025).
- Payloads larger than 1 MiB require the AWS Extended Client Library (S3-backed pointer); the same caveat as SendMessage applies — the pointer flows through unchanged and resolves against the proxy's S3 → Blob translation.
- Entry MessageId returned to the caller is proxy-synthesised (Guid, or MessageDeduplicationId for FIFO).
- Verified against in-process fakes; end-to-end emulator validation deferred to Slice 3.

### References

- <https://docs.aws.amazon.com/AWSSimpleQueueService/latest/APIReference/API_SendMessageBatch.html>
- <https://learn.microsoft.com/rest/api/servicebus/send-message-batch>

