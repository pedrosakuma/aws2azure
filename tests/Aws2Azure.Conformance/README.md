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

Tier 2 is a **live differential**: the same validly-signed request is sent to the
proxy (booted over a real **Azurite** backend so its Azure→S3 error translation
runs) and to **LocalStack S3** (an authoritative real-S3 shape); the two
canonical responses are diffed allow-list-aware. It needs Docker **and** is
opt-in: the tests run only when `AWS2AZURE_CONFORMANCE_TIER2=1` is set (the
fixture skips booting any container otherwise). GitHub's `ubuntu-latest` has a
Docker daemon, so gating on Docker presence alone would let the every-PR
`ci.yml` run boot the containers — the explicit switch prevents that. The
dedicated `conformance.yml` workflow (Docker) sets the switch and runs the
differential nightly / on the `run-integration` label. In record mode the
LocalStack response is written as the committed golden so Tier 1 can diff
against it offline.

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
  responses canonicalize to identical text. Both the S3-style **XML** `<Error>`
  envelope and the AWS **JSON**-protocol envelope (`{"__type":"…#Code",
  "message":"…"}`, used by DynamoDB / Kinesis / modern SQS) are understood:
  `__type` is reduced to the short error `Code` (namespace-prefix independent)
  so the dispatch key is compared uniformly across protocols, while the body
  *kind* (`xml-error` vs `json-error`) is still diffed so a genuine protocol
  switch surfaces as a divergence. The legacy AWS **Query** envelope
  (`<ErrorResponse><Error><Code>/<Message></Error><RequestId/></ErrorResponse>`,
  used by SQS Query and SNS) is unwrapped to the same top-level surface as the
  bare `<Error>` root, with `(root)` retained as `ErrorResponse` for faithfulness.
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
  - "Proxy omits the informational <HostId> error element real S3 emits [conformance:missing-field:HostId]"
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
dotnet test tests/Aws2Azure.Conformance            # Tier 1 offline; Tier 2 skips unless AWS2AZURE_CONFORMANCE_TIER2=1
AWS2AZURE_CONFORMANCE_TIER2=1 dotnet test tests/Aws2Azure.Conformance              # Tier 1 + Tier 2 (needs Docker)
AWS2AZURE_CONFORMANCE_TIER2=1 AWS2AZURE_CONFORMANCE_RECORD=1 dotnet test tests/Aws2Azure.Conformance   # (re)capture goldens from LocalStack
```

## Scope so far (issues #228, #234)

- **Tier 1 — S3 proxy-side errors** (offline, every PR): rejected before any
  Azure call, in either the SigV4 stage (`SignatureDoesNotMatch`,
  `InvalidAccessKeyId`, `RequestTimeTooSkewed`) or the request-validation stage
  (`InvalidBucketName` — a validly-signed request to a syntactically invalid
  bucket name).
- **Tier 1 — DynamoDB proxy-side errors** (offline, every PR): the first
  **JSON-protocol** service matrix, exercising the JSON-envelope canonicalizer
  end-to-end. Two parser-stage cases — a validly-signed AWS-JSON request rejected
  by the wire-protocol parser before any Cosmos call: `UnknownOperationException`
  (unknown `X-Amz-Target` op) and `SerializationException` (non-JSON body) — plus
  two SigV4 auth-stage cases: `InvalidSignatureException` (wrong secret) and
  `UnrecognizedClientException` (unknown access key), both **HTTP 400**. The
  proxy's `{"__type":"…#Code","message":"…"}` envelope is asserted against the
  AWS contract (status + `json-error` body kind + short `__type` dispatch code +
  `application/x-amz-json` media type). `X-Amz-Target` is part of the signed
  header set because the proxy enforces it via `RequiredSignedHeaders` (faithful
  to real DynamoDB).
- **Tier 1 — Kinesis proxy-side errors** (offline, every PR): the second
  **JSON-protocol** service matrix, mirroring DynamoDB's four cases against the
  Kinesis module. Kinesis speaks **AWS JSON 1.1** and renders `__type` as the
  *bare* error code with no `com.amazonaws…#` namespace prefix, so this matrix
  additionally validates the canonicalizer's prefix-free `__type` path. The two
  auth-stage cases (`InvalidSignatureException`, `UnrecognizedClientException`,
  both **HTTP 400**) exercise the issue #241 fix through a module that uses the
  *default* `EmitSigV4FailureAsync` (no per-request SQS-style override), while the
  parser-stage cases assert `UnknownOperationException` (unknown `X-Amz-Target`
  op) and `SerializationException` (non-JSON body).
- **Tier 1 — SQS proxy-side errors** (offline, every PR): the proxy's only
  **dual-protocol** module, so this matrix is the first to exercise BOTH the
  AWS-JSON `{"__type":…}` envelope and the legacy AWS **Query** `<ErrorResponse>`
  XML envelope — the latter validating the canonicalizer's new `<ErrorResponse>`
  unwrap path (which flattens `<Error><Code>/<Message>` to the same top-level
  surface as S3's bare `<Error>` root while keeping `(root)` = `ErrorResponse`).
  Five cases: a parser-stage `InvalidAction` (HTTP 400, validly-signed Query
  request naming an unknown action) plus two SigV4 auth pairs that pin **both
  branches** of the issue #241 SQS `EmitSigV4FailureAsync` override — the only
  per-request protocol-negotiated auth vocabulary in the proxy. The Query branch
  answers `SignatureDoesNotMatch` / `InvalidClientTokenId` at **HTTP 403** (the
  AWS Query front-door unknown-key code, issue #247); the
  AWS-JSON branch answers `InvalidSignatureException` / `UnrecognizedClientException`
  at **HTTP 400**. Each case asserts the protocol-correct raw wire shape (Query →
  `text/xml` + `<ErrorResponse>` root; JSON → `application/x-amz-json` + `__type`
  ending in the dispatch code) in addition to the protocol-independent status +
  short code.
- **Tier 1 — SNS proxy-side errors** (offline, every PR): the last service in
  the #234 "templatize the error matrix" checklist. SNS is single-protocol
  (legacy AWS **Query** only), so this matrix is the second to drive the AWS
  Query `<ErrorResponse>` XML envelope through the canonicalizer's unwrap path —
  this time via a module that uses the **default** `EmitSigV4FailureAsync` (the
  AWS Query XML 403 vocabulary, no per-request override), confirming the unwrap is
  protocol-agnostic. Three cases: a parser-stage `InvalidAction` (HTTP 400,
  validly-signed Query request naming an unknown action; this case must sign
  `content-type` because SNS enforces it via `RequiredSignedHeaders`) plus two
  SigV4 auth cases — `SignatureDoesNotMatch` (HTTP 403, wrong secret) and
  `InvalidClientTokenId` (HTTP 403, unknown access key — the shared AWS Query
  front-door code, issue #247). Each asserts the faithful Query wire shape
  (`text/xml` + `<ErrorResponse>` root) alongside the status + short code.
- **Tier 2 — S3 backend-mapped errors** (LocalStack differential, Docker):
  `NoSuchBucket` (GET on a missing bucket) and `NoSuchKey` (GET a missing key in
  an existing bucket), proxy-over-Azurite vs LocalStack S3. The accepted
  faithful divergences (proxy omits `x-amz-id-2` / `<HostId>` / `<BucketName>` /
  `<Key>`) are documented in `docs/gaps/s3/GetObject.yaml`. The error
  content-type charset parameter is treated as non-contractual and normalized
  out (SDK clients parse the XML body regardless).

> Note: SigV4 *auth* errors are protocol-aware (issues #241 and #247, fixed).
> XML services keep the 403 shape but the unknown-key code is service-specific —
> S3 returns `InvalidAccessKeyId`, the AWS Query front door (SNS, SQS-Query)
> returns `InvalidClientTokenId`; the shared codes are `SignatureDoesNotMatch` /
> `RequestTimeTooSkewed`. AWS-JSON services (DynamoDB, Kinesis, the modern SQS
> JSON path) return `InvalidSignatureException` / `UnrecognizedClientException`
> at **HTTP 400**, via `AuthErrorVocabulary.Resolve(dialect, status)` where the
> dialect comes from each module's `AuthErrorDialect`. The DynamoDB and Kinesis
> auth cases above are Tier-1-only: LocalStack can't be their oracle (it ignores
> signatures), so the expected outcome is the AWS JSON-protocol contract.

Real-AWS goldens (Tier 3) and further operations follow in later PRs.
