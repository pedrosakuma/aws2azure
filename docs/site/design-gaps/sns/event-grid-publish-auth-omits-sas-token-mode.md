# sns design gap / Event Grid publish auth omits SAS-token mode {#design-gap-sns-event-grid-publish-auth-omits-sas-token-mode}

[← Design-gap index](../../design-gaps.md)

- **Capability ID:** `design-gap:sns:event-grid-publish-auth-omits-sas-token-mode`
- **Status:** 🟡 partial
- **Disposition:** 🔵 by design

SNS Event Grid publishing currently supports only static access-key auth (`aeg-sas-key`) and Microsoft Entra bearer tokens. It does not accept or generate Event Grid SAS tokens (`aeg-sas-token` / `Authorization: SharedAccessSignature`) for time-bounded publish delegation.

**Impact.** Multi-tenant or delegated deployments cannot use short-lived, scopeable Event Grid SAS publish tokens through the proxy. Operators must either distribute a long-lived shared key or rely on Entra-issued bearer tokens.

**Workaround.** Use Entra ID where possible, or provision/rotate Event Grid shared keys outside the proxy. Do not assume the SNS Event Grid backend can consume precomputed SAS tokens today.

## References

- <https://learn.microsoft.com/en-us/azure/event-grid/authenticate-with-access-keys-shared-access-signatures>

