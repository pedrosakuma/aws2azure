namespace Aws2Azure.FootprintTests;

/// <summary>Locates the repository root by walking up from the test base directory.</summary>
internal static class RepoRoot
{
    public static string Find()
    {
        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            if (Directory.Exists(Path.Combine(dir, "src")) &&
                File.Exists(Path.Combine(dir, "aws2azure.slnx")))
            {
                return dir;
            }
            dir = Path.GetDirectoryName(dir);
        }
        throw new InvalidOperationException(
            "Could not locate aws2azure repo root from " + AppContext.BaseDirectory);
    }
}
