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
        Assert.Equal(8, policy.LoadShape.Concurrency);
        Assert.Equal(300, policy.LoadShape.RequestedDurationSeconds);
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
        RoleAssignmentAId =
            $"/subscriptions/sub/resourceGroups/rg/providers/Microsoft.Authorization/roleAssignments/a-{attempt}",
        RoleAssignmentBId =
            $"/subscriptions/sub/resourceGroups/rg/providers/Microsoft.Authorization/roleAssignments/b-{attempt}",
        RoleDefinitionId = "b86a8fe4-44ce-4948-aee5-eccb2c155cd7",
        RoleScopeDigestA = Sha256('7'),
        RoleScopeDigestB = Sha256('7'),
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
        GitSha = "0123456789abcdef",
        ArtifactDigest = "sha256:artifact",
        ConfigDigest = "sha256:config",
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
