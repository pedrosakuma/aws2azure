# sqs

## AddPermission

- **Status:** ⚪ stub
- **Azure equivalent:** `No native Service Bus equivalent — validates queue existence and returns success.`

### Sub-features

| Name | Status | Notes | Gap | Workaround |
|---|---|---|---|---|
| Queue existence validation | ✅ implemented | Returns NonExistentQueue if the SB queue does not exist. |  |  |
| Cross-account permission persistence | ⛔ unsupported |  | SQS resource-based access via SID/AccountId/Action does not map to SB. Authorization in SB is done via namespace-level Shared Access Signatures or AAD roles, neither of which the proxy provisions on a per-queue basis. |  |

### Behaviour differences

- The Permission payload is accepted and silently dropped; there is no AWS-style cross-account access control inside the proxy. Clients relying on AddPermission to grant access should configure SB SAS rules or Azure RBAC out of band, then map them to aws2azure access keys via the config file.
- Returns 200 OK on any well-formed payload to maximise SDK compatibility — including for Actions that SQS itself rejects on standard queues. A future revision may tighten validation once the credential model exposes scoped keys.

### References

- <https://docs.aws.amazon.com/AWSSimpleQueueService/latest/APIReference/API_AddPermission.html>

## ChangeMessageVisibility

- **Status:** 🟡 partial
- **Azure equivalent:** `Azure Service Bus queue runtime REST API — POST /{queue}/messages/{messageId}/{lockToken}?api-version=2021-05 (renew-lock); AMQP — `com.microsoft:renew-lock` over the queue's `$management` request-response link; visibility=0 maps to AMQP Abandon on the receiver link.`

### Sub-features

| Name | Status | Notes | Gap | Workaround |
|---|---|---|---|---|
| ReceiptHandle round-trip | ✅ implemented |  |  |  |
| VisibilityTimeout 0..43200 validation | ✅ implemented |  |  |  |
| VisibilityTimeout=0 (immediate release) | ✅ implemented | AMQP transport: dispatched to ServiceBusReceiver.AbandonAsync (matches SQS semantics — message becomes immediately available again, redelivery counter bumped). REST transport: unsupported; renew still extends the lock by LockDuration. |  |  |
| Arbitrary new visibility duration | ⛔ unsupported | SB extends the lock by the queue's configured LockDuration only (max 5 min). The proxy issues the renew and, when the granted seconds differ from the requested value, annotates the response with Aws2Azure-VisibilityClamped: requested=<N>;granted=<M>. |  |  |

### Behaviour differences

- SB renew-lock semantics do not accept a caller-supplied duration — every renew extends by the queue's LockDuration. When the requested timeout differs from what SB grants the proxy emits the Aws2Azure-VisibilityClamped: requested=<N>;granted=<M> diagnostic header. (The header is suppressed when they agree — typical for queues whose LockDuration equals the SDK default 30 s called with VisibilityTimeout=30.)
- VisibilityTimeout=0 is supported on the AMQP transport via Abandon (immediate release, redelivery counter incremented). It is NOT supported on the REST transport — the renew still extends the lock; clients needing immediate release on REST must DeleteMessage or wait for the lock to expire.
- Verified against in-process fakes; emulator-backed end-to-end validation lands with the Service Bus emulator fixture work.
- Header format: granted-seconds is derived from `lockedUntil - DateTimeOffset.UtcNow` (rounded to whole seconds). Clock skew between the proxy host and Service Bus can shift the value by 1-2 s; consumers should treat it as a diagnostic hint, not an SLA.
- Emulator divergence: the Service Bus emulator's $management node detaches the request/response link on the first com.microsoft:renew-lock request (visible to the proxy as 'channel has been closed'). Validated against real Azure only; the integration test against the emulator is skipped with a SkipException pointing to the real-Azure smoke.

### References

- <https://docs.aws.amazon.com/AWSSimpleQueueService/latest/APIReference/API_ChangeMessageVisibility.html>
- <https://learn.microsoft.com/rest/api/servicebus/renew-lock-for-a-message>
- <https://learn.microsoft.com/azure/service-bus-messaging/service-bus-amqp-protocol-guide>

## ChangeMessageVisibilityBatch

- **Status:** 🟡 partial
- **Azure equivalent:** `Azure Service Bus queue runtime REST API — N parallel POST /{queue}/messages/{messageId}/{lockToken}?action=renewlock&api-version=2021-05`

### Sub-features

| Name | Status | Notes | Gap | Workaround |
|---|---|---|---|---|
| 1..10 entries per batch | ✅ implemented |  |  |  |
| Per-entry Id validation (alnum/_/-, 1..80 chars, unique) | ✅ implemented |  |  |  |
| Per-entry VisibilityTimeout 0..43200 validation | ✅ implemented | Non-integer / out-of-range entries fail with SenderFault InvalidParameterValue while siblings succeed. |  |  |
| Bounded parallelism | ✅ implemented | 5-way concurrency cap; lock-renew calls are individually short. |  |  |
| Renew semantics | 🟡 partial | SB renewlock extends the lock by the queue's configured LockDuration, ignoring the requested VisibilityTimeout — see behavior_differences. |  |  |

### Behaviour differences

- SB has no per-call visibility override on REST. The proxy validates and accepts the VisibilityTimeout value but SB always extends by the queue's LockDuration. Callers needing an arbitrary new visibility must Delete+Send or rely on SetQueueAttributes to change the queue-wide LockDuration.
- VisibilityTimeout of 0 (which AWS uses to make a message immediately re-visible) is currently treated as a renewlock too — the message is not made re-visible. This divergence is tracked for Phase-2 NFR follow-up.
- Verified against in-process fakes; emulator-backed end-to-end validation lands with the SbEmulatorFixture build-out (tracked in p2-sb-emulator-fixture).
- AMQP transport (Phase 2.5): when a queue is configured with `transport: Amqp`, ChangeMessageVisibilityBatch routes to the AMQP path — each entry with VisibilityTimeout=0 abandons via the cached (session) receiver, restoring the SQS 'immediately re-visible' semantics on this path (closing the divergence above for AMQP queues). Positive VisibilityTimeout values RenewLock via the SB `$management` link (session-aware for v3 receipt handles); SB clamping is silent in the batch shape (the singular CMV emits the `Aws2Azure-VisibilityClamped` header but the batch response has no per-entry place to carry it).

### References

- <https://docs.aws.amazon.com/AWSSimpleQueueService/latest/APIReference/API_ChangeMessageVisibilityBatch.html>
- <https://learn.microsoft.com/rest/api/servicebus/renew-lock>

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
| Attribute.FifoQueue / ContentBasedDeduplication | 🟡 partial | Maps to RequiresSession + RequiresDuplicateDetection. FIFO routing is implemented end-to-end on the AMQP transport (MessageGroupId -> SB SessionId on send; session-aware receive pins one consumer per group for strict per-group ordering — see ReceiveMessage). Strict ordering requires `transport: Amqp`; the REST transport cannot express session-receive and therefore does not provide strict per-group ordering (won't implement — inherent SB REST limitation). |  |  |
| Attribute.RedrivePolicy | ✅ implemented | Slice 5: SQS RedrivePolicy JSON ({deadLetterTargetArn,maxReceiveCount}) is parsed and mapped to SB ForwardDeadLetteredMessagesTo (queue-name segment of the ARN) + MaxDeliveryCount. maxReceiveCount is bounded to 1..1000 per SQS. The target DLQ must already exist (auto-provisioning is intentionally not implemented; client owns DLQ lifecycle). |  |  |
| Attribute.RedriveAllowPolicy | ⛔ unsupported |  | Accepted silently; SB has no per-queue ACL controlling which sources may forward into a DLQ. |  |
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
- AMQP transport (Phase 2.5 slice 8b.4c): when the queue is configured with `transport: Amqp`, DeleteMessage settles the in-flight delivery via the AMQP `accepted` outcome against the cached receiver (via the lock-token in-flight cache landed in slice 8b.4b). Cache miss — already settled, lock expired, sender-settled at receive, or delivery came from a torn-down receiver instance — surfaces as ReceiptHandleIsInvalid, identical to the REST path's 404→ReceiptHandleIsInvalid mapping. Receipt handles minted by the AMQP path (version `2`) are rejected if presented to a REST-configured queue and vice versa — a queue's transport setting is part of the handle's contract.

### References

- <https://docs.aws.amazon.com/AWSSimpleQueueService/latest/APIReference/API_DeleteMessage.html>
- <https://learn.microsoft.com/rest/api/servicebus/delete-message>

## DeleteMessageBatch

- **Status:** ✅ implemented
- **Azure equivalent:** `Azure Service Bus queue runtime REST API — N parallel DELETE /{queue}/messages/{messageId}/{lockToken}?api-version=2021-05`

### Sub-features

| Name | Status | Notes | Gap | Workaround |
|---|---|---|---|---|
| 1..10 entries per batch | ✅ implemented | Matches SQS limit; enforced before any SB call. |  |  |
| Per-entry Id validation (alnum/_/-, 1..80 chars, unique) | ✅ implemented | Returns the AWS-shaped EmptyBatchRequest / TooManyEntriesInBatchRequest / BatchEntryIdsNotDistinct / InvalidBatchEntryId on the whole call. |  |  |
| Partial failure response shape | ✅ implemented | Per-entry Successful / Failed rows preserve the caller's Id ordering and carry SenderFault=true on rejects. |  |  |
| Bounded parallelism | ✅ implemented | SemaphoreSlim cap of 5 concurrent SB DELETEs per batch to avoid throttling small SB Standard namespaces. |  |  |
| ReceiptHandle round-trip | ✅ implemented | Same length-prefixed base64 ReceiptHandle as DeleteMessage; decoded per-entry. |  |  |

### Behaviour differences

- SB REST has no native batch-delete; the proxy fans out parallel DELETEs. A failing entry never aborts the batch — callers see per-entry results matching SQS semantics.
- Expired-lock vs already-deleted ambiguity from DeleteMessage applies per entry (see DeleteMessage.yaml behavior_differences).
- Verified against in-process fakes; emulator-backed end-to-end validation lands with the SbEmulatorFixture build-out (tracked in p2-sb-emulator-fixture).
- AMQP transport (Phase 2.5): when a queue is configured with `transport: Amqp`, DeleteMessageBatch routes to the AMQP path — each entry decodes the v2/v3 AMQP receipt handle minted by AMQP ReceiveMessage, looks up the cached (session) receiver via the lock-token cache, and calls `ServiceBusReceiver.CompleteAsync`. FIFO-aware: entries with different session-ids fan out to their own cached session receivers in parallel. Per-entry failures (stale handle, queue mismatch, cache miss, transport error) are surfaced as BatchResultErrorEntry items just like the REST path.

### References

- <https://docs.aws.amazon.com/AWSSimpleQueueService/latest/APIReference/API_DeleteMessageBatch.html>
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
| Attribute.RedrivePolicy | ✅ implemented | Slice 5: emitted as JSON {deadLetterTargetArn, maxReceiveCount} when the SB queue has ForwardDeadLetteredMessagesTo set. The ARN is synthetic (arn:aws-azure:sqs:azure-sb::{queue}) because the proxy has no AWS account/region model, but its shape parses cleanly with boto3 / AWSSDK clients. |  |  |
| Attribute.RedriveAllowPolicy | ⛔ unsupported |  | SB has no per-queue ACL controlling which sources may forward into a DLQ. |  |
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

## ListDeadLetterSourceQueues

- **Status:** ✅ implemented
- **Azure equivalent:** `Page through SB management GET /$Resources/queues?api-version=2021-05 and filter entries whose ForwardDeadLetteredMessagesTo equals the requested queue.`

### Sub-features

| Name | Status | Notes | Gap | Workaround |
|---|---|---|---|---|
| Queue existence probe | ✅ implemented | SQS returns NonExistentQueue when the DLQ target itself is unknown; the proxy issues a GET /{queue} before paging. |  |  |
| Page-walk + filter | ✅ implemented | SB management API caps a page at 100 entries; the proxy walks pages until a short page is observed, filtering each entry by ForwardDeadLetteredMessagesTo == target. |  |  |
| MaxResults / NextToken pagination | ✅ implemented | MaxResults defaults to 1000 (SQS hard cap); NextToken is an opaque integer cursor into the SB queue listing (the proxy emits it only when there is at least one more SB queue past the consumed cursor). A request that hits MaxResults receives a NextToken that the caller can pass back to continue. |  |  |

### Behaviour differences

- Linear scan: the proxy issues one or more SB management GETs per ListDeadLetterSourceQueues call. On namespaces with thousands of queues this is O(N) and may be slow; the NFR phase should consider a cached reverse index.
- Verified against in-process fakes; emulator-backed validation is blocked (SB emulator does not expose management REST). Real-Azure verification pending.
- Verified against in-process fakes; emulator-backed validation is blocked (SB emulator does not expose management REST). Real-Azure verification pending.

### References

- <https://docs.aws.amazon.com/AWSSimpleQueueService/latest/APIReference/API_ListDeadLetterSourceQueues.html>
- <https://learn.microsoft.com/rest/api/servicebus/list-queues>

## ListQueueTags

- **Status:** 🟡 partial
- **Azure equivalent:** `GET QueueDescription and decode aws2azure's base64 tag blob from UserMetadata.`

### Sub-features

| Name | Status | Notes | Gap | Workaround |
|---|---|---|---|---|
| Queue existence validation | ✅ implemented | Returns NonExistentQueue if the SB queue does not exist. |  |  |
| Tags round-trip | ✅ implemented | Decodes the SQS tag map persisted by TagQueue/UntagQueue in QueueDescription.UserMetadata. |  |  |
| Empty / foreign metadata handling | ✅ implemented | Missing, empty, non-base64, or non-aws2azure UserMetadata is treated as an empty SQS tag map. |  |  |

### Behaviour differences

- Tags are stored as an aws2azure-owned compact binary tag map, base64-encoded into Service Bus QueueDescription.UserMetadata. Azure-side tools will see the opaque base64 blob rather than individual tag keys.
- Service Bus UserMetadata is limited to roughly 1024 characters in the legacy management schema, so TagQueue may reject otherwise-valid SQS tag sets that cannot fit.
- Verified with in-process Service Bus management fakes. The Service Bus emulator does not validate or persist this management-plane UserMetadata path; real-Azure validation remains pending.

### References

- <https://docs.aws.amazon.com/AWSSimpleQueueService/latest/APIReference/API_ListQueueTags.html>
- <https://learn.microsoft.com/azure/service-bus-messaging/service-bus-xml-management-api>

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

## PurgeQueue

- **Status:** 🟡 partial
- **Azure equivalent:** `Azure Service Bus queue runtime REST API — emulated via drain-loop of POST /{queue}/messages/head + DELETE /{queue}/messages/{id}/{lockToken}`

### Sub-features

| Name | Status | Notes | Gap | Workaround |
|---|---|---|---|---|
| Drain-loop receive+delete | ✅ implemented | Peek-locks messages in bursts and DELETEs them; bounded by a 60s wall-clock budget per call. |  |  |
| 60s cool-down (PurgeQueueInProgress) | 🟡 partial | Enforced via an in-process ConcurrentDictionary keyed by namespace+queue — see behavior_differences. |  |  |
| Idempotency on empty queue | ✅ implemented | Returns 200 with empty body, like SQS. |  |  |

### Behaviour differences

- SB has no native purge. The proxy emulates it by draining peek-locked messages and deleting them. With a long LockDuration the drain may not be able to keep up if producers are sending faster than the proxy can delete — the 60s budget bounds wall-clock cost; the queue is therefore best-effort empty rather than guaranteed empty at the end of the call. The SQS contract guarantees a 60-second 'all messages enqueued at the time of the call will be deleted' window, which we approximate.
- The 60-second cool-down (PurgeQueueInProgress) is in-process only. Other replicas of the proxy will not observe the cool-down — a horizontally scaled deployment could allow multiple concurrent drains. Tracked for the NFR phase (shared coordination cache).
- Verified against in-process fakes; emulator-backed end-to-end validation lands with the SbEmulatorFixture build-out (tracked in p2-sb-emulator-fixture).

### References

- <https://docs.aws.amazon.com/AWSSimpleQueueService/latest/APIReference/API_PurgeQueue.html>
- <https://learn.microsoft.com/rest/api/servicebus/peek-lock-message-non-destructive-read>
- <https://learn.microsoft.com/rest/api/servicebus/delete-message>

## ReceiveMessage

- **Status:** ✅ implemented
- **Azure equivalent:** `Azure Service Bus queue runtime REST API — POST /{queue}/messages/head?timeout={waitSeconds}&api-version=2021-05 (peek-lock semantics)`

### Sub-features

| Name | Status | Notes | Gap | Workaround |
|---|---|---|---|---|
| Short polling (WaitTimeSeconds = 0) | ✅ implemented |  |  |  |
| Long polling (WaitTimeSeconds 1..20) | ✅ implemented | Uses SB's native server-side wait on the first peek-lock call (timeout query parameter); subsequent calls inside the same batch fall back to timeout=0 to drain quickly, matching the SQS 'return as soon as one message is available or WaitTimeSeconds elapses' contract. |  |  |
| MaxNumberOfMessages 1..10 | ✅ implemented | SB REST is single-message peek-lock; the proxy loops until count or queue empty. The first call blocks up to WaitTimeSeconds (long-poll); follow-up calls share a 5s aggregate budget added on top of WaitTimeSeconds. |  |  |
| VisibilityTimeout parameter | 🟡 partial | Accepted and validated (0..43200) but ignored at SB level — see behavior_differences. |  |  |
| AttributeNames / MessageAttributeNames filters | ✅ implemented | Includes 'All' shorthand. Returned system attributes: SentTimestamp, ApproximateReceiveCount, SequenceNumber, MessageGroupId (FIFO, from BrokerProperties.SessionId), MessageDeduplicationId (FIFO, from BrokerProperties.MessageId), DeadLetterQueueSourceArn (AMQP path only, when the message came from a /$DeadLetterQueue subqueue), and the proxy-prefixed Aws2Azure-DeadLetterReason / Aws2Azure-DeadLetterErrorDescription (AMQP path only, read from the dead-lettered message's application-properties). |  |  |
| Receipt handle round-trip | ✅ implemented | Opaque length-prefixed base64 of (MessageId, LockToken, SequenceNumber, LockedUntilUtc) — self-contained for DeleteMessage / ChangeMessageVisibility, safe against caller-controlled metacharacters in MessageDeduplicationId. |  |  |
| MessageAttributes (String/Number/Binary) round-trip | ✅ implemented | Reconstructed from Aws2Azure-AttrTypes side-channel header emitted by SendMessage. |  |  |
| MD5OfBody / MD5OfMessageAttributes | ✅ implemented |  |  |  |

### Behaviour differences

- VisibilityTimeout on ReceiveMessage cannot be set per-call — SB locks the message for the queue's configured LockDuration. The proxy validates the parameter but does not enforce it; clients needing custom per-message visibility must use ChangeMessageVisibility after receive.
- MaxNumberOfMessages > 1 is emulated by looping POST /messages/head, capped by ReceiveLoopBudget (5s) added on top of WaitTimeSeconds — the proxy may return fewer messages than requested even when the queue has more if the budget elapses.
- ApproximateFirstReceiveTimestamp and SenderId are not surfaced (SB does not provide them).
- Long polling uses SB's native server-side wait; if the first call returns empty after the wait the receive returns immediately with an empty list (no second wait). This matches SQS semantics.
- FIFO ordering — AMQP vs REST: SQS FIFO guarantees strict per-MessageGroupId order on receive (one in-flight message per group at a time, others stay invisible until the in-flight one is deleted). This is IMPLEMENTED on the AMQP transport (`transport: Amqp`): the receive path acquires a broker-assigned session receiver (AcceptNextSession) and holds the session lock, so a group's in-flight messages stay pinned to one consumer and strict order is preserved. The session-id is carried in the v3 receipt handle so DeleteMessage / ChangeMessageVisibility route the settle back to the same live session link. The REST transport cannot express session-receive and therefore does NOT provide strict per-group ordering — it surfaces MessageGroupId on each message (so application-side de-grouping still works) but does not block concurrent delivery of the same group to different consumers (won't implement — inherent SB REST limitation). FIFO settle is connection-affine: an in-flight FIFO message cannot be settled from a different live connection while its session lock is held (SB session locks are connection-bound and non-serializable); on lock expiry the broker releases the session and the group becomes redeliverable, which matches SQS visibility-timeout semantics and enables scale-up rebalance. Proactive idle-TTL eviction of pooled session receivers is implemented (#262): a background sweeper closes session-receiver links with no receive/settle activity within a configurable idle window (default 5 min, AWS2AZURE_SB_SESSION_IDLE_SECONDS), returning the broker session for another consumer and freeing the AMQP link — resource hygiene, not a correctness gap.
- MessageGroupId is surfaced from BrokerProperties.SessionId; MessageDeduplicationId from BrokerProperties.MessageId. Both only appear in the response when the AttributeNames filter requests them (or 'All').
- Verified against in-process fakes; emulator-backed end-to-end validation is blocked (SB emulator does not expose runtime REST). Real-Azure validation pending — see ADR-0001.
- AMQP transport (Phase 2.5 slice 8b.4c, MessageAttributes round-trip in Phase 6 / #99): when a queue is configured with `transport: Amqp` (see ServiceBusCredentials.Queues), ReceiveMessage uses the in-process Service Bus AMQP 1.0 client via a shared connection pool. The receipt handle minted on the AMQP path is a distinct opaque format (version `2`, base64 of `{queueName, lockToken-GUID, lockedUntilUtc}`) carrying the SB lock-token directly rather than the REST messageId/lockToken pair. Long polling (WaitTimeSeconds) is honoured on the first receive; the polling-loop emulation that the REST path uses to batch up to 10 messages is replaced by a single `ReceiveBatchAsync` call against the cached AMQP link, which is more efficient. MessageAttributes (String/Number/Binary DataType round-trip) are now reconstructed from the `Aws2Azure-AttrTypes` application-property registry written by the AMQP send path; MD5OfMessageAttributes is computed via the shared `SqsMessageMd5.OfAttributes` helper so both transports produce the same hash. FIFO MessageGroupId is surfaced from `properties.group-id` (with the receiver's session-id as a fallback).
- Dead-letter attribution (AMQP path, Phase 2.5): when SB delivers a message from a `<queue>/$DeadLetterQueue` subqueue, the proxy surfaces three system attributes — `DeadLetterQueueSourceArn` (synthesised as `arn:aws:sqs:us-east-1:000000000000:<sourceQueueName>` from the SB `x-opt-deadletter-source` annotation; us-east-1 is a placeholder since the proxy doesn't model AWS regions, and the account id is `QueueUrlBuilder.PlaceholderAccountId`), `Aws2Azure-DeadLetterReason`, and `Aws2Azure-DeadLetterErrorDescription` (the latter two read from the dead-lettered message's application-properties, prefixed `Aws2Azure-` because SQS has no AWS-standard counterpart). All three respect the AttributeNames filter, are omitted entirely for non-DLQ messages, and are only emitted on the AMQP path (the REST path does not subscribe to DLQ subqueues yet).
- Emulator divergence (Phase 2.7 Slice 6): the Azure Service Bus emulator does NOT echo the broker-assigned session-id back in the receiver attach response's `com.microsoft:session-filter` when the client requests `sessionId=null` (broker-assigned). The proxy's FIFO receive path correctly fails fast in that case with `InvalidOperationException`, but it means FIFO end-to-end coverage against the emulator is impossible. Real Azure Service Bus honours the AMQP contract and echoes the id, so the production path works. FIFO IT coverage lives in the real-Azure nightly smoke instead; lifecycle ITs against the emulator skip the two FIFO scenarios.
- Emulator divergence (Phase 2.7 Slice 7): the SB emulator's TTL-expiry sweeper does not reliably dead-letter messages once their `DefaultMessageTimeToLive` elapses and/or does not honour `ForwardDeadLetteredMessagesTo` on the resulting DLQ message within an integration-test budget — observed: forwarded message never arrives on the target queue within 45s. Real Service Bus's expiry pipeline runs every ~5s and performs the forward synchronously. DLQ-forward IT coverage lives in the real-Azure nightly smoke; the slice-7 emulator-backed DLQ test is gated with `Skip.If` documenting the divergence.

### References

- <https://docs.aws.amazon.com/AWSSimpleQueueService/latest/APIReference/API_ReceiveMessage.html>
- <https://learn.microsoft.com/rest/api/servicebus/peek-lock-message-non-destructive-read>

## RemovePermission

- **Status:** ⚪ stub
- **Azure equivalent:** `No native Service Bus equivalent — validates queue existence and returns success.`

### Sub-features

| Name | Status | Notes | Gap | Workaround |
|---|---|---|---|---|
| Queue existence validation | ✅ implemented | Returns NonExistentQueue if the SB queue does not exist. |  |  |
| Permission removal by Label | ⛔ unsupported |  | AddPermission never persists anything, so RemovePermission has nothing to remove. |  |

### Behaviour differences

- No-op: returns 200 OK regardless of the Label. See AddPermission gap doc for the underlying rationale.

### References

- <https://docs.aws.amazon.com/AWSSimpleQueueService/latest/APIReference/API_RemovePermission.html>

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

- Per-queue transport selection (Phase 2.7 Slice 2): when the credential's serviceBus.transport (or per-queue override) is set to 'amqp', SendMessage is routed natively over AMQP via ServiceBusAmqpSender. The SQS-visible behaviour is identical to the REST path — same validation, same idempotency-key contract, same MD5 algorithm — only the wire to Service Bus differs. SendMessageBatch still goes over REST (Slice 3).
- MessageId is synthesised proxy-side (SB does not echo the message id on the runtime POST). For FIFO the MessageDeduplicationId is reused as the MessageId; otherwise a fresh Guid is minted.
- FIFO required-param validation (Slice 5): on a .fifo queue, MessageGroupId is required and the proxy returns MissingParameter when it is omitted. On standard queues, MessageGroupId and MessageDeduplicationId are rejected with InvalidParameterValue — matching SQS's per-attribute domain.
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

- Per-queue transport selection (Phase 2.7 Slice 3): when the credential's serviceBus.transport (or per-queue override) is set to 'amqp', SendMessageBatch dispatches each entry over a single AMQP sender link and aggregates the per-transfer dispositions. Unlike the REST path, this gives real per-entry partial-failure granularity — one rejected entry surfaces in Failed[] while the others remain Successful. Validation, FIFO interlock, idempotency-key minting, MD5 and response shape are otherwise identical to REST.
- SB's runtime batch send is atomic: the whole batch either succeeds or fails together (no AMQP-style per-message ack). The proxy preserves SQS's BatchResultErrorEntry shape: on a batch-level SB error every entry surfaces in Failed[] with the mapped SQS error code (SenderFault=true for client-side rejections, false for server-side). Genuine per-entry partial success (one bad entry mixed with good ones) is not available over SB REST.
- FIFO required-param validation (Slice 5): on a .fifo queue, every entry must carry a MessageGroupId; the proxy rejects the whole batch with MissingParameter on the first violating entry (validation runs before the SB call). On standard queues, MessageGroupId / MessageDeduplicationId on any entry yields InvalidParameterValue.
- Same attribute-flattening + Aws2Azure-AttrTypes side-channel as SendMessage. The 1 MiB aggregate cap is computed over body + attribute name/type/value bytes per the SQS quota docs (raised from 256 KiB in August 2025).
- Payloads larger than 1 MiB require the AWS Extended Client Library (S3-backed pointer); the same caveat as SendMessage applies — the pointer flows through unchanged and resolves against the proxy's S3 → Blob translation.
- Entry MessageId returned to the caller is proxy-synthesised (Guid, or MessageDeduplicationId for FIFO).
- Verified against in-process fakes; end-to-end emulator validation deferred to Slice 3.

### References

- <https://docs.aws.amazon.com/AWSSimpleQueueService/latest/APIReference/API_SendMessageBatch.html>
- <https://learn.microsoft.com/rest/api/servicebus/send-message-batch>

## SetQueueAttributes

- **Status:** 🟡 partial
- **Azure equivalent:** `Azure Service Bus management REST API — PUT /{queue}?api-version=2021-05 with If-Match: * (whole-entity replace)`

### Sub-features

| Name | Status | Notes | Gap | Workaround |
|---|---|---|---|---|
| VisibilityTimeout → LockDuration | ✅ implemented |  |  |  |
| MessageRetentionPeriod → DefaultMessageTimeToLive | ✅ implemented |  |  |  |
| MaximumMessageSize → MaxMessageSizeInKilobytes | ✅ implemented | Bounded by SQS 1 MiB cap (Aug-2025) and the SB tier ceiling (Standard 256 KiB, Premium up to 100 MiB). |  |  |
| DelaySeconds (queue default) | ⛔ unsupported | Rejected with InvalidAttributeName on update — SB has no equivalent field and the proxy has no durable per-queue metadata store yet. Tracked for the NFR phase. Per-message DelaySeconds on SendMessage still works. |  |  |
| ReceiveMessageWaitTimeSeconds (queue default for long-poll) | ⛔ unsupported | Same rationale as DelaySeconds — rejected with InvalidAttributeName until a durable metadata store lands. Per-call WaitTimeSeconds on ReceiveMessage is fully supported. |  |  |
| ContentBasedDeduplication / RequiresDuplicateDetection toggle | ✅ implemented | Only on FIFO queues; SB rejects flipping the flag on Standard queues. |  |  |
| RedrivePolicy → ForwardDeadLetteredMessagesTo | ✅ implemented | Slice 5: JSON parsed and mapped to ForwardDeadLetteredMessagesTo + MaxDeliveryCount. SB read-merge-write replaces the whole queue entity so the patch is preserved across subsequent SetQueueAttributes calls. |  |  |
| Policy / KmsMasterKeyId / KmsDataKeyReusePeriodSeconds / SqsManagedSseEnabled | ⛔ unsupported | Returned as InvalidAttributeName for the unsupported attribute. SB has its own SAS/MSI/CMK story that does not translate 1:1. |  |  |
| Read-merge-write semantics | ✅ implemented | SB management is whole-entity replace — the proxy first GETs the queue, overlays only the patched fields, then PUTs with If-Match: * to avoid clobbering immutable / unmanaged fields. |  |  |

### Behaviour differences

- Updates are not atomic across multiple concurrent SetQueueAttributes calls on the same queue: last write wins. If-Match: * is used (not the ETag we read) because SB does not surface the ETag in management responses on every emulator/version we tested. NFR phase may tighten this with an If-Match: <etag>.
- Several SQS-only attributes have no SB equivalent (Policy, KmsMasterKeyId, KmsDataKeyReusePeriodSeconds, SqsManagedSseEnabled, RedriveAllowPolicy). The proxy rejects them with InvalidAttributeName until Slice 5 (RedrivePolicy) and the security-encryption pass land.
- FIFO-only attributes (FifoQueue, FifoThroughputLimit, DeduplicationScope) cannot be flipped on an existing queue (SB rejects the change); the proxy returns InvalidAttributeName.
- Verified against in-process fakes; emulator-backed end-to-end validation is blocked because the Service Bus emulator does not expose the management REST API. Real-Azure validation pending.

### References

- <https://docs.aws.amazon.com/AWSSimpleQueueService/latest/APIReference/API_SetQueueAttributes.html>
- <https://learn.microsoft.com/rest/api/servicebus/update-queue>

## TagQueue

- **Status:** 🟡 partial
- **Azure equivalent:** `GET + PUT QueueDescription with aws2azure's base64 tag blob stored in UserMetadata.`

### Sub-features

| Name | Status | Notes | Gap | Workaround |
|---|---|---|---|---|
| Queue existence validation | ✅ implemented | Returns NonExistentQueue if the SB queue does not exist. |  |  |
| Tag persistence | ✅ implemented | Merges requested SQS tags into the existing tag map and persists them in QueueDescription.UserMetadata. |  |  |
| SQS tag limits | ✅ implemented | Enforces at most 50 tags, key length 1..128, and value length 0..256 before writing. |  |  |
| UserMetadata capacity guard | 🟡 partial | Requests whose compact base64 blob would exceed Service Bus's 1024-character UserMetadata limit fail with InvalidParameterValue. |  |  |

### Behaviour differences

- Tags are stored as an aws2azure-owned compact binary tag map, base64-encoded into Service Bus QueueDescription.UserMetadata. This preserves AWS tag keys and values exactly but consumes the queue's native UserMetadata field.
- Service Bus UserMetadata is limited to roughly 1024 characters in the legacy management schema, so valid SQS tag sets near the 50-tag / 128-key / 256-value maximum may be rejected when the serialized blob does not fit.
- Verified with in-process Service Bus management fakes. The Service Bus emulator does not validate or persist this management-plane UserMetadata path; real-Azure validation remains pending.

### References

- <https://docs.aws.amazon.com/AWSSimpleQueueService/latest/APIReference/API_TagQueue.html>
- <https://learn.microsoft.com/azure/service-bus-messaging/service-bus-xml-management-api>

## UntagQueue

- **Status:** 🟡 partial
- **Azure equivalent:** `GET + PUT QueueDescription with aws2azure's base64 tag blob stored in UserMetadata.`

### Sub-features

| Name | Status | Notes | Gap | Workaround |
|---|---|---|---|---|
| Queue existence validation | ✅ implemented | Returns NonExistentQueue if the SB queue does not exist. |  |  |
| Tag removal | ✅ implemented | Reads the stored tag map from UserMetadata, removes requested keys, and writes the updated QueueDescription. |  |  |
| UserMetadata capacity guard | 🟡 partial | Updated tag blobs are kept within Service Bus's 1024-character UserMetadata limit. |  |  |

### Behaviour differences

- Tags are stored as an aws2azure-owned compact binary tag map, base64-encoded into Service Bus QueueDescription.UserMetadata. Removing the last tag clears that UserMetadata value.
- Service Bus UserMetadata is limited to roughly 1024 characters in the legacy management schema; aws2azure rejects updates that cannot fit the serialized tag map.
- Verified with in-process Service Bus management fakes. The Service Bus emulator does not validate or persist this management-plane UserMetadata path; real-Azure validation remains pending.

### References

- <https://docs.aws.amazon.com/AWSSimpleQueueService/latest/APIReference/API_UntagQueue.html>
- <https://learn.microsoft.com/azure/service-bus-messaging/service-bus-xml-management-api>

