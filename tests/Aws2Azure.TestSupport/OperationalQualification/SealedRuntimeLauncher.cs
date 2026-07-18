using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Aws2Azure.TestSupport.OperationalQualification;

public enum SealedRuntimeRole
{
    Candidate,
    Prior,
}

public sealed class SealedRuntimeProfileIdentity
{
    public string Id { get; set; } = string.Empty;
    public int Version { get; set; }
}

public sealed class SealedRuntimeEligibility
{
    public bool RollbackBaselineEligible { get; set; }
    public bool PromotionEligible { get; set; }
}

public sealed class SealedRuntimeSourceIdentity
{
    public string Repository { get; set; } = string.Empty;
    public string Sha { get; set; } = string.Empty;
    public string Ref { get; set; } = string.Empty;
}

public sealed class SealedRuntimeDigestIdentity
{
    public string AggregateDigest { get; set; } = string.Empty;
    public string ExecutableDigest { get; set; } = string.Empty;
    public string ManifestDigest { get; set; } = string.Empty;
}

public sealed class SealedRuntimeProducerIdentity
{
    public string Workflow { get; set; } = string.Empty;
    public string EventName { get; set; } = string.Empty;
    public long RunId { get; set; }
    public int RunAttempt { get; set; }
    public string RunUrl { get; set; } = string.Empty;
    public string AttemptUrl { get; set; } = string.Empty;
    public DateTimeOffset RunStartedAt { get; set; }
}

public sealed class SealedRuntimeArtifactIdentity
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string UploadDigest { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}

public sealed class SealedRuntimeAttestationIdentity
{
    public string PredicateType { get; set; } = string.Empty;
    public string Repository { get; set; } = string.Empty;
    public string SignerWorkflow { get; set; } = string.Empty;
    public string SourceSha { get; set; } = string.Empty;
    public string SourceRef { get; set; } = string.Empty;
    public string RunInvocationUrl { get; set; } = string.Empty;
    public string BundleDigest { get; set; } = string.Empty;
    public string ExecutableSubjectName { get; set; } = string.Empty;
    public string ExecutableSubjectDigest { get; set; } = string.Empty;
    public string ManifestSubjectName { get; set; } = string.Empty;
    public string ManifestSubjectDigest { get; set; } = string.Empty;
}

public sealed class SealedRuntimeIdentity
{
    public int SchemaVersion { get; set; }
    public string Role { get; set; } = string.Empty;
    public SealedRuntimeProfileIdentity Profile { get; set; } = new();
    public string Status { get; set; } = string.Empty;
    public SealedRuntimeEligibility Eligibility { get; set; } = new();
    public string? LedgerRecordDigest { get; set; }
    public SealedRuntimeSourceIdentity Source { get; set; } = new();
    public SealedRuntimeDigestIdentity Runtime { get; set; } = new();
    public SealedRuntimeProducerIdentity Producer { get; set; } = new();
    public SealedRuntimeArtifactIdentity Artifact { get; set; } = new();
    public SealedRuntimeAttestationIdentity Attestation { get; set; } = new();
}

public sealed class SealedRuntimeLaunchTarget
{
    internal SealedRuntimeLaunchTarget(
        string executablePath,
        string manifestPath,
        string identityPath,
        SealedRuntimeIdentity identity)
    {
        ExecutablePath = executablePath;
        ManifestPath = manifestPath;
        IdentityPath = identityPath;
        Identity = identity;
    }

    public string ExecutablePath { get; }
    public string ManifestPath { get; }
    public string IdentityPath { get; }
    public SealedRuntimeIdentity Identity { get; }
}

public sealed partial class SealedRuntimeSelection
{
    private SealedRuntimeSelection(
        string mode,
        string qualificationSha,
        SealedRuntimeLaunchTarget? candidate,
        SealedRuntimeLaunchTarget? prior)
    {
        Mode = mode;
        QualificationSha = qualificationSha;
        Candidate = candidate;
        Prior = prior;
    }

    public string Mode { get; }
    public string QualificationSha { get; }
    public bool IsSealed => Candidate is not null;
    public bool RequiresRollback => Prior is not null;
    public SealedRuntimeLaunchTarget? Candidate { get; }
    public SealedRuntimeLaunchTarget? Prior { get; }

    public static SealedRuntimeSelection Load(string profileId, int profileVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        if (profileVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(profileVersion));
        }

        var mode = Environment.GetEnvironmentVariable("AWS2AZURE_SEALED_RUNTIME_MODE");
        if (string.IsNullOrWhiteSpace(mode) || mode == "source_validation")
        {
            return new SealedRuntimeSelection(
                "source_validation",
                string.Empty,
                candidate: null,
                prior: null);
        }
        if (mode is not ("candidate" or "rollback"))
        {
            throw new InvalidDataException(
                "AWS2AZURE_SEALED_RUNTIME_MODE must be candidate, rollback, or source_validation.");
        }

        var qualificationSha = RequiredEnvironment("AWS2AZURE_QUALIFICATION_SHA");
        if (!IsGitSha(qualificationSha))
        {
            throw new InvalidDataException("AWS2AZURE_QUALIFICATION_SHA is not a full Git SHA.");
        }

        var candidate = LoadTarget(
            SealedRuntimeRole.Candidate,
            profileId,
            profileVersion,
            qualificationSha);
        SealedRuntimeLaunchTarget? prior = null;
        if (mode == "rollback")
        {
            prior = LoadTarget(
                SealedRuntimeRole.Prior,
                profileId,
                profileVersion,
                expectedQualificationSha: null);
            if (candidate.Identity.Source.Sha.Equals(
                    prior.Identity.Source.Sha,
                    StringComparison.Ordinal)
                || candidate.Identity.Runtime.AggregateDigest.Equals(
                    prior.Identity.Runtime.AggregateDigest,
                    StringComparison.Ordinal)
                || candidate.Identity.Runtime.ExecutableDigest.Equals(
                    prior.Identity.Runtime.ExecutableDigest,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Candidate and prior sealed runtimes must have distinct source, aggregate, and executable identities.");
            }
        }

        return new SealedRuntimeSelection(mode, qualificationSha, candidate, prior);
    }

    public SealedRuntimeLaunchTarget GetTarget(SealedRuntimeRole role)
    {
        return role switch
        {
            SealedRuntimeRole.Candidate => Candidate
                ?? throw new InvalidOperationException("No sealed candidate runtime is configured."),
            SealedRuntimeRole.Prior => Prior
                ?? throw new InvalidOperationException("No sealed prior runtime is configured."),
            _ => throw new ArgumentOutOfRangeException(nameof(role)),
        };
    }

    private static SealedRuntimeLaunchTarget LoadTarget(
        SealedRuntimeRole role,
        string profileId,
        int profileVersion,
        string? expectedQualificationSha)
    {
        var prefix = role == SealedRuntimeRole.Candidate
            ? "AWS2AZURE_SEALED_CANDIDATE"
            : "AWS2AZURE_SEALED_PRIOR";
        var executablePath = RequiredRegularFile(prefix + "_EXECUTABLE", executable: true);
        var manifestPath = RequiredRegularFile(prefix + "_MANIFEST", executable: false);
        var identityPath = RequiredRegularFile(prefix + "_IDENTITY", executable: false);
        var identity = JsonSerializer.Deserialize(
                           File.ReadAllText(identityPath),
                           SealedRuntimeIdentityJsonContext.Default.SealedRuntimeIdentity)
                       ?? throw new InvalidDataException(
                           $"{prefix}_IDENTITY contains an empty document.");

        ValidateIdentity(identity, role, profileId, profileVersion, expectedQualificationSha);
        var executableDigest = DigestFile(executablePath);
        var manifestDigest = DigestFile(manifestPath);
        if (!executableDigest.Equals(
                identity.Runtime.ExecutableDigest,
                StringComparison.Ordinal)
            || !manifestDigest.Equals(
                identity.Runtime.ManifestDigest,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"{role} sealed executable or manifest bytes do not match their verified identity.");
        }

        var manifest = JsonSerializer.Deserialize(
                           File.ReadAllText(manifestPath),
                           SealedRuntimeIdentityJsonContext.Default.SealedRuntimeManifest)
                       ?? throw new InvalidDataException($"{role} sealed manifest is empty.");
        if (manifest.SchemaVersion != 1
            || manifest.Source.Repository != identity.Source.Repository
            || manifest.Source.GitSha != identity.Source.Sha
            || manifest.Source.GitRef != identity.Source.Ref
            || manifest.Runtime.AggregateDigest != identity.Runtime.AggregateDigest
            || manifest.Runtime.Executable.Sha256 != identity.Runtime.ExecutableDigest
            || manifest.Artifact.Name != identity.Artifact.Name
            || manifest.Producer.RunId != identity.Producer.RunId
            || manifest.Producer.RunAttempt != identity.Producer.RunAttempt)
        {
            throw new InvalidDataException(
                $"{role} sealed manifest does not match its verified producer identity.");
        }

        return new SealedRuntimeLaunchTarget(
            executablePath,
            manifestPath,
            identityPath,
            identity);
    }

    private static void ValidateIdentity(
        SealedRuntimeIdentity identity,
        SealedRuntimeRole role,
        string profileId,
        int profileVersion,
        string? expectedQualificationSha)
    {
        var expectedRole = role == SealedRuntimeRole.Candidate ? "candidate" : "prior";
        if (identity.SchemaVersion != 1
            || identity.Role != expectedRole
            || identity.Profile.Id != profileId
            || identity.Profile.Version != profileVersion
            || !RepositoryRegex().IsMatch(identity.Source.Repository)
            || !IsGitSha(identity.Source.Sha)
            || !IsTrustedRef(identity.Source.Ref)
            || !IsDigest(identity.Runtime.AggregateDigest)
            || !IsDigest(identity.Runtime.ExecutableDigest)
            || !IsDigest(identity.Runtime.ManifestDigest)
            || identity.Runtime.AggregateDigest == identity.Runtime.ExecutableDigest
            || identity.Runtime.ExecutableDigest == identity.Runtime.ManifestDigest
            || identity.Producer.Workflow != ".github/workflows/sealed-runtime.yml"
            || identity.Producer.EventName != "workflow_dispatch"
            || identity.Producer.RunId <= 0
            || identity.Producer.RunAttempt <= 0
            || identity.Producer.RunUrl
                != $"https://github.com/{identity.Source.Repository}/actions/runs/" +
                   identity.Producer.RunId
            || identity.Producer.AttemptUrl
                != identity.Producer.RunUrl + "/attempts/" + identity.Producer.RunAttempt
            || identity.Artifact.Id <= 0
            || !IsDigest(identity.Artifact.UploadDigest)
            || identity.Artifact.CreatedAt == default
            || identity.Artifact.ExpiresAt <= identity.Artifact.CreatedAt
            || identity.Artifact.ExpiresAt <= DateTimeOffset.UtcNow
            || identity.Attestation.PredicateType != "https://slsa.dev/provenance/v1"
            || identity.Attestation.Repository != identity.Source.Repository
            || identity.Attestation.SignerWorkflow
                != identity.Source.Repository + "/.github/workflows/sealed-runtime.yml"
            || identity.Attestation.SourceSha != identity.Source.Sha
            || identity.Attestation.SourceRef != identity.Source.Ref
            || identity.Attestation.RunInvocationUrl != identity.Producer.AttemptUrl
            || !IsDigest(identity.Attestation.BundleDigest)
            || identity.Attestation.ExecutableSubjectName != "Aws2Azure.Proxy"
            || identity.Attestation.ExecutableSubjectDigest
                != identity.Runtime.ExecutableDigest
            || identity.Attestation.ManifestSubjectName != "sealed-runtime-manifest.json"
            || identity.Attestation.ManifestSubjectDigest != identity.Runtime.ManifestDigest)
        {
            throw new InvalidDataException($"{role} sealed runtime identity is incomplete or inconsistent.");
        }
        var expectedArtifactName =
            $"aws2azure-sealed-linux-x64-{identity.Runtime.AggregateDigest["sha256:".Length..]}" +
            $"-run-{identity.Producer.RunId}-attempt-{identity.Producer.RunAttempt}";
        if (identity.Artifact.Name != expectedArtifactName)
        {
            throw new InvalidDataException(
                $"{role} sealed artifact name does not bind its digest and producer attempt.");
        }
        if (expectedQualificationSha is not null
            && identity.Source.Sha != expectedQualificationSha)
        {
            throw new InvalidDataException(
                "The sealed candidate source SHA does not match the checked-out qualification SHA.");
        }
        if (role == SealedRuntimeRole.Candidate)
        {
            if (identity.Status != "candidate"
                || identity.Eligibility.RollbackBaselineEligible
                || identity.Eligibility.PromotionEligible
                || identity.LedgerRecordDigest is not null)
            {
                throw new InvalidDataException(
                    "A candidate selection must not impersonate an approved-runtime ledger record.");
            }
        }
        else if (identity.Status is not ("bootstrap" or "approved")
                 || !identity.Eligibility.RollbackBaselineEligible
                 || identity.Status == "bootstrap" && identity.Eligibility.PromotionEligible
                 || identity.Status == "approved" && !identity.Eligibility.PromotionEligible
                 || !IsDigest(identity.LedgerRecordDigest ?? string.Empty))
        {
            throw new InvalidDataException(
                "The prior runtime is not an eligible bootstrap or approved profile baseline.");
        }
    }

    private static string RequiredRegularFile(string name, bool executable)
    {
        var value = RequiredEnvironment(name);
        var fullPath = Path.GetFullPath(value);
        var info = new FileInfo(fullPath);
        if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException($"{name} must name a regular non-link file.");
        }
        if (executable && !OperatingSystem.IsWindows())
        {
            var mode = File.GetUnixFileMode(fullPath);
            if ((mode & (UnixFileMode.UserExecute
                         | UnixFileMode.GroupExecute
                         | UnixFileMode.OtherExecute)) == 0)
            {
                throw new InvalidDataException($"{name} is not executable.");
            }
        }
        return fullPath;
    }

    private static string RequiredEnvironment(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidDataException($"{name} is required for sealed qualification.")
            : value;
    }

    private static bool IsGitSha(string value) =>
        value.Length == 40 && value.All(character => character is >= '0' and <= '9'
            or >= 'a' and <= 'f');

    private static bool IsDigest(string value) =>
        value.Length == 71
        && value.StartsWith("sha256:", StringComparison.Ordinal)
        && value.AsSpan(7).IndexOfAnyExcept("0123456789abcdef") < 0;

    private static bool IsTrustedRef(string value) =>
        TrustedRefRegex().IsMatch(value);

    private static string DigestFile(string path) =>
        "sha256:" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    [GeneratedRegex(
        "^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$",
        RegexOptions.CultureInvariant)]
    private static partial Regex RepositoryRegex();

    [GeneratedRegex(
        "^refs/heads/main$|^refs/tags/v[0-9]+\\.[0-9]+\\.[0-9]+-rc([.-]?[0-9A-Za-z]+)*$",
        RegexOptions.CultureInvariant)]
    private static partial Regex TrustedRefRegex();
}

public static class SealedRuntimeLauncher
{
    private static readonly string[] PreservedEnvironmentNames =
    [
        "HOME",
        "LANG",
        "LC_ALL",
        "PATH",
        "DOTNET_ROOT",
        "DOTNET_ROOT_X64",
        "SSL_CERT_DIR",
        "SSL_CERT_FILE",
        "TZ",
    ];

    public static ProcessStartInfo CreateStartInfo(
        SealedRuntimeSelection selection,
        SealedRuntimeRole role,
        string repositoryRoot,
        int port,
        string configFile,
        IReadOnlyDictionary<string, string?>? childEnvironment = null)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(configFile);
        if (port is <= 0 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }

        ProcessStartInfo startInfo;
        if (selection.IsSealed)
        {
            var target = selection.GetTarget(role);
            startInfo = new ProcessStartInfo(target.ExecutablePath)
            {
                WorkingDirectory = Path.GetDirectoryName(target.ExecutablePath)!,
            };
        }
        else
        {
            if (role != SealedRuntimeRole.Candidate)
            {
                throw new InvalidOperationException(
                    "Source validation cannot launch a fabricated prior runtime.");
            }
            startInfo = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = repositoryRoot,
            };
            startInfo.ArgumentList.Add("run");
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add("Release");
            startInfo.ArgumentList.Add("--project");
            startInfo.ArgumentList.Add("src/Aws2Azure.Proxy/Aws2Azure.Proxy.csproj");
            startInfo.ArgumentList.Add("--no-build");
            startInfo.ArgumentList.Add("--no-restore");
            startInfo.ArgumentList.Add("--no-launch-profile");
        }

        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.UseShellExecute = false;
        startInfo.Environment.Clear();
        foreach (var name in PreservedEnvironmentNames)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrEmpty(value))
            {
                startInfo.Environment[name] = value;
            }
        }
        startInfo.Environment["AWS2AZURE_CONFIG_FILE"] = Path.GetFullPath(configFile);
        startInfo.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}";
        startInfo.Environment["DOTNET_ENVIRONMENT"] = "Testing";
        if (childEnvironment is not null)
        {
            foreach (var (name, value) in childEnvironment)
            {
                if (IsForbiddenChildEnvironment(name))
                {
                    throw new InvalidDataException(
                        $"Sensitive parent environment variable cannot be forwarded: {name}");
                }
                if (!string.IsNullOrWhiteSpace(value))
                {
                    startInfo.Environment[name] = value;
                }
            }
        }

        return startInfo;
    }

    public static string CreatePrivateDirectory(string parent, string prefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parent);
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        Directory.CreateDirectory(parent);
        var path = Path.Combine(parent, prefix + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        return path;
    }

    public static async Task WritePrivateFileAsync(
        string path,
        ReadOnlyMemory<byte> contents,
        CancellationToken cancellationToken = default)
    {
        var options = new FileStreamOptions
        {
            Access = FileAccess.Write,
            Mode = FileMode.CreateNew,
            Options = FileOptions.Asynchronous,
        };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }
        await using var stream = new FileStream(path, options);
        await stream.WriteAsync(contents, cancellationToken).ConfigureAwait(false);
    }

    private static bool IsForbiddenChildEnvironment(string name) =>
        name.Equals("GH_TOKEN", StringComparison.OrdinalIgnoreCase)
        || name.Equals("GITHUB_TOKEN", StringComparison.OrdinalIgnoreCase)
        || name.Equals("AZURE_SUBSCRIPTION_ID", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("ACTIONS_ID_TOKEN_REQUEST_", StringComparison.OrdinalIgnoreCase);
}

internal sealed class SealedRuntimeManifest
{
    public int SchemaVersion { get; set; }
    public SealedRuntimeManifestSource Source { get; set; } = new();
    public SealedRuntimeManifestRuntime Runtime { get; set; } = new();
    public SealedRuntimeManifestArtifact Artifact { get; set; } = new();
    public SealedRuntimeManifestProducer Producer { get; set; } = new();
}

internal sealed class SealedRuntimeManifestSource
{
    public string Repository { get; set; } = string.Empty;
    public string GitSha { get; set; } = string.Empty;
    public string GitRef { get; set; } = string.Empty;
}

internal sealed class SealedRuntimeManifestRuntime
{
    public string AggregateDigest { get; set; } = string.Empty;
    public SealedRuntimeManifestExecutable Executable { get; set; } = new();
}

internal sealed class SealedRuntimeManifestExecutable
{
    public string Sha256 { get; set; } = string.Empty;
}

internal sealed class SealedRuntimeManifestArtifact
{
    public string Name { get; set; } = string.Empty;
}

internal sealed class SealedRuntimeManifestProducer
{
    public long RunId { get; set; }
    public int RunAttempt { get; set; }
}

[JsonSerializable(typeof(SealedRuntimeIdentity))]
[JsonSerializable(typeof(SealedRuntimeManifest))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
internal sealed partial class SealedRuntimeIdentityJsonContext : JsonSerializerContext;
