using System.Text.RegularExpressions;

namespace Aws2Azure.DocsQuality;

/// <summary>
/// Structurally verifies copy-paste shell commands in hand-authored
/// documentation: every repo-relative path referenced by a <c>dotnet</c>
/// project/test/publish command, an <c>eng/</c> script, or a
/// <c>.github/scripts/</c> script must exist. This does not execute commands
/// (impractical for e.g. Docker/Azure-dependent flows); it proves the
/// referenced path is real, catching the common drift where a copy-pasted
/// command survives a file rename or removal.
/// </summary>
internal static partial class CommandSnippetValidator
{
    private static readonly string[] CommandLanguages = ["bash", "sh", "shell", "powershell", "pwsh", "console"];

    // Repo-relative path tokens worth checking: dotnet project/script paths
    // under the directories this repo actually uses for runnable artifacts.
    [GeneratedRegex(@"(?<![\w./-])(\.?/)?(?<path>(?:tools|eng|src|tests)/[\w./-]+|\.github/scripts/[\w./-]+)")]
    private static partial Regex RepoPathToken();

    public static IReadOnlyList<string> Validate(string repoRoot)
    {
        var violations = new List<string>();

        foreach (var file in DocTree.MarkdownFiles(repoRoot))
        {
            var relative = DocTree.ToRepoRelative(repoRoot, file);
            var text = File.ReadAllText(file);
            foreach (var block in FencedCodeBlockExtractor.Extract(text))
            {
                if (!CommandLanguages.Contains(block.Language, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                var lines = block.Content.Split('\n');
                for (var i = 0; i < lines.Length; i++)
                {
                    var line = lines[i];
                    var lineNumber = block.StartLine + i;
                    foreach (var candidate in ExtractCandidatePaths(line))
                    {
                        if (!PathExists(repoRoot, candidate))
                        {
                            violations.Add(
                                $"{relative}:{lineNumber}: references '{candidate}', which does not exist in the repository.");
                        }
                    }
                }
            }
        }

        return violations;
    }

    private static IEnumerable<string> ExtractCandidatePaths(string line)
    {
        if (line.Contains('<') || line.Contains('>') || line.Contains('$') || line.Contains('*')
            || line.TrimStart().StartsWith('#'))
        {
            // Placeholders (<service>), variable interpolation, globs, and
            // comments are not concrete copy-paste paths.
            yield break;
        }

        foreach (Match match in RepoPathToken().Matches(line))
        {
            var candidate = match.Groups["path"].Value.TrimEnd('.', ',', ')', ']', ';', ':');
            if (candidate.Length > 0)
            {
                yield return candidate;
            }
        }
    }

    private static bool PathExists(string repoRoot, string repoRelativePath)
    {
        var full = Path.Combine(repoRoot, repoRelativePath.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(full) || Directory.Exists(full))
        {
            return true;
        }

        // "dotnet run --project tools/X" style references may point at a
        // project directory referenced without its .csproj file name suffix
        // already handled by Directory.Exists above; also allow a bare
        // project name to resolve against a same-named .csproj file.
        return File.Exists(full + ".csproj");
    }
}
