namespace Aws2Azure.PerfTests;

/// <summary>
/// Perf scenarios are gated by the <c>AWS2AZURE_PERF=1</c> environment
/// variable so a default <c>dotnet test</c> across the solution does not
/// fire up emulators + the proxy unintentionally. Set
/// <c>AWS2AZURE_PERF=1</c> to run.
/// </summary>
internal static class PerfGate
{
    public static bool Enabled =>
        string.Equals(Environment.GetEnvironmentVariable("AWS2AZURE_PERF"), "1",
            StringComparison.Ordinal);
}
