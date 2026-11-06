using System.Text.Json;
using System.Text.RegularExpressions;

namespace Aws2Azure.UnitTests.Build;

/// <summary>
/// Guards repository-controlled SDK setup surfaces against silently drifting
/// away from the exact feature-band patch pinned in <c>global.json</c>.
/// </summary>
public sealed class SdkPinConsistencyTests
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

    [Fact]
    public void Shared_setup_action_defaults_to_the_exact_global_json_sdk_version()
    {
        var globalJsonVersion = ReadGlobalJsonSdkVersion();
        var actionText = File.ReadAllText(Path.Combine(RepoRoot, ".github", "actions", "dotnet-setup", "action.yml"));

        var matches = Regex.Matches(
            actionText,
            @"^\s+default:\s*(?<version>\d+\.\d+\.\d+)\s*$",
            RegexOptions.Multiline);

        var match = Assert.Single(matches.Cast<Match>());
        Assert.Equal(globalJsonVersion, match.Groups["version"].Value);
    }

    [Fact]
    public void Release_workflow_pins_the_exact_global_json_sdk_version()
    {
        var globalJsonVersion = ReadGlobalJsonSdkVersion();
        var workflowText = File.ReadAllText(Path.Combine(RepoRoot, ".github", "workflows", "release.yml"));

        var matches = Regex.Matches(
            workflowText,
            @"^\s+dotnet-version:\s*['""]?(?<version>\d+\.\d+\.\d+)['""]?\s*$",
            RegexOptions.Multiline);

        var match = Assert.Single(matches.Cast<Match>());
        Assert.Equal(globalJsonVersion, match.Groups["version"].Value);
    }

    [Fact]
    public void Global_json_preserves_latest_patch_roll_forward()
    {
        using var document = ReadGlobalJson();
        var sdk = document.RootElement.GetProperty("sdk");

        Assert.Equal("latestPatch", sdk.GetProperty("rollForward").GetString());
        Assert.False(sdk.GetProperty("allowPrerelease").GetBoolean());
    }

    private static string ReadGlobalJsonSdkVersion()
    {
        using var document = ReadGlobalJson();
        return document.RootElement.GetProperty("sdk").GetProperty("version").GetString()
            ?? throw new InvalidOperationException("global.json is missing sdk.version.");
    }

    private static JsonDocument ReadGlobalJson() =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot, "global.json")));

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
