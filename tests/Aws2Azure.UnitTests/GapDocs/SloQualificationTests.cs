using Aws2Azure.GapDocs;

namespace Aws2Azure.UnitTests.GapDocs;

public sealed class SloQualificationTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-07-16T16:00:00Z");

    [Fact]
    public void Validate_accepts_emulator_regression_as_proxy_overhead_only()
    {
        var document = Valid("emulator_regression", "passed");
        document.Signals =
        [
            Signal("throughput", "proxy_overhead", "blocking", "throughput_per_sec", min: 50)
        ];
        document.Scenarios[0].EvidenceSource = "emulator";

        var errors = SloQualificationValidator.Validate(document, Now);

        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_rejects_passing_verdict_with_blocking_finding()
    {
        var document = Valid("emulator_regression", "passed");
        document.Scenarios[0].EvidenceSource = "emulator";
        document.Findings.Add(new SloQualificationFinding
        {
            Code = "missing_scenario",
            Disposition = "blocking",
            Message = "A required scenario was missing."
        });

        var errors = SloQualificationValidator.Validate(document, Now);

        Assert.Contains(
            errors,
            error => error.Contains("must not contain blocking findings", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_rejects_inconclusive_emulator_verdict_with_breached_gate()
    {
        var document = Valid("emulator_regression", "inconclusive");
        document.Scenarios[0].EvidenceSource = "emulator";
        document.Signals[0].MeasuredValue = 49;
        document.Signals[0].MinValue = 50;

        var errors = SloQualificationValidator.Validate(document, Now);

        Assert.Contains(
            errors,
            error => error.Contains("below minimum", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_normalizes_null_finding_to_validation_errors()
    {
        var document = Valid("emulator_regression", "inconclusive");
        document.Scenarios[0].EvidenceSource = "emulator";
        document.Findings = [null!];

        var errors = SloQualificationValidator.Validate(document, Now);

        Assert.Contains(errors, error => error.Contains("findings[0].code missing", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("findings[0].message missing", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_rejects_emulator_capacity_claim_and_blocking_ab_experiment()
    {
        var emulator = Valid("emulator_regression", "passed");
        emulator.Signals[0].Source = "backend_capacity";
        var experiment = Valid("ab_experiment", "report_only");
        experiment.Signals[0].Disposition = "blocking";
        experiment.Signals[0].MaxValue = 100;

        var emulatorErrors = SloQualificationValidator.Validate(emulator, Now);
        var experimentErrors = SloQualificationValidator.Validate(experiment, Now);

        Assert.Contains(
            emulatorErrors,
            error => error.Contains("emulator_regression may only use source 'proxy_overhead'", StringComparison.Ordinal));
        Assert.Contains(
            experimentErrors,
            error => error.Contains("ab_experiment signals must be report_only", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_accepts_fresh_real_azure_qualification()
    {
        var document = Valid("real_azure_workload_qualification", "qualified");

        var errors = SloQualificationValidator.Validate(document, Now);

        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_allows_real_azure_candidate_without_numeric_signals()
    {
        var document = Valid("real_azure_workload_qualification", "candidate");
        document.Signals.Clear();
        document.Findings.Add(new SloQualificationFinding
        {
            Code = "load_evidence_missing",
            Disposition = "blocking",
            Message = "Load evidence is required before qualification."
        });

        var errors = SloQualificationValidator.Validate(document, Now);

        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_still_requires_capacity_gates_for_qualified_real_azure_artifact()
    {
        var document = Valid("real_azure_workload_qualification", "qualified");
        document.Signals.Clear();

        var errors = SloQualificationValidator.Validate(document, Now);

        Assert.Contains(errors, error => error.Contains("signals must contain", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_rejects_qualified_real_azure_run_with_zero_completions_or_low_samples()
    {
        var document = Valid("real_azure_workload_qualification", "qualified");
        document.Scenarios[0].Completions = 0;
        document.Scenarios[0].Skipped = 100;

        var errors = SloQualificationValidator.Validate(document, Now);

        Assert.Contains(errors, error => error.Contains("has zero completions", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("minimum is 100", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_rejects_failed_or_zero_completion_deterministic_required_evidence()
    {
        var document = Valid("real_azure_workload_qualification", "qualified");
        document.Scenarios.Add(new SloQualificationScenario
        {
            Id = "retry-exhaustion",
            Service = "s3",
            Operation = "PutObject",
            EvidenceSource = "deterministic",
            Completions = 0,
            Failures = 1,
            DurationSeconds = 1,
            CapturedAtUtc = Now.AddMinutes(-1),
        });

        var errors = SloQualificationValidator.Validate(document, Now);

        Assert.Contains(
            errors,
            error => error.Contains("retry-exhaustion", StringComparison.Ordinal)
                     && error.Contains("zero completions", StringComparison.Ordinal));
        Assert.Contains(
            errors,
            error => error.Contains("retry-exhaustion", StringComparison.Ordinal)
                     && error.Contains("failure rate", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_rejects_stale_individual_source_run()
    {
        var document = Valid("real_azure_workload_qualification", "qualified");
        document.Provenance.WindowStartUtc = Now.AddHours(-80);
        document.Provenance.SourceRuns[0].WindowStartUtc = Now.AddHours(-80);
        document.Provenance.SourceRuns[0].WindowEndUtc = Now.AddHours(-79);

        var errors = SloQualificationValidator.Validate(document, Now);

        Assert.Contains(
            errors,
            error => error.Contains("source_runs[0]", StringComparison.Ordinal)
                     && error.Contains("stale", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_requires_qualification_correctness_and_load_run_ids_to_be_distinct()
    {
        var document = Valid("real_azure_workload_qualification", "qualified");
        document.Provenance.RunId = document.Provenance.SourceRuns[0].RunId;

        var errors = SloQualificationValidator.Validate(document, Now);

        Assert.Contains(
            errors,
            error => error.Contains(
                "qualification provenance.run_id must be distinct",
                StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_rejects_stale_or_short_real_azure_qualification()
    {
        var document = Valid("real_azure_workload_qualification", "qualified");
        document.Provenance.GeneratedAtUtc = Now.AddHours(-80);
        document.Scenarios[0].CapturedAtUtc = Now.AddHours(-80);
        document.Scenarios[0].DurationSeconds = 60;

        var errors = SloQualificationValidator.Validate(document, Now);

        Assert.Contains(errors, error => error.Contains("artifact is stale", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("duration is 60", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("measurement is stale", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_rejects_failure_rate_above_published_limit()
    {
        var document = Valid("real_azure_workload_qualification", "qualified");
        document.Scenarios[0].Completions = 990;
        document.Scenarios[0].Failures = 10;

        var errors = SloQualificationValidator.Validate(document, Now);

        Assert.Contains(errors, error => error.Contains("failure rate", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_rejects_qualified_artifact_when_blocking_signal_misses_threshold()
    {
        var document = Valid("real_azure_workload_qualification", "qualified");
        document.Signals[0].MeasuredValue = 1500;

        var errors = SloQualificationValidator.Validate(document, Now);

        Assert.Contains(errors, error => error.Contains(
            "signal 'p99' measured 1500 above maximum 1000",
            StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_allows_unqualified_candidate_to_report_incomplete_evidence()
    {
        var document = Valid("real_azure_workload_qualification", "candidate");
        document.Scenarios[0].Completions = 0;
        document.Scenarios[0].DurationSeconds = 0;

        var errors = SloQualificationValidator.Validate(document, Now);

        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_rejects_scenario_outside_profile_and_future_provenance()
    {
        var document = Valid("real_azure_workload_qualification", "candidate");
        document.Scenarios[0].Operation = "DeleteBucket";
        document.Provenance.GeneratedAtUtc = Now.AddMinutes(10);

        var errors = SloQualificationValidator.Validate(document, Now);

        Assert.Contains(errors, error => error.Contains(
            "operation 's3/DeleteBucket' is not declared by the profile",
            StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains(
            "generated_at_utc must not be in the future",
            StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_rejects_non_finite_rules_thresholds_and_durations()
    {
        var document = Valid("real_azure_workload_qualification", "qualified");
        document.Rules.MinDurationSeconds = double.NaN;
        document.Rules.MaxFailureRate = double.NaN;
        document.Signals[0].MaxValue = double.NaN;
        document.Scenarios[0].DurationSeconds = double.PositiveInfinity;

        var errors = SloQualificationValidator.Validate(document, Now);

        Assert.Contains(errors, error => error.Contains("min_duration_seconds", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("max_failure_rate", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("max_value", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("duration_seconds", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_rejects_qualified_capacity_gate_attached_only_to_emulator()
    {
        var document = Valid("real_azure_workload_qualification", "qualified");
        document.Scenarios.Add(new SloQualificationScenario
        {
            Id = "emulator-put",
            Service = "s3",
            Operation = "PutObject",
            EvidenceSource = "emulator",
            Completions = 1000,
            DurationSeconds = 300,
            CapturedAtUtc = Now.AddMinutes(-1)
        });
        document.Signals[0].ScenarioId = "emulator-put";

        var errors = SloQualificationValidator.Validate(document, Now);

        Assert.Contains(errors, error => error.Contains(
            "backend_capacity signal must reference a real_azure scenario",
            StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains(
            "requires a blocking signal for a real_azure scenario",
            StringComparison.Ordinal));
    }

    [Fact]
    public void Load_normalizes_explicit_null_sections_for_validation()
    {
        var path = Path.Combine(AppContext.BaseDirectory, $"qualification-null-{Guid.NewGuid():N}.yaml");
        File.WriteAllText(
            path,
            """
            schema_version: 1
            artifact_kind: real_azure_workload_qualification
            verdict: candidate
            profile:
            candidate:
            provenance:
            rules:
            signals:
            scenarios:
            """);

        try
        {
            var document = SloQualificationLoader.Load(path);
            var errors = SloQualificationValidator.Validate(document, Now);

            Assert.NotEmpty(errors);
            Assert.Contains(errors, error => error.Contains("profile.id missing", StringComparison.Ordinal));
            Assert.Contains(errors, error => error.Contains("scenarios must contain", StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Validate_rejects_artifact_age_outside_timespan_range()
    {
        var document = Valid("real_azure_workload_qualification", "qualified");
        document.Rules.MaxArtifactAgeHours = int.MaxValue;

        var errors = SloQualificationValidator.Validate(document, Now);

        Assert.Contains(errors, error => error.Contains(
            "within the TimeSpan range",
            StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_computes_failure_rate_without_long_overflow()
    {
        var document = Valid("real_azure_workload_qualification", "qualified");
        document.Scenarios[0].Completions = long.MaxValue;
        document.Scenarios[0].Failures = long.MaxValue;

        var errors = SloQualificationValidator.Validate(document, Now);

        Assert.Contains(errors, error => error.Contains("failure rate", StringComparison.Ordinal));
    }

    private static SloQualificationDocument Valid(string artifactKind, string verdict)
    {
        var document = new SloQualificationDocument
        {
            SchemaVersion = 1,
            ArtifactKind = artifactKind,
            Verdict = verdict,
            SourceFile = "qualification.yaml",
            Profile = new SloQualificationProfile
            {
                Id = "s3-basic-object-crud",
                Version = 1,
                Services =
                [
                    new SloQualificationProfileService
                    {
                        Service = "s3",
                        Operations = ["PutObject", "GetObject"]
                    }
                ]
            },
            Candidate = new SloQualificationCandidate
            {
                GitSha = "0123456789abcdef",
                ArtifactDigest = "sha256:artifact",
                ConfigDigest = "sha256:config"
            },
            Provenance = new SloQualificationProvenance
            {
                RunId = "124",
                RunUrl = "https://github.com/example/repo/actions/runs/124",
                RunAttempt = 1,
                GeneratedAtUtc = Now.AddMinutes(-1),
                WindowStartUtc = Now.AddMinutes(-10),
                WindowEndUtc = Now.AddMinutes(-1),
                Region = "eastus2",
                BackendDescription = "Blob Storage Standard_LRS",
                CorrectnessRun = new SloQualificationSourceRun
                {
                    RunId = "122",
                    RunUrl = "https://github.com/example/repo/actions/runs/122",
                    RunAttempt = 1,
                    WindowStartUtc = Now.AddMinutes(-20),
                    WindowEndUtc = Now.AddMinutes(-11),
                    GitSha = "0123456789abcdef",
                    ArtifactDigest = "sha256:artifact",
                    ConfigDigest = "sha256:config"
                },
                SourceRuns =
                [
                    new SloQualificationSourceRun
                    {
                        RunId = "123",
                        RunUrl = "https://github.com/example/repo/actions/runs/123",
                        RunAttempt = 1,
                        WindowStartUtc = Now.AddMinutes(-10),
                        WindowEndUtc = Now.AddMinutes(-1),
                        GitSha = "0123456789abcdef",
                        ArtifactDigest = "sha256:artifact",
                        ConfigDigest = "sha256:config"
                    }
                ]
            },
            Rules = new SloQualificationRules
            {
                MaxArtifactAgeHours = 72,
                MinSamplesPerScenario = 100,
                MinDurationSeconds = 300,
                MaxFailureRate = 0.001,
                ZeroCompletionsDisqualify = true,
                OnlySkippedRealAzureDisqualifies = true,
                MinDistinctRuns = 1
            },
            Signals =
            [
                Signal("p99", "backend_capacity", "blocking", "p99_ms", max: 1000),
                Signal("network", "network_noise", "report_only", "p95_ms")
            ],
            Scenarios =
            [
                new SloQualificationScenario
                {
                    Id = "put-object",
                    Service = "s3",
                    Operation = "PutObject",
                    EvidenceSource = "real_azure",
                    Completions = 1000,
                    Failures = 0,
                    DurationSeconds = 300,
                    CapturedAtUtc = Now.AddMinutes(-1)
                }
            ]
        };
        if (artifactKind == "real_azure_workload_qualification")
        {
            QualificationTrustTestData.AttachSealedTrust(document, Now);
        }
        return document;
    }

    private static SloQualificationSignal Signal(
        string id,
        string source,
        string disposition,
        string metric,
        double? min = null,
        double? max = null) => new()
    {
        Id = id,
        Source = source,
        Disposition = disposition,
        Metric = metric,
        MinValue = min,
        MaxValue = max,
        ScenarioId = "put-object",
        MeasuredValue = metric switch
        {
            "throughput_per_sec" => 100,
            "p99_ms" => 500,
            _ => 100
        },
        Samples = 1000,
        CapturedAtUtc = Now.AddMinutes(-1)
    };
}
