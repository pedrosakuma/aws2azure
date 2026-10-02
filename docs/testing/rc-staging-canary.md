# Release-candidate staging and canary observation

Use this procedure once per advertised workload profile. RC observation is
real-Azure operational evidence; emulator, source-rebuild, mixed-version, and
cross-profile measurements do not qualify.

## Release coverage and pending producer support

Release plans now require all four profiles advertised as GA by the committed
`docs/site/workload-ga.json`: `dynamodb-basic-crud`, `s3-basic-object-crud`,
`secretsmanager-basic-lifecycle`, and `sqs-standard-messaging`. That authority
is evaluated at its pinned certification instant, not at the wall-clock time
of a new promotion. Reading its GA list is a **coverage check**, not fresh
qualification or live observation evidence. A later expiry/downgrade does not
automatically remove a release's evidence obligations; a newly advertised GA
profile blocks these tools until its release coverage is explicitly added.

`eng/release-readiness-gate.py` requires exactly one `real-azure`, `profile`,
and **profile-scoped `observation`** gate for each required profile, plus the
existing singleton CI/AOT/conformance/perf/footprint gates. A successful
observation workflow run alone no longer covers unspecified profiles.
Each observation gate uses `candidate_receipt` to select its immutable
`real-azure-rc-observation-selection-<profile>-run-<run>-attempt-<attempt>`
artifact and `observation-upload-identity.json`. The gate verifies the ZIP
digest, profile, candidate identity, producer attempt/source, passing verdict,
manifest descriptor and the referenced evidence artifact's metadata.
Promotion still downloads and strictly validates the actual YAML/binding.
Under the [per-release evidence policy](release-evidence-decisions.md), freshness
is checked at original issuance, then a separate reviewed decision determines
release eligibility. This receipt check replaces neither validation.
Offline gate fixtures place receipts beneath
`artifact-<artifact_id>/<receipt_name>` to keep identical basenames separate.

`eng/release-promotion.py` likewise rejects a promotion plan omitting any of
the four observations. Old two-profile plans are historical records, **not
valid inputs to a new promotion**. There is no grandfathering or skip flag.

**Four-profile producer wiring is implemented, not live-verified (#1009).**
Archive/GHCR selection, canonical identities, workflow routing and cohort
producers cover all four profiles. Shared profile/cohort matrices keep candidate
and stable processes on separate runners and isolated backend resource groups.
Assembly uses the same strict identity, readiness, capture and evidence checks
for every profile. Unknown profiles have no S3/SecretsManager fallback.
New promotion remains blocked until compatible exact approvals and genuine
passing observations exist. Offline tests do not supply that evidence.
S3/SecretsManager workload and readiness timing are unchanged; failed cohort
jobs now retain available sanitized captures/readiness/diagnostic sidecars too,
without turning a failed job into a passing observation.

### Four-profile packaging prerequisite

The archive producer exports each required profile's approved ledger from the
exact protected-main orchestration checkout. `eng/release_profile_coverage.py`
provides the shared coverage check and archive filenames. Local
`release-candidate-inputs.py create-context` takes one
`--profile-input PROFILE LEDGER_JSON PROFILE_YAML` for each required profile;
missing, duplicate, foreign, unsupported-version or expired approvals fail.
The same coverage applies to archive validation, GHCR bundle validation,
canonical identity generation/validation and final manifests.

One RC still has **one candidate source and one sealed linux-x64 runtime**.
All four approvals must independently bind that exact source, executable and
complete-runtime digests, sealed manifest, producer run/attempt and immutable
artifact id/name/upload digest. Distinct profile ledger digests remain required.
Different approvals are not relabeled as a shared runtime merely because they
are all GA, and different builds of one commit are not interchangeable.

Each profile goes through the existing exact sealed-artifact resolver, including
live artifact expiry/digest/attestation checks. Its resolved identity and ledger
are retained and attested in the archive payload; the GHCR consumer validates
each pair against the context and sealed manifest before using any executable.
The pre-existing S3/SecretsManager ledger filenames and S3 resolved-identity
filename remain unchanged. Creating a context and validating a bundle for GHCR
both reject expired sealed approvals; network retrieval additionally rechecks
current artifact availability. Historical byte integrity is not fresh eligibility.
Older two-profile records are not rewritten; current packaging/identity commands
reject them as inputs to a new four-profile candidate.

**The four profile approvals now bind one qualified runtime.**
The September 26-28 qualification campaign used source
`bf2274dc6469ce2a8a0c2db4b956391f24c74076`, sealed producer
`36280313825/1`, artifact `10918950988`, and complete-runtime digest
`sha256:32767e07b62b1e0f1bfa1b05500efa4e008bb93ec2fc6e85f59521f2f443e322`.
Each profile has one sealed correctness run and three distinct production-shaped
load runs, with its unchanged policy and exact historical rollback target.
The emitted qualification YAMLs are retained unchanged under
`docs/workloads/evidence/`; the matching approved-runtime ledgers bind their
digests and preserve each proof's prior identity.

| Profile | Qualified evaluator run | Correctness run | Three load runs (all attempt 1) |
|---|---|---|---|
| S3 CRUD | [36368783013](https://github.com/pedrosakuma/aws2azure/actions/runs/36368783013) | 36292783112 | 36366800480, 36367441737, 36368133678 |
| SecretsManager lifecycle | [36288126483](https://github.com/pedrosakuma/aws2azure/actions/runs/36288126483) | 36283772869 | 36284943737, 36286028409, 36287072545 |
| DynamoDB CRUD | [36292709091](https://github.com/pedrosakuma/aws2azure/actions/runs/36292709091) | 36288199735 | 36289409134, 36290477218, 36291653513 |
| SQS standard | [36283672084](https://github.com/pedrosakuma/aws2azure/actions/runs/36283672084) | 36280537667 | 36281869723, 36282465355, 36283122744 |

SQS's three source load artifacts each retain one supplementary `rollback-rest`
failure. The pre-existing policy excludes that REST counterpart; all required
AMQP rollback proofs passed. SecretsManager's successful rotation proof includes
the intended revoked-identity denial, not an explanation of the historical #1016
403. Neither caveat is waived or converted into a successful production operation.

All four rollback proofs target the previously approved `40c1ea99` runtime
(sealed producer `36171044613/1`, artifact `10880620175`). Both this prior and the
new candidate contain the cached-AMQP authorization-renewal fix in #1048.
This supplies the baseline transition described in #1056 without rewriting RC4:
RC4 remains bound to its historical `d6619b09` rollback target, which predates
that fix. Short qualification is not proof of long stable-cohort behavior;
a separately authorized observation is still required. S3 correctness emitted
per-blob lease-cleanup warnings, but the resource group was confirmed absent
before its load campaign. The historically failed SecretsManager correctness
run `36147184976` remains ineligible and is not part of these qualifications.

This alignment supplies the packaging prerequisite, not a published RC, completed
60-minute dual-cohort observation, calibration, or stable release promotion.
The later approved-ledger/orchestration commit may differ from the candidate
source: the new RC tag must identify the qualified source above, and packaging
must reuse its sealed x64 bytes rather than rebuild the approval commit.
Artifact availability, attestations and freshness are rechecked by consumers.
Existing `v1.1.1-rc.1`, `v1.1.1-rc.2`, `v1.1.1-rc.3`, and `v1.1.1-rc.4`
identities must not be moved or overwritten.

The DynamoDB/SQS observation policy YAMLs already exist at concurrency `8/8`.
They reference reviewed qualification throughput floors of 17 `GetItem`/s and
4 `ReceiveMessage`/s respectively, with zero failures. Their load harnesses
use concurrency 8 and the seven-operation profile lifecycles, but ordinary
short single-cohort qualification is **not 60-minute dual-cohort calibration**.
The new producers reuse the existing qualification workers and their successful
operation sequences; policies and qualification ledgers remain unchanged.
An explicit calibration/observation decision and authorization are still needed,
rather than inventing floors or marking unperformed observations verified.
The four RC5 observations subsequently completed; see the
[RC5 review](../releases/v1.1.1-rc.5.md) for results and current-time freshness.
Historical SecretsManager authorization-failure causality and observation-floor
calibration remain unproven; they are not a request to repeat its completed
worker-concurrency diagnostic.

## Diagnostic sidecars (not release evidence)

All four RC observation profiles write
`<capture-file>.<candidate|stable>.operation-timings.json` beside the canonical
capture. The split-cohort workflow already retains these files in each role's
capture artifact, including failed attempts when reporting can complete.
Inspect **both role artifacts**, not only the assembled observation artifact.
Preparation failures before measurement do not produce operation timings.

These reports have `promotable: false`. They record the exact runtime digest,
harness source, workload, operation schedule (including repeated operations),
worker concurrency, scheduled/actual start and worker completion status.
Per-operation summaries contain exact counts, cumulative/mean latency and
approximate p50/p95/p99 using 512 histogram buckets; at most 60 time windows per
operation bound additional storage. Measurement and drain are separate.
Latency is measured by the AWS-client harness, including the proxy and backend
round trip, **not** backend-only latency or a network RTT probe. Cleanup rows
are populated only where the existing workload explicitly measures cleanup;
an empty cleanup row is not evidence that cleanup took no time.

Process context includes architecture, available processor count, cumulative
test-harness CPU seconds during measurement/drain and working-set bytes sampled
at its end. These are **not proxy/backend CPU or memory**, not peak memory and
not per-worker attribution. In legacy dual-cohort mode, both roles share the
same test process, so their resource measurements overlap; do not sum them.
Unavailable process resource readings remain null and emit a sanitized warning.
No endpoint, resource name, payload, credential or raw exception text is added.
No additional network request, background sampler or proxy rebuild is used.

Diagnostics neither alter thresholds nor establish performance parity, fresh
qualification or release eligibility. Compare runner regions from the retained
Actions setup logs and backend regions from canonical captures: this sidecar
does not discover geography. Different runner/backend placements confound an
uncontrolled A/B comparison. A same-runner/backend, counterbalanced exact-binary
A/B and A/A experiment remains a separate, explicitly authorized Azure run.
Offline instrumentation checks do not execute that experiment.

### Controlled SecretsManager worker-concurrency diagnostic

The completed RC5 comparison and corrected floor interpretation are recorded
in [the RC5 review](../releases/v1.1.1-rc.5.md#secretsmanager-concurrency-question-answered-floor-not-recalibrated).
The instructions below describe the harness, not a request to repeat it.

`secretsmanager-concurrency-diagnostic.yml` is a manual-only, non-promotable
diagnostic for #1016/#1062. It requires protected `main`, an exact candidate
aggregate digest and explicit cumulative-budget acknowledgement. Implementation
and offline validation do **not** authorize its paid PR gates or dispatch.
It resolves the current approved candidate with the existing sealed-artifact
verifier before Azure login, rejects a digest mismatch, and never rebuilds the
proxy, changes a policy/ledger, or renews qualification. The `reaffirm` launcher
mode selects historical bytes; this diagnostic produces no reaffirmation
evidence or promotion eligibility.

One runner and one dedicated Standard Key Vault execute **5, 8, 8, 5, 5, 5**
workers in sequence, always using the same candidate. Each slot restarts the
sealed proxy with unchanged configuration, binding and backend, then runs a
separate 30-second warmup and 300-second measurement with the observation
worker's exact lifecycle, payloads, value assertions and SDK retry setting.
Qualification's stale-value polling delays differ from observation's fixed
500 ms delay, so the diagnostic does not mix those workers. The last two slots
are a 5/5 control; they are excluded from pooled 5-versus-8 rates.

The initial vault must have no active **or deleted** secrets. Before every
phase, paginated authenticated inventory must be empty, followed by a fixed
60-second quiet interval and another empty-inventory check. After workers drain,
the proxy stays alive while a read-only barrier requires **60 seconds of
observed empty inventory**, polling both collections every five seconds. The
first empty sample starts that window; any active or deleted entry resets it.
The total barrier deadline remains **120 seconds**, with at most 25 inventory
reads, and is never restarted when entries reappear. One empty response is
not enough: run `36774364326/1` observed zero deleted entries after warmup,
then two at the next phase's precheck (#1068). This matters because force deletion
can return before background purge completes. A nonempty initial vault, failed
inventory request, incomplete purge, repair cleanup, invalid lifecycle ratios,
operation failure or binding drift aborts subsequent phases; it is not a clean
comparison. The diagnostic never repairs a phase by deleting secrets itself.
The original pre-phase empty checks and 60-second quiet interval remain in
place, including after a successful stability barrier. These are bounded
observations of separately paginated collections, not proof of an atomic or
globally consistent snapshot. They reduce carryover but do not prove all quota
windows reset or rule out later visibility changes; later dirty prechecks
still abort.

Nominal workload time is 33 minutes **plus** 12 one-minute pre-phase quiet
intervals and at least 12 one-minute post-phase empty windows: about 57 minutes
before probes, drain, inventory-request latency, additional convergence,
provisioning and teardown. The harness deadline is unchanged at 80 minutes,
each phase gets its requested duration plus at most 120 seconds to drain, and
the workflow has an 85-minute measurement-step / 150-minute job bound. There
are no automatic experiment retries. OIDC assertions refresh every four
minutes with a 30-second request deadline; refresh failure cancels the workload
and invalidates the report. Only refresh timestamps, not tokens, are retained.

Each slot makes 12 anonymous Key Vault response-header probes before warmup
and after measurement. They must return 401; timings include connection effects
and are neither pure RTT nor backend processing latency. Inventory and probes
run outside the measured phase. The combined `report.json` retains all bounded
phase timings, inventory counts, barrier durations and partial failure state.
Each phase's `barrier_samples` retains at most 25 elapsed-time/active/deleted
count observations, including reappearances; `barrier_empty_seconds` records
the final uninterrupted observed-empty window. Samples remain in failed
reports, without secret names, tokens or endpoint URLs.
Each slot also retains `authorization_evidence`: at most 64 validated warning
events from the sealed proxy's existing SecretsManager logger (event 5: Entra
token acquisition; event 6: Key Vault authorization). Only the exact logger
header and message shape are accepted, with allowlisted operations, status codes
and bounded request identifiers. Unrelated or malformed text is not exported.
The snapshot exposes rejected-candidate and dropped-event counts, a truncation
flag and whether both process output streams reached EOF. Final publication
requests graceful termination of the last proxy on Linux and drains redirected
output before taking its snapshot (15-second bound; a forced-stop fallback is
reported as failure, not a successful comparison);
earlier publications can have `streams_closed: false`.
Event timestamps are harness observation times, not provider timestamps or
proof of the failed downstream HTTP method. Empty evidence, especially with
unclosed streams or rejected events, does not prove absence of authorization
failures. The capture expects the existing default simple-console format and
does not change logging configuration, sealed runtime bytes or failure policy.
`sealed-inputs.json` records immutable provenance. Both have `promotable: false`;
private harness output, endpoint names, config files and assertions are not
uploaded. The fixture's operation timings describe logical client actions,
including consistency polling and SDK retries, not physical backend requests.
CPU/working-set fields describe only the test harness. Runner geography must
be read from the Actions setup log, not inferred from the backend region.

The report calculates forward/reverse 8/5 ratios, rates pooled by actual
workload-plus-drain duration, per-worker rates, scaling efficiency relative to
the ideal 8/5 ratio, and 5/5 control drift. These are descriptive diagnostics,
not pass thresholds, proof of statistical equivalence or causal attribution.
Even an 8-worker rate above 9/s does not justify a 9/s floor for the committed
5-worker observation. Changes to load shape or the existing 4.5 override require
separate review and comparable canonical evidence. A short diagnostic does not
replace long observation, rotation/rollback proofs or qualification freshness.
Workflow cleanup confirms deletion of the owned resource group and checks that
its vault no longer has a soft-deleted reservation. Immediately before cleanup,
the workflow obtains a fresh Azure CLI OIDC login using the original identity
and subscription, with a five-minute timeout. The harness's projected-assertion
rotation does not refresh the separate Azure CLI session: run `36875986895/1`
completed all measurements but failed cleanup with `AADSTS700024` when that
session reused an expired assertion (#1072).
Both reauthentication and cleanup run after success, failure or cancellation
when the initial Azure login succeeded, including partial provisioning failures.
Cleanup is still attempted if reauthentication fails; neither failure is
suppressed, and report upload remains unconditional. This renewal does not
retry the experiment or make a failed workflow eligible for promotion.

### Controlled DynamoDB crossover

`dynamodb-controlled-crossover.yml` is a **manual-only diagnostic**, restricted
to protected `main` and explicit operator budget acknowledgement. It uses the
current DynamoDB approved candidate and that approval's fixed rollback target,
resolved through the existing sealed-artifact/attestation verifier. The supplied
candidate/prior aggregate digests must match before Azure login or provisioning.
It never rebuilds the selected proxies, updates a ledger, renews evidence or
generates an observation verdict. Expired/unavailable inputs are not bypassed.

One runner provisions one Strong-consistency serverless Cosmos account/database.
The fixed order is **prior, candidate, candidate, prior, prior, prior**: the
first four slots form counterbalanced A/B pairs, and the last two are a
same-runtime A/A control. Every slot restarts its selected sealed proxy on the
same port/configuration, then runs eight workers for a separate 30-second warmup
and five-minute measurement. Each phase reuses the existing strict-observation
CRUD worker, including fresh uniquely named worker tables, strongly consistent
reads, value assertions and table deletion. Warmup counts never enter the
measurement report. Nominal warmup/measurement time is 33 minutes, excluding
startup, probes, drain and resource provisioning/teardown.

The harness checks backend/configuration/AWS-binding digests and proxy liveness
between phases. It aborts subsequent slots on failures, cancellation or binding
drift and retains partial sanitized reports. There is a 45-minute harness
deadline, a 30-minute provisioning-step limit and a 120-minute job limit.
Provisioning and failed experiments are not automatically retried.

Before warmup and after measurement, each slot makes 12 bounded anonymous
Cosmos requests using the existing connectivity helper. These validate an
authentication-denial response and measure response-header latency, including
connection/TLS setup on the first request; **they are not pure network RTT,
backend processing latency or retry counters**. They run outside measured
workload windows. CPU/working-set fields still describe only the test harness.
Runner geography comes from the Actions setup log, not inferred from the backend.

The `diagnostic-dynamodb-crossover-<run>-<attempt>` artifact retains
`report.json`, exact sealed-input provenance and per-slot warmup/measurement
timing sidecars for 90 days. Only whitelisted JSON report paths are uploaded;
private runtime/configuration paths and raw test logs are not uploaded. Reports
are explicitly non-promotable even when all slots complete. The workflow shares
the real-Azure integration concurrency lane, never cancels an existing run,
always attempts teardown after successful Azure login, and uses the nightly
orphan-reaper tags as a backstop. Confirm the owned group is absent independently
before declaring cleanup complete.

Interpret both A/B orders alongside the A/A variation and the operation/time
windows. A disparity that disappears here supports an environmental explanation
but does not prove the original cause. This single bounded run is not a
statistical equivalence test or permission to raise thresholds or promote RC5.
Authorizing another profile, longer/repeated trials or stable promotion remains
a separate operator decision.

### Controlled S3 crossover

This is the minimal follow-up for
[#1062](https://github.com/pedrosakuma/aws2azure/issues/1062), implemented in the
manual-only `s3-controlled-crossover.yml` and
`S3RealAzureCrossoverTests.Exact_sealed_ABBA_and_AA_share_one_runner_and_backend`.
**Implementation and offline validation do not authorize paid gates or dispatch.**
The historical
real-Azure `S3RealAzureRcObservationTests` run `36473482616/1` measured candidate
53.348 versus prior 75.416 logical GetObject/s on separate runners/backends.
A controlled comparison has a concrete purpose: test whether that roughly
29% separation persists when those confounders are held fixed. The DynamoDB
result does not answer it by analogy.

**Fixed inputs.** Reuse the exact sealed RC5 candidate
`bf2274dc6469ce2a8a0c2db4b956391f24c74076`, producer `36280313825/1`,
artifact `10918950988`, aggregate
`sha256:32767e07b62b1e0f1bfa1b05500efa4e008bb93ec2fc6e85f59521f2f443e322`,
and fixed prior `40c1ea996ec8d971dd4bb2aeda5de904d123f5c5`,
producer `36171044613/1`, artifact `10880620175`, aggregate
`sha256:7d8b3c21181f246c8746e39454a5b22a1fc0dc73ee339279e97d6b997e417d6d`.
Pin and verify producer attempt, attestation, ZIP/executable/manifest digests
and diagnostic input eligibility **before provisioning**. Expired canonical
qualification is not silently renewed or bypassed: resolve through the existing
reviewed historical-runtime eligibility path, or stop for review if it rejects
the inputs. Never rebuild a proxy or choose a newer runtime to make the test run.

One job/runner, one dedicated `StorageV2` / `Standard_LRS` account in `eastus2`
using `deploy/realazure/s3-load.bicep`, unchanged shared-key configuration and
AWS binding. Record actual runner region/image/resources separately from the
backend region; unknown geography stays unknown. No other workloads/cohorts
share the diagnostic account. Before each slot, a read-only Azure CLI
`az storage account blob-service-properties show` checks the owned account and
resource group through the management plane, with a 60-second deadline.
Blob/container soft delete and versioning must all be explicitly `false`;
missing, null, malformed or failed reads abort. The Bicep deployment explicitly
disables all three. A separate signed data-plane check confirms blob soft delete
is disabled. Container retention and versioning are not inferred from missing
fields in the data-plane XML. These checks run outside measured workload time.
Use the same SDK build, path-style addressing,
streaming/checksum defaults and `MaxErrorRetry=2` in every arm.

**One six-slot schedule.** Prior, candidate, candidate, prior, prior, prior:
ABBA followed by A/A. Eight workers in every slot, a fresh selected proxy/client
per slot, separate 30-second warmup and 300-second measurement, unique
worker containers for each phase. Use the same fixed diagnostic workload label
and equal payload/metadata lengths in both arms; runtime role belongs in the
report, not in a different-sized payload. Warmup counts never enter measurement.
This is 33 minutes of requested workload, not a billed-runtime estimate.
Limits: 60-minute harness, 65-minute measurement step, 20-minute
provisioning step and 120-minute whole job including cleanup headroom.
No automatic provisioning/experiment retry or additional duration/profile matrix.

**Preserve the observation workload, not merely its operation names.**
The diagnostic reuses `S3RealAzureRcObservationTests.RunWorkerAsync`: one
container created/deleted per worker/phase; each completed object iteration
performs PutObject with the 65,536-character filler plus fixed prefix, two
HeadObject assertions, three GetObject variants (full body, bytes 17..80, and
expected conditional 304), prefix-scoped ListObjectsV2, and two DeleteObject
calls (including idempotent deletion). The main metric counts all three logical
GetObject variants, not full-object downloads or physical Blob requests.
Keep ETag, metadata, payload, range and list-content assertions.

Two source details at `51e4aa9f` are covered by the offline tests: the historical
`LifecycleOperationSchedule` lists only one DeleteObject although the worker
performs two, and legacy catches can swallow tracked worker failures. Historical
captures and the canonical observation schedule remain unchanged. The diagnostic
uses its own accurate schedule and opts into strict worker failure propagation,
without best-effort repair, plus started/completed-iteration accounting.
`S3Crossover.ValidatePhase` requires, after
successful drain, Put/List = N, Head/DeleteObject = 2N, GetObject = 3N and
CreateBucket/DeleteBucket = 8. Expected 304 is successful only after its
assertion; any actual error/throttle, incomplete worker, empty sample set or
binding drift invalidates the comparison and stops later slots.

**Phase isolation.** Check a paginated empty-container baseline before every
phase. Stop new iterations at the requested boundary and allow at most 60
additional seconds to drain, including the normal measured worker deletions.
Then independently confirm the owned account returned to the empty baseline
with a bounded 60-second paginated inventory check and a token independent of
the measurement deadline. Each read uses signed REST with a 15-second HTTP
timeout, one-MiB response limit and bounded opaque-marker pagination. A nonempty,
malformed or failed inventory aborts immediately; there is no repair or polling
to conceal contamination. A fixed 30-second quiet interval and second empty
check precede every phase. Inventory and quiet time are outside throughput;
the post-phase inventory and pre-phase quiet durations are recorded. This adds
six minutes of quiet intervals to the
33 requested workload minutes, excluding inventory, startup, drain and teardown.
Unique containers prevent namespace collisions but do not by
themselves rule out carryover traffic or service-side state.

**Bounded report.** Keep `promotable: false`, exact runtime and
backend/config/binding digests, slot/order, requested and actual workload/drain
durations, completed iterations, logical counts/rates, errors/throttles,
per-operation means/approximate percentiles and bounded time windows. Separate
warmup, measurement, drain and cleanup; aggregate lifecycle rows once, never
sum their duplicate time-window rows. Reuse `OperationTimingDiagnostics`;
its CPU/working-set fields describe the harness, not proxy CPU or peak memory.
The minimal implementation does not add network probes, proxy CPU sampling or
backend latency instrumentation; these remain unavailable, not zero.
SDK/internal retry counts also remain unknown. Upload allowlisted sanitized JSON only, not configurations,
credentials, endpoints, names, payloads or raw logs; retain partial failures.
The report's `stage` identifies the failed management/data-plane settings read,
runtime switch, phase boundary, workload or validation without retaining raw
exception messages. Run `36930800624/1` stopped before warmup with no comparison;
its older report lacked this stage. Offline reproduction confirmed that the old
guard rejected the documented Blob XML because it required a management-plane
container-retention field; the run did not retain the XML to prove that exact
trigger. See the [data-plane response contract](https://learn.microsoft.com/en-us/rest/api/storageservices/get-blob-service-properties)
and [management-plane properties](https://learn.microsoft.com/en-us/rest/api/storagerp/blob-services/get-service-properties?view=rest-storagerp-2023-05-01).

Interpret both candidate/prior pair ratios, pooled count/duration rates and the
separate A/A drift. A stable A/A with a consistent difference in both A/B orders
warrants deeper runtime investigation; comparable A/A drift or disagreement
between orders is inconclusive, not a runtime regression. If the large gap
disappears, report non-reproduction under these conditions, not equivalence or
proof of the historical cause. Predeclare these interpretations rather than
adding a new numeric pass threshold after seeing results.

**Execution prerequisites.** The `RcObservationOffline` tests cover exact order and digests,
counter/denominator invariants, warmup exclusion, conditional-304 handling,
failure/cancellation/timeout propagation, dirty baselines, binding drift and
sanitized partial reports. Review independently and classify paid PR gates
before requesting their separate authorization/budget. The workflow shares
`integration-real-azure` concurrency with `cancel-in-progress: false`,
requires protected `main` and explicit one-run budget confirmation, and uses
owned-resource tags plus scoped teardown. Fresh Azure CLI OIDC login immediately
before cleanup is required (lesson from #1072/#1073); it always attempts cleanup
after partial provisioning/failure/cancellation and independently confirm the
exact owned group is absent. Report upload is unconditional; raw harness logs
stay private. Diagnostic completion, workflow outcome and cleanup outcome are
distinct. None renews qualification/observation or authorizes stable promotion.

## DynamoDB and SQS cohort contracts

Both new producers require the split-cohort mode, actual real backend
configuration, verified candidate/prior executables and the committed 8/8 shape.
They use the existing runtime fixtures, sealed-artifact resolver, immutable
readiness rendezvous and 30-second cancellation-stop ceiling. DynamoDB uses the
existing Strong-consistency serverless Cosmos template; SQS uses the existing
Service Bus template and namespace-default AMQP transport, not the supplementary
REST lane or FIFO. No shipping handler or authentication code is instrumented.

Each process first creates and verifies a separate service canary, bounded to
two minutes, then publishes readiness. The eight worker tables/queues are created
inside the measurement window, as in qualification, not before the barrier.
Each worker owns its unique table/queue; its normal item/message is settled
before the next iteration. Inventories are at most eight worker resources plus
one canary per cohort. A failed observation worker stops instead of accumulating
failed iterations/messages; its recorded failure is never retried into success.
Existing ordinary qualification failure handling is unchanged.

| Profile | Before readiness | Exact-prior restoration on the candidate backend |
|---|---|---|
| DynamoDB CRUD | Create an ACTIVE hash-key table, write a nonce payload/version, verify a strongly consistent read | Read the candidate's persisted value, increment and read its version, delete and verify item absence, delete and verify table absence |
| SQS standard | Create a private queue, send/read the nonce message, explicitly abandon its candidate-owned lock with visibility zero | Receive and settle that persisted message using a fresh prior-owned receipt, send/receive/settle another message, verify no remaining message |

Restoration reuses the original configuration file and AWS binding; the fixtures
rehash the actual config bytes and the coordinator checks backend/config/binding
identity and process liveness before readiness and after the switch. The prior
comes from that profile's committed rollback target, not a source rebuild and
not an assumption that prior sources match across profiles. Cancellation or a
failed state/identity check cannot set restoration `verified`.

The operation-mix policy digest identifies the manifest-ordered operation set,
not equal operation weights. DynamoDB's repeated cycle remains PutItem, GetItem,
UpdateItem, GetItem, DeleteItem, then the qualification worker's second
idempotent DeleteItem completion marker, with 4-KiB payload padding. SQS keeps
512-byte padding and SendMessage, ReceiveMessage (five-second long poll),
GetQueueUrl, bounded ListQueues propagation polling, DeleteMessage. Resource
create/describe/delete and worker drain time remain inside the same stopwatch
denominator as qualification. Representative throughput counts successful
GetItem/ReceiveMessage calls; failure rate uses all tracked logical AWS calls.
SDK retries and the existing ListQueues propagation loop are not independently
counted backend attempts. Canary/setup probes outside the tracked worker chain
do not inflate representative samples.

The operation-mix identity hashes the explicit schedule, not just the unique
operation names. DynamoDB's measured schedule includes both GetItem calls and
both DeleteItem calls. The old `eec0ee…` policy declaration did not reproduce
that worker schedule; the corrected identity is `41147e…`, asserted against
the committed policy and the offline SDK worker trace. This corrects the shape
declaration only: no worker behavior, capacity floor, failure threshold, or
historical evidence is changed. All four committed observation policies are
checked against their harness schedules offline.

The shared coordinator retains existing per-operation diagnostic types and
failure counts even when exact-prior restoration succeeds. Incomplete/cancelled
attempts retain `cohort-capture.json.diagnostics.json` when the harness can write
it, not a synthetic complete observation. A measured failure can retain a
complete failing capture and rollback evidence; its harness/job still fails.
Canary cleanup has a separate ten-second cancellation budget, with always-run
resource-group deletion and the tagged orphan reaper as the backstop after
process loss. No cleanup exception becomes a successful observation.

For SQS, queue deletion runs only in that separate cleanup step, after the
restoration proof. Cleanup success means `DeleteQueue` was acknowledged or the
queue was already absent; it does **not** certify management-plane absence.
`GetQueueUrl` can remain stale-positive after deletion with no proven upper bound
([DeleteQueue gap](../gaps/sqs/DeleteQueue.yaml), #626), so the canary neither
probes for immediate absence nor adds an arbitrary propagation retry window.
A cleanup failure preserves an already verified restoration in raw diagnostics
but still fails the harness/job, with the allowlisted
`canary-cleanup-failed` reason in `harness-diagnostics.json`. Cancellation,
deletion failures, and failed restoration checks are not converted to success.
Offline `RcCrudCohortTests` cover stale-positive reads, deletion failure and
cancellation, already-missing queues, and retained cleanup ownership. This
contract correction does not retroactively accept a failed observation.

Even if the process exits **before readiness**, the always-run composite action
retains `harness-diagnostics.json` in the cohort capture artifact (90 days).
It is explicitly non-promotable: exit status, preparation/measurement stage,
allowlisted reason/exception categories, and known source filenames/line numbers
from at most the final 512 KiB. Raw harness/supervisor logs, exception messages,
arguments, URLs, and private configuration are never copied or printed. Private
logs are deleted with the projected credentials. Missing logs or forced
cancellation retain a bounded diagnostic record, not invented success evidence.

`RcCrudCohortTests` uses stateful offline SDK fakes for both service canaries,
the actual reused worker cycles, corruption, cancellation, binding drift and
failure-preserving restoration. The canonical PR command runs these tests
without Azure or emulators. Executed shell tests cover profile selection,
backend credential export, missing outputs and tagged cohort cleanup; the
existing fake-clock/transport readiness tests also cover all four profiles.
These checks verify implementation contracts, **not live calibration**.

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

The split per-profile observation jobs retain their own prepared
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
- On cancellation, the action writes abort and immediately signals the
  identity-checked owned supervisor: active measurement does not poll the
  readiness abort file. The supervisor forwards TERM to its owned harness
  process group, waits at most 20 seconds, then KILL with at most 5 seconds to
  settle. The stop step has a 30-second monotonic wait ceiling, leaving most
  of the runner's cancellation grace for upload, credential removal, login
  and Azure teardown. The same short TERM/KILL grace applies on the process
  deadline. An unsettled stop fails loudly; interrupted runs record failure,
  never successful restoration. Termination can prevent in-process cleanup
  or evidence publication, so existing always-teardown remains essential.
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
actual UTC start and reported cross-runner start skew. Schema-1 individual cohort
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

Each cohort must cover its full requested duration after its actual start.
The merger emits schema-2 combined captures preserving independent
`cohorts[].measurement_ended_at_utc` and candidate/stable metric capture
timestamps. The aggregate measurement end remains the **maximum**, never the
minimum. Restoration must begin after **candidate** measurement completion and
be verified within the candidate's local capture window; stable measurement may
finish before or after restoration. Stable attribution ends at its own measured
endpoint, not an invented extension through candidate restoration.

Canonical schema-4 evidence retains these per-cohort endpoints in its integrity
digest. UTC comparisons and serialization preserve all seven fractional digits
(100 ns), including a restoration just four ticks after candidate completion.
The strict schema-1 combined-capture path remains supported for historical
synchronous captures and still requires restoration after the aggregate end.
Schema-1 captures with new timing fields, schema-2 captures with absent,
shortened or inconsistent windows, and schema-3 evidence with new timing fields
fail closed. Existing historical schema-3 evidence keeps its original digest
and ordering rules. No old artifact is rewritten or promoted by this change.
Evidence generated in the future, before the observation ends, or after its
reviewed freshness budget remains invalid.

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

The strict schema-v4 YAML model is `RcObservationEvidence` in
`tools/Aws2Azure.GapDocs/RcObservation.cs`. Its top-level fields are:

```yaml
schema_version: 4
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
cohorts: [...] # each includes measurement_ended_at_utc in addition to attributable boundaries
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
does not equal all four approved runtime ledgers' runtime and attestation source.
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
requests deletion of its tagged resource group even on failure. Missing Azure
credentials, missing artifacts, test skips, rejected cleanup operations,
unreadable cleanup state, generation errors, or strict-validation errors fail
closed. Only a resource-group deletion already accepted by Azure but still
pending after the 20-minute confirmation budget produces a warning and a
pending-group job summary rather than failing observation/calibration.
The six-hour reaper remains strict, and operators must verify eventual absence.
This exception does not apply to in-harness canary cleanup or restoration
failures and does not make a historically failed run acceptable. Never copy job
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
