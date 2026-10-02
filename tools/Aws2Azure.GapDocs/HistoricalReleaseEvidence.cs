using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using YamlDotNet.Core;

namespace Aws2Azure.GapDocs;

public static class HistoricalReleaseEvidence
{
    public static IReadOnlyList<string> ValidateQualification(
        SloQualificationDocument document, DateTimeOffset now)
    {
        var issued = document.Provenance.GeneratedAtUtc;
        var errors = SloQualificationValidator.Validate(document, issued).ToList();
        errors.AddRange(WorkloadGaTemporalValidator.ValidateQualification(document, issued));
        if (issued == default || issued > now || document.Verdict != "qualified"
            || document.ArtifactKind != "real_azure_workload_qualification")
            errors.Add("Historical qualification must be a qualified real-Azure artifact issued in the past.");
        return errors;
    }

    public static IReadOnlyList<string> ValidateObservation(
        RcObservationEvidence evidence, RcObservationValidationContext binding, DateTimeOffset now)
    {
        var issued = evidence.Observation.GeneratedAtUtc;
        var errors = RcObservationValidator.Validate(evidence, binding, issued).ToList();
        if (issued == default || issued > now || evidence.Decision.Verdict != "pass")
            errors.Add("Historical observation must have passed and been issued in the past.");
        return errors;
    }

    public static int Run(string[] args, string repoRoot)
    {
        if (args.Length != 3)
        {
            Console.Error.WriteLine("Usage: validate-historical-release-evidence <verified-identity.json> <observations-directory> <report.json>");
            return 1;
        }
        try
        {
            var report = Build(repoRoot, args[0], args[1], DateTimeOffset.UtcNow);
            File.WriteAllText(args[2], JsonSerializer.Serialize(
                report, HistoricalReleaseEvidenceJsonContext.Default.HistoricalReleaseEvidenceReport) + "\n");
            Console.WriteLine("[release-evidence] Historical evidence validated at issuance; current health and release eligibility are NOT established.");
            return 0;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException
            or JsonException or YamlException or ArgumentException or KeyNotFoundException
            or InvalidOperationException)
        {
            Console.Error.WriteLine("[release-evidence] " + exception.Message);
            return 1;
        }
    }

    public static HistoricalReleaseEvidenceReport Build(
        string repoRoot, string identityPath, string observationsDirectory, DateTimeOffset now)
    {
        using var identity = JsonDocument.Parse(File.ReadAllText(identityPath));
        var root = identity.RootElement;
        var report = new HistoricalReleaseEvidenceReport
        {
            CandidateIdentityDigest = root.GetProperty("identity_digest").GetString()!,
            EvaluatedAtUtc = now,
        };
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var workload in root.GetProperty("workloads").EnumerateArray())
        {
            var profile = workload.GetProperty("profile").GetProperty("id").GetString()!;
            if (string.IsNullOrWhiteSpace(profile) || profile.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')
                || !seen.Add(profile))
                throw new InvalidDataException("Invalid or duplicate historical release profile.");
            var approved = workload.GetProperty("approved_runtime");
            var ledgerPath = Path.Combine(repoRoot, "docs", "workloads", "approved-runtimes", profile + ".yaml");
            if (Digest(ledgerPath) != approved.GetProperty("ledger_record_digest").GetString())
                throw new InvalidDataException("Current ledger differs from the exact RC ledger; review required.");
            var ledger = ApprovedRuntimeLedgerLoader.Load(ledgerPath);
            var manifest = WorkloadGaManifestLoader.Load(
                Path.Combine(repoRoot, "docs", "workloads", profile + ".yaml"));
            RequireNoErrors(ApprovedRuntimeLedgerValidator.Validate([ledger], [manifest], now));
            if (ledger.Status != "approved" || !ledger.Eligibility.PromotionEligible
                || ledger.Revocation is not null || ledger.Qualification is null)
                throw new InvalidDataException("Release runtime is not approved or has been revoked.");
            var decision = ledger.Qualification;
            var qualificationPath = Path.GetFullPath(decision.Artifact, repoRoot);
            if (!qualificationPath.StartsWith(Path.GetFullPath(repoRoot) + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || WorkloadGaEvaluator.ComputeQualificationArtifactDigest(qualificationPath) != decision.Digest)
                throw new InvalidDataException("Qualification path or digest differs from the approved ledger.");
            var qualification = SloQualificationLoader.Load(qualificationPath);
            RequireNoErrors(ValidateQualification(qualification, now));
            if (qualification.Profile.Id != profile || qualification.Profile.Version != ledger.Profile.Version
                || qualification.Candidate.ArtifactDigest != ledger.Runtime.AggregateDigest
                || qualification.Candidate.GitSha != ledger.Runtime.SourceSha
                || decision.CandidateRuntimeDigest != qualification.Candidate.ArtifactDigest
                || decision.QualifiedAt != qualification.Provenance.GeneratedAtUtc
                || decision.ReviewUrl != qualification.Provenance.RunUrl)
                throw new InvalidDataException("Qualification does not match its approved runtime and issuance.");

            var evidence = RcObservationLoader.Load(Path.Combine(observationsDirectory, profile, "evidence", "observation.yaml"));
            var binding = RcObservationCaptureLoader.LoadBinding(Path.Combine(observationsDirectory, profile, "evidence", "binding.json"));
            RequireNoErrors(ValidateObservation(evidence, binding, now));
            if (evidence.ReleaseCandidate.ManifestDigest != report.CandidateIdentityDigest
                || evidence.Profile.Id != profile || evidence.Profile.Version != ledger.Profile.Version
                || evidence.Candidate.RuntimeDigest != ledger.Runtime.AggregateDigest
                || evidence.Candidate.SourceSha != ledger.Runtime.SourceSha
                || evidence.Prior.RuntimeDigest != decision.RollbackTargetRuntimeDigest)
                throw new InvalidDataException("Observation differs from the release identity or qualified runtime pair.");
            report.Profiles.Add(new()
            {
                Profile = profile,
                QualificationDigest = decision.Digest,
                QualificationIssuedAtUtc = qualification.Provenance.GeneratedAtUtc,
                ObservationDigest = evidence.EvidenceDigest,
                ObservationIssuedAtUtc = evidence.Observation.GeneratedAtUtc,
                OriginalQualificationMaxAgeHours = qualification.Rules.MaxArtifactAgeHours,
                OriginalObservationMaxAgeHours = binding.MaximumEvidenceAge.TotalHours,
            });
            report.SourceArtifacts.Add(qualification.Provenance.CorrectnessRun!.EvidenceArtifact!);
            report.SourceArtifacts.AddRange(qualification.Provenance.SourceRuns.Select(run => run.EvidenceArtifact!));
        }
        if (report.Profiles.Count == 0)
            throw new InvalidDataException("Historical release identity contains no profiles.");
        return report;
    }

    private static string Digest(string path) =>
        "sha256:" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    private static void RequireNoErrors(IReadOnlyList<string> errors)
    {
        if (errors.Count > 0)
            throw new InvalidDataException(string.Join(Environment.NewLine, errors));
    }
}

public sealed class HistoricalReleaseEvidenceReport
{
    public int SchemaVersion => 1;
    public string Policy => "release-evidence-reuse-v1";
    public bool ValidWhenIssued => true;
    public bool ReleaseEligible => false;
    public bool CurrentHealthVerified => false;
    public string CandidateIdentityDigest { get; set; } = "";
    public DateTimeOffset EvaluatedAtUtc { get; set; }
    public List<HistoricalReleaseProfile> Profiles { get; set; } = [];
    public List<QualificationRunArtifactIdentity> SourceArtifacts { get; set; } = [];
}

public sealed class HistoricalReleaseProfile
{
    public string Profile { get; set; } = "";
    public string QualificationDigest { get; set; } = "";
    public DateTimeOffset QualificationIssuedAtUtc { get; set; }
    public string ObservationDigest { get; set; } = "";
    public DateTimeOffset ObservationIssuedAtUtc { get; set; }
    public int OriginalQualificationMaxAgeHours { get; set; }
    public double OriginalObservationMaxAgeHours { get; set; }
}

[JsonSerializable(typeof(HistoricalReleaseEvidenceReport))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, WriteIndented = true)]
internal sealed partial class HistoricalReleaseEvidenceJsonContext : JsonSerializerContext;
