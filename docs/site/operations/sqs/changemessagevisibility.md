# sqs / ChangeMessageVisibility {#operation-sqs-changemessagevisibility}

[← sqs operation index](../../sqs.md) · [Coverage matrix](../../coverage.md)

- **Capability ID:** `operation:sqs:changemessagevisibility`
- **Status:** 🟡 partial
- **Disposition:** 🔵 by design
- **Azure equivalent:** `Azure Service Bus queue runtime REST API — PUT /{queue}/messages/{messageId}/{lockToken}?api-version=2021-05 for visibility=0 (unlock), POST to the same path for positive values (renew-lock); AMQP — visibility=0 maps to Abandon and positive values use `com.microsoft:renew-lock`.`

## Sub-features

### ReceiptHandle round-trip {#sub-feature-receipthandle-round-trip}

- **Capability ID:** `sub-feature:sqs:changemessagevisibility:receipthandle-round-trip`
- **Status:** ✅ implemented

### VisibilityTimeout 0..43200 validation {#sub-feature-visibilitytimeout-043200-validation}

- **Capability ID:** `sub-feature:sqs:changemessagevisibility:visibilitytimeout-043200-validation`
- **Status:** ✅ implemented

### VisibilityTimeout=0 (immediate release) {#sub-feature-visibilitytimeout0--immediate-release}

- **Capability ID:** `sub-feature:sqs:changemessagevisibility:visibilitytimeout0--immediate-release`
- **Status:** ✅ implemented

REST transport uses the Service Bus Unlock Message operation (PUT); AMQP dispatches to ServiceBusReceiver.AbandonAsync. Both make the message immediately available again and advance the broker redelivery count.

### Arbitrary new visibility duration {#sub-feature-arbitrary-new-visibility-duration}

- **Capability ID:** `sub-feature:sqs:changemessagevisibility:arbitrary-new-visibility-duration`
- **Status:** ⛔ unsupported
- **Disposition:** 🔵 by design

SB extends the lock by the queue's configured LockDuration only (max 5 min). The proxy issues the renew and, when the granted seconds differ from the requested value, annotates the response with Aws2Azure-VisibilityClamped: requested=<N>;granted=<M>.

## Behaviour differences

- SB renew-lock semantics do not accept a caller-supplied duration — every renew extends by the queue's LockDuration. When the requested timeout differs from what SB grants the proxy emits the Aws2Azure-VisibilityClamped: requested=<N>;granted=<M> diagnostic header. (The header is suppressed when they agree — typical for queues whose LockDuration equals the SDK default 30 s called with VisibilityTimeout=30.)
- VisibilityTimeout=0 maps to immediate release on both transports: REST uses Unlock Message (PUT), while AMQP uses Abandon on the receiver link.
- Verified against in-process fakes; emulator-backed end-to-end validation lands with the Service Bus emulator fixture work.
- Header format: granted-seconds is derived from `lockedUntil - DateTimeOffset.UtcNow` (rounded to whole seconds). Clock skew between the proxy host and Service Bus can shift the value by 1-2 s; consumers should treat it as a diagnostic hint, not an SLA.
- Emulator divergence: the Service Bus emulator's $management node detaches the request/response link on the first com.microsoft:renew-lock request (visible to the proxy as 'channel has been closed'). Validated against real Azure only; the integration test against the emulator is skipped with a SkipException pointing to the real-Azure smoke.

## References

- <https://docs.aws.amazon.com/AWSSimpleQueueService/latest/APIReference/API_ChangeMessageVisibility.html>
- <https://learn.microsoft.com/rest/api/servicebus/unlock-message>
- <https://learn.microsoft.com/rest/api/servicebus/renew-lock-for-a-message>
- <https://learn.microsoft.com/azure/service-bus-messaging/service-bus-amqp-protocol-guide>

