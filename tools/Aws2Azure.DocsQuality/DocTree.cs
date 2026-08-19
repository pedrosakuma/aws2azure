namespace Aws2Azure.DocsQuality;

/// <summary>
/// Enumerates hand-authored Markdown files that are in scope for the docs
/// quality gates in this tool. Generated pages under <c>docs/site/**</c> are
/// produced by <c>tools/Aws2Azure.GapDocs</c> from the same canonical inputs
/// these gates already cross-check elsewhere, so they are excluded here.
/// </summary>
internal static class DocTree
{
    private static readonly string[] Roots = ["README.md", "docs"];
    private static readonly string[] Exclusions = ["docs/site/"];

    public static IReadOnlyList<string> MarkdownFiles(string repoRoot)
    {
        var files = new List<string>();
        foreach (var root in Roots)
        {
            var fullRoot = Path.Combine(repoRoot, ToNativePath(root));
            IEnumerable<string> candidates = Directory.Exists(fullRoot)
                ? Directory.EnumerateFiles(fullRoot, "*.md", SearchOption.AllDirectories)
                : File.Exists(fullRoot)
                    ? [fullRoot]
                    : [];

            foreach (var file in candidates)
            {
                var relative = Path.GetRelativePath(repoRoot, file).Replace('\\', '/');
                if (Exclusions.Any(prefix => relative.StartsWith(prefix, StringComparison.Ordinal)))
                {
                    continue;
                }
                files.Add(file);
            }
        }
        return files.OrderBy(path => path, StringComparer.Ordinal).ToList();
    }

    public static string ToRepoRelative(string repoRoot, string file) =>
        Path.GetRelativePath(repoRoot, file).Replace('\\', '/');

    private static string ToNativePath(string repositoryRelativePath) =>
        repositoryRelativePath.Replace('/', Path.DirectorySeparatorChar);
}
