# sqs design gap / No AWS region / account namespace {#design-gap-sqs-no-aws-region---account-namespace}

[← Design-gap index](../../design-gaps.md)

- **Capability ID:** `design-gap:sqs:no-aws-region---account-namespace`
- **Status:** 🔵 by design

The proxy is not backed by an AWS account, so queue ARNs are synthesised with a placeholder account id (000000000000) and the region taken from the SigV4 credential scope. Dead-letter source ARNs use us-east-1 as a placeholder.

**Impact.** Applications that parse the account id or region out of a queue ARN, or that assert cross-account/cross-region topology, will see placeholder values rather than real AWS identifiers.

**Workaround.** Do not depend on the account/region portion of returned ARNs. Region awareness is tracked as opt-in future work.

## References

- <https://github.com/pedrosakuma/aws2azure/issues/267>

