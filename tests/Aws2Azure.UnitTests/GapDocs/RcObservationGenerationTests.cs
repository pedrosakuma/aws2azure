using Aws2Azure.GapDocs;

namespace Aws2Azure.UnitTests.GapDocs;

public sealed class RcObservationGenerationTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 18, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Generator_resolves_reviewed_thresholds_and_emits_manifest_bound_pass()
    {
        var data = CreateData();

        var result = Generate(data);

        Assert.Equal("pass", result.Evidence.Decision.Verdict);
        Assert.Null(result.Evidence.Restoration);
        Assert.Equal(40, result.Evidence.Metrics.Single(metric =>
            metric.Id == "representative-load-throughput").Threshold);
        Assert.Equal(0, result.Evidence.Metrics.Single(metric =>
            metric.Id == "operation-failure-rate").Threshold);
        var candidate = result.Evidence.Cohorts.Single(cohort =>
            cohort.Role == "candidate");
        Assert.Contains(candidate.OperationDiagnostics, diagnostic =>
            diagnostic.Operation == "GetObject"
            && diagnostic.Completions + diagnostic.Failures == 1000);
        Assert.Equal(
            data.Capture.Observation.MeasurementEndedAtUtc,
            result.Evidence.Observation.EndedAtUtc);
        Assert.Empty(RcObservationValidator.Validate(
            result.Evidence,
            result.Binding,
            Now));
    }

    [Fact]
    public void Generator_rejects_diagnostics_that_do_not_match_aggregate_samples()
    {
        var data = CreateData();
        data.Capture.Cohorts[0] = data.Capture.Cohorts[0] with
        {
            OperationDiagnostics = OperationDiagnostics(4999, 1000),
        };

        var exception = Assert.Throws<InvalidDataException>(() => Generate(data));

        Assert.Contains("failure-rate samples", exception.Message);
    }

    [Fact]
    public void Observation_policies_bind_the_reviewed_per_profile_load_shapes()
    {
        var root = FindRepoRoot();
        var secrets = RcObservationPolicyLoader.Load(Path.Combine(
            root,
            "docs",
            "workloads",
            "observation",
            "secretsmanager-basic-lifecycle.yaml"));
        var s3 = RcObservationPolicyLoader.Load(Path.Combine(
            root,
            "docs",
            "workloads",
            "observation",
            "s3-basic-object-crud.yaml"));

        Assert.Equal(5, secrets.LoadShape.CandidateConcurrency);
        Assert.Equal(5, secrets.LoadShape.StableConcurrency);
        Assert.Equal(
            "sha256:a1eb9a5a04c0d38239ef7266b1c4a9d4e7e154edaf741b5d87db1a680a5f57bb",
            secrets.LoadShape.OperationMixIdentity);
        Assert.Equal(8, s3.LoadShape.CandidateConcurrency);
        Assert.Equal(8, s3.LoadShape.StableConcurrency);
        Assert.Equal(
            "sha256:83b92539e232cece8433271d2d04284eeb87e2509d4a12b7f3c3e86334dea9d3",
            s3.LoadShape.OperationMixIdentity);
    }

    [Fact]
    public void Generator_rejects_capture_load_shape_drift()
    {
        var data = CreateData();
        data.Capture.LoadShape.CandidateConcurrency++;

        var exception = Assert.Throws<InvalidDataException>(() => Generate(data));

        Assert.Contains("capture is incomplete", exception.Message);
    }

    [Fact]
    public void Generator_rejects_cohort_membership_that_does_not_match_load_shape()
    {
        var data = CreateData();
        data.Capture.Cohorts[0] = data.Capture.Cohorts[0] with
        {
            MemberDigests = data.Capture.Cohorts[0].MemberDigests.Skip(1).ToArray(),
        };

        var exception = Assert.Throws<InvalidDataException>(() => Generate(data));

        Assert.Contains("capture cohorts", exception.Message);
    }

    [Fact]
    public void Rendered_evidence_round_trips_archive_ghcr_and_identity_binding()
    {
        var result = Generate(CreateData());
        var directory = Path.Combine(
            FindRepoRoot(),
            "artifacts",
            "unit-rc-observation-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "observation.yaml");
        try
        {
            Directory.CreateDirectory(directory);
            RcObservationRenderer.Render(result.Evidence, path);

            var loaded = RcObservationLoader.Load(path);

            Assert.Equal(
                result.Evidence.ReleaseCandidate.GhcrInputs.IndexDigest,
                loaded.ReleaseCandidate.GhcrInputs.IndexDigest);
            Assert.Empty(RcObservationValidator.Validate(loaded, result.Binding, Now));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Generator_records_every_breach_and_exact_prior_restoration()
    {
        var data = CreateData();
        data.Capture.Metrics.Single(metric =>
            metric.Id == "representative-load-throughput").CandidateValue = 39;
        data.Capture.Metrics.Single(metric =>
            metric.Id == "operation-failure-rate").CandidateValue = 0.001;
        data.Capture.Cohorts[0] = data.Capture.Cohorts[0] with
        {
            OperationDiagnostics = OperationDiagnostics(5000, 1000, failures: 5),
        };

        var result = Generate(data);

        Assert.Equal("rollback", result.Evidence.Decision.Verdict);
        Assert.NotNull(result.Evidence.Restoration);
        Assert.All(result.Evidence.RollbackTriggers, trigger =>
        {
            Assert.Equal("fired", trigger.Status);
            Assert.False(trigger.OverrideApplied);
            Assert.False(trigger.Suppressed);
        });
        Assert.Equal(
            data.Prior.Runtime.AggregateDigest,
            result.Evidence.Restoration!.RuntimeDigest);
        Assert.Empty(RcObservationValidator.Validate(
            result.Evidence,
            result.Binding,
            Now));
    }

    [Fact]
    public void Passing_capture_requires_verified_restoration_of_exact_prior_environment()
    {
        var data = CreateData();
        data.Capture.Restoration = data.Capture.Restoration! with
        {
            RuntimeIdentityDigest = data.Input.CandidateIdentityDigest,
            RuntimeDigest = data.Candidate.Runtime.AggregateDigest,
            ConfigDigest = Digest('0'),
        };

        var exception = Assert.Throws<InvalidDataException>(() => Generate(data));

        Assert.Contains(
            "capture is incomplete",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Workflow_selection_rejects_candidate_not_in_approved_ledger()
    {
        var data = CreateData();
        var driftedSha = new string('f', 40);
        data.Candidate.Source.Sha = driftedSha;
        data.Candidate.Attestation.SourceSha = driftedSha;

        var exception = Assert.Throws<InvalidDataException>(() => Generate(data));

        Assert.Contains("approved profile ledger", exception.Message);
    }

    [Fact]
    public void Workflow_selection_exports_the_exact_committed_rollback_target()
    {
        var data = CreateData();

        var export = ApprovedRuntimeLedgerExport.CreateRollbackTarget(data.ApprovedRuntime);

        Assert.Equal(
            data.Prior.LedgerRecordDigest,
            export.LedgerRecordDigest);
        Assert.Equal(data.Prior.Source.Sha, export.Record.Runtime.SourceSha);
        Assert.Equal(data.Prior.Runtime.AggregateDigest,
            export.Record.Runtime.AggregateDigest);
        Assert.Equal(data.Prior.Artifact.Id, export.Record.Artifact.Id);
        Assert.Equal(data.Prior.Artifact.UploadDigest,
            export.Record.Artifact.UploadDigest);
    }

    [Fact]
    public void Archive_inputs_must_match_the_exact_approved_runtime_and_attempt()
    {
        var data = CreateData();
        data.ArchiveSelection.Workload.ApprovedRuntimeAggregateDigest = Digest('0');
        data.ArchiveSelection.Producer = data.ArchiveSelection.Producer with
        {
            RunAttempt = data.ArchiveSelection.Producer.RunAttempt + 1,
        };

        var exception = Assert.Throws<InvalidDataException>(() => Generate(data));

        Assert.Contains("archive inputs", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Ghcr_and_canonical_identity_must_match_the_exact_archive_interfaces()
    {
        var data = CreateData();
        data.GhcrSelection.ArchiveContentDigest = Digest('7');

        var ghcrException = Assert.Throws<InvalidDataException>(() => Generate(data));
        Assert.Contains("GHCR inputs", ghcrException.Message);

        data = CreateData();
        data.IdentitySelection.GhcrInputsDigest = Digest('7');

        var identityException = Assert.Throws<InvalidDataException>(() => Generate(data));
        Assert.Contains("Canonical RC identity", identityException.Message);
    }

    [Fact]
    public void Rehashed_generated_tampering_fails_the_trusted_binding()
    {
        var data = CreateData();
        var result = Generate(data);
        var trustedDigest = result.Binding.ExpectedEvidenceDigest;
        var tampered = result.Evidence with
        {
            Metrics =
            [
                result.Evidence.Metrics[0] with { StableValue = 99 },
                result.Evidence.Metrics[1],
            ],
        };
        tampered = tampered with
        {
            EvidenceDigest = RcObservationIntegrity.ComputePayloadDigest(tampered),
        };

        var errors = RcObservationValidator.Validate(tampered, result.Binding, Now);

        Assert.NotEqual(trustedDigest, tampered.EvidenceDigest);
        Assert.Contains(errors, error => error.Contains(
            "digest bound by the trusted RC manifest",
            StringComparison.Ordinal));
    }

    [Fact]
    public void Observation_policy_cannot_weaken_qualification_sample_or_age_rules()
    {
        var data = CreateData();
        data.Policy.MinimumSamplesPerCohort--;
        data.Policy.MaximumEvidenceAgeHours++;

        var exception = Assert.Throws<InvalidDataException>(() => Generate(data));

        Assert.Contains("reviewed workload qualification policy", exception.Message);
    }

    [Fact]
    public void Observation_policy_cannot_omit_a_blocking_qualification_threshold()
    {
        var data = CreateData();
        data.Policy.Metrics.RemoveAll(metric =>
            metric.ThresholdSource == "qualification_signal");

        var exception = Assert.Throws<InvalidDataException>(() => Generate(data));

        Assert.Contains("reviewed workload qualification policy", exception.Message);
    }

    [Fact]
    public void Committed_observation_policies_match_approved_profile_contracts()
    {
        var root = FindRepoRoot();
        var workloadsRoot = Path.Combine(root, "docs", "workloads");
        var policies = RcObservationPolicyLoader.LoadAll(
            Path.Combine(workloadsRoot, "observation"));
        var workloads = WorkloadGaManifestLoader.LoadAll(workloadsRoot);
        var approved = ApprovedRuntimeLedgerLoader.LoadAll(
            Path.Combine(workloadsRoot, "approved-runtimes"));

        var errors = RcObservationPolicyValidator.Validate(
            policies,
            workloads,
            approved,
            workloadsRoot);

        Assert.Empty(errors);
    }

    [Fact]
    public void Bootstrap_approved_runtime_with_unresolved_qualification_does_not_require_observation_policy()
    {
        // A brand-new profile's approved-runtime ledger legitimately starts as a
        // rollback-baseline-only "bootstrap" record while its qualification
        // policy still carries an unresolved blocking capacity floor — no
        // comparable production-shaped runs exist yet to review a threshold
        // from (issue #627's dynamodb-basic-crud bootstrap). An RC observation
        // policy has nothing meaningful to validate against until that floor
        // is resolved, so it must not be required yet.
        var tempRoot = Path.Combine(Path.GetTempPath(), $"aws2azure-rcobs-{Guid.NewGuid():N}");
        var qualificationDir = Path.Combine(tempRoot, "qualification");
        Directory.CreateDirectory(qualificationDir);
        File.WriteAllText(
            Path.Combine(qualificationDir, "new-profile.yaml"),
            """
            schema_version: 1
            profile_id: new-profile
            profile_version: 1
            rules:
              max_artifact_age_hours: 72
              min_samples_per_scenario: 300
              min_duration_seconds: 300
              max_failure_rate: 0
              zero_completions_disqualify: true
              only_skipped_real_azure_disqualifies: true
              min_distinct_runs: 3
            load_shape:
              concurrency: 8
              requested_duration_seconds: 300
            scenarios:
              - id: representative-load
                service: new
                operation: Get
                evidence_source: real_azure
                signals:
                  - id: representative-load-throughput
                    source: backend_capacity
                    disposition: blocking
                    metric: throughput_per_sec
                    threshold_status: unresolved
                    threshold_reason: "no comparable runs yet"
            """);
        var approved = new ApprovedRuntimeRecord
        {
            Profile = new ApprovedRuntimeProfile { Id = "new-profile", Version = 1 },
            Status = "bootstrap",
        };

        try
        {
            var errors = RcObservationPolicyValidator.Validate(
                [],
                [],
                [approved],
                tempRoot);

            Assert.Empty(errors);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Approved_runtime_with_unresolved_qualification_still_requires_observation_policy()
    {
        // Only a "bootstrap" record is exempt from the observation-policy
        // requirement while its qualification is unresolved. An "approved"
        // record must always have a resolved qualification, so an inconsistent
        // approved+unresolved combination must still be reported rather than
        // silently masked by the bootstrap exemption.
        var tempRoot = Path.Combine(Path.GetTempPath(), $"aws2azure-rcobs-{Guid.NewGuid():N}");
        var qualificationDir = Path.Combine(tempRoot, "qualification");
        Directory.CreateDirectory(qualificationDir);
        File.WriteAllText(
            Path.Combine(qualificationDir, "new-profile.yaml"),
            """
            schema_version: 1
            profile_id: new-profile
            profile_version: 1
            rules:
              max_artifact_age_hours: 72
              min_samples_per_scenario: 300
              min_duration_seconds: 300
              max_failure_rate: 0
              zero_completions_disqualify: true
              only_skipped_real_azure_disqualifies: true
              min_distinct_runs: 3
            load_shape:
              concurrency: 8
              requested_duration_seconds: 300
            scenarios:
              - id: representative-load
                service: new
                operation: Get
                evidence_source: real_azure
                signals:
                  - id: representative-load-throughput
                    source: backend_capacity
                    disposition: blocking
                    metric: throughput_per_sec
                    threshold_status: unresolved
                    threshold_reason: "no comparable runs yet"
            """);
        var approved = new ApprovedRuntimeRecord
        {
            Profile = new ApprovedRuntimeProfile { Id = "new-profile", Version = 1 },
            Status = "approved",
        };

        try
        {
            var errors = RcObservationPolicyValidator.Validate(
                [],
                [],
                [approved],
                tempRoot);

            Assert.Contains(
                errors,
                error => error.Contains(
                    "missing RC observation policy for approved runtime 'new-profile' v1",
                    StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    private static RcObservationGenerationResult Generate(TestData data) =>
        RcObservationGenerator.Generate(
            data.Capture,
            data.Policy,
            data.QualificationPolicy,
            data.Workload,
            data.Candidate,
            data.Prior,
            data.ApprovedRuntime,
            data.ArchiveSelection,
            data.GhcrSelection,
            data.IdentitySelection,
            data.Selection,
            data.Input);

    private static TestData CreateData()
    {
        var root = FindRepoRoot();
        var profile = "s3-basic-object-crud";
        var approved = ApprovedRuntimeLedgerLoader.Load(Path.Combine(
            root,
            "docs",
            "workloads",
            "approved-runtimes",
            profile + ".yaml"));
        var candidate = Candidate(approved);
        var prior = approved.Qualification!.RollbackTarget!;
        var started = Now.AddMinutes(-70);
        var measurementEnded = started.AddMinutes(60);
        var restorationStarted = measurementEnded.AddSeconds(1);
        var restorationVerified = restorationStarted.AddMinutes(2);
        var environment = new RcObservationAzureEnvironment
        {
            BackendKind = "blob",
            Region = "eastus2",
            BackendIdentityDigest = Digest('1'),
            ConfigDigest = Digest('2'),
            AwsBindingDigest = Digest('3'),
        };
        var candidateIdentityDigest = Digest('4');
        var priorIdentityDigest = Digest('5');
        var workloadManifestDigest = Digest('a');
        var observationPolicy = RcObservationPolicyLoader.Load(Path.Combine(
            root,
            "docs",
            "workloads",
            "observation",
            profile + ".yaml"));
        var capture = new RcObservationCapture
        {
            SchemaVersion = 1,
            Profile = new RcObservationCaptureProfile
            {
                Id = profile,
                Version = 1,
            },
            Azure = environment,
            Observation = new RcObservationCaptureWindow
            {
                StartedAtUtc = started,
                MeasurementEndedAtUtc = measurementEnded,
                EndedAtUtc = restorationVerified,
                RequestedWindowMinutes = 60,
            },
            LoadShape = new RcObservationCaptureLoadShape
            {
                CandidateConcurrency =
                    observationPolicy.LoadShape.CandidateConcurrency,
                StableConcurrency =
                    observationPolicy.LoadShape.StableConcurrency,
                OperationMixIdentity =
                    observationPolicy.LoadShape.OperationMixIdentity,
            },
            Cohorts =
            [
                Cohort(
                    "candidate-run",
                    "candidate",
                    candidateIdentityDigest,
                    candidate.Runtime.AggregateDigest,
                    environment,
                    started,
                    restorationStarted,
                    Digest('6'),
                    observationPolicy.LoadShape.CandidateConcurrency,
                    5000,
                    1000),
                Cohort(
                    "stable-run",
                    "stable",
                    priorIdentityDigest,
                    prior.Runtime.AggregateDigest,
                    environment,
                    started,
                    restorationVerified,
                    Digest('7'),
                    observationPolicy.LoadShape.StableConcurrency,
                    4500,
                    900),
            ],
            Metrics =
            [
                new RcObservationCaptureMetric
                {
                    Id = "representative-load-throughput",
                    Unit = "throughput_per_sec",
                    CandidateValue = 50,
                    StableValue = 45,
                    CandidateSamples = 1000,
                    StableSamples = 900,
                    CapturedAtUtc = measurementEnded,
                },
                new RcObservationCaptureMetric
                {
                    Id = "operation-failure-rate",
                    Unit = "ratio",
                    CandidateValue = 0,
                    StableValue = 0,
                    CandidateSamples = 5000,
                    StableSamples = 4500,
                    CapturedAtUtc = measurementEnded,
                },
            ],
            Restoration = new RcObservationRestoration
            {
                Verified = true,
                RuntimeIdentityDigest = priorIdentityDigest,
                RuntimeDigest = prior.Runtime.AggregateDigest,
                BackendIdentityDigest = environment.BackendIdentityDigest,
                ConfigDigest = environment.ConfigDigest,
                AwsBindingDigest = environment.AwsBindingDigest,
                StartedAtUtc = restorationStarted,
                VerifiedAtUtc = restorationVerified,
            },
        };
        var selection = new RcObservationCaptureArtifactSelection
        {
            SchemaVersion = 1,
            ProfileId = profile,
            Repository = candidate.Source.Repository,
            WorkflowPath = ".github/workflows/rc-observation-real-azure.yml",
            EventName = "workflow_dispatch",
            RunId = 30000000001,
            RunAttempt = 1,
            RunUrl =
                $"https://github.com/{candidate.Source.Repository}/actions/runs/30000000001",
            AttemptUrl =
                $"https://github.com/{candidate.Source.Repository}/actions/runs/30000000001/attempts/1",
            SourceSha = new string('d', 40),
            SourceRef = "refs/heads/main",
            Artifact = new RcObservationArtifactIdentity
            {
                Id = 9000000001,
                Name =
                    "real-azure-rc-observation-capture-" + profile +
                    "-run-30000000001-attempt-1",
                UploadDigest = Digest('8'),
            },
        };
        var archiveContentDigest = Digest('e');
        var archiveRunId = 29999999999;
        var archiveRunAttempt = 2;
        var archiveSelection = new RcObservationArchiveInputSelection
        {
            SchemaVersion = 1,
            CandidateId = "v1.0.0-rc.1",
            ContentDigest = archiveContentDigest,
            SourceSha = candidate.Source.Sha,
            SourceRef = "refs/tags/v1.0.0-rc.1",
            Producer = new RcObservationArchiveProducer
            {
                Repository = candidate.Source.Repository,
                WorkflowPath = ".github/workflows/release-candidate.yml",
                EventName = "workflow_dispatch",
                RunId = archiveRunId,
                RunAttempt = archiveRunAttempt,
                AttemptUrl =
                    $"https://github.com/{candidate.Source.Repository}/actions/runs/" +
                    $"{archiveRunId}/attempts/{archiveRunAttempt}",
                SourceSha = new string('7', 40),
                SourceRef = "refs/heads/main",
            },
            Artifact = new RcObservationArtifactIdentity
            {
                Id = 8999999999,
                Name =
                    $"aws2azure-rc-archives-v1.0.0-rc.1-" +
                    $"{archiveContentDigest["sha256:".Length..]}-run-{archiveRunId}-" +
                    $"attempt-{archiveRunAttempt}",
                UploadDigest = Digest('f'),
            },
            Workload = new RcObservationArchiveWorkloadIdentity
            {
                ProfileId = profile,
                ProfileVersion = 1,
                WorkloadManifestDigest = workloadManifestDigest,
                ApprovedRuntimeLedgerDigest =
                    RcObservationRenderer.DigestFile(approved.SourceFile),
                ApprovedRuntimeSourceSha = approved.Runtime.SourceSha,
                ApprovedRuntimeAggregateDigest = approved.Runtime.AggregateDigest,
                ApprovedRuntimeExecutableDigest = approved.Runtime.ExecutableDigest,
                ApprovedRuntimeArtifact = new RcObservationArtifactIdentity
                {
                    Id = approved.Artifact.Id,
                    Name = approved.Artifact.Name,
                    UploadDigest = approved.Artifact.UploadDigest,
                },
            },
        };
        var ghcrContentDigest = Digest('d');
        var ghcrRunId = 30000000000;
        var ghcrRunAttempt = 1;
        var ghcrSelection = new RcObservationGhcrInputSelection
        {
            SchemaVersion = 1,
            CandidateId = "v1.0.0-rc.1",
            ContentDigest = ghcrContentDigest,
            SourceSha = candidate.Source.Sha,
            Producer = new RcObservationArchiveProducer
            {
                Repository = candidate.Source.Repository,
                WorkflowPath = ".github/workflows/release-candidate-image.yml",
                EventName = "workflow_dispatch",
                RunId = ghcrRunId,
                RunAttempt = ghcrRunAttempt,
                AttemptUrl =
                    $"https://github.com/{candidate.Source.Repository}/actions/runs/" +
                    $"{ghcrRunId}/attempts/{ghcrRunAttempt}",
                SourceSha = new string('d', 40),
                SourceRef = "refs/heads/main",
            },
            Artifact = new RcObservationArtifactIdentity
            {
                Id = 9000000000,
                Name =
                    $"aws2azure-rc-ghcr-v1.0.0-rc.1-" +
                    $"{ghcrContentDigest["sha256:".Length..]}-run-{ghcrRunId}-" +
                    $"attempt-{ghcrRunAttempt}",
                UploadDigest = Digest('0'),
            },
            ArchiveContentDigest = archiveSelection.ContentDigest,
            ArchiveArtifact = archiveSelection.Artifact,
            IndexDigest = Digest('1'),
        };
        var identitySelection = new RcObservationCanonicalIdentitySelection
        {
            SchemaVersion = 1,
            ArtifactKind = "release_candidate_identity",
            CandidateId = "v1.0.0-rc.1",
            IdentityDigest = Digest('9'),
            ContentDigest = Digest('2'),
            ArchiveInputsDigest = archiveSelection.ContentDigest,
            GhcrInputsDigest = ghcrSelection.ContentDigest,
        };

        return new TestData
        {
            ApprovedRuntime = approved,
            Candidate = candidate,
            Prior = prior,
            Workload = WorkloadGaManifestLoader.Load(Path.Combine(
                root,
                "docs",
                "workloads",
                profile + ".yaml")),
            QualificationPolicy = WorkloadQualificationPolicyLoader.Load(Path.Combine(
                root,
                "docs",
                "workloads",
                "qualification",
                profile + ".yaml")),
            Policy = observationPolicy,
            Capture = capture,
            ArchiveSelection = archiveSelection,
            GhcrSelection = ghcrSelection,
            IdentitySelection = identitySelection,
            Selection = selection,
            Input = new RcObservationGenerationInput
            {
                ReleaseCandidateId = "v1.0.0-rc.1",
                DecisionOwner = "release-manager",
                CandidateIdentityDigest = candidateIdentityDigest,
                PriorIdentityDigest = priorIdentityDigest,
                ApprovedRuntimeLedgerDigest =
                    archiveSelection.Workload.ApprovedRuntimeLedgerDigest,
                WorkloadManifestDigest = workloadManifestDigest,
                QualificationPolicyDigest = Digest('b'),
                ObservationPolicyDigest = Digest('c'),
                GeneratedAtUtc = Now,
            },
        };
    }

    private static QualificationSealedRuntimeIdentity Candidate(
        ApprovedRuntimeRecord record) => new()
    {
        SchemaVersion = 1,
        Role = "candidate",
        Profile = new QualificationSealedRuntimeProfile
        {
            Id = record.Profile.Id,
            Version = record.Profile.Version,
        },
        Status = "candidate",
        Eligibility = new QualificationSealedRuntimeEligibility(),
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
            AttemptUrl = record.Producer.RunUrl + "/attempts/" +
                         record.Producer.RunAttempt,
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
            RunInvocationUrl = record.Producer.RunUrl + "/attempts/" +
                               record.Producer.RunAttempt,
            BundleDigest = Digest('d'),
            ExecutableSubjectName = record.Attestation.SubjectName,
            ExecutableSubjectDigest = record.Attestation.SubjectDigest,
            ManifestSubjectName = record.Attestation.ManifestSubjectName,
            ManifestSubjectDigest = record.Attestation.ManifestSubjectDigest,
        },
    };

    private static RcObservationCohort Cohort(
        string id,
        string role,
        string identityDigest,
        string runtimeDigest,
        RcObservationAzureEnvironment environment,
        DateTimeOffset started,
        DateTimeOffset ended,
        string member,
        int memberCount,
        long failureSamples,
        long throughputSamples) => new()
    {
        Id = id,
        Role = role,
        RuntimeIdentityDigest = identityDigest,
        RuntimeDigest = runtimeDigest,
        BackendKind = environment.BackendKind,
        Region = environment.Region,
        BackendIdentityDigest = environment.BackendIdentityDigest,
        ConfigDigest = environment.ConfigDigest,
        AwsBindingDigest = environment.AwsBindingDigest,
        ObservedFromUtc = started,
        ObservedUntilUtc = ended,
        MemberDigests = Enumerable.Range(0, memberCount)
            .Select(index => member[..^2] + index.ToString("x2"))
            .ToList(),
        OperationDiagnostics = OperationDiagnostics(failureSamples, throughputSamples),
    };

    private static RcObservationOperationDiagnostic[] OperationDiagnostics(
        long failureSamples,
        long throughputSamples,
        long failures = 0)
    {
        var remainingSamples = failureSamples - throughputSamples;
        return
        [
            new RcObservationOperationDiagnostic
            {
                Service = "s3",
                Operation = "GetObject",
                Completions = throughputSamples - failures,
                Failures = failures,
                Throttles = 0,
                FirstFailure = failures == 0
                    ? null
                    : new RcObservationFirstFailure
                    {
                        Category = "aws_service",
                        StatusCode = 503,
                        ErrorCode = "ServiceUnavailable",
                    },
            },
            new RcObservationOperationDiagnostic
            {
                Service = "s3",
                Operation = "CreateBucket",
                Completions = remainingSamples,
                Failures = 0,
                Throttles = 0,
            },
            new RcObservationOperationDiagnostic
            {
                Service = "s3",
                Operation = "DeleteBucket",
                Completions = 0,
                Failures = 0,
                Throttles = 0,
            },
            new RcObservationOperationDiagnostic
            {
                Service = "s3",
                Operation = "DeleteObject",
                Completions = 0,
                Failures = 0,
                Throttles = 0,
            },
            new RcObservationOperationDiagnostic
            {
                Service = "s3",
                Operation = "HeadObject",
                Completions = 0,
                Failures = 0,
                Throttles = 0,
            },
            new RcObservationOperationDiagnostic
            {
                Service = "s3",
                Operation = "ListObjectsV2",
                Completions = 0,
                Failures = 0,
                Throttles = 0,
            },
            new RcObservationOperationDiagnostic
            {
                Service = "s3",
                Operation = "PutObject",
                Completions = 0,
                Failures = 0,
                Throttles = 0,
            },
        ];
    }

    private static string Digest(char value) => "sha256:" + new string(value, 64);

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
               && !File.Exists(Path.Combine(directory.FullName, "aws2azure.slnx")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate repository root.");
    }

    private sealed class TestData
    {
        public required ApprovedRuntimeRecord ApprovedRuntime { get; init; }
        public required QualificationSealedRuntimeIdentity Candidate { get; init; }
        public required QualificationSealedRuntimeIdentity Prior { get; init; }
        public required WorkloadGaManifest Workload { get; init; }
        public required WorkloadQualificationPolicy QualificationPolicy { get; init; }
        public required RcObservationPolicy Policy { get; init; }
        public required RcObservationCapture Capture { get; init; }
        public required RcObservationArchiveInputSelection ArchiveSelection { get; init; }
        public required RcObservationGhcrInputSelection GhcrSelection { get; init; }
        public required RcObservationCanonicalIdentitySelection IdentitySelection { get; init; }
        public required RcObservationCaptureArtifactSelection Selection { get; init; }
        public required RcObservationGenerationInput Input { get; init; }
    }
}
