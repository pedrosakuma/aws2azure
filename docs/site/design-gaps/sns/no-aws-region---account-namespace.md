# sns design gap / No AWS region / account namespace {#design-gap-sns-no-aws-region---account-namespace}

[← Design-gap index](../../design-gaps.md)

- **Capability ID:** `design-gap:sns:no-aws-region---account-namespace`
- **Status:** 🔵 by design

Topic and subscription ARNs are synthesised as arn:aws:sns:{sigv4-region}:000000000000:{name}; the account id is a stable placeholder because the proxy is not backed by an AWS account namespace.

**Impact.** Applications that parse account id or cross-account references out of an ARN will see placeholder values.

**Workaround.** Do not depend on the account/region portion of returned ARNs.

## References

- <https://github.com/pedrosakuma/aws2azure/issues/267>

