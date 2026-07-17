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
- **rollback**: deploy the sealed candidate, create/read canary state, replace it
  with the previously approved sealed runtime without changing the backend, and
  verify the same state plus cleanup. A source build of "main" or a config-only
  restart is not rollback evidence.

The real-Azure conformance matrix includes deterministic retry exhaustion and
live restart checks for the initial candidate profiles. Rollback remains an
external deployment action because the repository cannot manufacture a
previously approved immutable artifact.

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
preserves the executable bit. Download, extract, validate, and verify both
attestations with:

```bash
gh run download "$run_id" \
  --repo pedrosakuma/aws2azure \
  --name "$artifact_name" \
  --dir artifacts/sealed-runtime-download
mkdir -p artifacts/sealed-runtime-bundle
tar -xf "artifacts/sealed-runtime-download/$artifact_name.tar" \
  -C artifacts/sealed-runtime-bundle
./eng/sealed-runtime-manifest.sh validate \
  artifacts/sealed-runtime-bundle/sealed-runtime-manifest.json
gh attestation verify \
  artifacts/sealed-runtime-bundle/runtime/Aws2Azure.Proxy \
  --repo pedrosakuma/aws2azure \
  --signer-workflow pedrosakuma/aws2azure/.github/workflows/sealed-runtime.yml \
  --source-ref "$expected_ref" \
  --source-digest "$expected_sha"
gh attestation verify \
  artifacts/sealed-runtime-bundle/sealed-runtime-manifest.json \
  --repo pedrosakuma/aws2azure \
  --signer-workflow pedrosakuma/aws2azure/.github/workflows/sealed-runtime.yml \
  --source-ref "$expected_ref" \
  --source-digest "$expected_sha"
```

Producing and attesting an artifact is necessary but is not approval, workload
qualification, rollback evidence, or a GA claim. The first trusted artifact may
be recorded as `bootstrap`; it has no predecessor and therefore cannot claim
rollback. A later artifact with a distinct complete runtime digest must be
deployed and then successfully rolled back to that bootstrap artifact before
rollback can become qualified. The approval ledger and correctness/load/
rollback consumers are intentionally deferred to dependent work.

## Reproduce

1. Run `integration-real-azure` for the candidate SHA. Retain the
   `real-azure-conformance` artifact containing correctness candidates,
   `runtime-sha256.txt`, and `config-manifest.json`.
2. Dispatch `workload-load-real-azure` for the profile at least the policy's
   `min_distinct_runs` (the schedule intentionally remains Secrets Manager only).
   Each profile uses its own concurrency group and ephemeral resource group and
   uploads `real-azure-workload-load-<profile>` with exactly one
   `load-evidence.json` plus the sealed runtime, candidate-config, and
   producer-config manifests. Do not reuse a run id or mix candidate/config
   digests, regions, SKUs, emulator results, or A/B arms.
   The producer publishes the final evidence filename only after every mandatory
   operation completed, the full CRUD iteration count is non-zero, and the
   operation mix has zero failures. A failed producer artifact must not contain
   a consumable `load-evidence.json`.
3. Complete the rollback action above and include its scenario row in every
   evidence bundle required by the reviewed policy.
4. Dispatch `qualification-real-azure` with the correctness run id, all load run
   ids, and the committed policy path.
5. Download the emitted YAML and CSV. Review blocking findings and the worst-run
   capacity values. Do not average away a breach, raise a threshold, or treat a
   rerun as resolution.
6. After review, commit the immutable YAML below `docs/workloads/evidence/` and
   reference it from the matching workload manifest. The GA evaluator verifies
   profile/operation/scenario/source coherence and freshness.

## Interpretation

`qualified` means every required scenario and blocking capacity/reliability gate
passed across the minimum distinct immutable runs. `candidate` means evidence is
missing, inconsistent, stale, insufficient, or failed; the findings state the
exact reason. External Azure throttling and network-noise signals remain visible
but cannot silently replace a failed backend-capacity gate.

For `s3-basic-object-crud`, `representative-load-throughput` remains the blocking
signal and the 40/s floor is unchanged. It is GetObject completions divided by
the total fixed eight-worker closed-loop CRUD window, not isolated GetObject or
Blob capacity. The latest qualification remains blocked at 37.2526/s.
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
unauthenticated Blob service-list request and requires HTTP 403. It is not
authenticated data-plane health. The sealed producer-config manifest records
the profile, region, backend topology, load shape, and source digests, but does
not yet record runner SKU/image, logical processors, process count, or affinity.
Treat that missing runner/process provenance as a limitation in cross-run
diagnosis; the shared load-evidence schema remains unchanged.
