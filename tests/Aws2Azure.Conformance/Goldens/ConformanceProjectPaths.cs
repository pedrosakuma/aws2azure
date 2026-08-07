namespace Aws2Azure.Conformance.Goldens;

/// <summary>
/// Source-tree path discovery shared by the conformance file stores. Tests run
/// from <c>bin/</c>, but the committed goldens/evidence live in the project
/// tree, so stores must climb back to the checked-in project root rather than
/// assume the current working directory.
/// </summary>
internal static class ConformanceProjectPaths
{
    private const string RelativePathFromRepoRoot = "tests/Aws2Azure.Conformance";

    public static string ProjectRoot() => ProjectRoot(new DirectoryInfo(AppContext.BaseDirectory));

    /// <summary>Testable overload: resolves the project root by walking up from <paramref name="start"/>.</summary>
    internal static string ProjectRoot(DirectoryInfo? start)
    {
        var dir = start;

        // Tests running from Aws2Azure.Conformance's own bin/ climb straight
        // to its project directory. Tests running from a sibling test
        // project (e.g. Aws2Azure.IntegrationTests, which references some of
        // this project's sources directly rather than via ProjectReference)
        // never encounter Aws2Azure.Conformance.csproj as an ancestor, so
        // fall back to locating the repo root (marked by the checked-in
        // solution file) and descending into the known relative path.
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Aws2Azure.Conformance.csproj")))
            {
                return dir.FullName;
            }

            if (File.Exists(Path.Combine(dir.FullName, "aws2azure.slnx")))
            {
                var fromRepoRoot = Path.Combine(dir.FullName, RelativePathFromRepoRoot);
                if (File.Exists(Path.Combine(fromRepoRoot, "Aws2Azure.Conformance.csproj")))
                {
                    return fromRepoRoot;
                }
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            "Could not locate Aws2Azure.Conformance.csproj to resolve the source-tree path.");
    }
}
