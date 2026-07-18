using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aws2Azure.GapDocs;

namespace Aws2Azure.UnitTests.GapDocs;

public sealed class RealAzureLoadQualificationTests
{
    private static readonly DateTimeOffset Now =
        DateTimeOffset.Parse("2026-07-16T22:00:00Z");

    [Fact]
    public void Generate_qualifies_only_after_repeated_production_shaped_runs()
    {
        var document = RealAzureLoadQualificationGenerator.Generate(
            Manifest(),
            Candidate(),
            Policy(),
            [Evidence(1), Evidence(2), Evidence(3)],
            Metadata());

        Assert.Equal("qualified", document.Verdict);
        Assert.Equal(3, document.Provenance.SourceRuns.Count);
        Assert.Equal(300, Assert.Single(document.Scenarios).Completions);
        Assert.Equal(750, Assert.Single(document.Signals).MeasuredValue);
        Assert.Empty(SloQualificationValidator.Validate(document, Now));
    }

    [Fact]
    public void Generate_keeps_zero_completion_run_as_blocking_candidate()
    {
        var zero = Evidence(3);
        zero.Scenarios[0].Completions = 0;
        zero.Scenarios[0].Failures = 1;
        zero.OperationMix[0].Completions = 0;
        zero.OperationMix[0].Failures = 1;

        var document = RealAzureLoadQualificationGenerator.Generate(
            Manifest(),
            Candidate(),
            Policy(),
            [Evidence(1), Evidence(2), zero],
            Metadata());

        Assert.Equal("candidate", document.Verdict);
        Assert.Contains(document.Findings, finding => finding.Code == "zero_completions");
        Assert.Empty(SloQualificationValidator.Validate(document, Now));
    }

    [Fact]
    public void Generate_rejects_candidate_or_config_drift_between_runs()
    {
        var drifted = Evidence(2);
        drifted.Candidate.ConfigDigest = "sha256:different";

        var exception = Assert.Throws<InvalidDataException>(() =>
            RealAzureLoadQualificationGenerator.Generate(
                Manifest(),
                Candidate(),
                Policy(),
                [Evidence(1), drifted, Evidence(3)],
                Metadata()));

        Assert.Contains("inconsistent", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Generate_rejects_load_producer_config_drift_between_runs()
    {
        var drifted = Evidence(2);
        drifted.Provenance.ProducerConfigDigest = "sha256:different-producer";

        var exception = Assert.Throws<InvalidDataException>(() =>
            RealAzureLoadQualificationGenerator.Generate(
                Manifest(),
                Candidate(),
                Policy(),
                [Evidence(1), drifted, Evidence(3)],
                Metadata()));

        Assert.Contains("inconsistent", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Generate_requires_exactly_one_capacity_signal_from_each_run()
    {
        var duplicated = Evidence(1);
        duplicated.Signals.Add(new RealAzureLoadSignalMeasurement
        {
            Id = "representative-load-p99",
            ScenarioId = "representative-load",
            Metric = "p99_ms",
            MeasuredValue = 600,
            Samples = 100,
            CapturedAtUtc = duplicated.Provenance.WindowEndUtc,
        });
        var missing = Evidence(2);
        missing.Signals.Clear();

        var document = RealAzureLoadQualificationGenerator.Generate(
            Manifest(),
            Candidate(),
            Policy(),
            [duplicated, missing, Evidence(3)],
            Metadata());

        Assert.Equal("candidate", document.Verdict);
        Assert.Contains(document.Findings, finding => finding.Code == "signal_duplicated");
        Assert.Contains(document.Findings, finding => finding.Code == "signal_missing");
    }

    [Fact]
    public void Generate_does_not_average_away_a_per_run_failure_rate_breach()
    {
        var failed = Evidence(1);
        failed.Scenarios[0].Failures = 1;
        failed.OperationMix[0].Failures = 1;
        var largeRun2 = Evidence(2);
        largeRun2.Scenarios[0].Completions = 10_000;
        largeRun2.OperationMix[0].Completions = 10_000;
        var largeRun3 = Evidence(3);
        largeRun3.Scenarios[0].Completions = 10_000;
        largeRun3.OperationMix[0].Completions = 10_000;

        var document = RealAzureLoadQualificationGenerator.Generate(
            Manifest(),
            Candidate(),
            Policy(),
            [failed, largeRun2, largeRun3],
            Metadata());

        Assert.Equal("candidate", document.Verdict);
        Assert.Contains(
            document.Findings,
            finding => finding.Code == "failure_rate_exceeded"
                       && finding.Message.Contains("load-1/1", StringComparison.Ordinal));
    }

    [Fact]
    public void Generate_blocks_operation_failures_not_reflected_in_scenario_rows()
    {
        var failed = Evidence(1);
        failed.OperationMix[0].Failures = 1;

        var document = RealAzureLoadQualificationGenerator.Generate(
            Manifest(),
            Candidate(),
            Policy(),
            [failed, Evidence(2), Evidence(3)],
            Metadata());

        Assert.Equal("candidate", document.Verdict);
        Assert.Contains(
            document.Findings,
            finding => finding.Code == "operation_failure_rate_exceeded"
                       && finding.Message.Contains("load-1/1", StringComparison.Ordinal));
    }

    [Fact]
    public void Generate_does_not_allow_one_fresh_run_to_hide_a_stale_source_run()
    {
        var stale = Evidence(1);
        stale.Provenance.WindowStartUtc = Now.AddHours(-80);
        stale.Provenance.WindowEndUtc = Now.AddHours(-79);
        stale.Provenance.GeneratedAtUtc = Now.AddHours(-79);
        stale.Scenarios[0].CapturedAtUtc = Now.AddHours(-79);
        stale.Signals[0].CapturedAtUtc = Now.AddHours(-79);

        var document = RealAzureLoadQualificationGenerator.Generate(
            Manifest(),
            Candidate(),
            Policy(),
            [stale, Evidence(2), Evidence(3)],
            Metadata());

        Assert.Equal("candidate", document.Verdict);
        Assert.Contains(document.Findings, finding => finding.Code == "stale_source_run");
    }

    [Fact]
    public void Generate_rejects_multiple_attempts_of_the_same_run_as_distinct_runs()
    {
        var secondAttempt = Evidence(2);
        secondAttempt.Provenance.RunId = "load-1";
        secondAttempt.Provenance.RunAttempt = 2;

        var exception = Assert.Throws<InvalidDataException>(() =>
            RealAzureLoadQualificationGenerator.Generate(
                Manifest(),
                Candidate(),
                Policy(),
                [Evidence(1), secondAttempt, Evidence(3)],
                Metadata()));

        Assert.Contains("run identity", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_blocks_a_stale_correctness_candidate()
    {
        var candidate = Candidate();
        candidate.Provenance.WindowStartUtc = Now.AddHours(-80);
        candidate.Provenance.WindowEndUtc = Now.AddHours(-79);
        candidate.Provenance.GeneratedAtUtc = Now.AddHours(-79);
        candidate.Scenarios[0].CapturedAtUtc = Now.AddHours(-79);

        var document = RealAzureLoadQualificationGenerator.Generate(
            Manifest(),
            candidate,
            Policy(),
            [Evidence(1), Evidence(2), Evidence(3)],
            Metadata());

        Assert.Equal("candidate", document.Verdict);
        Assert.Contains(
            document.Findings,
            finding => finding.Code == "stale_correctness_candidate");
    }

    [Fact]
    public void Generate_rejects_policy_that_omits_manifest_operational_scenario()
    {
        var manifest = Manifest();
        manifest.Evidence.RequiredScenarios.Add("restart");

        var exception = Assert.Throws<InvalidDataException>(() =>
            RealAzureLoadQualificationGenerator.Generate(
                manifest,
                Candidate(),
                Policy(),
                [Evidence(1), Evidence(2), Evidence(3)],
                Metadata()));

        Assert.Contains("exactly match", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_allows_one_shot_real_azure_operational_scenario_below_load_volume()
    {
        var manifest = Manifest();
        manifest.Evidence.RequiredScenarios.Add("restart");
        manifest.Evidence.RequiredRealAzureScenarios.Add("restart");
        var policy = Policy();
        policy.Scenarios.Add(new WorkloadQualificationScenarioPolicy
        {
            Id = "restart",
            Service = "s3",
            Operation = "PutObject",
            EvidenceSource = "real_azure",
        });
        var evidence = new[] { Evidence(1), Evidence(2), Evidence(3) };
        foreach (var run in evidence)
        {
            run.Scenarios.Add(new SloQualificationScenario
            {
                Id = "restart",
                Service = "s3",
                Operation = "PutObject",
                EvidenceSource = "real_azure",
                Completions = 1,
                DurationSeconds = 1,
                CapturedAtUtc = run.Provenance.WindowEndUtc,
            });
        }

        var document = RealAzureLoadQualificationGenerator.Generate(
            manifest,
            Candidate(),
            policy,
            evidence,
            Metadata());

        Assert.Equal("qualified", document.Verdict);
        var restart = Assert.Single(document.Scenarios, scenario => scenario.Id == "restart");
        Assert.Equal(3, restart.Completions);
        Assert.DoesNotContain(
            document.Findings,
            finding => finding.Code == "insufficient_scenario_evidence"
                       && finding.ScenarioId == "restart");
        Assert.Empty(SloQualificationValidator.Validate(document, Now));
    }

    [Fact]
    public void Generate_accepts_fresh_identity_rotation_proof_without_candidate_drift()
    {
        var inputs = RotationInputs();

        var document = RealAzureLoadQualificationGenerator.Generate(
            inputs.Manifest,
            inputs.Candidate,
            inputs.Policy,
            inputs.Evidence,
            Metadata());

        Assert.Equal("qualified", document.Verdict);
        Assert.Equal(
            3,
            Assert.Single(document.Scenarios, scenario => scenario.Id == "credential-rotation")
                .Completions);
    }

    [Fact]
    public void Generate_rejects_rotation_without_distinct_identity()
    {
        var inputs = RotationInputs();
        var proof = Assert.Single(inputs.Evidence[0].CredentialRotationProofs);
        proof.IdentityBObjectId = proof.IdentityAObjectId;

        var exception = Assert.Throws<InvalidDataException>(() =>
            RealAzureLoadQualificationGenerator.Generate(
                inputs.Manifest,
                inputs.Candidate,
                inputs.Policy,
                inputs.Evidence,
                Metadata()));

        Assert.Contains("distinct", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Generate_rejects_rotation_runtime_or_config_drift()
    {
        var inputs = RotationInputs();
        var proof = Assert.Single(inputs.Evidence[0].CredentialRotationProofs);
        proof.ProxyConfigDigestB = Sha256('b');

        var exception = Assert.Throws<InvalidDataException>(() =>
            RealAzureLoadQualificationGenerator.Generate(
                inputs.Manifest,
                inputs.Candidate,
                inputs.Policy,
                inputs.Evidence,
                Metadata()));

        Assert.Contains("drift", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Generate_rejects_rotation_without_old_access_denial()
    {
        var inputs = RotationInputs();
        Assert.Single(inputs.Evidence[0].CredentialRotationProofs)
            .OldAccessDeniedCompletions = 0;

        var exception = Assert.Throws<InvalidDataException>(() =>
            RealAzureLoadQualificationGenerator.Generate(
                inputs.Manifest,
                inputs.Candidate,
                inputs.Policy,
                inputs.Evidence,
                Metadata()));

        Assert.Contains("denial", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Generate_rejects_duplicate_or_out_of_window_rotation_proof()
    {
        var duplicated = RotationInputs();
        duplicated.Evidence[0].CredentialRotationProofs.Add(
            duplicated.Evidence[0].CredentialRotationProofs[0]);
        Assert.Throws<InvalidDataException>(() =>
            RealAzureLoadQualificationGenerator.Generate(
                duplicated.Manifest,
                duplicated.Candidate,
                duplicated.Policy,
                duplicated.Evidence,
                Metadata()));

        var outOfWindow = RotationInputs();
        var outside = outOfWindow.Evidence[0].Provenance.WindowEndUtc.AddSeconds(1);
        Assert.Single(outOfWindow.Evidence[0].CredentialRotationProofs).CompletedAtUtc = outside;
        Assert.Single(
            outOfWindow.Evidence[0].Scenarios,
            scenario => scenario.Id == "credential-rotation").CapturedAtUtc = outside;
        Assert.Throws<InvalidDataException>(() =>
            RealAzureLoadQualificationGenerator.Generate(
                outOfWindow.Manifest,
                outOfWindow.Candidate,
                outOfWindow.Policy,
                outOfWindow.Evidence,
                Metadata()));
    }

    [Theory]
    [InlineData("subscription")]
    [InlineData("resource-group")]
    public void Generate_rejects_over_scoped_rotation_role_assignments(string scopeKind)
    {
        var inputs = RotationInputs();
        var proof = Assert.Single(inputs.Evidence[0].CredentialRotationProofs);
        const string subscriptionId = "11111111-1111-1111-1111-111111111111";
        var scope = scopeKind == "subscription"
            ? $"/subscriptions/{subscriptionId}"
            : $"/subscriptions/{subscriptionId}/resourceGroups/rotation-rg";
        proof.RoleAssignmentAId = RoleAssignmentId(scope, 901);
        proof.RoleAssignmentBId = RoleAssignmentId(scope, 902);
        proof.RoleScopeDigestA = Digest(scope);
        proof.RoleScopeDigestB = Digest(scope);

        var exception = Assert.Throws<InvalidDataException>(() =>
            RealAzureLoadQualificationGenerator.Generate(
                inputs.Manifest,
                inputs.Candidate,
                inputs.Policy,
                inputs.Evidence,
                Metadata()));

        Assert.Contains("exact Key Vault", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_rejects_rotation_proof_above_retry_or_poll_budgets()
    {
        var excessiveRetries = RotationInputs();
        Assert.Single(excessiveRetries.Evidence[0].CredentialRotationProofs)
            .SetupPropagationRetries =
            RealAzureCredentialRotationBudgets.MaxSetupPropagationRetries + 1;
        Assert.Throws<InvalidDataException>(() =>
            RealAzureLoadQualificationGenerator.Generate(
                excessiveRetries.Manifest,
                excessiveRetries.Candidate,
                excessiveRetries.Policy,
                excessiveRetries.Evidence,
                Metadata()));

        var excessivePolls = RotationInputs();
        Assert.Single(excessivePolls.Evidence[0].CredentialRotationProofs)
            .RevocationPolls = RealAzureCredentialRotationBudgets.MaxRevocationPolls + 1;
        Assert.Throws<InvalidDataException>(() =>
            RealAzureLoadQualificationGenerator.Generate(
                excessivePolls.Manifest,
                excessivePolls.Candidate,
                excessivePolls.Policy,
                excessivePolls.Evidence,
                Metadata()));
    }

    [Fact]
    public void Generate_rejects_rotation_proof_above_setup_or_revocation_time_budgets()
    {
        var excessiveSetup = RotationInputs();
        var setupRun = excessiveSetup.Evidence[0];
        var setupProof = Assert.Single(setupRun.CredentialRotationProofs);
        setupRun.Provenance.WindowStartUtc = setupProof.RevocationRequestedAtUtc
            - RealAzureCredentialRotationBudgets.MaxSetupDuration
            - TimeSpan.FromMinutes(1);
        setupProof.StartedAtUtc = setupProof.RevocationRequestedAtUtc
            - RealAzureCredentialRotationBudgets.MaxSetupDuration
            - TimeSpan.FromSeconds(1);
        Assert.Throws<InvalidDataException>(() =>
            RealAzureLoadQualificationGenerator.Generate(
                excessiveSetup.Manifest,
                excessiveSetup.Candidate,
                excessiveSetup.Policy,
                excessiveSetup.Evidence,
                Metadata()));

        var excessiveRevocation = RotationInputs();
        var revocationRun = excessiveRevocation.Evidence[0];
        var revocationProof = Assert.Single(revocationRun.CredentialRotationProofs);
        var oldAccessDeniedAt = revocationProof.RevocationRequestedAtUtc
            + RealAzureCredentialRotationBudgets.MaxRevocationDuration
            + TimeSpan.FromSeconds(1);
        revocationProof.OldAccessDeniedAtUtc = oldAccessDeniedAt;
        revocationProof.CompletedAtUtc = oldAccessDeniedAt.AddSeconds(1);
        revocationRun.Provenance.WindowEndUtc = revocationProof.CompletedAtUtc.AddSeconds(1);
        revocationRun.Provenance.GeneratedAtUtc = revocationRun.Provenance.WindowEndUtc;
        Assert.Single(
            revocationRun.Scenarios,
            scenario => scenario.Id == "credential-rotation").CapturedAtUtc =
            revocationProof.CompletedAtUtc;
        Assert.Throws<InvalidDataException>(() =>
            RealAzureLoadQualificationGenerator.Generate(
                excessiveRevocation.Manifest,
                excessiveRevocation.Candidate,
                excessiveRevocation.Policy,
                excessiveRevocation.Evidence,
                Metadata()));
    }

    [Fact]
    public void Generate_qualifies_only_with_one_real_rollback_proof_per_run()
    {
        var inputs = RollbackInputs();

        var document = RealAzureLoadQualificationGenerator.Generate(
            inputs.Manifest,
            inputs.Candidate,
            inputs.Policy,
            inputs.Evidence,
            Metadata(),
            inputs.Prior);

        Assert.Equal("qualified", document.Verdict);
        Assert.Equal(3, document.RollbackProofs.Count);
        Assert.Equal(
            3,
            Assert.Single(document.Scenarios, scenario => scenario.Id == "rollback")
                .Completions);
        Assert.Empty(SloQualificationValidator.Validate(document, Now));
    }

    [Fact]
    public void Generate_rejects_missing_or_duplicate_rollback_proof()
    {
        var missing = RollbackInputs();
        missing.Evidence[0].RollbackProofs.Clear();
        Assert.Throws<InvalidDataException>(() =>
            RealAzureLoadQualificationGenerator.Generate(
                missing.Manifest,
                missing.Candidate,
                missing.Policy,
                missing.Evidence,
                Metadata(),
                missing.Prior));

        var duplicate = RollbackInputs();
        duplicate.Evidence[0].RollbackProofs.Add(
            duplicate.Evidence[0].RollbackProofs[0]);
        Assert.Throws<InvalidDataException>(() =>
            RealAzureLoadQualificationGenerator.Generate(
                duplicate.Manifest,
                duplicate.Candidate,
                duplicate.Policy,
                duplicate.Evidence,
                Metadata(),
                duplicate.Prior));
    }

    [Fact]
    public void Generate_rejects_rollback_to_same_runtime_or_configuration_drift()
    {
        var sameRuntime = RollbackInputs();
        var sameProof = Assert.Single(sameRuntime.Evidence[0].RollbackProofs);
        sameProof.Prior.Runtime.AggregateDigest =
            sameProof.Candidate.Runtime.AggregateDigest;
        Assert.Throws<InvalidDataException>(() =>
            RealAzureLoadQualificationGenerator.Generate(
                sameRuntime.Manifest,
                sameRuntime.Candidate,
                sameRuntime.Policy,
                sameRuntime.Evidence,
                Metadata(),
                sameRuntime.Prior));

        var configDrift = RollbackInputs();
        Assert.Single(configDrift.Evidence[0].RollbackProofs).PriorConfigDigest =
            Sha256('8');
        Assert.Throws<InvalidDataException>(() =>
            RealAzureLoadQualificationGenerator.Generate(
                configDrift.Manifest,
                configDrift.Candidate,
                configDrift.Policy,
                configDrift.Evidence,
                Metadata(),
                configDrift.Prior));
    }

    [Fact]
    public void Generate_rejects_out_of_order_or_out_of_window_rollback_timestamps()
    {
        var inputs = RollbackInputs();
        var proof = Assert.Single(inputs.Evidence[0].RollbackProofs);
        proof.PriorReadCompletedAtUtc = proof.PriorStartedAtUtc.AddSeconds(-1);

        Assert.Throws<InvalidDataException>(() =>
            RealAzureLoadQualificationGenerator.Generate(
                inputs.Manifest,
                inputs.Candidate,
                inputs.Policy,
                inputs.Evidence,
                Metadata(),
                inputs.Prior));
    }

    [Fact]
    public void Generate_rejects_prior_runtime_or_profile_ledger_inconsistency()
    {
        var inconsistent = RollbackInputs();
        Assert.Single(inconsistent.Evidence[1].RollbackProofs)
            .Prior.Attestation.BundleDigest = Sha256('7');
        Assert.Throws<InvalidDataException>(() =>
            RealAzureLoadQualificationGenerator.Generate(
                inconsistent.Manifest,
                inconsistent.Candidate,
                inconsistent.Policy,
                inconsistent.Evidence,
                Metadata(),
                inconsistent.Prior));

        var ledgerDrift = RollbackInputs();
        Assert.Single(ledgerDrift.Evidence[0].RollbackProofs).Prior.Artifact.Id++;
        Assert.Throws<InvalidDataException>(() =>
            RealAzureLoadQualificationGenerator.Generate(
                ledgerDrift.Manifest,
                ledgerDrift.Candidate,
                ledgerDrift.Policy,
                ledgerDrift.Evidence,
                Metadata(),
                ledgerDrift.Prior));
    }

    [Fact]
    public void Generate_rejects_reused_rollback_canary_digest()
    {
        var inputs = RollbackInputs();
        Assert.Single(inputs.Evidence[1].RollbackProofs).CanaryDigest =
            Assert.Single(inputs.Evidence[0].RollbackProofs).CanaryDigest;

        Assert.Throws<InvalidDataException>(() =>
            RealAzureLoadQualificationGenerator.Generate(
                inputs.Manifest,
                inputs.Candidate,
                inputs.Policy,
                inputs.Evidence,
                Metadata(),
                inputs.Prior));
    }

    [Fact]
    public void Qualified_validator_rejects_fabricated_missing_rollback_proof()
    {
        var inputs = RollbackInputs();
        var document = RealAzureLoadQualificationGenerator.Generate(
            inputs.Manifest,
            inputs.Candidate,
            inputs.Policy,
            inputs.Evidence,
            Metadata(),
            inputs.Prior);
        document.RollbackProofs.Clear();

        var errors = SloQualificationValidator.Validate(document, Now);

        Assert.Contains(errors, error => error.Contains(
            "one successful proof per source run",
            StringComparison.Ordinal));
    }

    [Fact]
    public void LoadEvidence_round_trips_structured_sealed_rollback_proof()
    {
        var inputs = RollbackInputs();
        var path = Path.Combine(
            AppContext.BaseDirectory,
            $"sealed-load-evidence-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(
                path,
                JsonSerializer.Serialize(
                    inputs.Evidence[0],
                    new JsonSerializerOptions
                    {
                        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
                    }));

            var loaded = RealAzureLoadQualificationGenerator.LoadEvidence(path);

            var proof = Assert.Single(loaded.RollbackProofs);
            Assert.Equal("rollback", proof.ScenarioId);
            Assert.Equal(
                inputs.Evidence[0].Candidate.Runtime!.Runtime.ManifestDigest,
                proof.Candidate.Runtime.ManifestDigest);
            Assert.Equal(
                inputs.Prior.Attestation.ManifestSubjectDigest,
                proof.Prior.Runtime.ManifestDigest);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Generate_preserves_only_expected_workflow_artifact_selections()
    {
        var inputs = RollbackInputs();
        inputs.Candidate.Provenance.RunId = "100";
        inputs.Candidate.Provenance.RunUrl =
            "https://github.com/example/repo/actions/runs/100";
        var correctness = RunSelection(
            100,
            ".github/workflows/integration-real-azure.yml",
            "real-azure-conformance");
        var loadSelections = new List<QualificationRunArtifactIdentity>();
        for (var index = 0; index < inputs.Evidence.Length; index++)
        {
            var runId = 201 + index;
            var run = inputs.Evidence[index];
            run.Provenance.RunId = runId.ToString();
            run.Provenance.RunUrl =
                $"https://github.com/example/repo/actions/runs/{runId}";
            Assert.Single(run.RollbackProofs).EvidenceRunId = run.Provenance.RunId;
            loadSelections.Add(RunSelection(
                runId,
                ".github/workflows/workload-load-real-azure.yml",
                "real-azure-workload-load-s3-basic-object-crud"));
        }

        var document = RealAzureLoadQualificationGenerator.Generate(
            inputs.Manifest,
            inputs.Candidate,
            inputs.Policy,
            inputs.Evidence,
            Metadata(),
            inputs.Prior,
            correctness,
            loadSelections);

        Assert.Equal(
            correctness.Artifact.UploadDigest,
            document.Provenance.CorrectnessRun!.EvidenceArtifact!.Artifact.UploadDigest);
        Assert.All(
            document.Provenance.SourceRuns,
            run => Assert.NotNull(run.EvidenceArtifact));
        var rendered = Path.Combine(
            AppContext.BaseDirectory,
            $"sealed-qualification-{Guid.NewGuid():N}.yaml");
        try
        {
            SloQualificationRenderer.RenderYaml(document, rendered);
            var roundTrip = SloQualificationLoader.Load(rendered);
            Assert.Equal(3, roundTrip.RollbackProofs.Count);
            Assert.NotNull(roundTrip.Provenance.CorrectnessRun!.EvidenceArtifact);
            Assert.Empty(SloQualificationValidator.Validate(roundTrip, Now));
        }
        finally
        {
            File.Delete(rendered);
        }

        correctness.WorkflowPath = ".github/workflows/arbitrary.yml";
        Assert.Throws<InvalidDataException>(() =>
            RealAzureLoadQualificationGenerator.Generate(
                inputs.Manifest,
                inputs.Candidate,
                inputs.Policy,
                inputs.Evidence,
                Metadata(),
                inputs.Prior,
                correctness,
                loadSelections));
    }

    [Fact]
    public void Generate_records_unresolved_capacity_threshold_and_blocks_qualification()
    {
        var policy = Policy();
        var signal = Assert.Single(policy.Scenarios[0].Signals);
        signal.ThresholdStatus = "unresolved";
        signal.ThresholdReason = "Three comparable live-Azure runs have not been reviewed.";
        signal.MaxValue = null;

        var document = RealAzureLoadQualificationGenerator.Generate(
            Manifest(),
            Candidate(),
            policy,
            [Evidence(1), Evidence(2), Evidence(3)],
            Metadata());

        Assert.Equal("candidate", document.Verdict);
        Assert.Contains(
            document.Findings,
            finding => finding.Code == "signal_threshold_unresolved");
        Assert.Equal("report_only", Assert.Single(document.Signals).Disposition);
        Assert.Empty(SloQualificationValidator.Validate(document, Now));
    }

    [Fact]
    public void Generate_reports_worst_run_for_unresolved_throughput_floor()
    {
        var policy = Policy();
        var signal = Assert.Single(policy.Scenarios[0].Signals);
        signal.Metric = "throughput_per_sec";
        signal.ThresholdStatus = "unresolved";
        signal.ThresholdReason = "Comparable live-Azure runs have not been reviewed.";
        signal.MaxValue = null;
        var evidence = new[] { Evidence(1), Evidence(2), Evidence(3) };
        evidence[0].Signals[0].Metric = "throughput_per_sec";
        evidence[0].Signals[0].MeasuredValue = 90;
        evidence[1].Signals[0].Metric = "throughput_per_sec";
        evidence[1].Signals[0].MeasuredValue = 75;
        evidence[2].Signals[0].Metric = "throughput_per_sec";
        evidence[2].Signals[0].MeasuredValue = 80;

        var document = RealAzureLoadQualificationGenerator.Generate(
            Manifest(),
            Candidate(),
            policy,
            evidence,
            Metadata());

        Assert.Equal(75, Assert.Single(document.Signals).MeasuredValue);
    }

    [Fact]
    public void Generate_aggregates_report_only_diagnostics_by_metric_direction()
    {
        var policy = Policy();
        policy.Scenarios[0].Signals.Add(new WorkloadQualificationSignalPolicy
        {
            Id = "crud-iterations-per-sec",
            Source = "backend_capacity",
            Disposition = "report_only",
            Metric = "throughput_per_sec",
        });
        policy.Scenarios[0].Signals.Add(new WorkloadQualificationSignalPolicy
        {
            Id = "representative-load-put-object-p99",
            Source = "backend_capacity",
            Disposition = "report_only",
            Metric = "p99_ms",
        });
        var evidence = new[] { Evidence(1), Evidence(2), Evidence(3) };
        var throughputValues = new[] { 30d, 20d, 25d };
        var latencyValues = new[] { 400d, 600d, 500d };
        for (var index = 0; index < evidence.Length; index++)
        {
            evidence[index].Signals.Add(new RealAzureLoadSignalMeasurement
            {
                Id = "crud-iterations-per-sec",
                ScenarioId = "representative-load",
                Metric = "throughput_per_sec",
                MeasuredValue = throughputValues[index],
                Samples = 100,
                CapturedAtUtc = evidence[index].Provenance.WindowEndUtc,
            });
            evidence[index].Signals.Add(new RealAzureLoadSignalMeasurement
            {
                Id = "representative-load-put-object-p99",
                ScenarioId = "representative-load",
                Metric = "p99_ms",
                MeasuredValue = latencyValues[index],
                Samples = 100,
                CapturedAtUtc = evidence[index].Provenance.WindowEndUtc,
            });
        }

        var document = RealAzureLoadQualificationGenerator.Generate(
            Manifest(),
            Candidate(),
            policy,
            evidence,
            Metadata());

        Assert.Equal(
            20,
            Assert.Single(document.Signals, item => item.Id == "crud-iterations-per-sec")
                .MeasuredValue);
        Assert.Equal(
            600,
            Assert.Single(
                document.Signals,
                item => item.Id == "representative-load-put-object-p99").MeasuredValue);
    }

    [Fact]
    public void Generate_rejects_operation_mix_that_omits_a_profile_operation()
    {
        var evidence = Evidence(1);
        evidence.OperationMix.Clear();

        var exception = Assert.Throws<InvalidDataException>(() =>
            RealAzureLoadQualificationGenerator.Generate(
                Manifest(),
                Candidate(),
                Policy(),
                [evidence, Evidence(2), Evidence(3)],
                Metadata()));

        Assert.Contains("operation mix", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SecretsManager_policy_uses_reviewed_real_azure_capacity_floor()
    {
        var repoRoot = FindRepoRoot();
        var policy = WorkloadQualificationPolicyLoader.Load(Path.Combine(
            repoRoot,
            "docs",
            "workloads",
            "qualification",
            "secretsmanager-basic-lifecycle.yaml"));
        var manifest = WorkloadGaManifestLoader.Load(Path.Combine(
            repoRoot,
            "docs",
            "workloads",
            "secretsmanager-basic-lifecycle.yaml"));

        Assert.Equal(
            manifest.Evidence.RequiredScenarios.Order(StringComparer.Ordinal),
            policy.Scenarios.Select(item => item.Id).Order(StringComparer.Ordinal));
        var capacity = Assert.Single(
            policy.Scenarios.SelectMany(item => item.Signals),
            signal => signal.Disposition == "blocking");
        Assert.Equal("resolved", capacity.ThresholdStatus);
        Assert.False(string.IsNullOrWhiteSpace(capacity.ThresholdReason));
        Assert.Equal(9, capacity.MinValue);
        Assert.Null(capacity.MaxValue);
        Assert.Equal(8, policy.LoadShape.Concurrency);
        Assert.Equal(300, policy.LoadShape.RequestedDurationSeconds);
    }

    [Fact]
    public void S3_policy_uses_reviewed_real_azure_capacity_floor()
    {
        var repoRoot = FindRepoRoot();
        var policy = WorkloadQualificationPolicyLoader.Load(Path.Combine(
            repoRoot,
            "docs",
            "workloads",
            "qualification",
            "s3-basic-object-crud.yaml"));
        var manifest = WorkloadGaManifestLoader.Load(Path.Combine(
            repoRoot,
            "docs",
            "workloads",
            "s3-basic-object-crud.yaml"));

        Assert.Equal(
            manifest.Evidence.RequiredScenarios.Order(StringComparer.Ordinal),
            policy.Scenarios.Select(item => item.Id).Order(StringComparer.Ordinal));
        var capacity = Assert.Single(
            policy.Scenarios.SelectMany(item => item.Signals),
            signal => signal.Disposition == "blocking");
        Assert.Equal("backend_capacity", capacity.Source);
        Assert.Equal("throughput_per_sec", capacity.Metric);
        Assert.Equal("resolved", capacity.ThresholdStatus);
        Assert.False(string.IsNullOrWhiteSpace(capacity.ThresholdReason));
        Assert.Equal(40, capacity.MinValue);
        Assert.Null(capacity.MaxValue);
        Assert.Contains("closed-loop CRUD topology", capacity.ThresholdReason, StringComparison.Ordinal);
        Assert.Equal(8, policy.LoadShape.Concurrency);
        Assert.Equal(300, policy.LoadShape.RequestedDurationSeconds);

        var representative = Assert.Single(
            policy.Scenarios,
            scenario => scenario.Id == "representative-load");
        var signals = representative.Signals.ToDictionary(item => item.Id, StringComparer.Ordinal);
        Assert.Equal(25, signals.Count);
        Assert.Equal("throughput_per_sec", signals["crud-iterations-per-sec"].Metric);
        Assert.Equal("throughput_per_sec", signals["aws-operations-per-sec"].Metric);
        foreach (var prefix in new[]
                 {
                     "representative-load",
                     "representative-load-create-bucket",
                     "representative-load-put-object",
                     "representative-load-head-object",
                     "representative-load-list-objects-v2",
                     "representative-load-delete-object",
                     "representative-load-delete-bucket",
                 })
        {
            Assert.Equal("throughput_per_sec", signals[$"{prefix}-throughput"].Metric);
            Assert.Equal("p95_ms", signals[$"{prefix}-p95"].Metric);
            Assert.Equal("p99_ms", signals[$"{prefix}-p99"].Metric);
        }
        var connectivity =
            signals["representative-load-unauthenticated-connectivity-header-p95"];
        Assert.Equal("network_noise", connectivity.Source);
        Assert.Equal("report_only", connectivity.Disposition);
    }

    [Fact]
    public void RenderTrend_marks_rows_as_real_azure_workload_qualification()
    {
        var output = Path.Combine(
            AppContext.BaseDirectory,
            $"real-azure-load-trend-{Guid.NewGuid():N}.csv");
        try
        {
            RealAzureLoadQualificationGenerator.RenderTrend([Evidence(1)], output);

            var content = File.ReadAllText(output);
            Assert.Contains("artifact_kind", content, StringComparison.Ordinal);
            Assert.Contains("real_azure_workload_qualification", content, StringComparison.Ordinal);
            Assert.DoesNotContain("emulator_regression", content, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(output);
        }
    }

    private static WorkloadGaManifest Manifest() => new()
    {
        SchemaVersion = 1,
        Id = "s3-basic-object-crud",
        Version = 1,
        Name = "S3 basic object CRUD",
        MinimumProxyVersion = "0.1.0",
        RealAzureSealMaxAgeDays = 90,
        Operations = ["s3:PutObject"],
        Evidence = new WorkloadGaEvidence
        {
            RequiredScenarios = ["representative-load"],
        },
    };

    private static WorkloadQualificationPolicy Policy() => new()
    {
        SchemaVersion = 1,
        ProfileId = "s3-basic-object-crud",
        ProfileVersion = 1,
        Rules = new SloQualificationRules
        {
            MaxArtifactAgeHours = 72,
            MinSamplesPerScenario = 300,
            MinDurationSeconds = 300,
            MaxFailureRate = 0.001,
            ZeroCompletionsDisqualify = true,
            OnlySkippedRealAzureDisqualifies = true,
            MinDistinctRuns = 3,
        },
        LoadShape = new RealAzureLoadShape
        {
            Concurrency = 4,
            RequestedDurationSeconds = 100,
        },
        Scenarios =
        [
            new WorkloadQualificationScenarioPolicy
            {
                Id = "representative-load",
                Service = "s3",
                Operation = "PutObject",
                EvidenceSource = "real_azure",
                Signals =
                [
                    new WorkloadQualificationSignalPolicy
                    {
                        Id = "representative-load-p99",
                        Source = "backend_capacity",
                        Disposition = "blocking",
                        Metric = "p99_ms",
                        MaxValue = 1000,
                    }
                ],
            }
        ],
    };

    private static SloQualificationDocument Candidate() => new()
    {
        SchemaVersion = 1,
        ArtifactKind = "real_azure_workload_qualification",
        Verdict = "candidate",
        Profile = Profile(),
        Candidate = CandidateIdentity(),
        Provenance = new SloQualificationProvenance
        {
            RunId = "correctness",
            RunUrl = "https://github.com/example/repo/actions/runs/correctness",
            RunAttempt = 1,
            GeneratedAtUtc = Now.AddHours(-1),
            WindowStartUtc = Now.AddHours(-1).AddMinutes(-1),
            WindowEndUtc = Now.AddHours(-1),
            Region = "eastus2",
            BackendDescription = "Blob Storage Standard_LRS",
        },
        Rules = new SloQualificationRules
        {
            MaxArtifactAgeHours = 72,
            MinSamplesPerScenario = 1,
            MinDurationSeconds = 0.001,
            MaxFailureRate = 0,
            ZeroCompletionsDisqualify = true,
            OnlySkippedRealAzureDisqualifies = true,
            MinDistinctRuns = 1,
        },
        Scenarios =
        [
            new SloQualificationScenario
            {
                Id = "conformance-001",
                Service = "s3",
                Operation = "PutObject",
                EvidenceSource = "real_azure",
                Completions = 1,
                DurationSeconds = 1,
                CapturedAtUtc = Now.AddHours(-1),
            }
        ],
        Findings =
        [
            new SloQualificationFinding
            {
                Code = "load_evidence_missing",
                Disposition = "blocking",
                Message = "Load evidence is required.",
            }
        ],
    };

    private static RealAzureLoadEvidence Evidence(int attempt) => new()
    {
        SchemaVersion = 1,
        Profile = Profile(),
        Candidate = CandidateIdentity(),
        Provenance = new RealAzureLoadEvidenceProvenance
        {
            RunId = $"load-{attempt}",
            RunUrl = $"https://github.com/example/repo/actions/runs/load-{attempt}",
            RunAttempt = 1,
            WindowStartUtc = Now.AddMinutes(-attempt * 10 - 5),
            WindowEndUtc = Now.AddMinutes(-attempt * 10),
            GeneratedAtUtc = Now.AddMinutes(-attempt * 10),
            Region = "eastus2",
            BackendDescription = "Blob Storage Standard_LRS",
            ProducerConfigDigest = "sha256:load-producer",
        },
        LoadShape = new RealAzureLoadShape
        {
            Concurrency = 4,
            RequestedDurationSeconds = 100,
        },
        OperationMix =
        [
            new RealAzureLoadOperationMeasurement
            {
                Service = "s3",
                Operation = "PutObject",
                Completions = 100,
                P95Milliseconds = 400,
                P99Milliseconds = 700,
            }
        ],
        Scenarios =
        [
            new SloQualificationScenario
            {
                Id = "representative-load",
                Service = "s3",
                Operation = "PutObject",
                EvidenceSource = "real_azure",
                Completions = 100,
                DurationSeconds = 100,
                CapturedAtUtc = Now.AddMinutes(-attempt * 10),
            }
        ],
        Signals =
        [
            new RealAzureLoadSignalMeasurement
            {
                Id = "representative-load-p99",
                ScenarioId = "representative-load",
                Metric = "p99_ms",
                MeasuredValue = attempt == 2 ? 750 : 700,
                Samples = 100,
                CapturedAtUtc = Now.AddMinutes(-attempt * 10),
            }
        ],
    };

    private static (
        WorkloadGaManifest Manifest,
        SloQualificationDocument Candidate,
        WorkloadQualificationPolicy Policy,
        RealAzureLoadEvidence[] Evidence) RotationInputs()
    {
        var manifest = Manifest();
        manifest.Id = "secretsmanager-basic-lifecycle";
        manifest.Name = "Secrets Manager basic lifecycle";
        manifest.Operations = ["secretsmanager:GetSecretValue"];
        manifest.Evidence.RequiredScenarios.Add("credential-rotation");
        manifest.Evidence.RequiredRealAzureScenarios.Add("credential-rotation");

        var candidate = Candidate();
        candidate.Profile.Id = manifest.Id;
        candidate.Candidate.Runtime!.Profile.Id = manifest.Id;
        candidate.Profile.Services =
        [
            new SloQualificationProfileService
            {
                Service = "secretsmanager",
                Operations = ["GetSecretValue"],
            }
        ];

        var policy = Policy();
        policy.ProfileId = manifest.Id;
        policy.Scenarios[0].Service = "secretsmanager";
        policy.Scenarios[0].Operation = "GetSecretValue";
        policy.Scenarios.Add(new WorkloadQualificationScenarioPolicy
        {
            Id = "credential-rotation",
            Service = "secretsmanager",
            Operation = "GetSecretValue",
            EvidenceSource = "real_azure",
        });

        var evidence = new[] { Evidence(1), Evidence(2), Evidence(3) };
        foreach (var run in evidence)
        {
            var attempt = int.Parse(run.Provenance.RunId.AsSpan("load-".Length));
            run.Profile.Id = manifest.Id;
            run.Candidate.Runtime!.Profile.Id = manifest.Id;
            run.Profile.Services =
            [
                new SloQualificationProfileService
                {
                    Service = "secretsmanager",
                    Operations = ["GetSecretValue"],
                }
            ];
            run.OperationMix[0].Service = "secretsmanager";
            run.OperationMix[0].Operation = "GetSecretValue";
            run.Scenarios[0].Service = "secretsmanager";
            run.Scenarios[0].Operation = "GetSecretValue";
            var completed = run.Provenance.WindowEndUtc.AddSeconds(-1);
            run.Scenarios.Add(new SloQualificationScenario
            {
                Id = "credential-rotation",
                Service = "secretsmanager",
                Operation = "GetSecretValue",
                EvidenceSource = "real_azure",
                Completions = 1,
                DurationSeconds = 4,
                CapturedAtUtc = completed,
            });
            run.CredentialRotationProofs.Add(RotationProof(attempt, run, completed));
        }

        return (manifest, candidate, policy, evidence);
    }

    private static (
        WorkloadGaManifest Manifest,
        SloQualificationDocument Candidate,
        WorkloadQualificationPolicy Policy,
        RealAzureLoadEvidence[] Evidence,
        ApprovedRuntimeRecord Prior) RollbackInputs()
    {
        var manifest = Manifest();
        manifest.Evidence.RequiredScenarios.Add("rollback");
        manifest.Evidence.RequiredRealAzureScenarios.Add("rollback");
        var candidate = Candidate();
        var policy = Policy();
        policy.Scenarios.Add(new WorkloadQualificationScenarioPolicy
        {
            Id = "rollback",
            Service = "s3",
            Operation = "PutObject",
            EvidenceSource = "real_azure",
        });
        var prior = ApprovedRuntimeLedgerLoader.Load(Path.Combine(
            FindRepoRoot(),
            "docs",
            "workloads",
            "approved-runtimes",
            "s3-basic-object-crud.yaml"));
        var priorIdentity = PriorRuntime(prior);
        var evidence = new[] { Evidence(1), Evidence(2), Evidence(3) };
        foreach (var run in evidence)
        {
            var runNumber = int.Parse(run.Provenance.RunId.AsSpan("load-".Length));
            var completed = run.Provenance.WindowEndUtc.AddSeconds(-1);
            run.Scenarios.Add(new SloQualificationScenario
            {
                Id = "rollback",
                Service = "s3",
                Operation = "PutObject",
                EvidenceSource = "real_azure",
                Completions = 1,
                DurationSeconds = 9,
                CapturedAtUtc = completed,
            });
            run.RollbackProofs.Add(new RealAzureRollbackProof
            {
                ScenarioId = "rollback",
                Service = "s3",
                Operation = "PutObject",
                EvidenceRunId = run.Provenance.RunId,
                EvidenceRunAttempt = run.Provenance.RunAttempt,
                Candidate = run.Candidate.Runtime!,
                Prior = PriorRuntime(prior),
                CandidateConfigDigest = Sha256('1'),
                PriorConfigDigest = Sha256('1'),
                CandidateBackendIdentityDigest = Sha256('2'),
                PriorBackendIdentityDigest = Sha256('2'),
                CandidateAwsBindingDigest = Sha256('3'),
                PriorAwsBindingDigest = Sha256('3'),
                CanaryDigest = Sha256((char)('3' + runNumber)),
                CleanupSemantics = "delete_object_delete_bucket_verify_no_such_bucket",
                StartedAtUtc = completed.AddSeconds(-9),
                CandidateCreateCompletedAtUtc = completed.AddSeconds(-8),
                CandidateReadCompletedAtUtc = completed.AddSeconds(-7),
                CandidateStoppedAtUtc = completed.AddSeconds(-6),
                PriorStartedAtUtc = completed.AddSeconds(-5),
                PriorReadCompletedAtUtc = completed.AddSeconds(-4),
                CleanupRequestedAtUtc = completed.AddSeconds(-3),
                CleanupVerifiedAtUtc = completed.AddSeconds(-2),
                CandidateRestoredAtUtc = completed.AddSeconds(-1),
                CompletedAtUtc = completed,
            });
        }

        Assert.Equal(
            SealedRuntimeEvidenceValidator.IdentityKey(priorIdentity),
            SealedRuntimeEvidenceValidator.IdentityKey(
                Assert.Single(evidence[0].RollbackProofs).Prior));
        return (manifest, candidate, policy, evidence, prior);
    }

    private static RealAzureCredentialRotationProof RotationProof(
        int attempt,
        RealAzureLoadEvidence run,
        DateTimeOffset completed) => new()
    {
        ScenarioId = "credential-rotation",
        Service = "secretsmanager",
        Operation = "GetSecretValue",
        RotationKind = "azure_backend_identity",
        AuthenticationMode = "workload_identity",
        BackendKind = "key_vault",
        IdentityAClientId = $"client-a-{attempt}",
        IdentityAObjectId = $"object-a-{attempt}",
        IdentityBClientId = $"client-b-{attempt}",
        IdentityBObjectId = $"object-b-{attempt}",
        RoleAssignmentAId = RoleAssignmentId(KeyVaultScope(attempt), attempt),
        RoleAssignmentBId = RoleAssignmentId(KeyVaultScope(attempt), attempt + 100),
        RoleDefinitionId = "b86a8fe4-44ce-4948-aee5-eccb2c155cd7",
        RoleScopeDigestA = Digest(KeyVaultScope(attempt)),
        RoleScopeDigestB = Digest(KeyVaultScope(attempt)),
        FederatedIssuerDigest = Sha256('1'),
        FederatedSubjectDigest = Sha256('2'),
        FederatedAudienceDigest = Sha256('3'),
        RuntimeArtifactDigestA = run.Candidate.ArtifactDigest,
        RuntimeArtifactDigestB = run.Candidate.ArtifactDigest,
        CandidateConfigDigestA = run.Candidate.ConfigDigest,
        CandidateConfigDigestB = run.Candidate.ConfigDigest,
        ProxyConfigDigestA = Sha256('4'),
        ProxyConfigDigestB = Sha256('4'),
        AwsBindingDigestA = Sha256('5'),
        AwsBindingDigestB = Sha256('5'),
        BackendTargetDigestA = Sha256('6'),
        BackendTargetDigestB = Sha256('6'),
        FederatedCredentialCompletions = 2,
        RevocationPolls = 1,
        GreenReadCompletions = 3,
        OldAccessDeniedCompletions = 1,
        OldAccessDeniedErrorCode = "AccessDeniedException",
        OldAccessDeniedHttpStatus = 403,
        StartedAtUtc = completed.AddSeconds(-4),
        RevocationRequestedAtUtc = completed.AddSeconds(-3),
        OldAccessDeniedAtUtc = completed.AddSeconds(-1),
        CompletedAtUtc = completed,
    };

    private static string Sha256(char value) => "sha256:" + new string(value, 64);

    private static string KeyVaultScope(int attempt) =>
        "/subscriptions/11111111-1111-1111-1111-111111111111" +
        $"/resourceGroups/rotation-rg-{attempt}" +
        $"/providers/Microsoft.KeyVault/vaults/rotation-vault-{attempt}";

    private static string RoleAssignmentId(string scope, int suffix) =>
        scope
        + "/providers/Microsoft.Authorization/roleAssignments/"
        + $"00000000-0000-0000-0000-{suffix:D12}";

    private static string Digest(string value) =>
        "sha256:" + Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(value.ToLowerInvariant())));

    private static SloQualificationProfile Profile() => new()
    {
        Id = "s3-basic-object-crud",
        Version = 1,
        Services =
        [
            new SloQualificationProfileService
            {
                Service = "s3",
                Operations = ["PutObject"],
            }
        ],
    };

    private static SloQualificationCandidate CandidateIdentity() => new()
    {
        GitSha = "0123456789abcdef0123456789abcdef01234567",
        ArtifactDigest = Sha256('a'),
        ConfigDigest = Sha256('b'),
        QualificationMode = "sealed",
        Runtime = CandidateRuntime(),
    };

    private static QualificationSealedRuntimeIdentity CandidateRuntime() => new()
    {
        SchemaVersion = 1,
        Role = "candidate",
        Profile = new QualificationSealedRuntimeProfile
        {
            Id = "s3-basic-object-crud",
            Version = 1,
        },
        Status = "candidate",
        Eligibility = new QualificationSealedRuntimeEligibility(),
        Source = new QualificationSealedRuntimeSource
        {
            Repository = "example/repo",
            Sha = "0123456789abcdef0123456789abcdef01234567",
            Ref = "refs/heads/main",
        },
        Runtime = new QualificationSealedRuntimeDigests
        {
            AggregateDigest = Sha256('a'),
            ExecutableDigest = Sha256('c'),
            ManifestDigest = Sha256('d'),
        },
        Producer = new QualificationSealedRuntimeProducer
        {
            Workflow = ".github/workflows/sealed-runtime.yml",
            EventName = "workflow_dispatch",
            RunId = 42,
            RunAttempt = 1,
            RunUrl = "https://github.com/example/repo/actions/runs/42",
            AttemptUrl = "https://github.com/example/repo/actions/runs/42/attempts/1",
            RunStartedAt = Now.AddDays(-2),
        },
        Artifact = new QualificationSealedRuntimeArtifact
        {
            Id = 7,
            Name = "aws2azure-sealed-linux-x64-" + new string('a', 64) +
                   "-run-42-attempt-1",
            UploadDigest = Sha256('e'),
            CreatedAt = Now.AddDays(-2),
            ExpiresAt = Now.AddDays(30),
        },
        Attestation = new QualificationSealedRuntimeAttestation
        {
            PredicateType = "https://slsa.dev/provenance/v1",
            Repository = "example/repo",
            SignerWorkflow = "example/repo/.github/workflows/sealed-runtime.yml",
            SourceSha = "0123456789abcdef0123456789abcdef01234567",
            SourceRef = "refs/heads/main",
            RunInvocationUrl = "https://github.com/example/repo/actions/runs/42/attempts/1",
            BundleDigest = Sha256('f'),
            ExecutableSubjectName = "Aws2Azure.Proxy",
            ExecutableSubjectDigest = Sha256('c'),
            ManifestSubjectName = "sealed-runtime-manifest.json",
            ManifestSubjectDigest = Sha256('d'),
        },
    };

    private static QualificationSealedRuntimeIdentity PriorRuntime(
        ApprovedRuntimeRecord record) => new()
    {
        SchemaVersion = 1,
        Role = "prior",
        Profile = new QualificationSealedRuntimeProfile
        {
            Id = record.Profile.Id,
            Version = record.Profile.Version,
        },
        Status = record.Status,
        Eligibility = new QualificationSealedRuntimeEligibility
        {
            RollbackBaselineEligible = record.Eligibility.RollbackBaselineEligible,
            PromotionEligible = record.Eligibility.PromotionEligible,
        },
        LedgerRecordDigest = ApprovedRuntimeLedgerExport.Create(record).LedgerRecordDigest,
        Source = new QualificationSealedRuntimeSource
        {
            Repository = record.Runtime.SourceRepository,
            Sha = record.Runtime.SourceSha,
            Ref = record.Attestation.SourceRef,
        },
        Runtime = new QualificationSealedRuntimeDigests
        {
            AggregateDigest = record.Runtime.AggregateDigest,
            ExecutableDigest = record.Runtime.ExecutableDigest,
            ManifestDigest = record.Attestation.ManifestSubjectDigest,
        },
        Producer = new QualificationSealedRuntimeProducer
        {
            Workflow = record.Producer.Workflow,
            EventName = "workflow_dispatch",
            RunId = record.Producer.RunId,
            RunAttempt = record.Producer.RunAttempt,
            RunUrl = record.Producer.RunUrl,
            AttemptUrl = record.Producer.RunUrl + "/attempts/" + record.Producer.RunAttempt,
            RunStartedAt = record.Artifact.CreatedAt.AddMinutes(-2),
        },
        Artifact = new QualificationSealedRuntimeArtifact
        {
            Id = record.Artifact.Id,
            Name = record.Artifact.Name,
            UploadDigest = record.Artifact.UploadDigest,
            CreatedAt = record.Artifact.CreatedAt,
            ExpiresAt = record.Artifact.ExpiresAt,
        },
        Attestation = new QualificationSealedRuntimeAttestation
        {
            PredicateType = record.Attestation.PredicateType,
            Repository = record.Attestation.Repository,
            SignerWorkflow = record.Attestation.SignerWorkflow,
            SourceSha = record.Attestation.SourceSha,
            SourceRef = record.Attestation.SourceRef,
            RunInvocationUrl =
                record.Producer.RunUrl + "/attempts/" + record.Producer.RunAttempt,
            BundleDigest = Sha256('9'),
            ExecutableSubjectName = record.Attestation.SubjectName,
            ExecutableSubjectDigest = record.Attestation.SubjectDigest,
            ManifestSubjectName = record.Attestation.ManifestSubjectName,
            ManifestSubjectDigest = record.Attestation.ManifestSubjectDigest,
        },
    };

    private static QualificationRunArtifactIdentity RunSelection(
        long runId,
        string workflow,
        string artifactName) => new()
    {
        SchemaVersion = 1,
        Repository = "example/repo",
        WorkflowPath = workflow,
        EventName = "workflow_dispatch",
        Conclusion = "success",
        RunId = runId,
        RunAttempt = 1,
        RunUrl = $"https://github.com/example/repo/actions/runs/{runId}",
        HeadSha = "0123456789abcdef0123456789abcdef01234567",
        HeadRef = "refs/heads/main",
        Artifact = new QualificationRunArtifact
        {
            Id = runId + 1000,
            Name = artifactName,
            UploadDigest = Sha256('6'),
            CreatedAt = Now.AddHours(-1),
            ExpiresAt = Now.AddDays(1),
        },
    };

    private static RealAzureLoadQualificationMetadata Metadata() => new()
    {
        RunId = "qualification",
        RunUrl = "https://github.com/example/repo/actions/runs/qualification",
        RunAttempt = 1,
        GeneratedAtUtc = Now,
    };

    private static string FindRepoRoot()
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
        throw new DirectoryNotFoundException("Could not locate repository root.");
    }
}
