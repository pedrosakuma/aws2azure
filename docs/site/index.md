# aws2azure — gap documentation

Authoritative inventory of which AWS operations the proxy translates, with the Azure mapping and the known behavioural gaps.

Start with the [coverage matrix](coverage.md) for a one-screen overview, then drill
into a service for per-operation detail. Cross-cutting, architectural limitations
that do not map to a single operation live in [design gaps](design-gaps.md).

## Services

- [dynamodb](dynamodb.md) — 19 operation(s), 10 design gap(s)
- [kinesis](kinesis.md) — 7 operation(s), 7 design gap(s)
- [s3](s3.md) — 74 operation(s), 8 design gap(s)
- [secretsmanager](secretsmanager.md) — 11 operation(s), 9 design gap(s)
- [sns](sns.md) — 14 operation(s), 10 design gap(s)
- [sqs](sqs.md) — 20 operation(s), 7 design gap(s)

## Cross-cutting

- [Coverage matrix](coverage.md) — every operation and status on one screen.
- [Maximum-viable completeness](completeness.md) — backlog vs structural boundaries.
- [Workload compatibility](workload-compatibility.md) — adoption patterns and go/no-go guidance.
- [Workload GA certification](workload-ga.md) — mechanical verdicts for versioned support profiles.
- [Design gaps](design-gaps.md) — architectural limitations spanning operations.
- [Real-Azure conformance & divergences](divergences.md) — verification state.
