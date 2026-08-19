# sqs / RemovePermission {#operation-sqs-removepermission}

[← sqs operation index](../../sqs.md) · [Coverage matrix](../../coverage.md)

- **Capability ID:** `operation:sqs:removepermission`
- **Status:** ⚪ stub
- **Disposition:** 🔵 by design
- **Azure equivalent:** `No native Service Bus equivalent — validates queue existence and returns success.`

## Sub-features

### Queue existence validation {#sub-feature-queue-existence-validation}

- **Capability ID:** `sub-feature:sqs:removepermission:queue-existence-validation`
- **Status:** ✅ implemented

Returns NonExistentQueue if the SB queue does not exist.

### Permission removal by Label {#sub-feature-permission-removal-by-label}

- **Capability ID:** `sub-feature:sqs:removepermission:permission-removal-by-label`
- **Status:** ⛔ unsupported
- **Disposition:** 🔵 by design

**Gap.** AddPermission never persists anything, so RemovePermission has nothing to remove.

## Behaviour differences

- No-op: returns 200 OK regardless of the Label. See AddPermission gap doc for the underlying rationale.

## References

- <https://docs.aws.amazon.com/AWSSimpleQueueService/latest/APIReference/API_RemovePermission.html>

