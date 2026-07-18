using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Aws2Azure.GapDocs;

public sealed record RcObservationEvidence
{
    private IReadOnlyList<RcObservationCohort> cohorts = Array.Empty<RcObservationCohort>();
    private IReadOnlyList<RcObservationMetric> metrics = Array.Empty<RcObservationMetric>();
    private IReadOnlyList<RcObservationRollbackTrigger> rollbackTriggers =
        Array.Empty<RcObservationRollbackTrigger>();

    public int SchemaVersion { get; init; }
    public string ArtifactKind { get; init; } = string.Empty;
    public string EvidenceDigest { get; init; } = string.Empty;
    public RcObservationReleaseCandidate ReleaseCandidate { get; init; } = new();
    public RcObservationRuntimeIdentity Candidate { get; init; } = new();
    public RcObservationRuntimeIdentity Prior { get; init; } = new();
    public RcObservationProfile Profile { get; init; } = new();
    public RcObservationPolicyIdentity Policy { get; init; } = new();
    public RcObservationAzureEnvironment Azure { get; init; } = new();
    public RcObservationProducer Producer { get; init; } = new();
    public RcObservationArtifactIdentity CaptureArtifact { get; init; } = new();
    public RcObservationWindow Observation { get; init; } = new();
    public IReadOnlyList<RcObservationCohort> Cohorts
    {
        get => cohorts;
        init => cohorts = Copy(value);
    }
    public IReadOnlyList<RcObservationMetric> Metrics
    {
        get => metrics;
        init => metrics = Copy(value);
    }
    public IReadOnlyList<RcObservationRollbackTrigger> RollbackTriggers
    {
        get => rollbackTriggers;
        init => rollbackTriggers = Copy(value);
    }
    public RcObservationDecision Decision { get; init; } = new();
    public RcObservationRestoration? Restoration { get; init; }

    [YamlIgnore]
    public string SourceFile { get; init; } = string.Empty;

    private static IReadOnlyList<T> Copy<T>(IEnumerable<T>? values) =>
        Array.AsReadOnly((values ?? Array.Empty<T>()).ToArray());
}

public sealed record RcObservationReleaseCandidate
{
    public string Id { get; init; } = string.Empty;
    public string ManifestDigest { get; init; } = string.Empty;
    public string SourceSha { get; init; } = string.Empty;
    public RcObservationArchiveInputsIdentity ArchiveInputs { get; init; } = new();
    public RcObservationGhcrInputsIdentity GhcrInputs { get; init; } = new();
}

public sealed record RcObservationArchiveInputsIdentity
{
    public string ContentDigest { get; init; } = string.Empty;
    public RcObservationArchiveProducer Producer { get; init; } = new();
    public RcObservationArtifactIdentity Artifact { get; init; } = new();
}

public sealed record RcObservationGhcrInputsIdentity
{
    public string ContentDigest { get; init; } = string.Empty;
    public RcObservationArchiveProducer Producer { get; init; } = new();
    public RcObservationArtifactIdentity Artifact { get; init; } = new();
    public string IndexDigest { get; init; } = string.Empty;
}

public sealed record RcObservationArchiveProducer
{
    public string Repository { get; init; } = string.Empty;
    public string WorkflowPath { get; init; } = string.Empty;
    public string EventName { get; init; } = string.Empty;
    public long RunId { get; init; }
    public int RunAttempt { get; init; }
    public string AttemptUrl { get; init; } = string.Empty;
    public string SourceSha { get; init; } = string.Empty;
    public string SourceRef { get; init; } = string.Empty;
}

public sealed record RcObservationRuntimeIdentity
{
    public string IdentityDigest { get; init; } = string.Empty;
    public string RuntimeDigest { get; init; } = string.Empty;
    public string SourceSha { get; init; } = string.Empty;
}

public sealed record RcObservationProfile
{
    public string Id { get; init; } = string.Empty;
    public int Version { get; init; }
}

public sealed record RcObservationPolicyIdentity
{
    public string WorkloadManifestDigest { get; init; } = string.Empty;
    public string QualificationPolicyDigest { get; init; } = string.Empty;
    public string ObservationPolicyDigest { get; init; } = string.Empty;
}

public sealed record RcObservationAzureEnvironment
{
    public string BackendKind { get; init; } = string.Empty;
    public string Region { get; init; } = string.Empty;
    public string BackendIdentityDigest { get; init; } = string.Empty;
    public string ConfigDigest { get; init; } = string.Empty;
    public string AwsBindingDigest { get; init; } = string.Empty;
}

public sealed record RcObservationProducer
{
    public string Repository { get; init; } = string.Empty;
    public string WorkflowPath { get; init; } = string.Empty;
    public string EventName { get; init; } = string.Empty;
    public long RunId { get; init; }
    public int RunAttempt { get; init; }
    public string RunUrl { get; init; } = string.Empty;
    public string AttemptUrl { get; init; } = string.Empty;
    public string SourceSha { get; init; } = string.Empty;
    public string SourceRef { get; init; } = string.Empty;
}

public sealed record RcObservationArtifactIdentity
{
    public long Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string UploadDigest { get; init; } = string.Empty;
}

public sealed record RcObservationWindow
{
    public DateTimeOffset StartedAtUtc { get; init; }
    public DateTimeOffset EndedAtUtc { get; init; }
    public DateTimeOffset GeneratedAtUtc { get; init; }
    public int MinimumWindowMinutes { get; init; }
}

public sealed record RcObservationCohort
{
    private IReadOnlyList<string> memberDigests = Array.Empty<string>();

    public string Id { get; init; } = string.Empty;
    public string Role { get; init; } = string.Empty;
    public string RuntimeIdentityDigest { get; init; } = string.Empty;
    public string RuntimeDigest { get; init; } = string.Empty;
    public string BackendKind { get; init; } = string.Empty;
    public string Region { get; init; } = string.Empty;
    public string BackendIdentityDigest { get; init; } = string.Empty;
    public string ConfigDigest { get; init; } = string.Empty;
    public string AwsBindingDigest { get; init; } = string.Empty;
    public DateTimeOffset ObservedFromUtc { get; init; }
    public DateTimeOffset ObservedUntilUtc { get; init; }
    public IReadOnlyList<string> MemberDigests
    {
        get => memberDigests;
        init => memberDigests = Array.AsReadOnly(
            (value ?? Array.Empty<string>()).ToArray());
    }
}

public sealed record RcObservationMetric
{
    public string Id { get; init; } = string.Empty;
    public string Unit { get; init; } = string.Empty;
    public string Comparison { get; init; } = string.Empty;
    public double? Threshold { get; init; }
    public double? CandidateValue { get; init; }
    public double? StableValue { get; init; }
    public long Samples { get; init; }
    public DateTimeOffset CapturedAtUtc { get; init; }
    public string Result { get; init; } = string.Empty;
}

public sealed record RcObservationRollbackTrigger
{
    public string Id { get; init; } = string.Empty;
    public string MetricId { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public bool? OverrideApplied { get; init; }
    public bool? Suppressed { get; init; }
}

public sealed record RcObservationDecision
{
    public string Verdict { get; init; } = string.Empty;
    public string Owner { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
    public DateTimeOffset DecidedAtUtc { get; init; }
}

public sealed record RcObservationRestoration
{
    public bool Verified { get; init; }
    public string RuntimeIdentityDigest { get; init; } = string.Empty;
    public string RuntimeDigest { get; init; } = string.Empty;
    public string BackendIdentityDigest { get; init; } = string.Empty;
    public string ConfigDigest { get; init; } = string.Empty;
    public string AwsBindingDigest { get; init; } = string.Empty;
    public DateTimeOffset StartedAtUtc { get; init; }
    public DateTimeOffset VerifiedAtUtc { get; init; }
}

public sealed record RcObservationValidationContext
{
    public string ExpectedEvidenceDigest { get; init; } = string.Empty;
    public string ReleaseCandidateId { get; init; } = string.Empty;
    public string RcManifestDigest { get; init; } = string.Empty;
    public string ArchiveInputsDigest { get; init; } = string.Empty;
    public string ArchiveProducerRepository { get; init; } = string.Empty;
    public string ArchiveProducerWorkflowPath { get; init; } = string.Empty;
    public long ArchiveProducerRunId { get; init; }
    public int ArchiveProducerRunAttempt { get; init; }
    public string ArchiveProducerSourceRef { get; init; } = string.Empty;
    public long ArchiveArtifactId { get; init; }
    public string ArchiveArtifactName { get; init; } = string.Empty;
    public string ArchiveArtifactUploadDigest { get; init; } = string.Empty;
    public string GhcrInputsDigest { get; init; } = string.Empty;
    public string GhcrProducerRepository { get; init; } = string.Empty;
    public string GhcrProducerWorkflowPath { get; init; } = string.Empty;
    public long GhcrProducerRunId { get; init; }
    public int GhcrProducerRunAttempt { get; init; }
    public string GhcrProducerSourceSha { get; init; } = string.Empty;
    public string GhcrProducerSourceRef { get; init; } = string.Empty;
    public long GhcrArtifactId { get; init; }
    public string GhcrArtifactName { get; init; } = string.Empty;
    public string GhcrArtifactUploadDigest { get; init; } = string.Empty;
    public string GhcrIndexDigest { get; init; } = string.Empty;
    public string CandidateSourceSha { get; init; } = string.Empty;
    public string CandidateIdentityDigest { get; init; } = string.Empty;
    public string CandidateRuntimeDigest { get; init; } = string.Empty;
    public string PriorSourceSha { get; init; } = string.Empty;
    public string PriorIdentityDigest { get; init; } = string.Empty;
    public string PriorRuntimeDigest { get; init; } = string.Empty;
    public string ProfileId { get; init; } = string.Empty;
    public int ProfileVersion { get; init; }
    public string WorkloadManifestDigest { get; init; } = string.Empty;
    public string QualificationPolicyDigest { get; init; } = string.Empty;
    public string ObservationPolicyDigest { get; init; } = string.Empty;
    public string AzureBackendKind { get; init; } = string.Empty;
    public string AzureRegion { get; init; } = string.Empty;
    public string AzureBackendIdentityDigest { get; init; } = string.Empty;
    public string ConfigDigest { get; init; } = string.Empty;
    public string AwsBindingDigest { get; init; } = string.Empty;
    public string ProducerRepository { get; init; } = string.Empty;
    public string ProducerWorkflowPath { get; init; } = string.Empty;
    public long ProducerRunId { get; init; }
    public int ProducerRunAttempt { get; init; }
    public string ProducerSourceSha { get; init; } = string.Empty;
    public string ProducerSourceRef { get; init; } = string.Empty;
    public long CaptureArtifactId { get; init; }
    public string CaptureArtifactName { get; init; } = string.Empty;
    public string CaptureArtifactUploadDigest { get; init; } = string.Empty;
    public int MinimumWindowMinutes { get; init; }
    public TimeSpan MaximumEvidenceAge { get; init; }
}

public static class RcObservationLoader
{
    public static RcObservationEvidence Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("RC observation evidence not found", path);
        }

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .WithDuplicateKeyChecking()
            .Build();
        using var reader = new StreamReader(path);
        var document = deserializer.Deserialize<RcObservationYaml>(reader)
            ?? throw new InvalidDataException($"{path}: empty document");
        return Map(document) with { SourceFile = path };
    }

    private static RcObservationEvidence Map(RcObservationYaml document) => new()
    {
        SchemaVersion = document.SchemaVersion,
        ArtifactKind = document.ArtifactKind!,
        EvidenceDigest = document.EvidenceDigest!,
        ReleaseCandidate = Map(document.ReleaseCandidate),
        Candidate = Map(document.Candidate),
        Prior = Map(document.Prior),
        Profile = Map(document.Profile),
        Policy = Map(document.Policy),
        Azure = Map(document.Azure),
        Producer = Map(document.Producer),
        CaptureArtifact = Map(document.CaptureArtifact),
        Observation = Map(document.Observation),
        Cohorts = (document.Cohorts ?? []).Select(Map).ToArray(),
        Metrics = (document.Metrics ?? []).Select(Map).ToArray(),
        RollbackTriggers = (document.RollbackTriggers ?? []).Select(Map).ToArray(),
        Decision = Map(document.Decision),
        Restoration = document.Restoration is null ? null : Map(document.Restoration),
    };

    private static RcObservationReleaseCandidate Map(
        RcObservationReleaseCandidateYaml? value) => new()
    {
        Id = value?.Id!,
        ManifestDigest = value?.ManifestDigest!,
        SourceSha = value?.SourceSha!,
        ArchiveInputs = Map(value?.ArchiveInputs),
        GhcrInputs = Map(value?.GhcrInputs),
    };

    private static RcObservationArchiveInputsIdentity Map(
        RcObservationArchiveInputsIdentityYaml? value) => new()
    {
        ContentDigest = value?.ContentDigest!,
        Producer = Map(value?.Producer),
        Artifact = Map(value?.Artifact),
    };

    private static RcObservationGhcrInputsIdentity Map(
        RcObservationGhcrInputsIdentityYaml? value) => new()
    {
        ContentDigest = value?.ContentDigest!,
        Producer = Map(value?.Producer),
        Artifact = Map(value?.Artifact),
        IndexDigest = value?.IndexDigest!,
    };

    private static RcObservationArchiveProducer Map(
        RcObservationArchiveProducerYaml? value) => new()
    {
        Repository = value?.Repository!,
        WorkflowPath = value?.WorkflowPath!,
        EventName = value?.EventName!,
        RunId = value?.RunId ?? 0,
        RunAttempt = value?.RunAttempt ?? 0,
        AttemptUrl = value?.AttemptUrl!,
        SourceSha = value?.SourceSha!,
        SourceRef = value?.SourceRef!,
    };

    private static RcObservationRuntimeIdentity Map(
        RcObservationRuntimeIdentityYaml? value) => new()
    {
        IdentityDigest = value?.IdentityDigest!,
        RuntimeDigest = value?.RuntimeDigest!,
        SourceSha = value?.SourceSha!,
    };

    private static RcObservationProfile Map(RcObservationProfileYaml? value) => new()
    {
        Id = value?.Id!,
        Version = value?.Version ?? 0,
    };

    private static RcObservationPolicyIdentity Map(
        RcObservationPolicyIdentityYaml? value) => new()
    {
        WorkloadManifestDigest = value?.WorkloadManifestDigest!,
        QualificationPolicyDigest = value?.QualificationPolicyDigest!,
        ObservationPolicyDigest = value?.ObservationPolicyDigest!,
    };

    private static RcObservationAzureEnvironment Map(
        RcObservationAzureEnvironmentYaml? value) => new()
    {
        BackendKind = value?.BackendKind!,
        Region = value?.Region!,
        BackendIdentityDigest = value?.BackendIdentityDigest!,
        ConfigDigest = value?.ConfigDigest!,
        AwsBindingDigest = value?.AwsBindingDigest!,
    };

    private static RcObservationProducer Map(RcObservationProducerYaml? value) => new()
    {
        Repository = value?.Repository!,
        WorkflowPath = value?.WorkflowPath!,
        EventName = value?.EventName!,
        RunId = value?.RunId ?? 0,
        RunAttempt = value?.RunAttempt ?? 0,
        RunUrl = value?.RunUrl!,
        AttemptUrl = value?.AttemptUrl!,
        SourceSha = value?.SourceSha!,
        SourceRef = value?.SourceRef!,
    };

    private static RcObservationArtifactIdentity Map(
        RcObservationArtifactIdentityYaml? value) => new()
    {
        Id = value?.Id ?? 0,
        Name = value?.Name!,
        UploadDigest = value?.UploadDigest!,
    };

    private static RcObservationWindow Map(RcObservationWindowYaml? value) => new()
    {
        StartedAtUtc = value?.StartedAtUtc ?? default,
        EndedAtUtc = value?.EndedAtUtc ?? default,
        GeneratedAtUtc = value?.GeneratedAtUtc ?? default,
        MinimumWindowMinutes = value?.MinimumWindowMinutes ?? 0,
    };

    private static RcObservationCohort Map(RcObservationCohortYaml? value) => new()
    {
        Id = value?.Id!,
        Role = value?.Role!,
        RuntimeIdentityDigest = value?.RuntimeIdentityDigest!,
        RuntimeDigest = value?.RuntimeDigest!,
        BackendKind = value?.BackendKind!,
        Region = value?.Region!,
        BackendIdentityDigest = value?.BackendIdentityDigest!,
        ConfigDigest = value?.ConfigDigest!,
        AwsBindingDigest = value?.AwsBindingDigest!,
        ObservedFromUtc = value?.ObservedFromUtc ?? default,
        ObservedUntilUtc = value?.ObservedUntilUtc ?? default,
        MemberDigests = value?.MemberDigests?.Select(item => item!).ToArray()
            ?? Array.Empty<string>(),
    };

    private static RcObservationMetric Map(RcObservationMetricYaml? value) => new()
    {
        Id = value?.Id!,
        Unit = value?.Unit!,
        Comparison = value?.Comparison!,
        Threshold = value?.Threshold,
        CandidateValue = value?.CandidateValue,
        StableValue = value?.StableValue,
        Samples = value?.Samples ?? 0,
        CapturedAtUtc = value?.CapturedAtUtc ?? default,
        Result = value?.Result!,
    };

    private static RcObservationRollbackTrigger Map(
        RcObservationRollbackTriggerYaml? value) => new()
    {
        Id = value?.Id!,
        MetricId = value?.MetricId!,
        Status = value?.Status!,
        OverrideApplied = value?.OverrideApplied,
        Suppressed = value?.Suppressed,
    };

    private static RcObservationDecision Map(RcObservationDecisionYaml? value) => new()
    {
        Verdict = value?.Verdict!,
        Owner = value?.Owner!,
        Reason = value?.Reason!,
        DecidedAtUtc = value?.DecidedAtUtc ?? default,
    };

    private static RcObservationRestoration Map(RcObservationRestorationYaml value) => new()
    {
        Verified = value.Verified,
        RuntimeIdentityDigest = value.RuntimeIdentityDigest!,
        RuntimeDigest = value.RuntimeDigest!,
        BackendIdentityDigest = value.BackendIdentityDigest!,
        ConfigDigest = value.ConfigDigest!,
        AwsBindingDigest = value.AwsBindingDigest!,
        StartedAtUtc = value.StartedAtUtc,
        VerifiedAtUtc = value.VerifiedAtUtc,
    };

    private sealed class RcObservationYaml
    {
        public int SchemaVersion { get; set; }
        public string? ArtifactKind { get; set; }
        public string? EvidenceDigest { get; set; }
        public RcObservationReleaseCandidateYaml? ReleaseCandidate { get; set; }
        public RcObservationRuntimeIdentityYaml? Candidate { get; set; }
        public RcObservationRuntimeIdentityYaml? Prior { get; set; }
        public RcObservationProfileYaml? Profile { get; set; }
        public RcObservationPolicyIdentityYaml? Policy { get; set; }
        public RcObservationAzureEnvironmentYaml? Azure { get; set; }
        public RcObservationProducerYaml? Producer { get; set; }
        public RcObservationArtifactIdentityYaml? CaptureArtifact { get; set; }
        public RcObservationWindowYaml? Observation { get; set; }
        public List<RcObservationCohortYaml?>? Cohorts { get; set; }
        public List<RcObservationMetricYaml?>? Metrics { get; set; }
        public List<RcObservationRollbackTriggerYaml?>? RollbackTriggers { get; set; }
        public RcObservationDecisionYaml? Decision { get; set; }
        public RcObservationRestorationYaml? Restoration { get; set; }
    }

    private sealed class RcObservationReleaseCandidateYaml
    {
        public string? Id { get; set; }
        public string? ManifestDigest { get; set; }
        public string? SourceSha { get; set; }
        public RcObservationArchiveInputsIdentityYaml? ArchiveInputs { get; set; }
        public RcObservationGhcrInputsIdentityYaml? GhcrInputs { get; set; }
    }

    private sealed class RcObservationArchiveInputsIdentityYaml
    {
        public string? ContentDigest { get; set; }
        public RcObservationArchiveProducerYaml? Producer { get; set; }
        public RcObservationArtifactIdentityYaml? Artifact { get; set; }
    }

    private sealed class RcObservationGhcrInputsIdentityYaml
    {
        public string? ContentDigest { get; set; }
        public RcObservationArchiveProducerYaml? Producer { get; set; }
        public RcObservationArtifactIdentityYaml? Artifact { get; set; }
        public string? IndexDigest { get; set; }
    }

    private sealed class RcObservationArchiveProducerYaml
    {
        public string? Repository { get; set; }
        public string? WorkflowPath { get; set; }
        public string? EventName { get; set; }
        public long RunId { get; set; }
        public int RunAttempt { get; set; }
        public string? AttemptUrl { get; set; }
        public string? SourceSha { get; set; }
        public string? SourceRef { get; set; }
    }

    private sealed class RcObservationRuntimeIdentityYaml
    {
        public string? IdentityDigest { get; set; }
        public string? RuntimeDigest { get; set; }
        public string? SourceSha { get; set; }
    }

    private sealed class RcObservationProfileYaml
    {
        public string? Id { get; set; }
        public int Version { get; set; }
    }

    private sealed class RcObservationPolicyIdentityYaml
    {
        public string? WorkloadManifestDigest { get; set; }
        public string? QualificationPolicyDigest { get; set; }
        public string? ObservationPolicyDigest { get; set; }
    }

    private sealed class RcObservationAzureEnvironmentYaml
    {
        public string? BackendKind { get; set; }
        public string? Region { get; set; }
        public string? BackendIdentityDigest { get; set; }
        public string? ConfigDigest { get; set; }
        public string? AwsBindingDigest { get; set; }
    }

    private sealed class RcObservationProducerYaml
    {
        public string? Repository { get; set; }
        public string? WorkflowPath { get; set; }
        public string? EventName { get; set; }
        public long RunId { get; set; }
        public int RunAttempt { get; set; }
        public string? RunUrl { get; set; }
        public string? AttemptUrl { get; set; }
        public string? SourceSha { get; set; }
        public string? SourceRef { get; set; }
    }

    private sealed class RcObservationArtifactIdentityYaml
    {
        public long Id { get; set; }
        public string? Name { get; set; }
        public string? UploadDigest { get; set; }
    }

    private sealed class RcObservationWindowYaml
    {
        public DateTimeOffset StartedAtUtc { get; set; }
        public DateTimeOffset EndedAtUtc { get; set; }
        public DateTimeOffset GeneratedAtUtc { get; set; }
        public int MinimumWindowMinutes { get; set; }
    }

    private sealed class RcObservationCohortYaml
    {
        public string? Id { get; set; }
        public string? Role { get; set; }
        public string? RuntimeIdentityDigest { get; set; }
        public string? RuntimeDigest { get; set; }
        public string? BackendKind { get; set; }
        public string? Region { get; set; }
        public string? BackendIdentityDigest { get; set; }
        public string? ConfigDigest { get; set; }
        public string? AwsBindingDigest { get; set; }
        public DateTimeOffset ObservedFromUtc { get; set; }
        public DateTimeOffset ObservedUntilUtc { get; set; }
        public List<string?>? MemberDigests { get; set; }
    }

    private sealed class RcObservationMetricYaml
    {
        public string? Id { get; set; }
        public string? Unit { get; set; }
        public string? Comparison { get; set; }
        public double? Threshold { get; set; }
        public double? CandidateValue { get; set; }
        public double? StableValue { get; set; }
        public long Samples { get; set; }
        public DateTimeOffset CapturedAtUtc { get; set; }
        public string? Result { get; set; }
    }

    private sealed class RcObservationRollbackTriggerYaml
    {
        public string? Id { get; set; }
        public string? MetricId { get; set; }
        public string? Status { get; set; }
        public bool? OverrideApplied { get; set; }
        public bool? Suppressed { get; set; }
    }

    private sealed class RcObservationDecisionYaml
    {
        public string? Verdict { get; set; }
        public string? Owner { get; set; }
        public string? Reason { get; set; }
        public DateTimeOffset DecidedAtUtc { get; set; }
    }

    private sealed class RcObservationRestorationYaml
    {
        public bool Verified { get; set; }
        public string? RuntimeIdentityDigest { get; set; }
        public string? RuntimeDigest { get; set; }
        public string? BackendIdentityDigest { get; set; }
        public string? ConfigDigest { get; set; }
        public string? AwsBindingDigest { get; set; }
        public DateTimeOffset StartedAtUtc { get; set; }
        public DateTimeOffset VerifiedAtUtc { get; set; }
    }
}

public static class RcObservationIntegrity
{
    public static string ComputePayloadDigest(RcObservationEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        var canonical = new StringBuilder(4096);
        Append(canonical, "schema_version", evidence.SchemaVersion);
        Append(canonical, "artifact_kind", evidence.ArtifactKind);
        Append(canonical, "release_candidate.id", evidence.ReleaseCandidate.Id);
        Append(
            canonical,
            "release_candidate.manifest_digest",
            evidence.ReleaseCandidate.ManifestDigest);
        Append(canonical, "release_candidate.source_sha", evidence.ReleaseCandidate.SourceSha);
        Append(
            canonical,
            "release_candidate.archive_inputs.content_digest",
            evidence.ReleaseCandidate.ArchiveInputs.ContentDigest);
        Append(
            canonical,
            "release_candidate.archive_inputs.producer.repository",
            evidence.ReleaseCandidate.ArchiveInputs.Producer.Repository);
        Append(
            canonical,
            "release_candidate.archive_inputs.producer.workflow_path",
            evidence.ReleaseCandidate.ArchiveInputs.Producer.WorkflowPath);
        Append(
            canonical,
            "release_candidate.archive_inputs.producer.event_name",
            evidence.ReleaseCandidate.ArchiveInputs.Producer.EventName);
        Append(
            canonical,
            "release_candidate.archive_inputs.producer.run_id",
            evidence.ReleaseCandidate.ArchiveInputs.Producer.RunId);
        Append(
            canonical,
            "release_candidate.archive_inputs.producer.run_attempt",
            evidence.ReleaseCandidate.ArchiveInputs.Producer.RunAttempt);
        Append(
            canonical,
            "release_candidate.archive_inputs.producer.attempt_url",
            evidence.ReleaseCandidate.ArchiveInputs.Producer.AttemptUrl);
        Append(
            canonical,
            "release_candidate.archive_inputs.producer.source_sha",
            evidence.ReleaseCandidate.ArchiveInputs.Producer.SourceSha);
        Append(
            canonical,
            "release_candidate.archive_inputs.producer.source_ref",
            evidence.ReleaseCandidate.ArchiveInputs.Producer.SourceRef);
        Append(
            canonical,
            "release_candidate.archive_inputs.artifact.id",
            evidence.ReleaseCandidate.ArchiveInputs.Artifact.Id);
        Append(
            canonical,
            "release_candidate.archive_inputs.artifact.name",
            evidence.ReleaseCandidate.ArchiveInputs.Artifact.Name);
        Append(
            canonical,
            "release_candidate.archive_inputs.artifact.upload_digest",
            evidence.ReleaseCandidate.ArchiveInputs.Artifact.UploadDigest);
        Append(
            canonical,
            "release_candidate.ghcr_inputs.content_digest",
            evidence.ReleaseCandidate.GhcrInputs.ContentDigest);
        Append(
            canonical,
            "release_candidate.ghcr_inputs.producer.repository",
            evidence.ReleaseCandidate.GhcrInputs.Producer.Repository);
        Append(
            canonical,
            "release_candidate.ghcr_inputs.producer.workflow_path",
            evidence.ReleaseCandidate.GhcrInputs.Producer.WorkflowPath);
        Append(
            canonical,
            "release_candidate.ghcr_inputs.producer.event_name",
            evidence.ReleaseCandidate.GhcrInputs.Producer.EventName);
        Append(
            canonical,
            "release_candidate.ghcr_inputs.producer.run_id",
            evidence.ReleaseCandidate.GhcrInputs.Producer.RunId);
        Append(
            canonical,
            "release_candidate.ghcr_inputs.producer.run_attempt",
            evidence.ReleaseCandidate.GhcrInputs.Producer.RunAttempt);
        Append(
            canonical,
            "release_candidate.ghcr_inputs.producer.attempt_url",
            evidence.ReleaseCandidate.GhcrInputs.Producer.AttemptUrl);
        Append(
            canonical,
            "release_candidate.ghcr_inputs.producer.source_sha",
            evidence.ReleaseCandidate.GhcrInputs.Producer.SourceSha);
        Append(
            canonical,
            "release_candidate.ghcr_inputs.producer.source_ref",
            evidence.ReleaseCandidate.GhcrInputs.Producer.SourceRef);
        Append(
            canonical,
            "release_candidate.ghcr_inputs.artifact.id",
            evidence.ReleaseCandidate.GhcrInputs.Artifact.Id);
        Append(
            canonical,
            "release_candidate.ghcr_inputs.artifact.name",
            evidence.ReleaseCandidate.GhcrInputs.Artifact.Name);
        Append(
            canonical,
            "release_candidate.ghcr_inputs.artifact.upload_digest",
            evidence.ReleaseCandidate.GhcrInputs.Artifact.UploadDigest);
        Append(
            canonical,
            "release_candidate.ghcr_inputs.index_digest",
            evidence.ReleaseCandidate.GhcrInputs.IndexDigest);
        Append(canonical, "candidate.identity_digest", evidence.Candidate.IdentityDigest);
        Append(canonical, "candidate.runtime_digest", evidence.Candidate.RuntimeDigest);
        Append(canonical, "candidate.source_sha", evidence.Candidate.SourceSha);
        Append(canonical, "prior.identity_digest", evidence.Prior.IdentityDigest);
        Append(canonical, "prior.runtime_digest", evidence.Prior.RuntimeDigest);
        Append(canonical, "prior.source_sha", evidence.Prior.SourceSha);
        Append(canonical, "profile.id", evidence.Profile.Id);
        Append(canonical, "profile.version", evidence.Profile.Version);
        Append(
            canonical,
            "policy.workload_manifest_digest",
            evidence.Policy.WorkloadManifestDigest);
        Append(
            canonical,
            "policy.qualification_policy_digest",
            evidence.Policy.QualificationPolicyDigest);
        Append(
            canonical,
            "policy.observation_policy_digest",
            evidence.Policy.ObservationPolicyDigest);
        Append(canonical, "azure.backend_kind", evidence.Azure.BackendKind);
        Append(canonical, "azure.region", evidence.Azure.Region);
        Append(
            canonical,
            "azure.backend_identity_digest",
            evidence.Azure.BackendIdentityDigest);
        Append(canonical, "azure.config_digest", evidence.Azure.ConfigDigest);
        Append(canonical, "azure.aws_binding_digest", evidence.Azure.AwsBindingDigest);
        Append(canonical, "producer.repository", evidence.Producer.Repository);
        Append(canonical, "producer.workflow_path", evidence.Producer.WorkflowPath);
        Append(canonical, "producer.event_name", evidence.Producer.EventName);
        Append(canonical, "producer.run_id", evidence.Producer.RunId);
        Append(canonical, "producer.run_attempt", evidence.Producer.RunAttempt);
        Append(canonical, "producer.run_url", evidence.Producer.RunUrl);
        Append(canonical, "producer.attempt_url", evidence.Producer.AttemptUrl);
        Append(canonical, "producer.source_sha", evidence.Producer.SourceSha);
        Append(canonical, "producer.source_ref", evidence.Producer.SourceRef);
        Append(canonical, "capture_artifact.id", evidence.CaptureArtifact.Id);
        Append(canonical, "capture_artifact.name", evidence.CaptureArtifact.Name);
        Append(
            canonical,
            "capture_artifact.upload_digest",
            evidence.CaptureArtifact.UploadDigest);
        Append(canonical, "observation.started_at_utc", evidence.Observation.StartedAtUtc);
        Append(canonical, "observation.ended_at_utc", evidence.Observation.EndedAtUtc);
        Append(canonical, "observation.generated_at_utc", evidence.Observation.GeneratedAtUtc);
        Append(
            canonical,
            "observation.minimum_window_minutes",
            evidence.Observation.MinimumWindowMinutes);
        Append(canonical, "cohorts.count", evidence.Cohorts.Count);
        for (var index = 0; index < evidence.Cohorts.Count; index++)
        {
            var cohort = evidence.Cohorts[index];
            var prefix = $"cohorts[{index}]";
            Append(canonical, prefix + ".id", cohort.Id);
            Append(canonical, prefix + ".role", cohort.Role);
            Append(canonical, prefix + ".runtime_identity_digest", cohort.RuntimeIdentityDigest);
            Append(canonical, prefix + ".runtime_digest", cohort.RuntimeDigest);
            Append(canonical, prefix + ".backend_kind", cohort.BackendKind);
            Append(canonical, prefix + ".region", cohort.Region);
            Append(canonical, prefix + ".backend_identity_digest", cohort.BackendIdentityDigest);
            Append(canonical, prefix + ".config_digest", cohort.ConfigDigest);
            Append(canonical, prefix + ".aws_binding_digest", cohort.AwsBindingDigest);
            Append(canonical, prefix + ".observed_from_utc", cohort.ObservedFromUtc);
            Append(canonical, prefix + ".observed_until_utc", cohort.ObservedUntilUtc);
            Append(canonical, prefix + ".member_digests.count", cohort.MemberDigests.Count);
            for (var memberIndex = 0; memberIndex < cohort.MemberDigests.Count; memberIndex++)
            {
                Append(
                    canonical,
                    $"{prefix}.member_digests[{memberIndex}]",
                    cohort.MemberDigests[memberIndex]);
            }
        }
        Append(canonical, "metrics.count", evidence.Metrics.Count);
        for (var index = 0; index < evidence.Metrics.Count; index++)
        {
            var metric = evidence.Metrics[index];
            var prefix = $"metrics[{index}]";
            Append(canonical, prefix + ".id", metric.Id);
            Append(canonical, prefix + ".unit", metric.Unit);
            Append(canonical, prefix + ".comparison", metric.Comparison);
            Append(canonical, prefix + ".threshold", metric.Threshold);
            Append(canonical, prefix + ".candidate_value", metric.CandidateValue);
            Append(canonical, prefix + ".stable_value", metric.StableValue);
            Append(canonical, prefix + ".samples", metric.Samples);
            Append(canonical, prefix + ".captured_at_utc", metric.CapturedAtUtc);
            Append(canonical, prefix + ".result", metric.Result);
        }
        Append(canonical, "rollback_triggers.count", evidence.RollbackTriggers.Count);
        for (var index = 0; index < evidence.RollbackTriggers.Count; index++)
        {
            var trigger = evidence.RollbackTriggers[index];
            var prefix = $"rollback_triggers[{index}]";
            Append(canonical, prefix + ".id", trigger.Id);
            Append(canonical, prefix + ".metric_id", trigger.MetricId);
            Append(canonical, prefix + ".status", trigger.Status);
            Append(canonical, prefix + ".override_applied", trigger.OverrideApplied);
            Append(canonical, prefix + ".suppressed", trigger.Suppressed);
        }
        Append(canonical, "decision.verdict", evidence.Decision.Verdict);
        Append(canonical, "decision.owner", evidence.Decision.Owner);
        Append(canonical, "decision.reason", evidence.Decision.Reason);
        Append(canonical, "decision.decided_at_utc", evidence.Decision.DecidedAtUtc);
        Append(canonical, "restoration.present", evidence.Restoration is not null);
        if (evidence.Restoration is not null)
        {
            Append(canonical, "restoration.verified", evidence.Restoration.Verified);
            Append(
                canonical,
                "restoration.runtime_identity_digest",
                evidence.Restoration.RuntimeIdentityDigest);
            Append(canonical, "restoration.runtime_digest", evidence.Restoration.RuntimeDigest);
            Append(
                canonical,
                "restoration.backend_identity_digest",
                evidence.Restoration.BackendIdentityDigest);
            Append(canonical, "restoration.config_digest", evidence.Restoration.ConfigDigest);
            Append(
                canonical,
                "restoration.aws_binding_digest",
                evidence.Restoration.AwsBindingDigest);
            Append(canonical, "restoration.started_at_utc", evidence.Restoration.StartedAtUtc);
            Append(canonical, "restoration.verified_at_utc", evidence.Restoration.VerifiedAtUtc);
        }

        return "sha256:" + Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }

    private static void Append(StringBuilder builder, string name, string? value)
    {
        value ??= string.Empty;
        builder.Append(name);
        builder.Append('=');
        builder.Append(value.Length.ToString(CultureInfo.InvariantCulture));
        builder.Append(':');
        builder.Append(value);
        builder.Append('\n');
    }

    private static void Append(StringBuilder builder, string name, int value) =>
        Append(builder, name, value.ToString(CultureInfo.InvariantCulture));

    private static void Append(StringBuilder builder, string name, long value) =>
        Append(builder, name, value.ToString(CultureInfo.InvariantCulture));

    private static void Append(StringBuilder builder, string name, double value) =>
        Append(builder, name, value.ToString("R", CultureInfo.InvariantCulture));

    private static void Append(StringBuilder builder, string name, double? value) =>
        Append(
            builder,
            name,
            value?.ToString("R", CultureInfo.InvariantCulture) ?? "<missing>");

    private static void Append(StringBuilder builder, string name, bool value) =>
        Append(builder, name, value ? "true" : "false");

    private static void Append(StringBuilder builder, string name, bool? value) =>
        Append(builder, name, value is null ? "<missing>" : value.Value ? "true" : "false");

    private static void Append(StringBuilder builder, string name, DateTimeOffset value) =>
        Append(builder, name, value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
}

public static partial class RcObservationValidator
{
    public const int CurrentSchemaVersion = 2;

    public static IReadOnlyList<string> Validate(
        RcObservationEvidence evidence,
        RcObservationValidationContext context,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(context);
        nowUtc = nowUtc.ToUniversalTime();
        var errors = new List<string>();
        var source = string.IsNullOrWhiteSpace(evidence.SourceFile)
            ? "RC observation evidence"
            : evidence.SourceFile;
        void Err(string message) => errors.Add($"{source}: {message}");

        ValidateContext(context);
        if (!HasCompleteShape(evidence))
        {
            Err("document contains null or malformed required fields");
            return errors;
        }
        ValidateIntegrity(evidence, context, Err);
        ValidateIdentities(evidence, context, Err);
        ValidatePolicyAndProducer(evidence, context, Err);
        ValidateEnvironment(evidence, context, Err);
        ValidateWindow(evidence, context, nowUtc, Err);
        ValidateCohorts(evidence, context, Err);
        var breachedMetrics = ValidateMetricsAndTriggers(evidence, Err);
        ValidateDecisionAndRestoration(evidence, context, breachedMetrics, nowUtc, Err);
        return errors;
    }

    private static bool HasCompleteShape(RcObservationEvidence evidence) =>
        evidence.ArtifactKind is not null
        && evidence.EvidenceDigest is not null
        && evidence.ReleaseCandidate is not null
        && evidence.ReleaseCandidate.Id is not null
        && evidence.ReleaseCandidate.ManifestDigest is not null
        && evidence.ReleaseCandidate.SourceSha is not null
        && evidence.ReleaseCandidate.ArchiveInputs is not null
        && evidence.ReleaseCandidate.ArchiveInputs.ContentDigest is not null
        && evidence.ReleaseCandidate.ArchiveInputs.Producer is not null
        && evidence.ReleaseCandidate.ArchiveInputs.Producer.Repository is not null
        && evidence.ReleaseCandidate.ArchiveInputs.Producer.WorkflowPath is not null
        && evidence.ReleaseCandidate.ArchiveInputs.Producer.EventName is not null
        && evidence.ReleaseCandidate.ArchiveInputs.Producer.AttemptUrl is not null
        && evidence.ReleaseCandidate.ArchiveInputs.Producer.SourceSha is not null
        && evidence.ReleaseCandidate.ArchiveInputs.Producer.SourceRef is not null
        && evidence.ReleaseCandidate.ArchiveInputs.Artifact is not null
        && evidence.ReleaseCandidate.ArchiveInputs.Artifact.Name is not null
        && evidence.ReleaseCandidate.ArchiveInputs.Artifact.UploadDigest is not null
        && evidence.ReleaseCandidate.GhcrInputs is not null
        && evidence.ReleaseCandidate.GhcrInputs.ContentDigest is not null
        && evidence.ReleaseCandidate.GhcrInputs.Producer is not null
        && evidence.ReleaseCandidate.GhcrInputs.Producer.Repository is not null
        && evidence.ReleaseCandidate.GhcrInputs.Producer.WorkflowPath is not null
        && evidence.ReleaseCandidate.GhcrInputs.Producer.EventName is not null
        && evidence.ReleaseCandidate.GhcrInputs.Producer.AttemptUrl is not null
        && evidence.ReleaseCandidate.GhcrInputs.Producer.SourceSha is not null
        && evidence.ReleaseCandidate.GhcrInputs.Producer.SourceRef is not null
        && evidence.ReleaseCandidate.GhcrInputs.Artifact is not null
        && evidence.ReleaseCandidate.GhcrInputs.Artifact.Name is not null
        && evidence.ReleaseCandidate.GhcrInputs.Artifact.UploadDigest is not null
        && evidence.ReleaseCandidate.GhcrInputs.IndexDigest is not null
        && HasRuntimeShape(evidence.Candidate)
        && HasRuntimeShape(evidence.Prior)
        && evidence.Profile is not null
        && evidence.Profile.Id is not null
        && evidence.Policy is not null
        && evidence.Policy.WorkloadManifestDigest is not null
        && evidence.Policy.QualificationPolicyDigest is not null
        && evidence.Policy.ObservationPolicyDigest is not null
        && evidence.Azure is not null
        && evidence.Azure.BackendKind is not null
        && evidence.Azure.Region is not null
        && evidence.Azure.BackendIdentityDigest is not null
        && evidence.Azure.ConfigDigest is not null
        && evidence.Azure.AwsBindingDigest is not null
        && evidence.Producer is not null
        && evidence.Producer.Repository is not null
        && evidence.Producer.WorkflowPath is not null
        && evidence.Producer.EventName is not null
        && evidence.Producer.RunUrl is not null
        && evidence.Producer.AttemptUrl is not null
        && evidence.Producer.SourceSha is not null
        && evidence.Producer.SourceRef is not null
        && evidence.CaptureArtifact is not null
        && evidence.CaptureArtifact.Name is not null
        && evidence.CaptureArtifact.UploadDigest is not null
        && evidence.Observation is not null
        && evidence.Cohorts is not null
        && evidence.Cohorts.All(cohort =>
            cohort is not null
            && cohort.Id is not null
            && cohort.Role is not null
            && cohort.RuntimeIdentityDigest is not null
            && cohort.RuntimeDigest is not null
            && cohort.BackendKind is not null
            && cohort.Region is not null
            && cohort.BackendIdentityDigest is not null
            && cohort.ConfigDigest is not null
            && cohort.AwsBindingDigest is not null
            && cohort.MemberDigests is not null
            && cohort.MemberDigests.All(member => member is not null))
        && evidence.Metrics is not null
        && evidence.Metrics.All(metric =>
            metric is not null
            && metric.Id is not null
            && metric.Unit is not null
            && metric.Comparison is not null
            && metric.Result is not null)
        && evidence.RollbackTriggers is not null
        && evidence.RollbackTriggers.All(trigger =>
            trigger is not null
            && trigger.Id is not null
            && trigger.MetricId is not null
            && trigger.Status is not null)
        && evidence.Decision is not null
        && evidence.Decision.Verdict is not null
        && evidence.Decision.Owner is not null
        && evidence.Decision.Reason is not null
        && (evidence.Restoration is null
            || evidence.Restoration.RuntimeIdentityDigest is not null
            && evidence.Restoration.RuntimeDigest is not null
            && evidence.Restoration.BackendIdentityDigest is not null
            && evidence.Restoration.ConfigDigest is not null
            && evidence.Restoration.AwsBindingDigest is not null);

    private static bool HasRuntimeShape(RcObservationRuntimeIdentity? identity) =>
        identity is not null
        && identity.IdentityDigest is not null
        && identity.RuntimeDigest is not null
        && identity.SourceSha is not null;

    private static void ValidateContext(RcObservationValidationContext context)
    {
        if (!IsDigest(context.ExpectedEvidenceDigest)
            || !ReleaseCandidateIdRegex().IsMatch(context.ReleaseCandidateId)
            || !IsDigest(context.RcManifestDigest)
            || !IsDigest(context.ArchiveInputsDigest)
            || !RepositoryRegex().IsMatch(context.ArchiveProducerRepository)
            || context.ArchiveProducerWorkflowPath !=
                ".github/workflows/release-candidate.yml"
            || context.ArchiveProducerRunId <= 0
            || context.ArchiveProducerRunAttempt <= 0
            || !TrustedRefRegex().IsMatch(context.ArchiveProducerSourceRef)
            || context.ArchiveArtifactId <= 0
            || string.IsNullOrWhiteSpace(context.ArchiveArtifactName)
            || !IsDigest(context.ArchiveArtifactUploadDigest)
            || !IsDigest(context.GhcrInputsDigest)
            || !RepositoryRegex().IsMatch(context.GhcrProducerRepository)
            || context.GhcrProducerWorkflowPath !=
                ".github/workflows/release-candidate-image.yml"
            || context.GhcrProducerRunId <= 0
            || context.GhcrProducerRunAttempt <= 0
            || !IsGitSha(context.GhcrProducerSourceSha)
            || context.GhcrProducerSourceRef != "refs/heads/main"
            || context.GhcrArtifactId <= 0
            || string.IsNullOrWhiteSpace(context.GhcrArtifactName)
            || !IsDigest(context.GhcrArtifactUploadDigest)
            || !IsDigest(context.GhcrIndexDigest)
            || !IsGitSha(context.CandidateSourceSha)
            || !IsDigest(context.CandidateIdentityDigest)
            || !IsDigest(context.CandidateRuntimeDigest)
            || !IsGitSha(context.PriorSourceSha)
            || !IsDigest(context.PriorIdentityDigest)
            || !IsDigest(context.PriorRuntimeDigest)
            || string.IsNullOrWhiteSpace(context.ProfileId)
            || context.ProfileVersion <= 0
            || !IsDigest(context.WorkloadManifestDigest)
            || !IsDigest(context.QualificationPolicyDigest)
            || !IsDigest(context.ObservationPolicyDigest)
            || string.IsNullOrWhiteSpace(context.AzureBackendKind)
            || !RegionRegex().IsMatch(context.AzureRegion)
            || !IsDigest(context.AzureBackendIdentityDigest)
            || !IsDigest(context.ConfigDigest)
            || !IsDigest(context.AwsBindingDigest)
            || !RepositoryRegex().IsMatch(context.ProducerRepository)
            || context.ProducerWorkflowPath !=
                ".github/workflows/rc-observation-real-azure.yml"
            || context.ProducerRunId <= 0
            || context.ProducerRunAttempt <= 0
            || !IsGitSha(context.ProducerSourceSha)
            || !ObservationProducerRefRegex().IsMatch(context.ProducerSourceRef)
            || context.CaptureArtifactId <= 0
            || string.IsNullOrWhiteSpace(context.CaptureArtifactName)
            || !IsDigest(context.CaptureArtifactUploadDigest)
            || context.MinimumWindowMinutes <= 0
            || context.MaximumEvidenceAge <= TimeSpan.Zero)
        {
            throw new ArgumentException("RC observation validation context is incomplete or invalid.");
        }
    }

    private static void ValidatePolicyAndProducer(
        RcObservationEvidence evidence,
        RcObservationValidationContext context,
        Action<string> err)
    {
        if (evidence.Policy.WorkloadManifestDigest != context.WorkloadManifestDigest
            || evidence.Policy.QualificationPolicyDigest !=
                context.QualificationPolicyDigest
            || evidence.Policy.ObservationPolicyDigest != context.ObservationPolicyDigest
            || !IsDigest(evidence.Policy.WorkloadManifestDigest)
            || !IsDigest(evidence.Policy.QualificationPolicyDigest)
            || !IsDigest(evidence.Policy.ObservationPolicyDigest))
        {
            err("reviewed workload or observation policy identity drift detected");
        }

        var producer = evidence.Producer;
        var expectedRunUrl =
            $"https://github.com/{context.ProducerRepository}/actions/runs/" +
            context.ProducerRunId;
        if (producer.Repository != context.ProducerRepository
            || producer.WorkflowPath != context.ProducerWorkflowPath
            || producer.EventName != "workflow_dispatch"
            || producer.RunId != context.ProducerRunId
            || producer.RunAttempt != context.ProducerRunAttempt
            || producer.RunUrl != expectedRunUrl
            || producer.AttemptUrl != expectedRunUrl + "/attempts/" +
                context.ProducerRunAttempt
            || producer.SourceSha != context.ProducerSourceSha
            || producer.SourceRef != context.ProducerSourceRef
            || !RepositoryRegex().IsMatch(producer.Repository)
            || !ObservationProducerRefRegex().IsMatch(producer.SourceRef))
        {
            err("observation producer does not match the exact trusted workflow attempt");
        }

        if (evidence.CaptureArtifact.Id != context.CaptureArtifactId
            || evidence.CaptureArtifact.Name != context.CaptureArtifactName
            || evidence.CaptureArtifact.UploadDigest !=
                context.CaptureArtifactUploadDigest
            || evidence.CaptureArtifact.Id <= 0
            || !IsDigest(evidence.CaptureArtifact.UploadDigest))
        {
            err("capture artifact does not match its exact immutable upload identity");
        }
    }

    private static void ValidateIntegrity(
        RcObservationEvidence evidence,
        RcObservationValidationContext context,
        Action<string> err)
    {
        if (evidence.SchemaVersion != CurrentSchemaVersion)
        {
            err(
                $"unsupported schema_version '{evidence.SchemaVersion}'; " +
                $"expected {CurrentSchemaVersion}");
        }
        if (evidence.ArtifactKind != "rc_observation")
        {
            err("artifact_kind must be 'rc_observation'");
        }
        if (!IsDigest(evidence.EvidenceDigest))
        {
            err("evidence_digest must use lowercase sha256");
            return;
        }

        var computed = RcObservationIntegrity.ComputePayloadDigest(evidence);
        if (evidence.EvidenceDigest != computed)
        {
            err("evidence_digest does not match the canonical observation payload");
        }
        if (evidence.EvidenceDigest != context.ExpectedEvidenceDigest)
        {
            err("evidence_digest does not match the digest bound by the trusted RC manifest");
        }
    }

    private static void ValidateIdentities(
        RcObservationEvidence evidence,
        RcObservationValidationContext context,
        Action<string> err)
    {
        if (evidence.ReleaseCandidate.Id != context.ReleaseCandidateId
            || evidence.ReleaseCandidate.ManifestDigest != context.RcManifestDigest
            || evidence.ReleaseCandidate.SourceSha != context.CandidateSourceSha)
        {
            err("release_candidate does not exactly match the trusted RC manifest identity");
        }
        var archive = evidence.ReleaseCandidate.ArchiveInputs;
        var archiveProducer = archive.Producer;
        var expectedArchiveAttempt =
            $"https://github.com/{context.ArchiveProducerRepository}/actions/runs/" +
            $"{context.ArchiveProducerRunId}/attempts/" +
            context.ArchiveProducerRunAttempt;
        var expectedArchiveName =
            $"aws2azure-rc-archives-{context.ReleaseCandidateId}-" +
            $"{context.ArchiveInputsDigest["sha256:".Length..]}-run-" +
            $"{context.ArchiveProducerRunId}-attempt-" +
            context.ArchiveProducerRunAttempt;
        if (archive.ContentDigest != context.ArchiveInputsDigest
            || archiveProducer.Repository != context.ArchiveProducerRepository
            || archiveProducer.WorkflowPath != context.ArchiveProducerWorkflowPath
            || archiveProducer.EventName != "workflow_dispatch"
            || archiveProducer.RunId != context.ArchiveProducerRunId
            || archiveProducer.RunAttempt != context.ArchiveProducerRunAttempt
            || archiveProducer.AttemptUrl != expectedArchiveAttempt
            || archiveProducer.SourceSha != context.CandidateSourceSha
            || archiveProducer.SourceRef != context.ArchiveProducerSourceRef
            || archive.Artifact.Id != context.ArchiveArtifactId
            || archive.Artifact.Name != context.ArchiveArtifactName
            || archive.Artifact.Name != expectedArchiveName
            || archive.Artifact.UploadDigest != context.ArchiveArtifactUploadDigest
            || !IsDigest(archive.ContentDigest)
            || !IsDigest(archive.Artifact.UploadDigest))
        {
            err(
                "release_candidate archive inputs do not match the exact trusted " +
                "workflow attempt and artifact");
        }
        var ghcr = evidence.ReleaseCandidate.GhcrInputs;
        var ghcrProducer = ghcr.Producer;
        var expectedGhcrAttempt =
            $"https://github.com/{context.GhcrProducerRepository}/actions/runs/" +
            $"{context.GhcrProducerRunId}/attempts/" +
            context.GhcrProducerRunAttempt;
        var expectedGhcrName =
            $"aws2azure-rc-ghcr-{context.ReleaseCandidateId}-" +
            $"{context.GhcrInputsDigest["sha256:".Length..]}-run-" +
            $"{context.GhcrProducerRunId}-attempt-" +
            context.GhcrProducerRunAttempt;
        if (ghcr.ContentDigest != context.GhcrInputsDigest
            || ghcrProducer.Repository != context.GhcrProducerRepository
            || ghcrProducer.WorkflowPath != context.GhcrProducerWorkflowPath
            || ghcrProducer.EventName != "workflow_dispatch"
            || ghcrProducer.RunId != context.GhcrProducerRunId
            || ghcrProducer.RunAttempt != context.GhcrProducerRunAttempt
            || ghcrProducer.AttemptUrl != expectedGhcrAttempt
            || ghcrProducer.SourceSha != context.GhcrProducerSourceSha
            || ghcrProducer.SourceRef != context.GhcrProducerSourceRef
            || ghcr.Artifact.Id != context.GhcrArtifactId
            || ghcr.Artifact.Name != context.GhcrArtifactName
            || ghcr.Artifact.Name != expectedGhcrName
            || ghcr.Artifact.UploadDigest != context.GhcrArtifactUploadDigest
            || ghcr.IndexDigest != context.GhcrIndexDigest
            || !IsDigest(ghcr.ContentDigest)
            || !IsDigest(ghcr.Artifact.UploadDigest)
            || !IsDigest(ghcr.IndexDigest))
        {
            err(
                "release_candidate GHCR inputs do not match the exact trusted " +
                "workflow attempt, artifact, and index");
        }
        if (evidence.Candidate.IdentityDigest != context.CandidateIdentityDigest
            || evidence.Candidate.RuntimeDigest != context.CandidateRuntimeDigest
            || evidence.Candidate.SourceSha != context.CandidateSourceSha
            || !IsDigest(evidence.Candidate.IdentityDigest)
            || !IsDigest(evidence.Candidate.RuntimeDigest)
            || !IsGitSha(evidence.Candidate.SourceSha))
        {
            err("candidate does not exactly match the trusted RC candidate identity");
        }
        if (evidence.Prior.IdentityDigest != context.PriorIdentityDigest
            || evidence.Prior.RuntimeDigest != context.PriorRuntimeDigest
            || evidence.Prior.SourceSha != context.PriorSourceSha
            || !IsDigest(evidence.Prior.IdentityDigest)
            || !IsDigest(evidence.Prior.RuntimeDigest)
            || !IsGitSha(evidence.Prior.SourceSha))
        {
            err("prior does not exactly match the trusted approved-runtime identity");
        }
        if (evidence.Candidate.IdentityDigest == evidence.Prior.IdentityDigest
            || evidence.Candidate.RuntimeDigest == evidence.Prior.RuntimeDigest)
        {
            err("candidate and prior identities must be distinct");
        }
        if (evidence.Profile.Id != context.ProfileId
            || evidence.Profile.Version != context.ProfileVersion)
        {
            err("profile id/version does not match the RC manifest profile selection");
        }
    }

    private static void ValidateEnvironment(
        RcObservationEvidence evidence,
        RcObservationValidationContext context,
        Action<string> err)
    {
        if (evidence.Azure.BackendKind != context.AzureBackendKind
            || evidence.Azure.Region != context.AzureRegion
            || evidence.Azure.BackendIdentityDigest != context.AzureBackendIdentityDigest
            || evidence.Azure.ConfigDigest != context.ConfigDigest
            || evidence.Azure.AwsBindingDigest != context.AwsBindingDigest
            || string.IsNullOrWhiteSpace(evidence.Azure.BackendKind)
            || !RegionRegex().IsMatch(evidence.Azure.Region)
            || !IsDigest(evidence.Azure.BackendIdentityDigest)
            || !IsDigest(evidence.Azure.ConfigDigest)
            || !IsDigest(evidence.Azure.AwsBindingDigest))
        {
            err("Azure backend, region, configuration, or AWS binding drift detected");
        }
    }

    private static void ValidateWindow(
        RcObservationEvidence evidence,
        RcObservationValidationContext context,
        DateTimeOffset nowUtc,
        Action<string> err)
    {
        var window = evidence.Observation;
        var duration = window.EndedAtUtc - window.StartedAtUtc;
        if (window.MinimumWindowMinutes != context.MinimumWindowMinutes
            || window.MinimumWindowMinutes <= 0
            || window.StartedAtUtc == default
            || window.EndedAtUtc <= window.StartedAtUtc
            || duration < TimeSpan.FromMinutes(window.MinimumWindowMinutes))
        {
            err("observation window is incomplete or below the trusted minimum");
        }
        if (evidence.Decision.Verdict == "rollback"
            && evidence.Restoration is not null
            && evidence.Restoration.StartedAtUtc - window.StartedAtUtc
                < TimeSpan.FromMinutes(window.MinimumWindowMinutes))
        {
            err("rollback was initiated before the trusted minimum observation window elapsed");
        }
        if (window.StartedAtUtc > nowUtc
            || window.EndedAtUtc > nowUtc
            || window.GeneratedAtUtc > nowUtc)
        {
            err("observation contains future timestamps");
        }
        if (window.GeneratedAtUtc < window.EndedAtUtc)
        {
            err("observation generated_at_utc must not precede ended_at_utc");
        }
        if (window.EndedAtUtc != default
            && nowUtc - window.EndedAtUtc > context.MaximumEvidenceAge)
        {
            err("observation window is stale");
        }
    }

    private static void ValidateCohorts(
        RcObservationEvidence evidence,
        RcObservationValidationContext context,
        Action<string> err)
    {
        if (evidence.Cohorts.Count != 2)
        {
            err("exactly one candidate and one stable cohort are required");
            return;
        }

        var candidateCohorts = evidence.Cohorts
            .Where(item => item.Role == "candidate")
            .ToList();
        var stableCohorts = evidence.Cohorts
            .Where(item => item.Role == "stable")
            .ToList();
        if (candidateCohorts.Count != 1 || stableCohorts.Count != 1)
        {
            err("cohorts must have distinct candidate and stable roles");
            return;
        }
        var candidate = candidateCohorts[0];
        var stable = stableCohorts[0];
        if (string.IsNullOrWhiteSpace(candidate.Id)
            || string.IsNullOrWhiteSpace(stable.Id)
            || candidate.Id == stable.Id)
        {
            err("candidate and stable cohort ids must be non-empty and distinct");
        }
        ValidateCohort(
            candidate,
            evidence.Candidate,
            evidence,
            context,
            err);
        ValidateCohort(
            stable,
            evidence.Prior,
            evidence,
            context,
            err);

        var members = new HashSet<string>(StringComparer.Ordinal);
        foreach (var cohort in evidence.Cohorts)
        {
            if (cohort.MemberDigests.Count == 0)
            {
                err($"cohort '{cohort.Id}' must contain at least one immutable member digest");
            }
            foreach (var member in cohort.MemberDigests)
            {
                if (!IsDigest(member))
                {
                    err($"cohort '{cohort.Id}' contains a malformed member digest");
                }
                else if (!members.Add(member))
                {
                    err("candidate and stable cohorts are mixed or contain duplicate members");
                }
            }
        }
    }

    private static void ValidateCohort(
        RcObservationCohort cohort,
        RcObservationRuntimeIdentity runtime,
        RcObservationEvidence evidence,
        RcObservationValidationContext context,
        Action<string> err)
    {
        if (cohort.RuntimeIdentityDigest != runtime.IdentityDigest
            || cohort.RuntimeDigest != runtime.RuntimeDigest)
        {
            err($"cohort '{cohort.Id}' contains runtime identity drift");
        }
        if (cohort.BackendKind != context.AzureBackendKind
            || cohort.Region != context.AzureRegion
            || cohort.BackendIdentityDigest != context.AzureBackendIdentityDigest
            || cohort.ConfigDigest != context.ConfigDigest
            || cohort.AwsBindingDigest != context.AwsBindingDigest)
        {
            err($"cohort '{cohort.Id}' contains backend, region, config, or binding drift");
        }
        var expectedUntil = cohort.Role == "candidate"
                            && evidence.Decision.Verdict == "rollback"
                            && evidence.Restoration is not null
            ? evidence.Restoration.StartedAtUtc
            : evidence.Observation.EndedAtUtc;
        if (cohort.ObservedFromUtc != evidence.Observation.StartedAtUtc
            || cohort.ObservedUntilUtc != expectedUntil)
        {
            err($"cohort '{cohort.Id}' does not cover its exact attributable window");
        }
    }

    private static HashSet<string> ValidateMetricsAndTriggers(
        RcObservationEvidence evidence,
        Action<string> err)
    {
        var breached = new HashSet<string>(StringComparer.Ordinal);
        var metrics = new Dictionary<string, RcObservationMetric>(StringComparer.Ordinal);
        if (evidence.Metrics.Count == 0)
        {
            err("at least one thresholded metric is required");
        }
        foreach (var metric in evidence.Metrics)
        {
            if (string.IsNullOrWhiteSpace(metric.Id)
                || string.IsNullOrWhiteSpace(metric.Unit)
                || !metrics.TryAdd(metric.Id, metric))
            {
                err("metrics must have unique non-empty ids and units");
                continue;
            }
            if (metric.Threshold is not double threshold
                || metric.CandidateValue is not double candidateValue
                || metric.StableValue is not double stableValue
                || !double.IsFinite(threshold)
                || !double.IsFinite(candidateValue)
                || !double.IsFinite(stableValue)
                || metric.Samples <= 0)
            {
                err($"metric '{metric.Id}' contains malformed or non-finite values");
                continue;
            }
            if (metric.CapturedAtUtc < evidence.Observation.StartedAtUtc
                || metric.CapturedAtUtc > evidence.Observation.EndedAtUtc)
            {
                err($"metric '{metric.Id}' was captured outside the observation window");
            }

            var isBreached = metric.Comparison switch
            {
                "less_than_or_equal" => candidateValue > threshold,
                "greater_than_or_equal" => candidateValue < threshold,
                _ => false,
            };
            if (metric.Comparison is not ("less_than_or_equal" or "greater_than_or_equal"))
            {
                err($"metric '{metric.Id}' has an unknown comparison");
                continue;
            }
            var expectedResult = isBreached ? "breach" : "pass";
            if (metric.Result != expectedResult)
            {
                err(
                    $"metric '{metric.Id}' threshold result is '{metric.Result}' " +
                    $"but must be '{expectedResult}'");
            }
            if (isBreached)
            {
                breached.Add(metric.Id);
            }
        }

        var triggerIds = new HashSet<string>(StringComparer.Ordinal);
        var triggerMetrics = new HashSet<string>(StringComparer.Ordinal);
        foreach (var trigger in evidence.RollbackTriggers)
        {
            if (string.IsNullOrWhiteSpace(trigger.Id) || !triggerIds.Add(trigger.Id))
            {
                err("rollback triggers must have unique non-empty ids");
            }
            if (!metrics.ContainsKey(trigger.MetricId) || !triggerMetrics.Add(trigger.MetricId))
            {
                err($"rollback trigger '{trigger.Id}' references an unknown or duplicate metric");
            }
            var expectedStatus = breached.Contains(trigger.MetricId) ? "fired" : "armed";
            if (trigger.Status != expectedStatus)
            {
                err(
                    $"rollback trigger '{trigger.Id}' status is '{trigger.Status}' " +
                    $"but must be '{expectedStatus}'");
            }
            if (trigger.OverrideApplied is not false || trigger.Suppressed is not false)
            {
                err(
                    $"rollback trigger '{trigger.Id}' must explicitly declare " +
                    "override_applied and suppressed as false");
            }
        }
        if (metrics.Count != triggerMetrics.Count)
        {
            err("every thresholded metric must have exactly one explicit rollback trigger");
        }
        return breached;
    }

    private static void ValidateDecisionAndRestoration(
        RcObservationEvidence evidence,
        RcObservationValidationContext context,
        IReadOnlySet<string> breachedMetrics,
        DateTimeOffset nowUtc,
        Action<string> err)
    {
        var decision = evidence.Decision;
        if (decision.Verdict is not ("pass" or "rollback")
            || string.IsNullOrWhiteSpace(decision.Owner)
            || string.IsNullOrWhiteSpace(decision.Reason))
        {
            err("decision must contain a pass/rollback verdict, owner, and reason");
        }
        if (decision.DecidedAtUtc < evidence.Observation.EndedAtUtc
            || decision.DecidedAtUtc > evidence.Observation.GeneratedAtUtc
            || decision.DecidedAtUtc > nowUtc)
        {
            err("decision timestamp must be after observation and no later than generation");
        }
        if (breachedMetrics.Count > 0 && decision.Verdict != "rollback")
        {
            err("threshold breach cannot be marked pass");
        }
        if (breachedMetrics.Count == 0 && decision.Verdict == "rollback")
        {
            err("rollback verdict requires at least one fired threshold trigger");
        }

        if (decision.Verdict != "rollback")
        {
            if (evidence.Restoration is not null)
            {
                err("restoration evidence is only valid for a rollback verdict");
            }
            return;
        }
        if (evidence.Restoration is null)
        {
            err("rollback verdict requires verified exact-prior restoration");
            return;
        }

        var restoration = evidence.Restoration;
        if (!restoration.Verified
            || restoration.RuntimeIdentityDigest != context.PriorIdentityDigest
            || restoration.RuntimeDigest != context.PriorRuntimeDigest
            || restoration.BackendIdentityDigest != context.AzureBackendIdentityDigest
            || restoration.ConfigDigest != context.ConfigDigest
            || restoration.AwsBindingDigest != context.AwsBindingDigest)
        {
            err("rollback did not verify restoration of the exact trusted prior environment");
        }
        if (restoration.StartedAtUtc < evidence.Observation.StartedAtUtc
            || restoration.VerifiedAtUtc <= restoration.StartedAtUtc
            || restoration.VerifiedAtUtc > evidence.Observation.EndedAtUtc)
        {
            err("restoration timestamps must be ordered and inside the observation window");
        }
    }

    private static bool IsDigest(string? value) =>
        value is not null && DigestRegex().IsMatch(value);

    private static bool IsGitSha(string? value) =>
        value is not null && GitShaRegex().IsMatch(value);

    [GeneratedRegex("^sha256:[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex DigestRegex();

    [GeneratedRegex("^[0-9a-f]{40}$", RegexOptions.CultureInvariant)]
    private static partial Regex GitShaRegex();

    [GeneratedRegex(
        "^v(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)-rc\\.[1-9][0-9]*$",
        RegexOptions.CultureInvariant)]
    private static partial Regex ReleaseCandidateIdRegex();

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,62}$", RegexOptions.CultureInvariant)]
    private static partial Regex RegionRegex();

    [GeneratedRegex(
        "^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$",
        RegexOptions.CultureInvariant)]
    private static partial Regex RepositoryRegex();

    [GeneratedRegex(
        "^refs/tags/v[0-9]+\\.[0-9]+\\.[0-9]+-rc([.-]?[0-9A-Za-z]+)*$",
        RegexOptions.CultureInvariant)]
    private static partial Regex TrustedRefRegex();

    [GeneratedRegex(
        "^(refs/heads/main|refs/tags/v[0-9]+\\.[0-9]+\\.[0-9]+-rc([.-]?[0-9A-Za-z]+)*)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex ObservationProducerRefRegex();
}
