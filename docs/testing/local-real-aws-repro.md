# Local real-AWS reproduction

This doc is the real-AWS-side companion to
[Real-AWS capture for Tier-3 differential](real-aws-capture.md). It implements
roadmap issue **#842**, a follow-up identified while scoping
[**#839**](https://github.com/pedrosakuma/aws2azure/issues/839) (the
real-Azure equivalent): the real-Azure side of local reproduction is scripted
by `eng/repro-real-azure.sh` (PR
[#841](https://github.com/pedrosakuma/aws2azure/pull/841), not yet merged as
of this writing), but the real-AWS golden-capture flow (`capture-real-aws.yml`,
issue #708) had no equivalent local script.
[`eng/repro-real-aws.sh`](../../eng/repro-real-aws.sh) scripts that flow.

> **Non-goal.** This is purely about local/agent reproducibility. It does not
> change CI cadence, the OIDC trust policy, or the `capture-real-aws.yml`
> workflow itself, and it does not add any new AWS-side proxy behavior.

## How this differs from the real-Azure script

`eng/repro-real-azure.sh` has an "up" step that provisions a whole resource
group + Bicep deployment, because the real-Azure integration tests need
long-lived backends (Cosmos DB, Service Bus, ...) handed to them via
environment variables.

The real-AWS capture flow is architecturally different: it is
**self-provisioning**.
[`RealAwsConformanceCaptureFixture`](../../tests/Aws2Azure.IntegrationTests/Conformance/RealAwsConformanceCaptureTests.cs)
and the `RealAws`-tagged test methods create their own ephemeral
`aws2azure-it-*` S3 bucket / DynamoDB table / Kinesis stream / SNS topic / SQS
queue per test and delete them again on the happy path — exactly what
`capture-real-aws.yml`'s own `dotnet test --filter "Category=RealAws"` step
relies on. There is no Bicep/ARM-equivalent template to deploy, and no
service-specific env vars for the tests to read beyond standard AWS SDK
credential variables.

What a local run *does* need that CI does not is its own credential story: CI
authenticates via GitHub OIDC `AssumeRoleWithWebIdentity`
(`aws-actions/configure-aws-credentials`), which cannot be minted outside
GitHub Actions. `eng/repro-real-aws.sh` therefore focuses entirely on that
gap — provisioning a personal, least-privilege IAM identity and turning it
into the short-lived session credentials the fixture requires — rather than
on infrastructure provisioning.

> **Incident note.** An early draft of this local script was accidentally
> exercised against a real AWS account while its `down` subcommand was still
> being tested, under credentials that turned out to be the account **root
> user**, and using a teardown mode that scanned the whole account by age
> rather than by what that specific local run had created. Nothing was
> ultimately lost, but both gaps were real design flaws, not just bad luck:
> nothing stopped `up`/`down` from running as root, and the default teardown
> had account-wide blast radius. `eng/repro-real-aws.sh` now closes both
> gaps directly — see "Identity safety check" and "Teardown / cleanup" below.

## Prerequisites

- AWS CLI (`aws`) v2, `jq`, and .NET SDK matching the repo (`dotnet build -c
  Release` must already succeed).
- Access, at least once, to an identity in the dedicated real-AWS test account
  with `iam:CreateUser`, `iam:PutUserPolicy`, and `iam:CreateAccessKey`
  permissions (an administrator identity — **not** the IAM user this script
  creates) to run `setup-iam`. If you already have a suitable
  narrowly-scoped IAM role you can assume instead (see `--role-arn` below),
  you can skip `setup-iam` entirely.

## One-time credential setup

Unlike CI's OIDC role, a local run cannot mint a GitHub Actions identity
token, so it needs its own IAM principal. `eng/repro-real-aws.sh setup-iam`
creates one:

```bash
eng/repro-real-aws.sh setup-iam
```

This creates IAM user `aws2azure-local-repro` (override with `--user-name`)
and attaches:

- The **exact** least-privilege policy already documented in
  [Real-AWS capture for Tier-3 differential](real-aws-capture.md#one-time-operator-setup-already-completed),
  extracted verbatim into
  [`eng/aws-least-privilege-policy.json`](../../eng/aws-least-privilege-policy.json)
  so the doc and the script read one shared source of truth instead of two
  copies that could drift. It is scoped to the same `aws2azure-it-*` naming
  contract the capture tests and reaper already use.
- A second, minimal inline policy granting only `sts:GetSessionToken` on the
  user's own identity (see "Why session credentials, not a long-lived key"
  below).

It then creates one long-lived access key and writes it to
`.local/real-aws-iam-user.env` (`chmod 0600`, git-ignored — see `.gitignore`).
Treat that file as a live, long-lived credential. `--force` rotates the key
(deleting the oldest one first, since AWS caps a user at two access keys);
`teardown-iam` removes the access key(s), both inline policies, and the user
itself when you are done with local repro:

```bash
eng/repro-real-aws.sh teardown-iam
```

If the capture matrix ever needs new AWS actions, update
`eng/aws-least-privilege-policy.json` (which `docs/testing/real-aws-capture.md`
now copies from) by **adding only the newly required actions**, exactly as
`real-aws-capture.md` already instructs for the CI role's copy of the same
policy — never introduce a second, drifted policy.

## Why session credentials, not a long-lived key

`RealAwsConformanceCaptureFixture` only initializes its AWS SDK clients when
`AWS_ACCESS_KEY_ID`, `AWS_SECRET_ACCESS_KEY`, **and** `AWS_SESSION_TOKEN` are
all set (it builds a `SessionAWSCredentials`, mirroring the temporary
credentials `aws-actions/configure-aws-credentials` produces from
`AssumeRoleWithWebIdentity` in CI). A plain long-lived IAM user access key
pair — with no session token — will **not** satisfy it; the tests report
`fixture.IsConfigured == false` and every case skips silently. `eng/repro-real-aws.sh up`
mints a genuine session token locally so this matches CI's credential shape:

```bash
eng/repro-real-aws.sh up
```

By default this reads the long-lived key from `.local/real-aws-iam-user.env`
(written by `setup-iam`) and calls `aws sts get-session-token` to mint a
short-lived (1 hour by default; `--duration-seconds` to change it, up to
AWS's 129,600s/36h `get-session-token` cap) `AWS_ACCESS_KEY_ID` /
`AWS_SECRET_ACCESS_KEY` / `AWS_SESSION_TOKEN` triple. If you already have a
suitable local principal (per issue #842's "or an assumed role" option), pass
`--role-arn arn:aws:iam::...:role/your-role` instead, and the script calls
`aws sts assume-role` against it rather than `setup-iam`'s IAM user.

This prints the same style of cost/safety warning as
`eng/repro-real-azure.sh` before doing anything, and writes the session
credentials to a sourceable, `chmod 0600`, git-ignored env file (default
`.local/real-aws.env`), with every value quoted via `printf '%q'` for the
same reason `eng/repro-real-azure.sh`'s env file is: an unquoted value with a
shell metacharacter would be mishandled by `source`.

## Identity safety check

Every subcommand that can mint session credentials or delete resources
(`up`, `down`, `sweep-all-orphans`) starts by calling
`aws sts get-caller-identity` and inspecting the returned `Arn`:

- If the `Arn` ends in `:root` (the account root user), the script **refuses
  to continue** with an error explaining why and how to fix it (run
  `setup-iam` once, then let `up` read that IAM user's credentials, or export
  the least-privilege IAM user's own keys yourself). This is the exact
  guard the incident above was missing. Pass `--allow-root` only if you
  deliberately intend to proceed as root (rare — e.g. a one-off manual
  cleanup where the scoped IAM user has already been torn down).
- If the `Arn` doesn't look like the `setup-iam`-created identity (i.e. does
  not contain `aws2azure`), the script prints a warning but does **not**
  block — you may legitimately be using `--role-arn` with a differently
  named role, or a hand-created IAM user.
- `setup-iam` and `teardown-iam` use the same check in **advisory** mode
  only: they warn (not block) on root, since creating/deleting the IAM user
  itself plausibly requires an administrator identity in the account.

## Run the capture

```bash
source .local/real-aws.env
dotnet build -c Release --nologo
dotnet test tests/Aws2Azure.IntegrationTests/Aws2Azure.IntegrationTests.csproj \
  -c Release --no-build \
  --filter "Category=RealAws"
```

This is the identical `dotnet test` invocation
[`capture-real-aws.yml`](../../.github/workflows/capture-real-aws.yml) uses.
It captures/refreshes `.aws.golden` files under
`tests/Aws2Azure.Conformance/fixtures/**` (consumed by
[`OfflineConformanceDiffRunner`](../../tests/Aws2Azure.Conformance/Diff/OfflineConformanceDiffRunner.cs)).
Review any golden diff the same way the automated
`automation/real-aws-goldens` refresh PR is reviewed in CI before committing
it.

Every case gates on `fixture.IsConfigured`, so with no credentials (or a
missing session token) the whole `RealAws` category skips rather than fails —
you can safely `dotnet test --filter "Category=RealAws"` at any time without
credentials configured.

## Teardown / cleanup

The capture tests create and delete their own `aws2azure-it-*` resources on
the happy path — there is no separate resource group or deployment to
delete, unlike the real-Azure side. The remaining risk is a resource orphaned
by an interrupted or cancelled local run (e.g. `Ctrl-C` mid-test). There are
now two, deliberately distinct, teardown subcommands — **`down` is the safe
default; `sweep-all-orphans` is the dangerous, account-wide one.** This split
exists specifically because of the incident noted above: the original single
`down` reused the CI reaper's account-wide, age-based scan unconditionally,
which is exactly what put resources belonging to *other* runs at risk of
deletion from a local invocation.

### `down` — session-scoped (default, safe)

```bash
eng/repro-real-aws.sh down
```

`aws2azure-it-*` resource names embed their creation time
(`aws2azure-it-<unix-epoch>-<suffix>`). `eng/repro-real-aws.sh up` records the
epoch it started at as `AWS2AZURE_REPRO_SESSION_START` in the same env file it
writes session credentials to (default `.local/real-aws.env`). `down` reads
that value back (or accepts an explicit `--since <epoch|ISO8601>` if you
don't have the env file, e.g. after the shell that ran `up` is long gone),
does a **read-only** listing of S3/DynamoDB/Kinesis/SNS/SQS, keeps only the
`aws2azure-it-*` resources whose embedded epoch is at/after that timestamp,
prints the resulting list for review, and only then deletes each one —
by delegating to the shared
[`cleanup-real-aws-resources.sh`](../../.github/scripts/cleanup-real-aws-resources.sh)
script per matched resource (`NAME_PREFIX=<exact resource name>
MAX_AGE_HOURS=0`), so the nontrivial per-service deletion logic (S3 object
versions, multipart uploads, etc.) is never reimplemented, only scoped down
to one resource at a time.

Because this matches by embedded timestamp rather than a unique per-run ID,
a concurrent run (local or CI) that happened to start *after* your session
began could, in principle, also match. `down` prints the full list of
resources it is about to reap before deleting anything so you can review it;
if anything looks unexpected, abort and cross-check for a concurrent run
before proceeding.

### `sweep-all-orphans` — account-wide, age-based (dangerous, opt-in)

```bash
eng/repro-real-aws.sh sweep-all-orphans --max-age-hours 6
```

This is the old, unscoped `down` behavior, kept as an explicitly separate,
clearly-labeled subcommand: it reuses
`cleanup-real-aws-resources.sh` exactly as
[`real-aws-reaper.yml`](../../.github/workflows/real-aws-reaper.yml) does —
scanning the **entire account** for any `aws2azure-it-*` resource older than
`--max-age-hours` (default 6, matching the reaper) and reaping it, regardless
of which run (local or CI, yours or someone else's) created it. Because of
that blast radius, it requires typing the exact confirmation phrase
`REAP-ALL-ORPHANS` on top of the usual cost-warning confirmation (the global
`--yes` flag does **not** skip this; only the explicit `--force-sweep-all`
flag does, for scripted/CI-adjacent use). Only reach for this if you know
there is no other run concurrently using the account and you specifically
want reaper-equivalent, account-wide behavior — otherwise prefer `down`.

## Cost expectations

Same model as documented in
["Cost and safety model"](real-aws-capture.md#cost-and-safety-model): S3,
DynamoDB, SNS, and SQS each have a permanent always-free tier that
comfortably covers a local capture run. Kinesis is the one exception — it has
no always-free tier, but a short-lived capture run is still expected to cost
only fractions of a cent. There is no equivalent to the real-Azure side's
Cosmos DB provisioning-time cost, since nothing is provisioned ahead of time
here.

## Known sharp edges

None found on the AWS side equivalent to `eng/repro-real-azure.sh`'s two
Azure-specific sharp edges (Windows `az` CLI CRLF under WSL; `;`-unsafe
unquoted values breaking `source`). `eng/repro-real-aws.sh` still defensively
strips `\r` and quotes every captured value with `printf '%q'` in its emitted
env files as cheap insurance, but this has not been observed to matter in
practice for a native `aws` CLI installation. If you do hit an equivalent
issue (e.g. a Windows-native `aws.exe` resolved under WSL), the fix pattern
is identical to the one documented for the Azure script.

The one real sharp edge found on this side was not a CLI encoding quirk but a
process one: nothing stopped an AWS-touching subcommand from running under
ambient root credentials, and the default teardown scanned the whole account
by age instead of by what the local run actually created (see the incident
note above). Both are now closed by the identity safety check and the
session-scoped `down` default, respectively — not worked around, fixed.

## Related documents

- [Real-AWS capture for Tier-3 differential](real-aws-capture.md)
- `eng/repro-real-azure.sh` and `docs/testing/local-real-azure-repro.md` — the
  real-Azure companion script/doc from PR
  [#841](https://github.com/pedrosakuma/aws2azure/pull/841) (link omitted
  above until that PR merges; this doc's structure/tone deliberately mirrors
  it)
- [`eng/repro-real-aws.sh`](../../eng/repro-real-aws.sh)
- [`eng/aws-least-privilege-policy.json`](../../eng/aws-least-privilege-policy.json)
- [`.github/scripts/cleanup-real-aws-resources.sh`](../../.github/scripts/cleanup-real-aws-resources.sh)
- [`RealAwsConformanceCaptureTests.cs`](../../tests/Aws2Azure.IntegrationTests/Conformance/RealAwsConformanceCaptureTests.cs)
- Issues [#708](https://github.com/pedrosakuma/aws2azure/issues/708),
  [#838](https://github.com/pedrosakuma/aws2azure/issues/838),
  [#839](https://github.com/pedrosakuma/aws2azure/issues/839), and
  [#842](https://github.com/pedrosakuma/aws2azure/issues/842)
