# Aws2Azure.Conformance

An AWS **wire-conformance** test harness. The proxy's core thesis is that an AWS
SDK pointed at it cannot tell it is talking to Azure — so every response,
**including errors**, must be indistinguishable from one real AWS could produce.
This project mechanically checks that claim.

## Why a dedicated harness

Unit tests assert the proxy does what *we* think AWS does. That just re-encodes
our own assumptions. Conformance instead compares the proxy against an
**independent oracle**:

1. the **AWS API contract** (documented HTTP status + error `Code` + envelope
   shape) — an oracle that needs no backend, and
2. **captured golden responses** from a real AWS implementation (LocalStack,
   later real AWS) — an oracle that catches header/charset/structural drift the
   contract alone doesn't pin down.

## Tiers

| Tier | Source of truth | Cadence | Blocking | Needs Docker |
|------|-----------------|---------|----------|--------------|
| **1** | AWS contract + committed goldens | every PR (`ci.yml`) | yes | no |
| **2** | live LocalStack differential (also (re)captures goldens) | nightly / `run-integration` label | no | yes |
| 3 | real AWS | manual / scheduled | no | yes (deferred) |

Tier 1 is fully offline: auth/validation errors are rejected in the SigV4 stage
**before** any Azure call, so the proxy boots in-process via
`WebApplicationFactory` with a dummy Blob credential and no container.

## How it works

```
raw HTTP response ──▶ AwsErrorCanonicalizer ──▶ CanonicalResponse ──▶ Render()
                         (mask volatile values,        (status + AWS-semantic
                          drop transport headers,        headers + envelope
                          sort for determinism)          fields)
```

- **Canonicalization** (`Canonicalization/`) reduces a response to the surface
  AWS clients actually contract on. Non-deterministic values (request/host ids,
  dates) and the non-contractual `Message` wording are masked; transport/server
  headers are dropped; everything is sorted. Two *faithfully equivalent*
  responses canonicalize to identical text.
- **Goldens** (`Goldens/`, `fixtures/<service>/*.golden`) store a canonical
  response plus provenance. Plain text, reviewed in PRs. Captured from
  LocalStack/AWS — **never hand-authored** (hand-authoring re-encodes the
  proxy's assumptions and catches nothing). Record mode:
  `AWS2AZURE_CONFORMANCE_RECORD=1`.
- **Diff + allow-list** (`Canonicalization/CanonicalDiff.cs`, `AllowList/`):
  differences become tagged `Divergence`s. A divergence is *accepted* only if a
  gap doc documents it.

## Accepting a known divergence

The gap-doc YAML is the single source of truth. To accept a faithful-divergence,
add a machine-readable tag to `behavior_differences` in
`docs/gaps/<service>/<Operation>.yaml`:

```yaml
behavior_differences:
  - "Proxy omits the server-side x-amz-id-2 correlation header [conformance:missing-header:x-amz-id-2]"
  - "Content-Type carries charset=utf-8 unlike bare AWS application/xml [conformance:header-value:content-type]"
```

Divergence tags: `status`, `body-kind`, `missing-header:<name>`,
`extra-header:<name>`, `header-value:<name>`, `missing-field:<name>`,
`extra-field:<name>`, `field-value:<name>`.

A bare `[conformance:<tag>]` accepts the divergence **service-wide** (use for
genuinely cross-cutting gaps, e.g. the proxy omitting `x-amz-id-2` on every S3
error). To accept a divergence for **one case only** — so a narrow waiver can't
silently suppress the same divergence elsewhere — scope it with the case name:
`[conformance:<caseName>::<tag>]` (e.g.
`[conformance:signature-does-not-match::field-value:Code]`).

Any divergence **not** covered by a documented tag fails the Tier-1 run.

## Running

```bash
dotnet test tests/Aws2Azure.Conformance            # Tier 1 (offline)
```

## Scope of the first slice (issue #228)

S3 proxy-side auth errors: `SignatureDoesNotMatch`, `InvalidAccessKeyId`,
`RequestTimeTooSkewed`. The Tier-2 LocalStack capture (which lands authoritative
goldens, flagged emulator-derived) and backend-mapped errors (`NoSuchBucket` /
`NoSuchKey`, needing an Azure-Blob backend) follow in stacked PRs.
