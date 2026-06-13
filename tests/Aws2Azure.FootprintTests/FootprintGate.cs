namespace Aws2Azure.FootprintTests;

/// <summary>
/// Footprint scenarios are gated by the <c>AWS2AZURE_FOOTPRINT=1</c>
/// environment variable so a default <c>dotnet test</c> across the solution
/// does not perform a (multi-minute) Native-AOT publish of the proxy. Mirrors
/// <c>Aws2Azure.PerfTests.PerfGate</c>. Set <c>AWS2AZURE_FOOTPRINT=1</c> to run.
/// </summary>
internal static class FootprintGate
{
    public static bool Enabled =>
        string.Equals(Environment.GetEnvironmentVariable("AWS2AZURE_FOOTPRINT"), "1",
            StringComparison.Ordinal);
}
