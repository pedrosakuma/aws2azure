using System.Text.RegularExpressions;

namespace Aws2Azure.UnitTests.GapDocs;

public sealed class RcObservationWorkflowTests
{
    private static readonly string Workflow = File.ReadAllText(Path.Combine(
        FindRepositoryRoot(),
        ".github",
        "workflows",
        "rc-observation-real-azure.yml"));

    [Fact]
    public void Workflow_is_manual_and_selects_only_committed_profiles()
    {
        Assert.Contains("workflow_dispatch:", Workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("pull_request:", Workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("schedule:", Workflow, StringComparison.Ordinal);
        Assert.Contains(
            "profiles='[\"s3-basic-object-crud\",\"secretsmanager-basic-lifecycle\"]'",
            Workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "s3-basic-object-crud|secretsmanager-basic-lifecycle)",
            Workflow,
            StringComparison.Ordinal);
        Assert.DoesNotContain("candidate_run_id:", Workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("candidate_artifact_id:", Workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void Workflow_requires_protected_main_and_enforces_policy_window()
    {
        Assert.Contains(
            "[ \"$REF\" != refs/heads/main ]",
            Workflow,
            StringComparison.Ordinal);
        Assert.Contains("[ \"$REF_PROTECTED\" != true ]", Workflow, StringComparison.Ordinal);
        Assert.Contains("[ \"$WINDOW_MINUTES\" -lt 60 ]", Workflow, StringComparison.Ordinal);
        Assert.Contains("[ \"$WINDOW_MINUTES\" -gt 180 ]", Workflow, StringComparison.Ordinal);
        Assert.Contains(
            "docs/workloads/observation/$PROFILE.yaml",
            Workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "[ \"${#RELEASE_CANDIDATE_ID}\" -gt 128 ]",
            Workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "-rc\\.[1-9][0-9]*$",
            Workflow,
            StringComparison.Ordinal);
        Assert.Contains("candidate_source_sha:", Workflow, StringComparison.Ordinal);
        Assert.Contains(
            "archive_workflow_source_sha:",
            Workflow,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "release_candidate_manifest_digest:",
            Workflow,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Workflow_has_non_promotable_secrets_calibration_branch()
    {
        Assert.Contains("mode:", Workflow, StringComparison.Ordinal);
        Assert.Contains("secretsmanager-calibration", Workflow, StringComparison.Ordinal);
        Assert.Contains("calibration_duration_minutes:", Workflow, StringComparison.Ordinal);
        Assert.Contains("calibration_candidate_concurrency:", Workflow, StringComparison.Ordinal);
        Assert.Contains("calibration_stable_concurrency:", Workflow, StringComparison.Ordinal);
        Assert.Contains(
            "[ \"$MODE\" = secretsmanager-calibration ] &&",
            Workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "Secrets calibration must dispatch only profile=secretsmanager-basic-lifecycle",
            Workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "AWS2AZURE_RC_CALIBRATION_REPORT_PATH=\"$CAPTURE_ROOT/calibration-report.json\"",
            Workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "validate-rc-calibration-report",
            Workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "real-azure-rc-calibration-${{ matrix.profile }}-run-",
            Workflow,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Calibration_branch_cannot_upload_observation_evidence_or_receipts()
    {
        Assert.Contains(
            "if: always() && env.MODE == 'observation'",
            Workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "env.MODE == 'observation' &&\n          steps.observe.outcome == 'success'",
            Workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "env.MODE == 'observation' && steps.strict_validate.outcome == 'success'",
            Workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "env.MODE == 'observation' && steps.evidence_upload.outcome == 'success'",
            Workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "[ \"${{ steps.calibration_upload.outcome }}\" != success ]",
            Workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "Promotable observation evidence: \\`false\\`",
            Workflow,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Workflow_rejects_archive_and_ghcr_sources_outside_protected_main_history()
    {
        Assert.Contains(
            "\"/repos/$GITHUB_REPOSITORY/branches/main\"",
            Workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "select(.name == \"main\" and .protected == true)",
            Workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "for producer in archive ghcr; do",
            Workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"/repos/$GITHUB_REPOSITORY/compare/$producer_sha...$main_sha\"",
            Workflow,
            StringComparison.Ordinal);
        Assert.Equal(
            1,
            CountOccurrences(
                Workflow,
                "python3 eng/release-candidate-image.py validate-protected-main"));
        Assert.Contains(
            "--main-branch-json \"$main_branch_json\"",
            Workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "--main-compare-json \"$compare_json\"",
            Workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "--source-sha \"$producer_sha\"",
            Workflow,
            StringComparison.Ordinal);
        Assert.True(
            Workflow.IndexOf(
                "\"/repos/$GITHUB_REPOSITORY/branches/main\"",
                StringComparison.Ordinal) <
            Workflow.IndexOf(
                "\"/repos/$GITHUB_REPOSITORY/compare/$producer_sha...$main_sha\"",
                StringComparison.Ordinal));
    }

    [Fact]
    public void Workflow_binds_exact_ghcr_inputs_and_derives_identity_without_observations()
    {
        Assert.Contains("ghcr_workflow_source_sha:", Workflow, StringComparison.Ordinal);
        Assert.Contains("ghcr_artifact_digest:", Workflow, StringComparison.Ordinal);
        Assert.Contains(
            "--workflow \".github/workflows/release-candidate-image.yml\"",
            Workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "python3 eng/release-candidate-image.py validate-ghcr-input",
            Workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "gh attestation verify \"$ghcr_inputs\"",
            Workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            ".archive_input.content_digest == $archive[0].content_digest",
            Workflow,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            ".archive_input.producer == $archive[0].producer",
            Workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "python3 eng/release-candidate-manifest.py identity",
            Workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "python3 eng/release-candidate-manifest.py validate-identity",
            Workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "--ghcr-selection \"$CAPTURE_ROOT/ghcr-input-selection.json\"",
            Workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "--identity-selection \"$CAPTURE_ROOT/canonical-identity-selection.json\"",
            Workflow,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Workflow_resolves_and_attests_the_exact_rc_archive_inputs()
    {
        Assert.Contains("archive_run_id:", Workflow, StringComparison.Ordinal);
        Assert.Contains("archive_run_attempt:", Workflow, StringComparison.Ordinal);
        Assert.Contains("archive_artifact_id:", Workflow, StringComparison.Ordinal);
        Assert.Contains("archive_artifact_name:", Workflow, StringComparison.Ordinal);
        Assert.Contains(
            "./eng/download-qualified-run-artifact.sh \\",
            Workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "--workflow \".github/workflows/release-candidate.yml\"",
            Workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "--expected-sha \"$ARCHIVE_WORKFLOW_SOURCE_SHA\"",
            Workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "--expected-ref refs/heads/main",
            Workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            ".producer.source_sha == env.ARCHIVE_WORKFLOW_SOURCE_SHA",
            Workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "python3 eng/release-candidate-inputs.py validate",
            Workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            ".pending_interfaces.observation_evidence.issue == 582",
            Workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "gh attestation verify \"$archive_inputs\"",
            Workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            ".runInvocationURI == $attempt_uri",
            Workflow,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "expected_workflow_ref=",
            Workflow,
            StringComparison.Ordinal);
        Assert.Equal(
            3,
            CountOccurrences(
                Workflow,
                "githubWorkflowRef ==\n                    $source_ref"));
        Assert.Equal(
            3,
            CountOccurrences(
                Workflow,
                "--signer-workflow \"$GITHUB_REPOSITORY/.github/workflows/"));
        Assert.Contains(
            "archive-input-selection.json",
            Workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"/repos/$GITHUB_REPOSITORY/git/ref/tags/$RELEASE_CANDIDATE_ID\"",
            Workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "[ \"$tag_object_sha\" != \"$CANDIDATE_SOURCE_SHA\" ]",
            Workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "--archive-selection \"$CAPTURE_ROOT/archive-input-selection.json\"",
            Workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "--approved-runtime \"$CAPTURE_ROOT/current-approved-runtime.json\"",
            Workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            ".approved_ledger_source",
            Workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "aws2azure-rc-archives-${RELEASE_CANDIDATE_ID}-${content_digest#sha256:}" +
            "-run-${ARCHIVE_RUN_ID}-attempt-${ARCHIVE_RUN_ATTEMPT}",
            Workflow,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Workflow_resolves_approved_candidate_and_exact_prior()
    {
        Assert.Contains(
            "export-approved-runtime \\",
            Workflow,
            StringComparison.Ordinal);
        Assert.Contains("--rollback-target \\", Workflow, StringComparison.Ordinal);
        Assert.Contains("--candidate \"$candidate_identity\"", Workflow, StringComparison.Ordinal);
        Assert.Contains("--ledger-json \"$current_ledger\"", Workflow, StringComparison.Ordinal);
        Assert.Equal(
            2,
            CountOccurrences(Workflow, "./eng/resolve-sealed-runtime.sh \\"));
        Assert.Contains("--role candidate \\", Workflow, StringComparison.Ordinal);
        Assert.Contains("--role prior \\", Workflow, StringComparison.Ordinal);
        Assert.Contains(
            "[ \"$candidate_sha\" != \"$CANDIDATE_SOURCE_SHA\" ]",
            Workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "[ \"$candidate_runtime\" = \"$prior_runtime\" ]",
            Workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "AWS2AZURE_SEALED_RUNTIME_MODE=rollback",
            Workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "AWS2AZURE_QUALIFICATION_SHA=$candidate_sha",
            Workflow,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Workflow_captures_both_services_and_restoration_without_success_skip()
    {
        Assert.Contains("TEST_FILTER=Category=S3RcObservation", Workflow, StringComparison.Ordinal);
        Assert.Contains(
            "TEST_FILTER=Category=SecretsManagerRcObservation",
            Workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "deploy/realazure/s3-load.bicep",
            Workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "deploy/realazure/secretsmanager-load.bicep",
            Workflow,
            StringComparison.Ordinal);
        Assert.Contains(".restoration.verified == true", Workflow, StringComparison.Ordinal);
        Assert.Contains("timeout --signal=TERM --kill-after=120s", Workflow, StringComparison.Ordinal);
        Assert.Contains("no skip or emulator fallback", Workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void Workflow_binds_upload_identity_and_strictly_validates_evidence()
    {
        var captureUpload = Workflow.IndexOf(
            "Upload immutable raw capture",
            StringComparison.Ordinal);
        var generation = Workflow.IndexOf(
            "Generate immutable profile observation YAML",
            StringComparison.Ordinal);
        Assert.True(captureUpload >= 0 && generation > captureUpload);
        Assert.Contains(
            "steps.capture_upload.outputs.artifact-id",
            Workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "steps.capture_upload.outputs.artifact-digest",
            Workflow,
            StringComparison.Ordinal);
        Assert.Contains("validate-rc-observation", Workflow, StringComparison.Ordinal);
        Assert.Contains("--expected-evidence-digest", Workflow, StringComparison.Ordinal);
        Assert.Contains(
            "steps.evidence_upload.outputs.artifact-id",
            Workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "steps.evidence_upload.outputs.artifact-digest",
            Workflow,
            StringComparison.Ordinal);
        Assert.Equal(
            2,
            CountOccurrences(
                Workflow,
                "if [[ \"$upload_digest\" =~ ^[0-9a-f]{64}$ ]]; then"));
        Assert.Contains(
            "manifest_observation:",
            Workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "github-actions:$GITHUB_REPOSITORY:run-$GITHUB_RUN_ID:" +
            "attempt-$GITHUB_RUN_ATTEMPT:artifact-$artifact_id:$upload_digest",
            Workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "real-azure-rc-observation-capture-${{ matrix.profile }}-run-" +
            "${{ github.run_id }}-attempt-${{ github.run_attempt }}",
            Workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "[ \"${{ steps.generate.outputs.verdict }}\" != pass ]",
            Workflow,
            StringComparison.Ordinal);
        Assert.DoesNotContain(@"\\(", Workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void Workflow_has_minimal_permissions_and_pinned_external_actions()
    {
        Assert.Contains("  actions: read", Workflow, StringComparison.Ordinal);
        Assert.Contains("  attestations: read", Workflow, StringComparison.Ordinal);
        Assert.Contains("  contents: read", Workflow, StringComparison.Ordinal);
        Assert.Contains("  id-token: write", Workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("  packages: write", Workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("  contents: write", Workflow, StringComparison.Ordinal);

        var uses = Workflow.Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("uses:", StringComparison.Ordinal))
            .Select(line => line["uses:".Length..].Trim().Split(' ', 2)[0])
            .ToArray();
        Assert.NotEmpty(uses);
        Assert.All(uses, action =>
        {
            if (action.StartsWith("./", StringComparison.Ordinal))
            {
                return;
            }
            Assert.Matches(
                new Regex("@[0-9a-f]{40}$", RegexOptions.CultureInvariant),
                action);
        });
    }

    [Fact]
    public void Workflow_fails_closed_and_always_attempts_private_cleanup()
    {
        Assert.Contains(
            "if: always() && env.MODE == 'observation' && steps.capture_upload.outcome == 'success'",
            Workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "env.MODE == 'observation' &&\n          steps.observe.outcome == 'success' &&",
            Workflow,
            StringComparison.Ordinal);
        Assert.Contains("rm -rf \"$PRIVATE_ROOT\"", Workflow, StringComparison.Ordinal);
        Assert.Contains(
            "-name 'real-azure-it-*' -o -name 'secretsmanager-it-*'",
            Workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "cleanup-real-azure-resource-groups.sh \"$RG_NAME\"",
            Workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "steps.receipt_upload.outcome",
            Workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "RC observation did not produce a complete trigger-free pass",
            Workflow,
            StringComparison.Ordinal);
    }

    private static int CountOccurrences(string value, string needle)
    {
        var count = 0;
        var offset = 0;
        while ((offset = value.IndexOf(needle, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += needle.Length;
        }
        return count;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "aws2azure.slnx")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not find the repository root.");
    }
}
