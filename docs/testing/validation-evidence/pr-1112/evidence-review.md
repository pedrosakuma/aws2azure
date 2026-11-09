# PR #1112 validation-evidence reuse review

Status: pending owner approval. This report does not authorize acceptance,
workflow execution, labels, or Azure spend.

## Reviewed binding

- Repository: `pedrosakuma/aws2azure`
- PR: `#1112`
- Reviewed base and merge base:
  `316df3187bda86c01aea23f881842d0e6b5d6ce3`
- Historical successful source:
  `541bb8853656fb2b614262d03b9026178dc66b5a`
- Reviewed target:
  `b7ac40c20ca6167a8748f6716d542566230852d6`
- Tool checkout:
  `316df3187bda86c01aea23f881842d0e6b5d6ce3`
- Tool version: `validation-evidence-v1`
- Target diff SHA256:
  `724bc622f9a3e2d7582ddeaa0f8244f17064a0540d121abc1b9cebbe746140a2`
- Original plan SHA256:
  `38cf8e255ee686fbd9f893991caf6d6ecb71eaad22db375dbae9d3ee613926f4`
- Source-to-target delta SHA256:
  `ddd8d6b15b4b9b4e5aba861c64c4214ae19a688634ec7c1a22f4be5fd697d3c7`

The reviewed `ChangeAwareValidation` tool was built from the policy checkout
and run from a clean Linux checkout of the exact target. The complete
source-to-target Git delta changes only
`tests/Aws2Azure.IntegrationTests/S3/S3AwsChunkedTests.cs`: 51 insertions and
5 deletions. It replaces the placeholder CRC32 trailer in the successful-path
fixture with a CRC32 computed from the request body and adds a separate
checksum-mismatch test that expects `BadDigest`. No shipping code,
configuration, workflow, gap source, generated output, or runtime dependency
changes between the successful source and reviewed target.

## Authoritative run

- Run: `42660882106`, attempt `1`
- URL: <https://github.com/pedrosakuma/aws2azure/actions/runs/42660882106>
- Workflow: `.github/workflows/integration-real-azure.yml`
- Event: `pull_request`
- Head SHA: `541bb8853656fb2b614262d03b9026178dc66b5a`
- Status/conclusion: `completed` / `success`
- Created/started: `2026-11-09T17:03:34Z`
- Completed/updated: `2026-11-09T17:33:21Z`
- Job `real-azure`: `completed` / `success`,
  `2026-11-09T17:03:37Z` through `2026-11-09T17:33:20Z`

GitHub reported three available, unexpired artifacts. Each downloaded ZIP
matched GitHub's published digest over the actual downloaded bytes:

| Artifact | ID | Expiry | ZIP SHA256 |
| --- | ---: | --- | --- |
| `source-validation-real-azure-conformance` | `13694678250` | `2026-11-23T17:19:25Z` | `1a5f0c706645ed83b2a7cc2a4a368adf0b53317a36ac5799a1ab4c5a5211a96b` |
| `real-azure-canonical-evidence` | `13694538524` | `2027-02-07T17:03:34Z` | `042055f686c373dea5585a6ed2109e281f6b2ea9147263ae90a069599d326ae8` |
| `real-azure-divergences` | `13694443555` | `2027-02-07T17:03:34Z` | `1bd27a246c145c7ce2aefd8fb4dab793b4984291f42f39996829dd2892b15ee4` |

The decision binds the source-validation archive because it retains the run
reports, TRX files, conformance evidence, and canonical-case evidence together.
The ZIP is retained outside Git and must be supplied unchanged to the local
evidence package before validation or use.

## Archive inspection

The source-validation archive contained 297 files. Its generated summary
reported 114 passed, 0 failed, 2 skipped, and 0 not-run scenario outcomes,
including 15 passed S3 scenarios and positive real-Azure evidence. Retained
TRX counters were:

| Report | Total | Executed | Passed | Failed | Not executed |
| --- | ---: | ---: | ---: | ---: | ---: |
| `matrix-unit.trx` | 21 | 21 | 21 | 0 | 0 |
| `matrix-integration.trx` | 97 | 95 | 95 | 0 | 2 |
| `conformance-case-evidence.trx` | 6 | 6 | 6 | 0 | 0 |

Important limitation: the retained run does not contain either
`S3AwsChunkedTests` test by name. It establishes successful source behavior
against real Azure, including S3 `PutObject`, while reuse depends on the
independently reviewed fact that the later delta is test-fixture-only. This is
not a new execution, renewed measurement, current Azure health assessment, or
canonical workload qualification for the target.

Supporting extracted-file hashes:

- `real-azure-conformance/summary.md`:
  `6c50dcc316976197645111cb0f4b4b8425d88cca8300875554b01c3ba9082199`
- `real-azure-conformance/real-azure-evidence.json`:
  `2138b5828a34e1d222e4fe7f253b7f40f3788cbf83756f90f2ee4558d7bc55c0`
- `real-azure/matrix-integration.trx`:
  `ee9057389cebe70f1df8c158a350a395cacb2b2dcd78d1a16bf5856231b32a54`
- `real-azure/matrix-unit.trx`:
  `0de1c44ac90938eab7164bb7fdf93d00d4fa3dbb37f6d7d4d9f02033e80c7a03`
- `real-azure/conformance-case-evidence.trx`:
  `c8039f837ac63612da5bd96f4ccead60a1a0d14ce15d4612320212875bd47a53`

## Availability and revocation review

At `2026-11-09T19:36:27Z`, GitHub still returned the run, successful attempt,
job, and all three artifacts as available and unexpired. Repository issue/PR
searches for the run ID, selected artifact ID, source SHA, and revocation terms,
plus the PR conversation and review threads, found no withdrawal, incident, or
revocation. The latest protected record and approval must be checked again
before every use or merge; this snapshot cannot reveal a later deletion or
revocation.

## Proposed validity window

The proposal expires at `2026-11-16T23:59:59Z`, approximately one week after
review and more than six days before the selected archive expires. The short
window is justified by the same-day successful source run, the exact
test-fixture-only delta, and the need to finish the current PR stack without
claiming renewed Azure validation. A changed target, base, merge base, diff,
tool semantics, artifact availability, approval, or revocation invalidates the
proposal immediately.

Owner approval must explicitly pin the SHA256 of the prospective final
`accepted` decision bytes in the attributable review thread:
<https://github.com/pedrosakuma/aws2azure/pull/1112#issuecomment-6749513917>.
