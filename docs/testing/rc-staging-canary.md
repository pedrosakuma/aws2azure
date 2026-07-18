# Release-candidate staging and canary observation

Use this procedure once per advertised workload profile. RC observation is
real-Azure operational evidence; emulator, source-rebuild, mixed-version, and
cross-profile measurements do not qualify.

## Freeze the identities

Before shifting traffic, retain one trusted tuple:

- RC id, immutable RC-manifest digest, and source SHA;
- exact candidate identity and complete-runtime digests from that manifest;
- exact prior approved-runtime ledger identity and complete-runtime digests;
- workload profile id and schema version;
- Azure backend kind, region, backend-identity digest, proxy-config digest, and
  AWS-binding digest;
- reviewed minimum observation window and maximum evidence age.

The observation validator receives this tuple from the trusted RC manifest and
release policy. Values copied only from the observation YAML are not trust
anchors. `evidence_digest` is the SHA-256 of the canonical semantic payload,
excluding the digest field itself, and must also equal the immutable digest
recorded by the RC manifest. Editing the YAML and replacing its self-digest
therefore remains tampering.

## Stage the profile

1. Deploy the exact candidate bytes and production-shaped configuration to real
   Azure. Do not rebuild the source SHA.
2. Keep the stable cohort on the exact trusted prior runtime.
3. Freeze backend, region, capacity-relevant configuration, AWS binding, and
   backend identity for the observation window.
4. Give candidate and stable cohorts distinct ids and immutable member digests.
   A workload instance/client/member may appear in only one cohort.
5. Route equivalent profile traffic to both cohorts. Do not aggregate their
   metrics before evaluating candidate thresholds.
6. Record thresholded candidate and stable values, sample counts, capture
   timestamps, and one explicit rollback trigger per metric.

Sidecars are canaried as whole application instances. Standalone deployments
use distinct clients or routing partitions. A deployment that cannot identify
and isolate candidate and stable members is not an RC canary.

## Observe and decide

The window starts only after both cohorts serve the intended traffic and ends
after the reviewed minimum duration. Every cohort must cover that exact window,
and every metric must be captured inside it. Evidence generated in the future,
before the window ends, or after its reviewed freshness budget is invalid.

Each metric uses one mechanical comparison:

- `less_than_or_equal`: candidate value breaches when it is above the threshold;
- `greater_than_or_equal`: candidate value breaches when it is below the
  threshold.

Values and thresholds must be finite. A breach must be recorded as `breach`,
its trigger must be `fired`, and the decision must be `rollback`. A passing
metric has an `armed` trigger. Overrides and suppression are forbidden; change
the reviewed release policy and produce new evidence instead of waiving an
observed trigger.

## Roll back

On any fired trigger, stop candidate promotion and restore the exact trusted
prior identity. Keep the same backend identity, config digest, and AWS-binding
digest. Record ordered, in-window restoration timestamps and set `verified`
only after traffic and profile invariants prove that the prior runtime is
serving. A restart, config-only change, rebuilt prior SHA, or different
approved runtime is not restoration.

A `pass` verdict must have no breached metric and no restoration block. A
`rollback` verdict must have a fired trigger plus verified restoration of the
exact prior runtime and environment.

## Evidence shape

The strict YAML model is `RcObservationEvidence` in
`tools/Aws2Azure.GapDocs/RcObservation.cs`. Its top-level fields are:

```yaml
schema_version: 1
artifact_kind: rc_observation
evidence_digest: sha256:<canonical-payload-digest>
release_candidate: { id: ..., manifest_digest: ..., source_sha: ... }
candidate: { identity_digest: ..., runtime_digest: ..., source_sha: ... }
prior: { identity_digest: ..., runtime_digest: ..., source_sha: ... }
profile: { id: ..., version: 1 }
azure:
  backend_kind: blob
  region: westus2
  backend_identity_digest: sha256:...
  config_digest: sha256:...
  aws_binding_digest: sha256:...
observation:
  started_at_utc: ...
  ended_at_utc: ...
  generated_at_utc: ...
  minimum_window_minutes: 60
cohorts: [...]
metrics: [...]
rollback_triggers: [...]
decision: { verdict: pass, owner: ..., reason: ..., decided_at_utc: ... }
```

For a rollback verdict, add `restoration` with the exact prior identity/runtime,
unchanged backend/config/binding digests, ordered timestamps, and
`verified: true`.

Duplicate YAML keys, unknown fields, malformed digests, identity or environment
drift, mixed cohorts, incomplete/stale/future windows, non-finite metrics,
misreported thresholds, trigger overrides, unverified rollback, and
manifest-digest mismatches fail validation.

The real-Azure producer/consumer workflow that captures and binds this evidence
is intentionally separate from this contract and must not claim acceptance
until it supplies the trusted validation tuple above.
