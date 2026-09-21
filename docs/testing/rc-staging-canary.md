# Release-candidate staging and canary observation

Use this procedure once per advertised workload profile. RC observation is
real-Azure operational evidence; emulator, source-rebuild, mixed-version, and
cross-profile measurements do not qualify.

## Freeze the identities

Before shifting traffic, retain one trusted tuple:

- RC id, protected-tag source SHA, and canonical RC identity digest;
- exact protected-main RC archive orchestration SHA, successful workflow
  run/attempt, artifact id/name/upload digest, and canonical archive-input
  content digest;
- exact successful GHCR workflow source SHA, run/attempt, artifact
  id/name/upload digest, canonical GHCR-input content digest, and OCI index
  digest;
- exact candidate identity and complete-runtime digests from that manifest;
- exact prior approved-runtime ledger identity and complete-runtime digests;
- workload profile id and schema version;
- Azure backend kind, region, backend-identity digest, proxy-config digest, and
  AWS-binding digest;
- reviewed minimum observation window and maximum evidence age.

The observation validator receives this tuple from a canonical identity receipt,
the archive and GHCR interfaces, the approved-runtime ledger, committed
policies, and immutable workflow-artifact selections. Values copied only from
the observation YAML are not trust anchors. The identity receipt is not a final
manifest and contains no observation descriptors.
`evidence_digest` is the SHA-256 of the canonical semantic payload, excluding
the digest field itself. The later final RC manifest binds that digest; editing
the YAML and replacing its self-digest therefore remains tampering.

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

### Live readiness rendezvous

The split S3 and SecretsManager observation jobs retain their own prepared
harness and sealed proxy processes on separate runners. Both candidate and
stable must create and read back a canary through their selected runtime before
publishing readiness. A queued job, provisioned resource group, or successful
health probe alone is not readiness. Calibration mode is unchanged.

Within the same job, a composite action uploads an immutable ready artifact.
The candidate coordinator validates both ready artifacts and both still-running
cohort jobs, then publishes one start release 120 seconds in the future. Each
live harness accepts only the release bound to its exact ready-file digest.
There is no preparation job whose processes must survive job completion, and
no select-time-plus-30-minute start guess.

Signals bind repository, run, attempt, workflow SHA, profile, RC/source
identity, window, deadline, and both sealed-runtime identity/content digests.
Artifact names include profile, role where applicable, run and attempt;
publication refuses overwrite. Readers verify current-attempt job metadata,
artifact identity, ZIP digest, member name and bounded JSON. Duplicate,
foreign, expired, malformed or substituted signals fail rather than become
"not ready." GitHub API failures also fail immediately; only absent signals
are polled, every 45 seconds. Native artifact uploads and the existing
`actions: read` permission suffice; no repository write permission is added.

**Bounds and failure handling:**

- Live readiness waits at most 45 minutes, shortened by the remaining absolute
  budget. Release requires a full 120-second lead within both ready deadlines;
  a start over 60 seconds late fails.
- Selection sets the cohort active-process deadline to requested window plus
  120 minutes from selection. Preparation has its own remaining-budget timeout
  and refuses provisioning without room for measurement and cleanup.
  Measurement still receives its existing fresh post-barrier CTS budget;
  readiness wait is not charged to the requested measurement duration.
- The supervisor terminates its owned harness process group on its deadline,
  with bounded TERM/KILL grace. The action always aborts/settles it before the
  workflow's existing runtime/credential removal and Azure teardown. Ordinary
  readiness failure allows in-process canary cleanup first; hard termination
  cannot guarantee restoration or evidence publication and never counts as
  accepted observation.
- Selection is bounded to 15 minutes, each cohort job to 300 minutes, and
  assembly to its existing 120 minutes. GitHub queue delays and asynchronous
  Azure deletion are not controlled by a wall-clock workflow deadline; the
  existing always-teardown/reaper remains necessary after runner loss.
- A missing/failed peer, failed coordinator or cancelled attempt cannot supply
  a successful paired capture. Rerun **both cohort jobs/full workflow** together;
  rerunning just one into a new attempt deliberately cannot reuse the old
  peer's readiness.

Each cohort capture retains `readiness/{context,ready,release-artifact,release,started}.json`.
The assembled capture also retains `stable-readiness/` and
`readiness-comparison.json`. These distinguish readiness, scheduled release,
actual UTC start and reported cross-runner start skew. Schema-1 capture/cohort
attribution retains its existing **scheduled** boundary; it is not proof of
simultaneous worker starts. Assembly checks both actual-start receipts, a
common immutable release, at most 60 seconds of lateness/skew, unchanged
runtime identities, and each full requested measurement duration. Runner clock
offsets are not independently calibrated. A peer can still fail after release;
the paired capture/zero-failure checks, not the release alone, determine success.

Offline coverage lives in `eng/test-rc-observation-readiness.py`,
`RcObservationReadinessTests`, and `RcObservationWorkflowTests`. No live-Azure
readiness run is asserted by these tests. Thresholds, exact-prior restoration
and the zero-failure policy are unchanged. This synchronization change neither
fixes nor disambiguates the historical public 403: retained evidence cannot
distinguish a Key Vault rejection from mapped nontransient Entra failures
([#1016 evidence analysis](https://github.com/pedrosakuma/aws2azure/issues/1016#issuecomment-5768667084)).
Calibration, threshold restoration and that historical limitation remain
separate from readiness.

### Measurement and decision

The measurement window starts only after both cohorts serve the intended
traffic and ends after the reviewed minimum duration. Candidate and stable
cohorts must cover that full measurement window, and every metric must be
captured inside it. When restoration is required, candidate attribution ends
when the exact-prior switch starts and stable attribution continues until
restoration verification. Evidence generated in the future, before the
measurement window ends, or after its reviewed freshness budget is invalid.

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
digest. Record ordered restoration timestamps and set `verified` only after
traffic and profile invariants prove that the prior runtime is serving. A
restart, config-only change, rebuilt prior SHA, or different approved runtime
is not restoration.

A `pass` verdict must have no breached metric and no restoration block. A
`rollback` verdict must have a fired trigger plus verified restoration of the
exact prior runtime and environment.

## Evidence shape

The strict schema-v2 YAML model is `RcObservationEvidence` in
`tools/Aws2Azure.GapDocs/RcObservation.cs`. Its top-level fields are:

```yaml
schema_version: 2
artifact_kind: rc_observation
evidence_digest: sha256:<canonical-payload-digest>
release_candidate:
  id: ...
  manifest_digest: sha256:...
  source_sha: ...
  archive_inputs:
    content_digest: sha256:...
    producer:
      repository: pedrosakuma/aws2azure
      workflow_path: .github/workflows/release-candidate.yml
      event_name: workflow_dispatch
      run_id: 120
      run_attempt: 1
      attempt_url: https://github.com/pedrosakuma/aws2azure/actions/runs/120/attempts/1
      source_sha: ... # protected-main archive orchestration SHA
      source_ref: refs/heads/main
    artifact:
      id: 450
      name: aws2azure-rc-archives-v1.2.3-rc.1-<content-digest>-run-120-attempt-1
      upload_digest: sha256:...
  ghcr_inputs:
    content_digest: sha256:...
    producer:
      repository: pedrosakuma/aws2azure
      workflow_path: .github/workflows/release-candidate-image.yml
      event_name: workflow_dispatch
      run_id: 121
      run_attempt: 1
      attempt_url: https://github.com/pedrosakuma/aws2azure/actions/runs/121/attempts/1
      source_sha: ...
      source_ref: refs/heads/main
    artifact:
      id: 451
      name: aws2azure-rc-ghcr-v1.2.3-rc.1-<content-digest>-run-121-attempt-1
      upload_digest: sha256:...
    index_digest: sha256:...
candidate: { identity_digest: ..., runtime_digest: ..., source_sha: ... }
prior: { identity_digest: ..., runtime_digest: ..., source_sha: ... }
profile: { id: ..., version: 1 }
policy:
  workload_manifest_digest: sha256:...
  qualification_policy_digest: sha256:...
  observation_policy_digest: sha256:...
azure:
  backend_kind: blob
  region: westus2
  backend_identity_digest: sha256:...
  config_digest: sha256:...
  aws_binding_digest: sha256:...
producer:
  repository: pedrosakuma/aws2azure
  workflow_path: .github/workflows/rc-observation-real-azure.yml
  event_name: workflow_dispatch
  run_id: 123
  run_attempt: 1
  run_url: https://github.com/pedrosakuma/aws2azure/actions/runs/123
  attempt_url: https://github.com/pedrosakuma/aws2azure/actions/runs/123/attempts/1
  source_sha: ...
  source_ref: refs/heads/main
capture_artifact:
  id: 456
  name: real-azure-rc-observation-capture-s3-basic-object-crud-run-123-attempt-1
  upload_digest: sha256:...
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

The immutable archive producer in
`.github/workflows/release-candidate.yml` stops before this step. Its attested
`release-candidate-archive-inputs.json` records observation evidence as pending
on the remaining workflow scope of #582 rather than fabricating a digest or pass
verdict. `.github/workflows/release-candidate-image.yml` consumes that exact
artifact by run, attempt, id, name, upload digest, and content digest and emits
attested `release-candidate-ghcr-inputs.json`. Use its recorded index digest, not
an RC tag alone, for deployment. The observation workflow validates both
interfaces and derives the canonical identity that the final manifest must
reproduce after real observation descriptors are added. It does not invent a
final manifest or placeholder evidence.

## Operator execution

The minimum reviewed window is 60 minutes, so PR workflows do not run this
costly live-Azure procedure. Merge the implementation, seal and qualify the
candidate, create the protected RC tag at the exact approved candidate SHA, then
run both RC workflows from protected `main`. The archive workflow checks out the
candidate tag into a separate source path while its workflow, helpers, and
approved ledgers remain pinned to one exact protected-main commit. Record the
candidate source and each workflow source independently.

For `v1.0.0-rc.1`, dispatch the archive producer with:

```bash
trusted_sha="$(gh api repos/pedrosakuma/aws2azure/branches/main --jq .commit.sha)"
gh workflow run release-candidate.yml \
  --ref main \
  -f candidate=v1.0.0-rc.1 \
  -f orchestration_sha="$trusted_sha"
```

The workflow fails if `main` resolves to a different SHA, if the dispatch ref is
not protected `main`, if the candidate tag is not protected, or if its commit
does not equal both approved runtime ledgers' runtime and attestation source.
After the archive succeeds, dispatch the image workflow from protected `main`
with both identities:

```bash
gh workflow run release-candidate-image.yml \
  --ref main \
  -f candidate=v1.0.0-rc.1 \
  -f archive_source_sha=c885a4b7bfbc35390a32b98139495c19dfb7da0b \
  -f archive_workflow_source_sha="$trusted_sha" \
  -f archive_run_id=<release-candidate-workflow-run-id> \
  -f archive_run_attempt=<release-candidate-workflow-attempt> \
  -f archive_artifact_id=<immutable-archive-artifact-id> \
  -f archive_artifact_name=<immutable-archive-artifact-name> \
  -f archive_artifact_digest=sha256:<64-hex-upload-digest> \
  -f archive_content_digest=sha256:<64-hex-content-digest>
```

Dispatch the observation workflow from protected `main`:

```bash
gh workflow run rc-observation-real-azure.yml \
  --ref main \
  -f profile=all \
  -f release_candidate_id=v1.2.3-rc.1 \
  -f candidate_source_sha=<40-hex-protected-tag-sha> \
  -f archive_workflow_source_sha=<40-hex-protected-main-archive-sha> \
  -f archive_run_id=<release-candidate-workflow-run-id> \
  -f archive_run_attempt=<release-candidate-workflow-attempt> \
  -f archive_artifact_id=<immutable-archive-artifact-id> \
  -f archive_artifact_name=aws2azure-rc-archives-v1.2.3-rc.1-<64-hex-content-digest>-run-<run-id>-attempt-<attempt> \
  -f archive_artifact_digest=sha256:<64-hex-upload-digest> \
  -f archive_content_digest=sha256:<64-hex-content-digest> \
  -f ghcr_workflow_source_sha=<40-hex-protected-main-sha> \
  -f ghcr_run_id=<release-candidate-image-run-id> \
  -f ghcr_run_attempt=<release-candidate-image-attempt> \
  -f ghcr_artifact_id=<immutable-ghcr-artifact-id> \
  -f ghcr_artifact_name=aws2azure-rc-ghcr-v1.2.3-rc.1-<64-hex-content-digest>-run-<run-id>-attempt-<attempt> \
  -f ghcr_artifact_digest=sha256:<64-hex-upload-digest> \
  -f ghcr_content_digest=sha256:<64-hex-content-digest> \
  -f observation_window_minutes=60 \
  -f azure_location=eastus2
```

The archive and GHCR inputs are mandatory because the workflow binds specific
immutable producer attempts and artifacts rather than selecting mutable
“latest” results. It verifies the protected tag and protected-main producer
identities, artifact API upload digests, canonical interface documents, GitHub
provenance attestations, exact archive-to-GHCR linkage, and OCI index digest.
It then generates a `release_candidate_identity` receipt from archive + GHCR
interfaces. Because canonical identity excludes `observation_evidence`, the
eventual final manifest can add the real per-profile descriptors and reproduce
the same identity without circularity.

### Short Secrets Manager calibration

Use calibration only to review shared-Key-Vault pressure before a full
observation. It reuses the exact RC archive/GHCR/runtime/backend resolution
above, runs only `secretsmanager-basic-lifecycle`, restores the exact prior
runtime, and uploads one sanitized JSON report with
`artifact_kind: rc_observation_calibration` and `promotable: false`. It never
generates or uploads `observation.yaml`, `binding.json`, or a manifest
selection receipt, and it cannot satisfy promotion gates.

Dispatch one shape at a time (for example candidate/stable `6/6`, then `5/5`
if needed) by reusing the same immutable identities:

```bash
gh workflow run rc-observation-real-azure.yml \
  --ref main \
  -f mode=secretsmanager-calibration \
  -f profile=secretsmanager-basic-lifecycle \
  -f release_candidate_id=v1.2.3-rc.1 \
  -f candidate_source_sha=<40-hex-protected-tag-sha> \
  -f archive_workflow_source_sha=<40-hex-protected-main-archive-sha> \
  -f archive_run_id=<release-candidate-workflow-run-id> \
  -f archive_run_attempt=<release-candidate-workflow-attempt> \
  -f archive_artifact_id=<immutable-archive-artifact-id> \
  -f archive_artifact_name=aws2azure-rc-archives-v1.2.3-rc.1-<64-hex-content-digest>-run-<run-id>-attempt-<attempt> \
  -f archive_artifact_digest=sha256:<64-hex-upload-digest> \
  -f archive_content_digest=sha256:<64-hex-content-digest> \
  -f ghcr_workflow_source_sha=<40-hex-protected-main-sha> \
  -f ghcr_run_id=<release-candidate-image-run-id> \
  -f ghcr_run_attempt=<release-candidate-image-attempt> \
  -f ghcr_artifact_id=<immutable-ghcr-artifact-id> \
  -f ghcr_artifact_name=aws2azure-rc-ghcr-v1.2.3-rc.1-<64-hex-content-digest>-run-<run-id>-attempt-<attempt> \
  -f ghcr_artifact_digest=sha256:<64-hex-upload-digest> \
  -f ghcr_content_digest=sha256:<64-hex-content-digest> \
  -f observation_window_minutes=60 \
  -f calibration_duration_minutes=10 \
  -f calibration_candidate_concurrency=6 \
  -f calibration_stable_concurrency=6 \
  -f azure_location=eastus2
```

The report captures requested duration, candidate/stable/total concurrency,
operation-mix identity, exact identity linkage, per-operation completions,
failures, throttles, first failure category/code, and per-cohort
`GetSecretValue` throughput. Treat it as diagnostic input for choosing a later
reviewed observation shape only; do not bind it into an RC manifest.

The reviewed `secretsmanager-basic-lifecycle` observation policy uses five
candidate and five stable workers against the shared Key Vault. Calibration
showed that `6/6` and `5/5` completed without failures or throttles, while
`4/4` fell below the unchanged absolute throughput floor; `5/5` is therefore
the smallest tested parallel shape satisfying both constraints. S3 retains
`8/8`. Normal observation reads these values and the operation-mix identity
from the committed per-profile policy; calibration inputs cannot override
them. The capture and final evidence bind the shape and reject cohort-count or
operation-mix drift.

Do not provide candidate/prior sealed-runtime run or artifact ids: those are
selected from the exact attested
approved-runtime export carried by the archive and its ledger-pinned rollback
target. The approved-ledger source may be a protected-main descendant of the
candidate tag, so the workflow never substitutes the tag checkout's older
ledger. It rejects an RC tag whose SHA or profile-approved runtime differs from
the archive inputs.

S3 and Secrets Manager execute as separate matrix jobs with distinct
candidate/prior proxy endpoints and disjoint object/secret namespaces. Cohort
member digests also bind the workflow run/attempt, role, worker, and exact
endpoint so the two populations remain attributable.

Each job performs the full profile lifecycle for the enforced 60–180 minute
window, records every threshold trigger without overrides or suppression, and
then conducts an exact-prior restoration drill against the same backend,
configuration, and AWS binding. A fired trigger produces and uploads rollback
evidence only after restoration verifies, then fails the job. A pass records
the successful drill in raw capture but omits a restoration claim from the
pass evidence.

Retain all three 90-day artifacts. Every name includes the exact workflow run
and attempt, so a rerun cannot be confused with its predecessor:

1. `real-azure-rc-observation-capture-<profile>-run-<run>-attempt-<attempt>`:
   raw capture, normalized exact archive and GHCR selections, canonical identity
   receipt, selected candidate/prior identities, the archive's approved-ledger
   source, and attested ledger exports (its upload identity is bound inside the
   generated evidence);
2. `real-azure-rc-observation-<profile>-run-<run>-attempt-<attempt>`: strict YAML
   plus its trusted binding;
3. `real-azure-rc-observation-selection-<profile>-run-<run>-attempt-<attempt>`:
   exact final artifact id/name/upload digest plus a `manifest_observation`
   object. Copy that object unchanged into the RC-manifest descriptor; its
   identifier binds repository, run, attempt, artifact id, and upload digest.

Before final manifest generation, select the evidence artifact by the receipt's
numeric id, verify its API upload digest, and rerun `validate-rc-observation`
with the downloaded `observation.yaml`, `binding.json`, and receipt
`evidence_digest`. Complete this within the policy's 72-hour freshness window;
a retained but stale 90-day artifact is diagnostic only and must not be bound
into a new RC manifest.

Finalize the canonical manifest from the reproduced identity receipt and both
selection receipts:

```bash
python3 eng/release-candidate-manifest.py finalize \
  release-candidate-identity.json \
  release-candidate-manifest.json \
  --observation s3-observation-upload-identity.json \
  --observation secretsmanager-observation-upload-identity.json
```

The command fails closed on rollback verdicts, candidate/identity drift,
duplicate evidence, or incomplete profile coverage. It then runs the strict
manifest validator against the exact archive files; it does not rebuild or
repackage them.

The workflow deletes projected credentials and sealed runtime bytes, then
deallocates its tagged resource group even on failure. Missing Azure
credentials, missing artifacts, test skips, incomplete cleanup evidence,
generation errors, or strict-validation errors fail closed. Never copy job
logs, backend keys, projected tokens, or generated proxy configuration into an
observation artifact.

The two profiles run as independent matrix jobs. With the minimum 60-minute
measurement window selected, allow approximately 75–110 minutes wall-clock for
artifact resolution, provisioning, exact-prior restoration, evidence upload,
and verified resource-group deletion. Each job is hard-bounded at 240 minutes.
The workflow creates no billable Azure compute: it temporarily uses one
Standard LRS storage account for S3 and one Standard Key Vault for Secrets
Manager. Transaction, storage, and data-transfer charges remain
consumption/region dependent, so the coordinator must approve the subscription
budget before dispatch rather than treating this procedure as zero-cost.
