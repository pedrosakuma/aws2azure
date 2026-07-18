using Aws2Azure.GapDocs;

namespace Aws2Azure.UnitTests.GapDocs;

internal static class QualificationTrustTestData
{
    public const string SourceSha = "0123456789abcdef0123456789abcdef01234567";
    public const string Repository = "example/repo";
    public const string SourceRef = "refs/heads/main";

    public static void AttachSealedTrust(
        SloQualificationDocument document,
        DateTimeOffset now)
    {
        document.Candidate.GitSha = SourceSha;
        document.Candidate.ArtifactDigest = Sha256('a');
        document.Candidate.ConfigDigest = Sha256('b');
        document.Candidate.QualificationMode = "sealed";
        document.Candidate.Runtime = CandidateRuntime(document.Profile.Id, document.Profile.Version, now);

        if (document.Provenance.CorrectnessRun is not null)
        {
            AttachSourceRun(
                document,
                document.Provenance.CorrectnessRun,
                ".github/workflows/integration-real-azure.yml",
                "real-azure-conformance",
                now);
        }
        foreach (var run in document.Provenance.SourceRuns)
        {
            AttachSourceRun(
                document,
                run,
                ".github/workflows/workload-load-real-azure.yml",
                "real-azure-workload-load-" + document.Profile.Id,
                now);
        }
    }

    public static QualificationRunArtifactIdentity RunArtifact(
        string profileId,
        long runId,
        int runAttempt,
        string workflow,
        string artifactName,
        DateTimeOffset now) => new()
    {
        SchemaVersion = 1,
        ProfileId = profileId,
        Repository = Repository,
        WorkflowPath = workflow,
        EventName = "workflow_dispatch",
        Conclusion = "success",
        RunId = runId,
        RunAttempt = runAttempt,
        RunUrl = $"https://github.com/{Repository}/actions/runs/{runId}",
        HeadSha = SourceSha,
        HeadRef = SourceRef,
        Artifact = new QualificationRunArtifact
        {
            Id = runId + 1000,
            Name = artifactName,
            UploadDigest = Sha256('6'),
            CreatedAt = now.AddHours(-1),
            ExpiresAt = now.AddDays(1),
        },
    };

    private static void AttachSourceRun(
        SloQualificationDocument document,
        SloQualificationSourceRun run,
        string workflow,
        string artifactName,
        DateTimeOffset now)
    {
        var runId = long.Parse(run.RunId, System.Globalization.CultureInfo.InvariantCulture);
        run.RunUrl = $"https://github.com/{Repository}/actions/runs/{runId}";
        run.GitSha = document.Candidate.GitSha;
        run.ArtifactDigest = document.Candidate.ArtifactDigest;
        run.ConfigDigest = document.Candidate.ConfigDigest;
        run.EvidenceArtifact = RunArtifact(
            document.Profile.Id,
            runId,
            run.RunAttempt,
            workflow,
            artifactName,
            now);
    }

    private static QualificationSealedRuntimeIdentity CandidateRuntime(
        string profileId,
        int profileVersion,
        DateTimeOffset now) => new()
    {
        SchemaVersion = 1,
        Role = "candidate",
        Profile = new QualificationSealedRuntimeProfile
        {
            Id = profileId,
            Version = profileVersion,
        },
        Status = "candidate",
        Eligibility = new QualificationSealedRuntimeEligibility(),
        Source = new QualificationSealedRuntimeSource
        {
            Repository = Repository,
            Sha = SourceSha,
            Ref = SourceRef,
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
            RunUrl = $"https://github.com/{Repository}/actions/runs/42",
            AttemptUrl = $"https://github.com/{Repository}/actions/runs/42/attempts/1",
            RunStartedAt = now.AddDays(-2),
        },
        Artifact = new QualificationSealedRuntimeArtifact
        {
            Id = 7,
            Name = "aws2azure-sealed-linux-x64-" + new string('a', 64) +
                   "-run-42-attempt-1",
            UploadDigest = Sha256('e'),
            CreatedAt = now.AddDays(-2),
            ExpiresAt = now.AddDays(30),
        },
        Attestation = new QualificationSealedRuntimeAttestation
        {
            PredicateType = "https://slsa.dev/provenance/v1",
            Repository = Repository,
            SignerWorkflow = Repository + "/.github/workflows/sealed-runtime.yml",
            SourceSha = SourceSha,
            SourceRef = SourceRef,
            RunInvocationUrl = $"https://github.com/{Repository}/actions/runs/42/attempts/1",
            BundleDigest = Sha256('f'),
            ExecutableSubjectName = "Aws2Azure.Proxy",
            ExecutableSubjectDigest = Sha256('c'),
            ManifestSubjectName = "sealed-runtime-manifest.json",
            ManifestSubjectDigest = Sha256('d'),
        },
    };

    private static string Sha256(char value) => "sha256:" + new string(value, 64);
}
