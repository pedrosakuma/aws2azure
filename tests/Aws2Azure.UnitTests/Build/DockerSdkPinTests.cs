using System.Text.Json;
using System.Text.RegularExpressions;

namespace Aws2Azure.UnitTests.Build;

/// <summary>
/// Guards against the Docker build SDK image silently drifting away from the
/// feature band pinned in <c>global.json</c>. A floating tag (e.g.
/// <c>mcr.microsoft.com/dotnet/sdk:10.0</c>) tracks the latest SDK patch and
/// can advance to a feature band that <c>global.json</c>'s
/// <c>rollForward: latestPatch</c> refuses to run against, breaking the
/// Docker/footprint build with an SDK-not-found error (see issue #785).
/// </summary>
public sealed class DockerSdkPinTests
{
    private static readonly string RepoRoot = FindRepoRoot();

    [Fact]
    public void Dockerfile_build_stage_pins_the_exact_global_json_sdk_version()
    {
        var globalJsonVersion = ReadGlobalJsonSdkVersion();
        var dockerfileText = File.ReadAllText(Path.Combine(RepoRoot, "Dockerfile"));

        var match = Regex.Match(
            dockerfileText,
            @"^FROM\s+mcr\.microsoft\.com/dotnet/sdk:(?<tag>\S+)\s+AS\s+build",
            RegexOptions.Multiline);

        Assert.True(match.Success, "Dockerfile must have a 'FROM mcr.microsoft.com/dotnet/sdk:<tag> AS build' stage.");

        var tag = match.Groups["tag"].Value;
        Assert.False(
            tag == "10.0" || Regex.IsMatch(tag, @"^\d+\.\d+$"),
            $"Dockerfile SDK image tag '{tag}' is a floating major.minor tag; " +
            $"it must pin the exact feature-band patch version '{globalJsonVersion}' from global.json.");

        Assert.Equal(globalJsonVersion, tag);
    }

    private static string ReadGlobalJsonSdkVersion()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot, "global.json")));
        return document.RootElement.GetProperty("sdk").GetProperty("version").GetString()
            ?? throw new InvalidOperationException("global.json is missing sdk.version.");
    }

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

        throw new InvalidOperationException("Could not locate repository root.");
    }
}
