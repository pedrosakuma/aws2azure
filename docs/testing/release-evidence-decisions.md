# Per-release evidence decisions

Stable promotion uses policy `release-evidence-reuse-v1`: **historical validity**
and **current release eligibility** are distinct. The blanket 72-hour promotion
renewal requirement mixed original qualification quality with external health.
The policy review in [#1062](https://github.com/pedrosakuma/aws2azure/issues/1062#issuecomment-5943369935)
found no risk-calibrated basis for requiring a complete repeat solely due to
elapsed time. New promotions require schema-v2 plans; schema-v1 plans remain
readable historical records but cannot pass `--require-decision`.

## What does not change

Original evidence and its bindings, timestamps, digests, minimum duration,
sample counts, thresholds and rollback requirements remain immutable. The
original age budgets still apply **at issuance**, including source-run and
blocking-signal ages. Evidence that was invalid when issued cannot be rescued
by a release decision. Diagnostics and short health checks cannot substitute
for missing canonical qualification or the long observation.

`validate-qualification`, `validate-rc-observation`, qualification generation,
observation capture and the pinned GA authority retain their existing semantics.
This policy does not extend artifact retention, restore deleted artifacts,
override revocation, promote workload status or authorize Azure spending.

## Historical gate

After downloading checksum-verified immutable artifacts and reproducing the
canonical RC identity with `eng/release-candidate-manifest.py`, the workflow runs:

```bash
dotnet run --project tools/Aws2Azure.GapDocs -c Release -- \
  validate-historical-release-evidence \
  <verified-identity.json> <observations-directory> <historical-report.json>
```

The observation layout is `<directory>/<profile>/evidence/observation.yaml`
and `binding.json`. This command consumes an **already verified identity**;
it is not a replacement for archive/manifest verification. It checks all
included profiles' unchanged RC-bound current ledgers, qualification digests,
candidate/prior bindings, original validity, current ledger status and artifact
expiry. Its report explicitly says `release_eligible: false` and
`current_health_verified: false`.

The promotion gate separately requires all four advertised profiles and checks
the original source producers/artifact availability through GitHub now. Exact
successful run attempts, artifact identities and observation receipts remain
mandatory; unavailable, expired or revoked evidence fails closed.

## Explicit decision, no automatic approval

The promotion plan's `evidence_decision` points to a committed JSON record.
Start with [the pending RC5 record](../releases/v1.1.1-evidence-decision.json).
An owner must review relevant changes since the evidence was issued, unresolved
incidents, external-service/configuration changes and the applicable scope of
each profile. Record references, rationale, reviewer identity and review time.
This is an attributable human assessment on protected main, not an automated
incident feed or a claim of universal present-day Azure health.

Each profile chooses `reuse` with an explicit rationale, or `checks_required`
with specific evidence. Invalidating changes require new canonical evidence and
updated exact bindings, not a rationale that waives failed historical checks.
Pending/blocked reviews, unresolved incidents and incomplete checks stop release.

Required checks identify their purpose, rationale, exact producer workflow/run/
attempt/source and artifact id/name/upload digest. They need a passing reviewed
outcome and an explicit `not_before_utc` / `valid_until_utc` window justified for
that risk. There is no new universal TTL. The source must have completed within
the window and before review; it is checked again before publication.
A green job alone is insufficient: the owner must assess its actual result and
scope before recording `pass`. A reuse decision does not claim a new measurement.

Bind approval to the exact release inputs:

```bash
python3 eng/release-promotion.py <plan.json> --review-inputs-digest
python3 eng/release-promotion.py <plan.json> --require-decision
python3 eng/release-promotion.py <plan.json> --gate-history <historical-report.json>
```

Compute the digest after finishing the reviewed inputs and store it as
`reviewed_inputs_digest`. It covers the plan, readiness plan, release notes,
implementation, tests, tooling, workflows, deployment files, workload/gap policy
and root build/discovery files; the decision itself is excluded to avoid a
circular hash. A relevant edit invalidates approval. Merge/rebase changes can
require recomputing and reviewing it; do not copy a digest to bypass review.

Dispatch must use current protected main, which is checked again immediately
before publication so an older orchestration cannot ignore a newer incident or
revocation decision. The workflow retains the historical report, decision and
eligibility result alongside unchanged evidence. The result records
`canonical_evidence_renewed: false`. Promotion still requires separate explicit
authorization and never rebuilds the sealed payload.
