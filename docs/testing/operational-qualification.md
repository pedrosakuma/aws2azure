# Operational workload qualification runbook

Operational qualification is separate from emulator regression and from
feature-specific A/B experiments.

## Evidence classes

| Evidence | Used for | Never claims |
|---|---|---|
| Emulator `perf.yml` qualification | Proxy-overhead regression | Azure capacity or workload GA |
| Real-Azure workload load evidence | Profile capacity/reliability under a production-shaped topology | A/B feature causality |
| `perf-real-azure.yml` | CosmosBinary/LSI A/B falsification | Workload qualification |

## Required scenario semantics

- **throttling, timeout, service-unavailable, cancellation, retry-exhaustion**:
  deterministic injection is acceptable when it proves the AWS-native envelope,
  cancellation propagation, and bounded SDK retry behavior. It is not reported
  as a naturally observed Azure incident.
- **concurrency/read-after-write/redelivery**: live Azure when required by the
  profile manifest.
- **restart**: write state through the official AWS SDK, terminate the
  out-of-process proxy, restart the same sealed runtime/config, and read or
  address the pre-existing Azure state.
- **credential-rotation**: keep candidate runtime/config bytes, AWS binding, and
  backend unchanged while overlapping blue/green backend identities. Revoke the
  blue identity's exact backend-scoped role, prove green remains successful,
  and require the drained blue process to return the AWS-native access-denied
  shape within the reviewed propagation budget. Immediately after FIC creation,
  Entra may transiently return AADSTS70021, AADSTS700212, or AADSTS70025; only
  those exact codes on HTTP 400/401 are retried within the five-minute setup
  budget. Other `invalid_client` codes fail immediately.
- **rollback**: deploy the sealed candidate, create/read canary state, replace it
  with the previously approved sealed runtime without changing the backend, and
  verify the same state plus cleanup. A source build of "main" or a config-only
  restart is not rollback evidence.

The load workflow executes rollback after representative load and the other
profile scenarios. It launches the selected candidate bytes first, then the
profile ledger's exact prior bytes on the same endpoint, generated config, AWS
binding, and Azure backend. S3 proves candidate create/read followed by prior
read/delete/absence. Secrets Manager proves candidate create/read followed by
prior read, force-delete, and the profile's Key Vault soft-delete-compatible
absence check. The fixture restores the candidate process before teardown.

## Sealed runtime producer and bootstrap

`sealed-runtime.yml` is the trusted build-once producer for the exact
`linux-x64` Native-AOT bytes used by future correctness, load, and rollback
consumers. It is manual-only and rejects pull-request refs, unprotected refs,
and refs other than `main` or a `vX.Y.Z-rc...` tag. The workflow publishes the
proxy once, copies that same executable and its non-secret runtime JSON files
into one 90-day artifact, validates a source-generated JSON manifest, and
attests both the executable and manifest. No consumer may independently rebuild
the SHA and call that output the same sealed runtime.

The artifact name contains the full 64-hex complete-runtime SHA-256 plus the
GitHub run id and attempt. The manifest also records the repository, source
SHA/ref, RID, executable digest, complete canonical `runtime-sha256.txt` digest,
workflow/run/attempt URLs, and timestamps. Select an artifact by the exact run
id, attempt, and artifact name; a rerun is a distinct producer identity even
when its runtime digest is identical. GitHub artifact upload does not preserve
Unix file modes, so the artifact contains a deterministic tar archive that
preserves the executable bit. Consumers do not use `gh run download` by artifact name alone. The repository
consumer queries the run and artifact APIs, checks the upload digest, safely
extracts both archive layers, validates the manifest, and verifies both subjects
against the expected workflow, ref, source SHA, and run attempt:

```bash
./eng/resolve-sealed-runtime.sh \
 --repository pedrosakuma/aws2azure \
 --run-id "$run_id" \
 --run-attempt "$run_attempt" \
 --expected-sha "$expected_sha" \
 --expected-ref refs/heads/main \
 --profile s3-basic-object-crud \
 --profile-version 1 \
 --role candidate \
 --destination artifacts/private/candidate \
 --identity-output artifacts/candidate-runtime.json
```

Producing and attesting an artifact is necessary but is not approval, workload
qualification, rollback evidence, or a GA claim. The first trusted artifact may
be recorded as `bootstrap`; it has no predecessor and therefore cannot claim
rollback. A later artifact with a distinct complete runtime digest must be
deployed and then successfully rolled back to that bootstrap artifact before
rollback can become qualified. The profile-owned ledger under
`docs/workloads/approved-runtimes/` records this distinction mechanically:
bootstrap may be a rollback baseline but is never promotion eligible. Only the
later distinct candidate, with qualification evidence naming both candidate and
rollback-target identities, may become the first approved runtime. Bootstrap is
allowed only as the prior rollback target; it can never be selected as the
candidate or promoted itself.

## Reproduce

1. Merge the consumer change to protected `main`. A PR SHA cannot have a trusted
   `sealed-runtime.yml` artifact whose source SHA already equals its future merge
   commit, so PR and scheduled source-build paths are explicitly non-qualifying.
2. Dispatch `sealed-runtime` on the new protected `main`. Record its numeric run
   id, run attempt, artifact id/name, upload digest, runtime aggregate digest,
   executable digest, and source SHA. Do not use an older pre-merge seal.
   Keep qualification checked out at that exact SHA for the whole sequence
   (coordinate a short `main` freeze or create an allowed protected RC tag).
3. Dispatch `integration-real-azure` with the matching profile/service and the
   exact candidate producer run id/attempt (artifact id is optional when the run
   is unambiguous). Retain the `real-azure-conformance` artifact.
4. Dispatch `workload-load-real-azure` **three sequential times** for the same
   profile and exact candidate producer run/attempt. Each run resolves the
   profile ledger prior independently and produces one genuine rollback proof.
   Do not reuse a run id or mix candidate/config identities, regions, SKUs,
   emulator results, A/B arms, or source-validation artifacts.
   The producer publishes the final evidence filename only after every mandatory
   operation completed, the full CRUD iteration count is non-zero, and the
   operation mix has zero failures. A failed producer artifact must not contain
   a consumable `load-evidence.json`.
5. Dispatch `qualification-real-azure` with the correctness run id/attempt, the
   three load run ids and aligned attempts, and the committed policy path. The
   workflow accepts only successful manual runs from the expected workflow
   paths, repository, protected ref, checked-out SHA, and exact unexpired
   artifact identities.
6. Download the emitted YAML and CSV. Review blocking findings and the worst-run
   capacity values. Do not average away a breach, raise a threshold, or treat a
   rerun as resolution.
7. After review, commit the immutable YAML below `docs/workloads/evidence/` and
   reference it from the matching workload manifest. The GA evaluator verifies
   profile/operation/scenario/source coherence and freshness.

The bootstrap records currently shared by S3 and Secrets Manager remain
profile-owned. Every load proof records the prior ledger file digest/status and
the exact prior producer, artifact, manifest, executable, and attestation
identities. The candidate and prior aggregate and executable digests must differ.

## Interpretation

`qualified` means every required scenario and blocking capacity/reliability gate
passed across the minimum distinct immutable runs. `candidate` means evidence is
missing, inconsistent, stale, insufficient, or failed; the findings state the
exact reason. External Azure throttling and network-noise signals remain visible
but cannot silently replace a failed backend-capacity gate.

For `s3-basic-object-crud`, `representative-load-throughput` remains the blocking
signal and the 40/s floor is unchanged. It is GetObject completions divided by
the total fixed eight-worker closed-loop CRUD window, not isolated GetObject or
Blob capacity. The qualification committed for the GA profile passed at
268.6159/s; use the immutable evidence artifact rather than this explanatory
number for release decisions.
`crud-iterations-per-sec` counts only iterations that complete PutObject, both
HeadObject calls, all three GetObject variants, ListObjectsV2, the initial
DeleteObject, and the idempotent DeleteObject. `aws-operations-per-sec` counts
all successful AWS SDK calls in the operation mix per load-window second.
Per-operation throughput uses the same window; per-operation p95/p99 uses all
attempt latencies. Qualification selects the minimum throughput and maximum
latency across runs, making the closed-loop controlling operation visible.
These report-only diagnostics localize a cause; they cannot justify changing a
threshold by themselves.

The S3 connectivity signal measures response-header latency for an intentionally
unauthenticated Blob service-list request. It accepts HTTP 403 only with a known
authentication-denial error code, or HTTP 409 only with the exact
`PublicAccessNotPermitted` error code returned when account-level public access
is disabled. Both are diagnostic network-noise connectivity responses, not
workload success, authenticated data-plane health, or Blob capacity. The sealed
producer-config manifest records the profile, region, backend topology, load
shape, sealed candidate/prior identity digests, ledger source, and qualification
implementation digests, but does not yet record runner SKU/image, logical
processors, process count, or affinity. Treat that missing runner/process
provenance as a limitation in cross-run diagnosis.
