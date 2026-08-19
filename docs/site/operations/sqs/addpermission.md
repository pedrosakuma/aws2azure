# sqs / AddPermission {#operation-sqs-addpermission}

[← sqs operation index](../../sqs.md) · [Coverage matrix](../../coverage.md)

- **Capability ID:** `operation:sqs:addpermission`
- **Status:** ⚪ stub
- **Disposition:** 🔵 by design
- **Azure equivalent:** `No native Service Bus equivalent — validates queue existence and returns success.`

## Sub-features

### Queue existence validation {#sub-feature-queue-existence-validation}

- **Capability ID:** `sub-feature:sqs:addpermission:queue-existence-validation`
- **Status:** ✅ implemented

Returns NonExistentQueue if the SB queue does not exist.

### Cross-account permission persistence {#sub-feature-cross-account-permission-persistence}

- **Capability ID:** `sub-feature:sqs:addpermission:cross-account-permission-persistence`
- **Status:** ⛔ unsupported
- **Disposition:** 🔵 by design

**Gap.** SQS resource-based access via SID/AccountId/Action does not map to SB. Authorization in SB is done via namespace-level Shared Access Signatures or AAD roles, neither of which the proxy provisions on a per-queue basis.

## Behaviour differences

- The Permission payload is accepted and silently dropped; there is no AWS-style cross-account access control inside the proxy. Clients relying on AddPermission to grant access should configure SB SAS rules or Azure RBAC out of band, then map them to aws2azure access keys via the config file.
- Returns 200 OK on any well-formed payload to maximise SDK compatibility — including for Actions that SQS itself rejects on standard queues. A future revision may tighten validation once the credential model exposes scoped keys.

## References

- <https://docs.aws.amazon.com/AWSSimpleQueueService/latest/APIReference/API_AddPermission.html>

