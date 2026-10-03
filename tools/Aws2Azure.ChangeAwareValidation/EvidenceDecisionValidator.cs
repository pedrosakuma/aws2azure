using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace Aws2Azure.ChangeAwareValidation;

public static class EvidenceDecisionValidator
{
    public const string ToolVersion = "validation-evidence-v1";

    public static ReviewBinding Review(string repository, ValidationPlan plan, string? evidenceSource = null)
    {
        var comparison = plan.Comparison
            ?? throw new InvalidOperationException("Evidence requires a git comparison.");
        RequireClean(repository);
        // Re-read rather than trusting a caller-supplied plan's commit identities.
        var current = GitDiffReader.Read(comparison.BaseCommit, repository);
        Require(current.Comparison.HeadCommit == comparison.HeadCommit &&
            current.Comparison.MergeBase == comparison.MergeBase &&
            current.ChangedPaths.SequenceEqual(plan.ChangedPaths),
            "The repository no longer matches the classified inputs.");
        if (evidenceSource is not null)
        {
            Require(IsCommit(evidenceSource), "Evidence source must be a full commit SHA.");
            Require(GitDiffReader.RunGit(repository, "rev-parse", $"{evidenceSource}^{{commit}}") == evidenceSource,
                "Evidence source does not resolve exactly.");
            Require(GitDiffReader.RunGit(repository, "merge-base", evidenceSource, comparison.HeadCommit) == evidenceSource,
                "Evidence source must be an ancestor of the reviewed head.");
        }
        var binding = new ReviewBinding(
            ToolVersion, comparison.BaseCommit, comparison.MergeBase, comparison.HeadCommit,
            DiffHash(repository, comparison.MergeBase, comparison.HeadCommit),
            Hash(JsonSerializer.SerializeToUtf8Bytes(
                ValidationPlanBuilder.Build(plan.ChangedPaths), ValidationJsonContext.Default.ValidationPlan)),
            evidenceSource,
            evidenceSource is null ? null : DiffHash(repository, evidenceSource, comparison.HeadCommit));
        RequireClean(repository);
        return binding;
    }

    public static ValidationPlan Apply(
        string repository, ValidationPlan plan, string decisionPath, string approvedDigest,
        DateTimeOffset? now = null)
    {
        var time = now ?? DateTimeOffset.UtcNow;
        var bytes = File.ReadAllBytes(decisionPath);
        Require(IsHash(approvedDigest) && Hash(bytes) == approvedDigest,
            "Decision SHA256 differs from the externally approved digest.");
        RejectDuplicateProperties(bytes);
        var decision = JsonSerializer.Deserialize(bytes, ValidationJsonContext.Default.EvidenceDecision)
            ?? throw new InvalidOperationException("Decision is null.");
        Require(decision.SchemaVersion == 1 && decision.Status == "accepted",
            "Only schema 1 accepted decisions may be applied.");
        Require(decision.Gate == "real-azure", "Only the real-azure gate supports evidence decisions.");
        Require(decision.Mode is "offline-covered" or "reuse", "Unsupported decision mode.");
        Text(decision.Rationale, "rationale");
        Text(decision.ApprovedBy, "approvedBy");
        Reference(decision.ApprovalReference);
        Require(decision.ReviewedAtUtc != default && decision.ReviewedAtUtc <= time &&
            decision.ValidUntilUtc > time && decision.ValidUntilUtc > decision.ReviewedAtUtc,
            "Decision review/validity window is invalid or expired.");
        Require(decision.Binding is not null, "Missing binding.");
        var actual = Review(repository, plan, decision.Binding!.EvidenceSourceCommit);
        Require(actual == decision.Binding, "Stale input, base, head, classifier, or evidence-source binding.");
        Require((decision.Mode == "reuse") == (actual.EvidenceSourceCommit is not null),
            "Only reuse decisions require an evidence source.");
        var gate = plan.Gates.Single(g => g.Name == "real-azure");
        Require(gate.Status == "required" && decision.OriginalReasons is not null &&
            gate.Reasons.SequenceEqual(decision.OriginalReasons),
            "Decision does not match the original required gate reasons.");
        var directory = Path.GetDirectoryName(Path.GetFullPath(decisionPath))!;
        var snapshotBytes = File.ReadAllBytes(VerifyBoundFile(directory, new EvidenceFile(decision.EvidencePath, decision.EvidenceSha256)));
        Require(Hash(snapshotBytes) == decision.EvidenceSha256, "Evidence changed while reading.");
        RejectDuplicateProperties(snapshotBytes);
        var evidence = JsonSerializer.Deserialize(snapshotBytes, ValidationJsonContext.Default.EvidenceSnapshot)
            ?? throw new InvalidOperationException("Evidence is null.");
        Require(evidence.SchemaVersion == 1 && evidence.Kind == decision.Mode &&
            evidence.Repository == "pedrosakuma/aws2azure" && !evidence.Revoked,
            "Unsupported, wrong-repository, or revoked evidence.");
        Text(evidence.Scope, "evidence scope");
        Text(evidence.VerifiedBy, "evidence verifiedBy");
        Reference(evidence.VerificationReference);
        Require(evidence.VerifiedAtUtc != default &&
            evidence.VerifiedAtUtc <= decision.ReviewedAtUtc,
            "Evidence must have been verified before approval.");
        var evidenceDirectory = Path.GetDirectoryName(Path.GetFullPath(Path.Combine(directory, decision.EvidencePath)))!;
        if (decision.Mode == "offline-covered")
        {
            Require(evidence.SourceCommit == actual.HeadCommit && evidence.Offline is not null && evidence.Run is null,
                "Offline evidence must cover the exact target head, not an Azure run.");
            var offline = evidence.Offline!;
            Text(offline.Verifier, "offline verifier");
            Text(offline.VerifierVersion, "offline verifier version");
            Text(offline.Command, "offline command");
            Require(offline.Result == "pass", "Offline verifier did not pass.");
            VerifyBoundFile(evidenceDirectory, offline.Report);
        }
        else
        {
            Require(evidence.SourceCommit == actual.EvidenceSourceCommit &&
                evidence.Run is not null && evidence.Offline is null,
                "Run evidence does not match the reviewed successful source.");
            var run = evidence.Run!;
            Require(run.RunId > 0 && run.Attempt > 0 &&
                run.WorkflowPath == ".github/workflows/integration-real-azure.yml" &&
                run.Event == "pull_request" && run.Status == "completed" && run.Conclusion == "success" &&
                run.CreatedAtUtc != default && run.CompletedAtUtc >= run.CreatedAtUtc &&
                run.CompletedAtUtc <= evidence.VerifiedAtUtc,
                "Incomplete or unsupported successful run identity.");
            Require(run.Artifacts is { Length: > 0 }, "Run evidence requires available artifact archives.");
            var ids = new HashSet<long>();
            foreach (var artifact in run.Artifacts)
            {
                Require(artifact is not null && artifact.Id > 0 && ids.Add(artifact.Id) &&
                    !artifact.Expired && artifact.ExpiresAtUtc > time, "Artifact is duplicate, missing or expired.");
                Text(artifact!.Name, "artifact name");
                VerifyBoundFile(evidenceDirectory, artifact.Archive);
            }
        }
        RequireClean(repository);
        return plan with
        {
            SchemaVersion = 2,
            ReviewBinding = actual,
            RequiredLabels = plan.Gates.Where(g => g.Status == "required" && g.Name != decision.Gate)
                .SelectMany(g => g.Labels).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            EvidenceAcceptance = new EvidenceAcceptance(
                decision.Gate, decision.Mode == "reuse" ? "evidence-reused" : "accepted",
                approvedDigest, decision, evidence, false, false,
                "Attributable human acceptance of pinned local evidence; hashes verify bytes, not GitHub provenance. " +
                "No live lookup, new execution, current Azure health, or branch-protection approval.")
        };
    }

    private static void RequireClean(string repository)
    {
        Require(GitDiffReader.RunGit(repository, "-c", "core.fileMode=true", "status",
            "--porcelain=v1", "--untracked-files=all", "--ignore-submodules=none").Length == 0,
            "Evidence requires a clean worktree and index, including untracked files.");
        var entries = GitDiffReader.RunGit(repository, "ls-files", "-v", "-z").Split('\0', StringSplitOptions.RemoveEmptyEntries);
        Require(entries.All(e => e[0] == 'H'), "Sparse, assume-unchanged, or unresolved index entries are unsupported.");
    }

    internal static string DiffHash(string repository, string source, string target)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = repository, RedirectStandardOutput = true,
            RedirectStandardError = true, UseShellExecute = false
        };
        foreach (var arg in new[] { "-c", "core.quotePath=true", "diff", "--binary", "--full-index",
            "--no-ext-diff", "--no-textconv", "--no-renames", "--no-color", "--src-prefix=a/",
            "--dst-prefix=b/", "--diff-algorithm=myers", "--no-indent-heuristic", "--unified=3",
            "--inter-hunk-context=0", "--ignore-submodules=none", source, target, "--" })
        {
            start.ArgumentList.Add(arg);
        }
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Unable to start git.");
        var error = process.StandardError.ReadToEndAsync();
        var digest = Convert.ToHexStringLower(SHA256.HashData(process.StandardOutput.BaseStream));
        process.WaitForExit();
        Require(process.ExitCode == 0, $"Cannot fingerprint git diff: {error.GetAwaiter().GetResult()}");
        return digest;
    }

    internal static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static string VerifyBoundFile(string directory, EvidenceFile? file)
    {
        Require(file is not null, "Missing evidence file.");
        Text(file!.Path, "evidence path");
        Require(!Path.IsPathRooted(file.Path) &&
            !file.Path.Replace('\\', '/').Split('/').Contains(".."),
            "Evidence paths must be relative without parent traversal.");
        var path = Path.Combine(directory, file.Path);
        using var stream = File.OpenRead(path);
        Require(stream.Length > 0 && IsHash(file.Sha256) &&
            Convert.ToHexStringLower(SHA256.HashData(stream)) == file.Sha256,
            "Missing, empty or changed evidence bytes.");
        return path;
    }

    private static bool IsHash(string? value) =>
        value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsCommit(string? value) =>
        value is { Length: 40 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static void Text(string? value, string field) =>
        Require(!string.IsNullOrWhiteSpace(value), $"Missing {field}.");

    private static void Reference(string? value) =>
        Require(Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "https" &&
            uri.Host == "github.com" && uri.AbsolutePath.StartsWith("/pedrosakuma/aws2azure/", StringComparison.Ordinal),
            "Approval and verification references must identify this repository on GitHub.");

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void RejectDuplicateProperties(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes);
        Visit(document.RootElement);
        static void Visit(JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in value.EnumerateObject())
                {
                    Require(names.Add(property.Name), $"Duplicate JSON field '{property.Name}'.");
                    Visit(property.Value);
                }
            }
            else if (value.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in value.EnumerateArray())
                {
                    Visit(item);
                }
            }
        }
    }
}
