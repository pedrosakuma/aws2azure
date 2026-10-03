using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aws2Azure.ChangeAwareValidation;

namespace Aws2Azure.UnitTests.ChangeAwareValidation;

public sealed class EvidenceDecisionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExactDecisionPreservesOriginalGateAndOtherLabels(bool reuse)
    {
        using var fixture = new Fixture(reuse);
        var original = fixture.Plan();
        var accepted = fixture.Apply();
        Assert.Contains("run-real-azure", original.RequiredLabels);
        Assert.DoesNotContain("run-real-azure", accepted.RequiredLabels);
        Assert.Equal(original.RequiredLabels.Where(l => l != "run-real-azure"), accepted.RequiredLabels);
        Assert.Equal(
            JsonSerializer.Serialize(original, ValidationJsonContext.Default.ValidationPlan),
            JsonSerializer.Serialize(accepted with
            {
                SchemaVersion = 1, ReviewBinding = null, EvidenceAcceptance = null,
                RequiredLabels = original.RequiredLabels
            }, ValidationJsonContext.Default.ValidationPlan));
        Assert.Equal("required", accepted.Gates.Single(g => g.Name == "real-azure").Status);
        Assert.Equal(reuse ? "evidence-reused" : "accepted", accepted.EvidenceAcceptance!.Disposition);
        Assert.False(accepted.EvidenceAcceptance.NewlyExecuted);
        Assert.False(accepted.EvidenceAcceptance.LiveProvenanceVerified);
        Assert.Equal(1, original.SchemaVersion);
        Assert.Null(original.EvidenceAcceptance);
    }

    [Theory]
    [InlineData("dirty")]
    [InlineData("staged")]
    [InlineData("untracked")]
    [InlineData("binary")]
    [InlineData("rename")]
    [InlineData("delete")]
    [InlineData("mode")]
    [InlineData("assume-unchanged")]
    [InlineData("skip-worktree")]
    [InlineData("new-head")]
    [InlineData("production-commit")]
    [InlineData("base")]
    public void ChangedRepositoryCannotInheritDecision(string edit)
    {
        using var fixture = new Fixture();
        switch (edit)
        {
            case "dirty": File.AppendAllText(fixture.SourcePath, " "); break;
            case "staged":
                File.AppendAllText(fixture.SourcePath, " ");
                fixture.Git("add", ".");
                break;
            case "untracked": File.WriteAllText(Path.Combine(fixture.Repository, "new.txt"), "new"); break;
            case "binary": File.WriteAllBytes(fixture.SourcePath, [0, 255, 1]); break;
            case "rename": fixture.Git("mv", fixture.RelativeSource, "renamed.cs"); break;
            case "delete": File.Delete(fixture.SourcePath); break;
            case "mode": fixture.Git("update-index", "--chmod=+x", fixture.RelativeSource); break;
            case "assume-unchanged":
                fixture.Git("update-index", "--assume-unchanged", fixture.RelativeSource);
                File.AppendAllText(fixture.SourcePath, "hidden");
                break;
            case "skip-worktree":
                fixture.Git("update-index", "--skip-worktree", fixture.RelativeSource);
                break;
            case "new-head": fixture.Git("commit", "--allow-empty", "-qm", "different head"); break;
            case "production-commit":
                File.AppendAllText(fixture.SourcePath, "new production implementation");
                fixture.Git("add", ".");
                fixture.Git("commit", "-qm", "changed shipping code");
                break;
            case "base": fixture.Base = fixture.Git("rev-parse", "HEAD"); break;
        }
        Assert.Throws<InvalidOperationException>(() => fixture.Apply());
    }

    [Theory]
    [InlineData("status", "pending")]
    [InlineData("status", "blocked")]
    [InlineData("status", "revoked")]
    [InlineData("gate", "perf")]
    [InlineData("gate", "made-up")]
    [InlineData("mode", "skip")]
    [InlineData("approvedBy", "")]
    [InlineData("approvalReference", "https://example.com/approval")]
    [InlineData("rationale", "")]
    [InlineData("evidenceSha256", "bad")]
    [InlineData("validUntilUtc", "2025-01-01T00:00:00Z")]
    [InlineData("reviewedAtUtc", "2099-01-01T00:00:00Z")]
    public void UnsupportedOrIncompleteDecisionFailsEvenWhenPinned(string property, string value)
    {
        using var fixture = new Fixture();
        fixture.ChangeDecision(json => json[property] = value);
        Assert.Throws<InvalidOperationException>(() => fixture.Apply());
    }

    [Theory]
    [InlineData("toolVersion")]
    [InlineData("headCommit")]
    [InlineData("mergeBase")]
    [InlineData("baseCommit")]
    [InlineData("diffSha256")]
    [InlineData("originalPlanSha256")]
    public void ChangedBindingFails(string property)
    {
        using var fixture = new Fixture();
        fixture.ChangeDecision(json => json["binding"]![property] = "changed");
        Assert.Throws<InvalidOperationException>(() => fixture.Apply());
    }

    [Fact]
    public void RationaleEditsInvalidateExternalApprovalPin()
    {
        using var fixture = new Fixture();
        var pin = fixture.Pin;
        fixture.ChangeDecision(json => json["rationale"] = "Different scope");
        Assert.Throws<InvalidOperationException>(() =>
            EvidenceDecisionValidator.Apply(fixture.Repository, fixture.Plan(), fixture.DecisionPath, pin, Fixture.Now));
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("malformed")]
    [InlineData("null")]
    public void InvalidJsonFailsClosed(string kind)
    {
        using var fixture = new Fixture();
        switch (kind)
        {
            case "unknown": fixture.ChangeDecision(json => json["extra"] = true); break;
            case "missing": fixture.ChangeDecision(json => json.AsObject().Remove("rationale")); break;
            case "duplicate":
                File.WriteAllText(fixture.DecisionPath,
                    File.ReadAllText(fixture.DecisionPath).Replace("\"status\":", "\"status\":\"pending\",\"status\":", StringComparison.Ordinal));
                break;
            case "malformed": File.WriteAllText(fixture.DecisionPath, "{"); break;
            case "null": fixture.ChangeDecision(json => json["binding"] = null); break;
        }
        var error = Record.Exception(() => fixture.Apply());
        Assert.True(error is JsonException or InvalidOperationException, error?.ToString());
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("changed")]
    [InlineData("revoked")]
    [InlineData("wrong-source")]
    [InlineData("unknown-field")]
    [InlineData("failed")]
    [InlineData("expired")]
    [InlineData("missing-artifact")]
    [InlineData("changed-artifact")]
    [InlineData("missing-run")]
    [InlineData("wrong-workflow")]
    public void UnavailableOrInvalidReuseEvidenceFails(string kind)
    {
        using var fixture = new Fixture(true);
        var evidence = JsonNode.Parse(File.ReadAllText(fixture.EvidencePath))!;
        switch (kind)
        {
            case "missing": File.Delete(fixture.EvidencePath); break;
            case "changed": File.AppendAllText(fixture.EvidencePath, " "); break;
            case "missing-artifact": File.Delete(fixture.ReportPath); break;
            case "changed-artifact": File.AppendAllText(fixture.ReportPath, "changed"); break;
            default:
                switch (kind)
                {
                    case "revoked": evidence["revoked"] = true; break;
                    case "wrong-source": evidence["sourceCommit"] = fixture.Git("rev-parse", "HEAD"); break;
                    case "unknown-field": evidence["unrecognized"] = "value"; break;
                    case "failed": evidence["run"]!["conclusion"] = "failure"; break;
                    case "expired": evidence["run"]!["artifacts"]![0]!["expiresAtUtc"] = "2025-01-01T00:00:00Z"; break;
                    case "missing-run": evidence["run"] = null; break;
                    case "wrong-workflow": evidence["run"]!["workflowPath"] = ".github/workflows/qualification-real-azure.yml"; break;
                }
                File.WriteAllText(fixture.EvidencePath, evidence.ToJsonString());
                fixture.ChangeDecision(json => json["evidenceSha256"] = Fixture.HashFile(fixture.EvidencePath));
                break;
        }
        var error = Record.Exception(() => fixture.Apply());
        Assert.True(error is IOException or JsonException or InvalidOperationException, error?.ToString());
    }

    [Fact]
    public void DiffFingerprintPreservesWhitespaceBinaryAndMode()
    {
        using var fixture = new Fixture();
        var initial = fixture.Binding();
        File.AppendAllText(fixture.SourcePath, " ");
        fixture.Git("add", ".");
        fixture.Git("commit", "-qm", "whitespace");
        Assert.NotEqual(initial.DiffSha256, fixture.Binding().DiffSha256);
        var whitespace = fixture.Binding();
        File.WriteAllBytes(fixture.SourcePath, [0, 255, 7]);
        fixture.Git("add", ".");
        fixture.Git("commit", "-qm", "binary");
        Assert.NotEqual(whitespace.DiffSha256, fixture.Binding().DiffSha256);
        var binary = fixture.Binding();
        fixture.Git("update-index", "--chmod=+x", fixture.RelativeSource);
        fixture.Git("commit", "-qm", "mode");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(fixture.SourcePath, File.GetUnixFileMode(fixture.SourcePath) | UnixFileMode.UserExecute |
                UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        }
        Assert.NotEqual(binary.DiffSha256, fixture.Binding().DiffSha256);
    }

    [Fact]
    public void CliIsStrictByDefaultAndRequiresExplicitPin()
    {
        using var fixture = new Fixture();
        var baseline = fixture.Cli("--base", fixture.Base);
        Assert.Equal(0, baseline.ExitCode);
        Assert.Contains("run-real-azure", baseline.Output);
        Assert.DoesNotContain("evidenceAcceptance", baseline.Output);
        var review = fixture.Cli("--base", fixture.Base, "--review-inputs");
        Assert.Equal(0, review.ExitCode);
        Assert.Contains("reviewBinding", review.Output);
        Assert.DoesNotContain("evidenceAcceptance", review.Output);
        Assert.Equal(2, fixture.Cli("--decision", fixture.DecisionPath).ExitCode);
        var accepted = fixture.Cli("--base", fixture.Base, "--decision", fixture.DecisionPath, "--decision-sha256", fixture.Pin);
        Assert.Equal(0, accepted.ExitCode);
        Assert.Contains("\"disposition\":\"accepted\"", accepted.Output);
        File.AppendAllText(fixture.SourcePath, " ");
        var stale = fixture.Cli("--base", fixture.Base, "--decision", fixture.DecisionPath, "--decision-sha256", fixture.Pin);
        Assert.Equal(2, stale.ExitCode);
        Assert.Empty(stale.Output);
    }

    private sealed class Fixture : IDisposable
    {
        public static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
        private readonly string _root = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "decision-tests", Guid.NewGuid().ToString("N"));
        public string Repository { get; }
        public string RelativeSource => "src/Aws2Azure.Core/Azure/SharedKeyAuthenticator.cs";
        public string SourcePath => Path.Combine(Repository, RelativeSource);
        public string DecisionPath => Path.Combine(_root, "decision.json");
        public string EvidencePath => Path.Combine(_root, "evidence.json");
        public string ReportPath => Path.Combine(_root, "report.txt");
        public string Base { get; set; }
        public string Pin => HashFile(DecisionPath);

        public Fixture(bool reuse = false)
        {
            Repository = Path.Combine(_root, "target");
            Directory.CreateDirectory(Path.GetDirectoryName(SourcePath)!);
            Git("init", "-q");
            Git("config", "user.name", "Evidence test");
            Git("config", "user.email", "evidence@example.invalid");
            File.WriteAllText(SourcePath, "original");
            Git("add", ".");
            Git("commit", "-qm", "base");
            Base = Git("rev-parse", "HEAD");
            File.WriteAllText(SourcePath, "reviewed");
            Git("add", ".");
            Git("commit", "-qm", "reviewed input");
            var head = Git("rev-parse", "HEAD");
            File.WriteAllText(ReportPath, "Deterministic fixture evidence only.");
            var file = new EvidenceFile("report.txt", HashFile(ReportPath));
            var reference = "https://github.com/pedrosakuma/aws2azure/issues/1087#issuecomment-test";
            var snapshot = new EvidenceSnapshot(1, reuse ? "reuse" : "offline-covered",
                "pedrosakuma/aws2azure", reuse ? Base : head, "Fixture scope", "test-reviewer", reference,
                Now.AddMinutes(-2), false,
                reuse ? null : new OfflineVerification("fixture-verifier", "1", "fixture test", "pass", file),
                reuse ? new RunEvidence(42, 1, ".github/workflows/integration-real-azure.yml", "pull_request",
                    "completed", "success", Now.AddHours(-2), Now.AddHours(-1),
                    [new RunArtifact(7, "fixture", Now.AddDays(1), false, file)]) : null);
            File.WriteAllBytes(EvidencePath, JsonSerializer.SerializeToUtf8Bytes(snapshot, ValidationJsonContext.Default.EvidenceSnapshot));
            var decision = new EvidenceDecision(1, "accepted", "real-azure", snapshot.Kind,
                EvidenceDecisionValidator.Review(Repository, Plan(), reuse ? Base : null),
                Plan().Gates.Single(g => g.Name == "real-azure").Reasons, "Reviewed fixture delta only",
                "test-reviewer", reference, Now.AddMinutes(-1), Now.AddHours(1), "evidence.json", HashFile(EvidencePath));
            File.WriteAllBytes(DecisionPath, JsonSerializer.SerializeToUtf8Bytes(decision, ValidationJsonContext.Default.EvidenceDecision));
        }

        public ValidationPlan Plan()
        {
            var diff = GitDiffReader.Read(Base, Repository);
            return ValidationPlanBuilder.Build(diff.ChangedPaths, diff.Comparison);
        }
        public ReviewBinding Binding() => EvidenceDecisionValidator.Review(Repository, Plan());
        public ValidationPlan Apply() => EvidenceDecisionValidator.Apply(Repository, Plan(), DecisionPath, Pin, Now);
        public static string HashFile(string path) => EvidenceDecisionValidator.Hash(File.ReadAllBytes(path));
        public void ChangeDecision(Action<JsonNode> edit)
        {
            var node = JsonNode.Parse(File.ReadAllText(DecisionPath))!;
            edit(node);
            File.WriteAllText(DecisionPath, node.ToJsonString());
        }
        public string Git(params string[] args) => GitDiffReader.RunGit(Repository, args);
        public (int ExitCode, string Output) Cli(params string[] args)
        {
            var start = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = Repository, RedirectStandardOutput = true,
                RedirectStandardError = true, UseShellExecute = false
            };
            start.ArgumentList.Add("exec");
            start.ArgumentList.Add("--runtimeconfig");
            start.ArgumentList.Add(Path.ChangeExtension(typeof(EvidenceDecisionTests).Assembly.Location, ".runtimeconfig.json"));
            start.ArgumentList.Add(typeof(EvidenceDecisionValidator).Assembly.Location);
            foreach (var arg in args)
            {
                start.ArgumentList.Add(arg);
            }
            using var process = Process.Start(start)!;
            var error = process.StandardError.ReadToEndAsync();
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            Assert.True(process.ExitCode is 0 or 2, error.GetAwaiter().GetResult());
            return (process.ExitCode, output);
        }
        public void Dispose() => Directory.Delete(_root, true);
    }
}
