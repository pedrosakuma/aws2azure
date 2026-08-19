# sqs / SendMessage {#operation-sqs-sendmessage}

[← sqs operation index](../../sqs.md) · [Coverage matrix](../../coverage.md)

- **Capability ID:** `operation:sqs:sendmessage`
- **Status:** ✅ implemented
- **Azure equivalent:** `Azure Service Bus queue runtime REST API — POST /{queue}/messages?api-version=2021-05`
- **Real-Azure verified:** ✅ 2026-07-20 · [evidence](https://github.com/pedrosakuma/aws2azure/actions/runs/29769257977) · [workflow run](https://github.com/pedrosakuma/aws2azure/actions/runs/29769257977)

## Sub-features

### MessageBody round-trip (≤1 MiB) {#sub-feature-messagebody-round-trip--1-mib}

- **Capability ID:** `sub-feature:sqs:sendmessage:messagebody-round-trip--1-mib`
- **Status:** ✅ implemented

1 MiB cap counts the body and message attributes together, matching SQS's August 2025 quota increase from 256 KiB to 1 MiB.

### MessageAttributes (String/Number) {#sub-feature-messageattributes--string-number}

- **Capability ID:** `sub-feature:sqs:sendmessage:messageattributes--string-number`
- **Status:** ✅ implemented

Mapped to SB application properties as strings.

### MessageAttributes (Binary) {#sub-feature-messageattributes--binary}

- **Capability ID:** `sub-feature:sqs:sendmessage:messageattributes--binary`
- **Status:** ✅ implemented

Base64-encoded into the side-channel header so receive can rebuild the SQS-shaped attribute.

### MessageAttributes (custom .suffix types) {#sub-feature-messageattributes--custom-suffix-types}

- **Capability ID:** `sub-feature:sqs:sendmessage:messageattributes--custom-suffix-types`
- **Status:** ✅ implemented

### MD5OfMessageBody / MD5OfMessageAttributes in response {#sub-feature-md5ofmessagebody---md5ofmessageattributes-in-response}

- **Capability ID:** `sub-feature:sqs:sendmessage:md5ofmessagebody---md5ofmessageattributes-in-response`
- **Status:** ✅ implemented

Computed locally to match AWS algorithm; clients use them to detect transport corruption.

### DelaySeconds (0..900) {#sub-feature-delayseconds--0900}

- **Capability ID:** `sub-feature:sqs:sendmessage:delayseconds--0900`
- **Status:** ✅ implemented

Translated to BrokerProperties.ScheduledEnqueueTimeUtc (UtcNow + delay).

### MessageDeduplicationId (FIFO) {#sub-feature-messagededuplicationid--fifo}

- **Capability ID:** `sub-feature:sqs:sendmessage:messagededuplicationid--fifo`
- **Status:** ✅ implemented
- **Real-Azure verified:** ✅ 2026-07-28 · [evidence](https://github.com/pedrosakuma/aws2azure/actions/runs/30333267557) · [workflow run](https://github.com/pedrosakuma/aws2azure/actions/runs/30333267557)

Becomes SB MessageId for SB's dedup window. The fifo-amqp-boundaries real-Azure scenario replays an identical FIFO batch and confirms the duplicates stay suppressed; SB's default dedup window still differs from SQS — see behavior_differences.

### MessageGroupId (FIFO) {#sub-feature-messagegroupid--fifo}

- **Capability ID:** `sub-feature:sqs:sendmessage:messagegroupid--fifo`
- **Status:** ✅ implemented
- **Real-Azure verified:** ✅ 2026-07-28 · [evidence](https://github.com/pedrosakuma/aws2azure/actions/runs/30333267557) · [workflow run](https://github.com/pedrosakuma/aws2azure/actions/runs/30333267557)

Becomes SB SessionId. Reviewed real-Azure evidence covers both direct FIFO send and the downstream session receive/settle path in the fifo-amqp-boundaries scenario.

### MessageSystemAttribute AWSTraceHeader {#sub-feature-messagesystemattribute-awstraceheader}

- **Capability ID:** `sub-feature:sqs:sendmessage:messagesystemattribute-awstraceheader`
- **Status:** ⛔ unsupported
- **Disposition:** ⚫ non-goal

## Behaviour differences

- Per-queue transport selection (Phase 2.7 Slice 2): when the credential's serviceBus.transport (or per-queue override) is set to 'amqp', SendMessage is routed natively over AMQP via ServiceBusAmqpSender. The SQS-visible behaviour is identical to the REST path — same validation, same idempotency-key contract, same MD5 algorithm — only the wire to Service Bus differs. SendMessageBatch still goes over REST (Slice 3).
- MessageId is synthesised proxy-side (SB does not echo the message id on the runtime POST). For FIFO the MessageDeduplicationId is reused as the MessageId; otherwise a fresh Guid is minted.
- FIFO required-param validation (Slice 5): on a .fifo queue, MessageGroupId is required and the proxy returns MissingParameter when it is omitted. On standard queues, MessageGroupId and MessageDeduplicationId are rejected with InvalidParameterValue — matching SQS's per-attribute domain.
- SQS attribute data types (String/Number/Binary/'String.Custom') are flattened to SB application-property strings. The proxy emits an Aws2Azure-AttrTypes side-channel header so the receive path can faithfully reconstruct the original SQS shape — without it, all attributes would surface as String on receive.
- SQS's per-message cap is 1 MiB (1,048,576 bytes) — raised from 256 KiB in August 2025 — and includes the body plus every message attribute's name + data type + value bytes. The proxy enforces the same 1 MiB cap. The *effective* cap is also bounded by the backing Service Bus tier: SB Standard rejects anything over 256 KiB regardless, SB Premium honours up to 100 MiB. Per-queue MaximumMessageSize (1024..1048576) is recorded at CreateQueue time but not re-validated per send — SB itself rejects oversized payloads.
- Payloads larger than 1 MiB must use the AWS Extended Client Library, which stores the body in S3 and embeds a JSON pointer in the SQS message. That pointer flows through this proxy unchanged: the receive side returns the same pointer, and the embedded S3 reference resolves against the proxy's S3 → Blob translation, so end-to-end large-message support works as long as the client uses the Extended Client and the same proxy fronts both S3 and SQS.
- ScheduledEnqueueTimeUtc has millisecond resolution in SB; SQS DelaySeconds is integer seconds, so no loss occurs.
- The standard-queue send path is validated against real Azure Service Bus through the message-lifecycle scenario; FIFO session delivery is separately sealed by the sqs-fifo-amqp fifo-amqp-boundaries scenario.

## References

- <https://docs.aws.amazon.com/AWSSimpleQueueService/latest/APIReference/API_SendMessage.html>
- <https://learn.microsoft.com/rest/api/servicebus/send-message-to-queue>

