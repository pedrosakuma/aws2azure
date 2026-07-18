using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aws2Azure.TestSupport.OperationalQualification;

namespace Aws2Azure.UnitTests.OperationalQualification;

[Collection(SealedRuntimeLauncherCollection.Name)]
public sealed class SealedRuntimeLauncherTests
{
    private static readonly string[] RuntimeEnvironmentNames =
    [
        "AWS2AZURE_SEALED_RUNTIME_MODE",
        "AWS2AZURE_QUALIFICATION_SHA",
        "AWS2AZURE_SEALED_CANDIDATE_EXECUTABLE",
        "AWS2AZURE_SEALED_CANDIDATE_MANIFEST",
        "AWS2AZURE_SEALED_CANDIDATE_IDENTITY",
        "AWS2AZURE_SEALED_PRIOR_EXECUTABLE",
        "AWS2AZURE_SEALED_PRIOR_MANIFEST",
        "AWS2AZURE_SEALED_PRIOR_IDENTITY",
        "ACTIONS_ID_TOKEN_REQUEST_TOKEN",
        "ACTIONS_ID_TOKEN_REQUEST_URL",
        "GH_TOKEN",
        "AZURE_SUBSCRIPTION_ID",
    ];

    [Fact]
    public void Load_fails_closed_when_sealed_paths_are_absent()
    {
        using var environment = PreserveEnvironment();
        Environment.SetEnvironmentVariable("AWS2AZURE_SEALED_RUNTIME_MODE", "rollback");
        Environment.SetEnvironmentVariable(
            "AWS2AZURE_QUALIFICATION_SHA",
            "0123456789abcdef0123456789abcdef01234567");

        var exception = Assert.Throws<InvalidDataException>(() =>
            SealedRuntimeSelection.Load("s3-basic-object-crud", 1));

        Assert.Contains(
            "AWS2AZURE_SEALED_CANDIDATE_EXECUTABLE",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Source_validation_preserves_the_ordinary_dotnet_run_path()
    {
        using var environment = PreserveEnvironment();
        Environment.SetEnvironmentVariable(
            "AWS2AZURE_SEALED_RUNTIME_MODE",
            "source_validation");
        var selection = SealedRuntimeSelection.Load("s3-basic-object-crud", 1);

        var startInfo = SealedRuntimeLauncher.CreateStartInfo(
            selection,
            SealedRuntimeRole.Candidate,
            FindRepoRoot(),
            43122,
            Path.Combine(FindRepoRoot(), "config.json"));

        Assert.False(selection.IsSealed);
        Assert.Equal("dotnet", startInfo.FileName);
        Assert.Equal("run", startInfo.ArgumentList[0]);
        Assert.Contains(
            "src/Aws2Azure.Proxy/Aws2Azure.Proxy.csproj",
            startInfo.ArgumentList);
        Assert.Throws<InvalidOperationException>(() =>
            SealedRuntimeLauncher.CreateStartInfo(
                selection,
                SealedRuntimeRole.Prior,
                FindRepoRoot(),
                43122,
                Path.Combine(FindRepoRoot(), "config.json")));
        Assert.Throws<InvalidDataException>(() =>
            SealedRuntimeLauncher.CreateStartInfo(
                selection,
                SealedRuntimeRole.Candidate,
                FindRepoRoot(),
                43122,
                Path.Combine(FindRepoRoot(), "config.json"),
                new Dictionary<string, string?> { ["GH_TOKEN"] = "forbidden" }));
    }

    [Fact]
    [Trait("Category", "ProcessIntegration")]
    public async Task Exact_executable_launch_scrubs_parent_secrets_and_uses_no_shell()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var environment = PreserveEnvironment();
        var scratch = CreateScratch();
        try
        {
            var output = Path.Combine(scratch, "child-environment.txt");
            var candidate = WriteRuntime(
                scratch,
                "candidate",
                "0123456789abcdef0123456789abcdef01234567",
                aggregateCharacter: 'a',
                runId: 42);
            ConfigureCandidate(candidate);
            Environment.SetEnvironmentVariable("AWS2AZURE_SEALED_RUNTIME_MODE", "candidate");
            Environment.SetEnvironmentVariable(
                "AWS2AZURE_QUALIFICATION_SHA",
                candidate.Identity.Source.Sha);
            Environment.SetEnvironmentVariable("ACTIONS_ID_TOKEN_REQUEST_TOKEN", "oidc-secret");
            Environment.SetEnvironmentVariable(
                "ACTIONS_ID_TOKEN_REQUEST_URL",
                "https://example.invalid/oidc");
            Environment.SetEnvironmentVariable("GH_TOKEN", "github-secret");
            Environment.SetEnvironmentVariable("AZURE_SUBSCRIPTION_ID", "subscription-secret");

            var selection = SealedRuntimeSelection.Load("s3-basic-object-crud", 1);
            var config = Path.Combine(scratch, "config.json");
            await SealedRuntimeLauncher.WritePrivateFileAsync(
                config,
                Encoding.UTF8.GetBytes("{}"));
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(config));
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(scratch));
            var startInfo = SealedRuntimeLauncher.CreateStartInfo(
                selection,
                SealedRuntimeRole.Candidate,
                scratch,
                43123,
                config,
                new Dictionary<string, string?>
                {
                    ["AZURE_CLIENT_ID"] = "explicit-client",
                    ["AWS2AZURE_TEST_ENV_OUTPUT"] = output,
                });

            Assert.Equal(candidate.ExecutablePath, startInfo.FileName);
            Assert.Empty(startInfo.ArgumentList);
            Assert.False(startInfo.UseShellExecute);
            Assert.DoesNotContain("ACTIONS_ID_TOKEN_REQUEST_TOKEN", startInfo.Environment.Keys);
            Assert.DoesNotContain("ACTIONS_ID_TOKEN_REQUEST_URL", startInfo.Environment.Keys);
            Assert.DoesNotContain("GH_TOKEN", startInfo.Environment.Keys);
            Assert.DoesNotContain("AZURE_SUBSCRIPTION_ID", startInfo.Environment.Keys);
            Assert.Equal("explicit-client", startInfo.Environment["AZURE_CLIENT_ID"]);

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Fake sealed executable did not start.");
            await process.WaitForExitAsync();
            Assert.Equal(0, process.ExitCode);
            var childEnvironment = await File.ReadAllTextAsync(output);
            Assert.Contains("AWS2AZURE_CONFIG_FILE=", childEnvironment, StringComparison.Ordinal);
            Assert.Contains("AZURE_CLIENT_ID=explicit-client", childEnvironment, StringComparison.Ordinal);
            Assert.DoesNotContain("oidc-secret", childEnvironment, StringComparison.Ordinal);
            Assert.DoesNotContain("github-secret", childEnvironment, StringComparison.Ordinal);
            Assert.DoesNotContain("subscription-secret", childEnvironment, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    [Fact]
    public void Rollback_selection_rejects_hash_equivalent_candidate_and_prior()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var environment = PreserveEnvironment();
        var scratch = CreateScratch();
        try
        {
            var candidate = WriteRuntime(
                scratch,
                "candidate",
                "0123456789abcdef0123456789abcdef01234567",
                aggregateCharacter: 'a',
                runId: 42);
            var prior = WriteRuntime(
                scratch,
                "prior",
                "89abcdef0123456789abcdef0123456789abcdef",
                aggregateCharacter: 'a',
                runId: 43,
                executableContents: candidate.ExecutableContents);
            ConfigureCandidate(candidate);
            ConfigurePrior(prior);
            Environment.SetEnvironmentVariable("AWS2AZURE_SEALED_RUNTIME_MODE", "rollback");
            Environment.SetEnvironmentVariable(
                "AWS2AZURE_QUALIFICATION_SHA",
                candidate.Identity.Source.Sha);

            var exception = Assert.Throws<InvalidDataException>(() =>
                SealedRuntimeSelection.Load("s3-basic-object-crud", 1));

            Assert.Contains("distinct", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    private static RuntimeFiles WriteRuntime(
        string scratch,
        string role,
        string sourceSha,
        char aggregateCharacter,
        long runId,
        string? executableContents = null)
    {
        var runtimeDirectory = Path.Combine(scratch, role);
        Directory.CreateDirectory(runtimeDirectory);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                runtimeDirectory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        var executable = Path.Combine(runtimeDirectory, "Aws2Azure.Proxy");
        var outputReference = "${AWS2AZURE_TEST_ENV_OUTPUT:?}";
        executableContents ??= $"#!/usr/bin/env bash\nset -euo pipefail\nenv | sort > \"{outputReference}\"\n";
        File.WriteAllText(executable, executableContents);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                executable,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        var executableDigest = Digest(File.ReadAllBytes(executable));
        var aggregateDigest = "sha256:" + new string(aggregateCharacter, 64);
        var artifactName =
            $"aws2azure-sealed-linux-x64-{new string(aggregateCharacter, 64)}" +
            $"-run-{runId}-attempt-1";
        var manifest = Path.Combine(runtimeDirectory, "sealed-runtime-manifest.json");
        File.WriteAllText(
            manifest,
            JsonSerializer.Serialize(
                new
                {
                    schema_version = 1,
                    source = new
                    {
                        repository = "example/repo",
                        git_sha = sourceSha,
                        git_ref = "refs/heads/main",
                    },
                    runtime = new
                    {
                        aggregate_digest = aggregateDigest,
                        executable = new { sha256 = executableDigest },
                    },
                    artifact = new { name = artifactName },
                    producer = new { run_id = runId, run_attempt = 1 },
                }));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(manifest, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        var manifestDigest = Digest(File.ReadAllBytes(manifest));
        var identity = new SealedRuntimeIdentity
        {
            SchemaVersion = 1,
            Role = role,
            Profile = new SealedRuntimeProfileIdentity
            {
                Id = "s3-basic-object-crud",
                Version = 1,
            },
            Status = role == "candidate" ? "candidate" : "bootstrap",
            Eligibility = new SealedRuntimeEligibility
            {
                RollbackBaselineEligible = role == "prior",
                PromotionEligible = false,
            },
            LedgerRecordDigest = role == "prior" ? "sha256:" + new string('9', 64) : null,
            Source = new SealedRuntimeSourceIdentity
            {
                Repository = "example/repo",
                Sha = sourceSha,
                Ref = "refs/heads/main",
            },
            Runtime = new SealedRuntimeDigestIdentity
            {
                AggregateDigest = aggregateDigest,
                ExecutableDigest = executableDigest,
                ManifestDigest = manifestDigest,
            },
            Producer = new SealedRuntimeProducerIdentity
            {
                Workflow = ".github/workflows/sealed-runtime.yml",
                EventName = "workflow_dispatch",
                RunId = runId,
                RunAttempt = 1,
                RunUrl = $"https://github.com/example/repo/actions/runs/{runId}",
                AttemptUrl = $"https://github.com/example/repo/actions/runs/{runId}/attempts/1",
                RunStartedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
            },
            Artifact = new SealedRuntimeArtifactIdentity
            {
                Id = runId,
                Name = artifactName,
                UploadDigest = "sha256:" + new string('8', 64),
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(1),
            },
            Attestation = new SealedRuntimeAttestationIdentity
            {
                PredicateType = "https://slsa.dev/provenance/v1",
                Repository = "example/repo",
                SignerWorkflow = "example/repo/.github/workflows/sealed-runtime.yml",
                SourceSha = sourceSha,
                SourceRef = "refs/heads/main",
                RunInvocationUrl =
                    $"https://github.com/example/repo/actions/runs/{runId}/attempts/1",
                BundleDigest = "sha256:" + new string('7', 64),
                ExecutableSubjectName = "Aws2Azure.Proxy",
                ExecutableSubjectDigest = executableDigest,
                ManifestSubjectName = "sealed-runtime-manifest.json",
                ManifestSubjectDigest = manifestDigest,
            },
        };
        var identityPath = Path.Combine(runtimeDirectory, "identity.json");
        File.WriteAllText(
            identityPath,
            JsonSerializer.Serialize(
                identity,
                new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
                }));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(identityPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        return new RuntimeFiles(
            executable,
            manifest,
            identityPath,
            executableContents,
            identity);
    }

    private static void ConfigureCandidate(RuntimeFiles runtime)
    {
        Environment.SetEnvironmentVariable(
            "AWS2AZURE_SEALED_CANDIDATE_EXECUTABLE",
            runtime.ExecutablePath);
        Environment.SetEnvironmentVariable(
            "AWS2AZURE_SEALED_CANDIDATE_MANIFEST",
            runtime.ManifestPath);
        Environment.SetEnvironmentVariable(
            "AWS2AZURE_SEALED_CANDIDATE_IDENTITY",
            runtime.IdentityPath);
    }

    private static void ConfigurePrior(RuntimeFiles runtime)
    {
        Environment.SetEnvironmentVariable(
            "AWS2AZURE_SEALED_PRIOR_EXECUTABLE",
            runtime.ExecutablePath);
        Environment.SetEnvironmentVariable(
            "AWS2AZURE_SEALED_PRIOR_MANIFEST",
            runtime.ManifestPath);
        Environment.SetEnvironmentVariable(
            "AWS2AZURE_SEALED_PRIOR_IDENTITY",
            runtime.IdentityPath);
    }

    private static string CreateScratch() =>
        SealedRuntimeLauncher.CreatePrivateDirectory(
            AppContext.BaseDirectory,
            "sealed-launcher-test");

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

    private static string Digest(ReadOnlySpan<byte> value) =>
        "sha256:" + Convert.ToHexStringLower(SHA256.HashData(value));

    private static EnvironmentRestorer PreserveEnvironment() =>
        new(RuntimeEnvironmentNames);

    private sealed record RuntimeFiles(
        string ExecutablePath,
        string ManifestPath,
        string IdentityPath,
        string ExecutableContents,
        SealedRuntimeIdentity Identity);

    private sealed class EnvironmentRestorer : IDisposable
    {
        private readonly Dictionary<string, string?> _values;

        public EnvironmentRestorer(IEnumerable<string> names)
        {
            _values = names.ToDictionary(
                name => name,
                Environment.GetEnvironmentVariable,
                StringComparer.Ordinal);
            foreach (var name in _values.Keys)
            {
                Environment.SetEnvironmentVariable(name, null);
            }
        }

        public void Dispose()
        {
            foreach (var (name, value) in _values)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SealedRuntimeLauncherCollection
{
    public const string Name = "sealed-runtime-launcher";
}
