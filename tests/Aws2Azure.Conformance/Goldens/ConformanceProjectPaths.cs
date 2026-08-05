namespace Aws2Azure.Conformance.Goldens;

/// <summary>
/// Source-tree path discovery shared by the conformance file stores. Tests run
/// from <c>bin/</c>, but the committed goldens/evidence live in the project
/// tree, so stores must climb back to the checked-in project root rather than
/// assume the current working directory.
/// </summary>
internal static class ConformanceProjectPaths
{
    public static string ProjectRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null &&
               !File.Exists(Path.Combine(dir.FullName, "Aws2Azure.Conformance.csproj")))
        {
            dir = dir.Parent;
        }

        if (dir is null)
        {
            throw new InvalidOperationException(
                "Could not locate Aws2Azure.Conformance.csproj to resolve the source-tree path.");
        }

        return dir.FullName;
    }
}
