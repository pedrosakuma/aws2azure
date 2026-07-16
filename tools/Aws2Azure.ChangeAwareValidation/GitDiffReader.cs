using System.Diagnostics;

namespace Aws2Azure.ChangeAwareValidation;

internal static class GitDiffReader
{
    public static GitDiff Read(string requestedBaseRef)
    {
        var repoRoot = RunGitFromCurrentDirectory("rev-parse", "--show-toplevel");
        var resolvedBaseRef = ResolveBaseRef(repoRoot, requestedBaseRef);
        var baseCommit = RunGit(repoRoot, "rev-parse", $"{resolvedBaseRef}^{{commit}}");
        var headCommit = RunGit(repoRoot, "rev-parse", "HEAD^{commit}");
        var mergeBase = RunGit(repoRoot, "merge-base", baseCommit, headCommit);
        var trackedPathsOutput = RunGit(
            repoRoot,
            "diff",
            "--name-only",
            "--diff-filter=ACDMRTUXB",
            mergeBase);
        var untrackedPathsOutput = RunGit(
            repoRoot,
            "ls-files",
            "--others",
            "--exclude-standard");
        var trackedPaths = trackedPathsOutput.Length == 0
            ? []
            : trackedPathsOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var untrackedPaths = untrackedPathsOutput.Length == 0
            ? []
            : untrackedPathsOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var changedPaths = trackedPaths
            .Concat(untrackedPaths)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        return new GitDiff(
            changedPaths,
            new BaseComparison(
                requestedBaseRef,
                resolvedBaseRef,
                baseCommit,
                mergeBase,
                headCommit,
                $"{mergeBase}..HEAD+index+worktree",
                true));
    }

    private static string ResolveBaseRef(string repoRoot, string requestedBaseRef)
    {
        var remoteRef = $"origin/{requestedBaseRef}";
        if (requestedBaseRef == "main" &&
            TryRunGit(repoRoot, out _, "rev-parse", "--verify", $"{remoteRef}^{{commit}}"))
        {
            return remoteRef;
        }

        if (TryRunGit(repoRoot, out _, "rev-parse", "--verify", $"{requestedBaseRef}^{{commit}}"))
        {
            return requestedBaseRef;
        }

        if (TryRunGit(repoRoot, out _, "rev-parse", "--verify", $"{remoteRef}^{{commit}}"))
        {
            return remoteRef;
        }

        throw new InvalidOperationException(
            $"Cannot resolve base ref '{requestedBaseRef}' or '{remoteRef}'. Fetch main before classifying the diff.");
    }

    private static string RunGitFromCurrentDirectory(params string[] arguments)
    {
        return RunGit(Directory.GetCurrentDirectory(), arguments);
    }

    private static string RunGit(string workingDirectory, params string[] arguments)
    {
        if (TryRunGit(workingDirectory, out var output, arguments))
        {
            return output;
        }

        throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed.");
    }

    private static bool TryRunGit(
        string workingDirectory,
        out string output,
        params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Unable to start git.");
        output = process.StandardOutput.ReadToEnd().Trim();
        var error = process.StandardError.ReadToEnd().Trim();
        process.WaitForExit();
        if (process.ExitCode == 0)
        {
            return true;
        }

        output = error;
        return false;
    }
}

internal sealed record GitDiff(string[] ChangedPaths, BaseComparison Comparison);
