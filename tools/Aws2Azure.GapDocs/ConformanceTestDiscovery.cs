using System.Diagnostics;

namespace Aws2Azure.GapDocs;

public static class ConformanceTestDiscoveryValidator
{
    public static IReadOnlyList<string> Validate(
        ConformanceExecutionPlan plan,
        IReadOnlyDictionary<string, IReadOnlyList<string>> discoveredByProject)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(discoveredByProject);
        var errors = new List<string>();

        foreach (var project in plan.TestProjects)
        {
            if (!discoveredByProject.TryGetValue(
                    project.Project,
                    out var discovered))
            {
                errors.Add(
                    $"No xUnit discovery output was provided for '{project.Project}'.");
                continue;
            }

            foreach (var expected in project.Tests)
            {
                if (!discovered.Any(test =>
                        test.Equals(expected, StringComparison.Ordinal)
                        || test.StartsWith(
                            expected + "(",
                            StringComparison.Ordinal)))
                {
                    errors.Add(
                        $"Planned test was not discovered in {project.Project}: {expected}");
                }
            }
        }

        return errors;
    }

    public static IReadOnlyList<string> ParseListTestsOutput(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var discovered = new List<string>();
        using var reader = new StringReader(output);
        while (reader.ReadLine() is { } line)
        {
            var candidate = line.Trim();
            if (candidate.StartsWith(
                    "Aws2Azure.IntegrationTests.",
                    StringComparison.Ordinal)
                || candidate.StartsWith(
                    "Aws2Azure.UnitTests.",
                    StringComparison.Ordinal))
            {
                discovered.Add(candidate);
            }
        }
        return discovered;
    }
}

public static class ConformanceTestDiscoveryRunner
{
    public static IReadOnlyList<string> Validate(
        ConformanceExecutionPlan plan,
        string repositoryRoot,
        string configuration,
        bool noBuild)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(configuration);

        var errors = new List<string>();
        var discoveredByProject =
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var project in plan.TestProjects)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                WorkingDirectory = repositoryRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            startInfo.ArgumentList.Add("test");
            startInfo.ArgumentList.Add(project.Project);
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add(configuration);
            if (noBuild)
            {
                startInfo.ArgumentList.Add("--no-build");
            }
            startInfo.ArgumentList.Add("--nologo");
            startInfo.ArgumentList.Add("--list-tests");

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException(
                    $"Could not start xUnit discovery for '{project.Project}'.");
            var standardOutput = process.StandardOutput.ReadToEndAsync();
            var standardError = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            Task.WaitAll(standardOutput, standardError);
            if (process.ExitCode != 0)
            {
                var detail = standardError.Result.Trim();
                errors.Add(
                    $"xUnit discovery failed for '{project.Project}' with exit " +
                    $"{process.ExitCode}: {detail}");
                continue;
            }

            discoveredByProject[project.Project] =
                ConformanceTestDiscoveryValidator.ParseListTestsOutput(
                    standardOutput.Result);
        }

        errors.AddRange(
            ConformanceTestDiscoveryValidator.Validate(
                plan,
                discoveredByProject));
        return errors;
    }
}
