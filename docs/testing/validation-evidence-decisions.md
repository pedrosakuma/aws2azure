# Explicit PR validation evidence decisions

Policy/verifier `validation-evidence-v1` implements [#1087](https://github.com/pedrosakuma/aws2azure/issues/1087).
Without an explicit decision, ChangeAwareValidation remains the strict,
path-based schema-1 classifier. It never contacts Azure or GitHub, dispatches
workflows, modifies labels/checks, or approves a PR.

The coordinator may accept **only `real-azure`** using one of:

* `offline-covered`: reviewed deterministic evidence covers the particular
  changed behavior; this is explicitly **not live Azure validation**.
* `reuse`: an identified historical successful integration-real-azure run and
  available archive bytes cover the unchanged behavior. Review the complete
  source-to-target delta. This is not a rerun, renewed measurement, current
  Azure health assessment, or canonical workload qualification.

This is a narrow human decision, not a path whitelist or `--skip` switch.
Every other required gate remains required. A production change cannot inherit
an old record: exact target, base, merge-base, full diff and original classifier
output must match. A new approval of a different target is a new human policy
decision, not authorization conferred by this tool.

## Trust boundary and review procedure

The owner independently verifies the run/report, actual test coverage,
artifact availability, repository, source, workflow, attempt, conclusion,
timestamps, scope and any revocation. Save that assessment as a metadata
snapshot and retain the actual report/archive bytes. A self-reported `pass` or
`success` string is **not provenance verification**. The tool verifies local
byte hashes and structural consistency only; it cannot authenticate GitHub
metadata, the reviewer, an approval comment, or a downloaded archive's contents.
The output always says `liveProvenanceVerified: false`, `newlyExecuted: false`.

Keep records on a separately reviewed policy branch/protected main, outside
the target worktree. Review and approve the **final record SHA256** in an
attributable repository discussion. The coordinator obtains that digest from
the trusted approval, not by hashing an arbitrary submitted record and calling
that approval. The external pin covers rationale, reviewer, references,
validity window, source binding and evidence snapshot digest; the snapshot
in turn pins report/archive bytes. It is an integrity pin, not a signature.
Anyone able to choose both a new record and its "approved" pin can describe a
new acceptance; organizational review, not cryptography, establishes authority.

Before every use/merge, the owner must check the latest protected record and
approval, withdrawals, incidents and live artifact availability. An old local
copy cannot reveal a later remote revocation or deletion. Do not use an old
snapshot to conceal either. A fresh snapshot/review requires a new record and
approved digest. There is no universal evidence TTL: the reviewer must justify
`validUntilUtc` for this specific decision; the validator enforces it and
artifact expiry at invocation. It rejects locally revoked evidence, absent,
empty, changed or expired artifacts, incomplete metadata and failed results.

Pending/blocked/unknown decisions are errors, never a success-shaped fallback.
Explicit errors exit **2**, emit no plan on stdout, and leave default policy
unchanged. No missing evidence can turn into "offline" automatically.

## Exact invocation, including an older frozen target

Build the reviewed tool in the **policy checkout**, not the older target.
The CLI classifies the process working directory. Using its built DLL avoids
build/restore side effects in the target:

```bash
policy=/absolute/path/to/reviewed-policy-checkout
target=/absolute/path/to/frozen-target-checkout
dotnet build "$policy/tools/Aws2Azure.ChangeAwareValidation" -c Release
tool="$policy/tools/Aws2Azure.ChangeAwareValidation/bin/Release/net10.0/Aws2Azure.ChangeAwareValidation.dll"
cd "$target"

# Default strict plan (fetch current main first).
dotnet "$tool" --base main --pretty

# Binding proposal only: no approval, label removal or execution.
dotnet "$tool" --base main --review-inputs --pretty
# Reuse additionally binds the complete historical-source -> target delta:
dotnet "$tool" --base main --review-inputs \
  --evidence-source 04f0ba585f58714cd863e220251ffbf9aad06ae8 --pretty

# After independent review of the final record and evidence:
dotnet "$tool" --base main \
  --decision "$policy/docs/testing/validation-evidence/pr-1017.json" \
  --decision-sha256 <SHA256-from-owner-approval> --pretty
```

`--base main` resolves `origin/main` when available. For an explicitly reviewed
base, use its full commit SHA with `--base`; the owner must separately confirm
that this is still the correct PR base before merge. Do not pin an obsolete
base simply to avoid re-review. A rebase, changed head, changed resolved base,
changed merge-base, dirty index/worktree, untracked file, binary edit, file
mode change, rename or deletion invalidates acceptance. Sparse/skip-worktree
and assume-unchanged index entries are unsupported. Ignored untracked build
outputs are not source inputs; tracked ignored files remain covered. Keep all
source/evidence inputs versioned or explicitly digest-bound.

The content fingerprint uses binary/full-index Git diffs, not `patch-id` or
path lists (whitespace remains significant), with fixed diff formatting.
Full commit identities bind complete trees and parentage. The original plan
digest binds the complete path-derived plan, including reasons and policy.
`toolVersion` binds verifier semantics; changes to those semantics must bump
the constant and require re-review. Record the trusted policy checkout SHA
alongside the approval, since the version string is not executable attestation.
No concurrent mutation of the target, record or evidence store is supported.

The schema-2 accepted plan retains `gates[].status: required`, reasons,
commands and labels unchanged. Its `evidenceAcceptance.disposition` is
`accepted` (offline) or `evidence-reused`; only `run-real-azure` is excluded
from the **effective** `requiredLabels`. Apply all those remaining labels.
Archive the plan with the decision/evidence review. Existing GitHub required
checks and branch protection still apply: this tool does not synthesize green
checks. CI does not currently invoke it. Any label/execution needs separate
operator authorization, especially paid workflows.

## Record schemas

JSON is case-sensitive and strict: all shown properties are required,
including explicit nullable fields; unknown/duplicate properties are rejected.
Digests are lowercase SHA256 hex. Git identities are full commit SHAs.
Dates are ISO-8601 offsets (use UTC). Relative evidence paths resolve from the
containing JSON file; absolute paths and `..` are rejected.

Decision schema 1 (start `pending`; copy the **entire** `reviewBinding` from
`--review-inputs` and the original real-azure `reasons`, never manufacture them):

```json
{
  "schemaVersion": 1,
  "status": "pending",
  "gate": "real-azure",
  "mode": "offline-covered",
  "binding": {
    "toolVersion": "validation-evidence-v1",
    "baseCommit": "<resolved base>",
    "mergeBase": "<merge base>",
    "headCommit": "<reviewed target>",
    "diffSha256": "<merge-base to target binary diff SHA256>",
    "originalPlanSha256": "<original path-only plan SHA256>",
    "evidenceSourceCommit": null,
    "evidenceDeltaSha256": null
  },
  "originalReasons": ["<exact original required gate reasons>"],
  "rationale": "<coverage, limitations, scope and validity-window justification>",
  "approvedBy": "<attributable owner>",
  "approvalReference": "https://github.com/pedrosakuma/aws2azure/pull/1086#issuecomment-5961974706",
  "reviewedAtUtc": "<actual final review time>",
  "validUntilUtc": "<risk-justified deadline>",
  "evidencePath": "pr-1086/evidence.json",
  "evidenceSha256": "<snapshot file SHA256>"
}
```

For `reuse`, both evidence source binding fields are non-null; the source
must be an ancestor of the exact reviewed target. Snapshot schema 1:

```json
{
  "schemaVersion": 1,
  "kind": "offline-covered",
  "repository": "pedrosakuma/aws2azure",
  "sourceCommit": "<exact target for offline, original successful source for reuse>",
  "scope": "<what the evidence establishes and does not establish>",
  "verifiedBy": "<human who inspected actual evidence>",
  "verificationReference": "https://github.com/pedrosakuma/aws2azure/issues/1087#<review>",
  "verifiedAtUtc": "<actual verification time, before final approval>",
  "revoked": false,
  "offline": {
    "verifier": "<test script or harness>",
    "verifierVersion": "<immutable verifier source SHA or version>",
    "command": "<actual command>",
    "result": "pass",
    "report": { "path": "report.txt", "sha256": "<report byte SHA256>" }
  },
  "run": null
}
```

For reuse, set `kind: reuse`, `offline: null`, and `run` to:

```json
{
  "runId": 37054680583,
  "attempt": 1,
  "workflowPath": ".github/workflows/integration-real-azure.yml",
  "event": "pull_request",
  "status": "completed",
  "conclusion": "success",
  "createdAtUtc": "2026-10-02T19:31:35Z",
  "completedAtUtc": "2026-10-02T20:00:17Z",
  "artifacts": [{
    "id": 11248732839,
    "name": "real-azure-canonical-evidence",
    "expiresAtUtc": "2026-12-31T19:31:35Z",
    "expired": false,
    "archive": {
      "path": "real-azure-canonical-evidence.zip",
      "sha256": "b76a00d1ba2bc471b967501b2fb89539bd4aef63951f6dc65f79afa3a7cfb7bb"
    }
  }]
}
```

Retain archives in the reviewed evidence package, not necessarily in Git.
The final decision/snapshot and their digest approval must be versioned.
Review archive content and authoritative upload digest before authoring the
snapshot; the tool does not download, decompress or interpret it. Do not
equate `source-validation-real-azure-conformance` with canonical workload
qualification.

## Frozen cases and merge order

No accepted record is issued by this implementation. Versioned **pending**
proposals and evidence snapshots now exist at the paths below, with the
coordinator's exact-head offline report retained in the #1086 package. The
rationale approvals below predate the final bindings/metadata package and must
not be misrepresented as approvals of an as-yet-unreviewed final digest.
`approvedBy` explicitly says owner approval is pending, and `reviewedAtUtc`
records proposal/evidence review, not completed acceptance. The proposed
deadline `2026-10-03T23:59:59Z` bounds this particular frozen maintenance wave
and is not a universal evidence TTL.

| Record/package location to finalize | Exact target | Decision |
| --- | --- | --- |
| `docs/testing/validation-evidence/pr-1017.json` and `pr-1017/evidence.json` | `6790ab6483a84bf58c7673a321d6db38da703191` | Reuse run `37054680583/1` at `04f0ba585f58714cd863e220251ffbf9aad06ae8`; only subsequent change is the four-line immutable-SHA assertion in `eng/test-rc-observation-cleanup.py`. [Operator rationale](https://github.com/pedrosakuma/aws2azure/pull/1017#issuecomment-5961974861). |
| `docs/testing/validation-evidence/pr-1086.json` and `pr-1086/evidence.json` | `eb03638bd0de4d2204c04f18ed05423f2eafc4ae` | Offline event-selection expression tests, not live Azure. Reviewed base `69fec38ab9201c17721ca5697a1ec53cb082d258`. [Operator rationale](https://github.com/pedrosakuma/aws2azure/pull/1086#issuecomment-5961974706). |

The second #1017 artifact reported during operator review was
`11248617803`, `source-validation-real-azure-conformance`, SHA256
`6c62895bbc71c3840540f78e9e026dec7bf0ba66312142efc3db0a33dc161df2`,
expiry `2026-10-16T19:46:31Z`. These are historical identities, not a claim
that archives remain available now; verify and retain the bytes before approval.

The committed #1017 proposal uses **only artifact `11248617803`**, whose ZIP
was independently downloaded and digest-verified by the coordinator. It does
not use the canonical artifact illustrated in the generic schema above.
The reviewed source-validation results are 21/21 matrix-unit tests passed,
97 total matrix-integration tests with **95 executed, 95 passed, 0 failed**,
and 6/6 conformance-case-evidence tests passed. These remain source-validation
records, not renewed canonical qualification. The #1086 report records 10/10
offline authorization tests passed at the exact frozen head. See the
[evidence review](https://github.com/pedrosakuma/aws2azure/issues/1087#issuecomment-5962352578).

To assemble a local package without dirtying either frozen target or committing
the ZIP, copy the versioned proposal tree to a separate evidence directory:

```bash
# Run from the policy checkout. Keep this directory outside the frozen target.
package="$PWD/tools/Aws2Azure.ChangeAwareValidation/bin/evidence-package"
mkdir -p "$package"
cp -R docs/testing/validation-evidence/. "$package/"
cp /absolute/path/to/verified/reuse-azure-source-validation.zip \
  "$package/pr-1017/source-validation-real-azure-conformance.zip"
sha256sum "$package/pr-1017/source-validation-real-azure-conformance.zip"
# Expected: 6c62895bbc71c3840540f78e9e026dec7bf0ba66312142efc3db0a33dc161df2
```

Supply `--decision "$package/pr-1017.json"` (or `pr-1086.json`) from the frozen
target cwd with the matching externally approved digest. The current pending
proposals deliberately exit 2 even with their correct proposal hashes.
Owner acceptance requires finalizing the versioned record's status, actual
approver, final review timestamp/reference and rationale, then approving the
SHA256 of those **final bytes**. Recopy the finalized records unchanged into
the package; proposal digests cannot authorize the later accepted bytes.
Do not change status in an unreviewed local copy to make validation pass.

The parent/coordinator independently reviews this policy PR, verifies final
evidence, supplies/versions decisions for the exact frozen targets and records
their approved digests. Keep #1017/#1086 frozen and open until that happens.
Policy may merge first, or the trusted reviewed tool may run from its separate
checkout against frozen targets. Recheck the actual base before each merge;
a changed `origin/main` means a new binding review, not copying an old hash.
Never amend a target to include its own approval digest or ignore dirty source
to solve a circular hash. No Azure run, paid label or branch-protection change
is authorized by this procedure.
