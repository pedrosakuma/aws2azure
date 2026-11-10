# PR #1112 reconstructed validation-evidence reuse review

Status: pending owner approval. This report does not authorize acceptance,
workflow execution, labels, Azure spend, or merge.

## Review result

PR #1112 was reconstructed by merging current policy `main` into a history
that retains the exact successful evidence source as an ancestor. Fresh
`validation-evidence-v1` review inputs were generated from a clean Linux/ext4
checkout using the reviewed tool at policy commit
`46309f6300f36ae0a868019cf33736a34b23586e`.

- Resolved base and merge base:
  `46309f6300f36ae0a868019cf33736a34b23586e`
- Reviewed target:
  `bc333992e2afcef4e1839388baf3495f394a8902`
- Reviewed target tree:
  `29435f58eca7e03e3b168c13c49dfae8508753a2`
- Historical successful source:
  `541bb8853656fb2b614262d03b9026178dc66b5a`
- Target diff SHA256:
  `a6d9ff58076bde4e36934a8ad497beb05c5cb9d1c32acb89d6af4379ad4c9d30`
- Original plan SHA256:
  `38cf8e255ee686fbd9f893991caf6d6ecb71eaad22db375dbae9d3ee613926f4`
- Source-to-target delta SHA256:
  `e4adb87cb14cddaf3bf12662939873beb7072bda042fe2cb96ef883dcc96c2d6`

Git independently confirms the successful source is an ancestor of the
reviewed head. The reconstructed target binds the exact tree above; its
complete content delta from the successful source is enumerated below.

The generated strict plan JSON has SHA256
`520957cc1b76eb17b42ab8e13a292223cea5f13faba7691f3c0f389d1b691ae4`.
The generated review-input JSON has SHA256
`20c1d87e5ea6d7249ae6bbbbe1ef0af83de3d4d5977eb003dfa6c72d52372375`.
The review binding was copied directly from that output and not reconstructed
manually.

## Complete source-to-target delta

The complete delta from successful source to reviewed target changes seven
paths:

- the three existing versioned PR #1112 evidence files;
- `documentation-manifest.json`, `llms.txt`, and `mkdocs.yml`;
- `tests/Aws2Azure.IntegrationTests/S3/S3AwsChunkedTests.cs`.

The integration fixture computes the request body's valid CRC32 trailer for
the successful path and adds a separate checksum-mismatch test expecting
`BadDigest`. No additional shipping code, runtime configuration, workflow,
dependency, gap source, generated capability output, or unit-test
implementation changes occur after the successful source. The merge of policy
main introduces no extra target-tree content beyond those reviewed paths.

## Authoritative run and artifact

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

At `2026-11-10T13:12:45Z`, GitHub reported selected artifact
`13694678250`, `source-validation-real-azure-conformance`, as available and
unexpired through `2026-11-23T17:19:25Z`. A fresh download was 260,280 bytes
and had SHA256
`1a5f0c706645ed83b2a7cc2a4a368adf0b53317a36ac5799a1ab4c5a5211a96b`,
matching GitHub's published digest and the previously retained archive.

The unchanged archive contains 297 files. Its generated summary reports
114 passed, 0 failed, 2 skipped, and 0 not-run scenario outcomes, including
15 passed S3 scenarios and positive real-Azure evidence. Retained TRX counters
are:

| Report | Total | Executed | Passed | Failed | Not executed |
| --- | ---: | ---: | ---: | ---: | ---: |
| `matrix-unit.trx` | 21 | 21 | 21 | 0 | 0 |
| `matrix-integration.trx` | 97 | 95 | 95 | 0 | 2 |
| `conformance-case-evidence.trx` | 6 | 6 | 6 | 0 | 0 |

Important limitation: the retained run does not contain either
`S3AwsChunkedTests` test by name. It establishes successful source behavior
against real Azure, including S3 `PutObject`, while reuse depends on the
independently reviewed non-shipping delta. This is not a new execution,
renewed measurement, current Azure health assessment, or canonical workload
qualification for the target.

Supporting extracted-file hashes are:

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

## Availability, revocation, and validity

GitHub still returned the successful run, attempt, job, and selected artifact.
Repository and PR searches found no later incident, withdrawal, or revocation.
The prior approvals are not reused for this reconstructed binding.

The proposal retains the previously reviewed deadline
`2026-11-16T23:59:59Z`, more than six days before artifact expiry. Any changed
target, base, merge base, diff, tool semantics, artifact availability,
approval, or revocation invalidates it immediately.

Owner approval must explicitly pin the SHA256 of the prospective final
`accepted` decision bytes in the attributable review thread:
<https://github.com/pedrosakuma/aws2azure/pull/1112#issuecomment-6768241159>.
