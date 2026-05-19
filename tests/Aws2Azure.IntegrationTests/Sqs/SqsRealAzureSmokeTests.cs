using System;
using System.Net.Http;
using System.Threading.Tasks;
using Aws2Azure.IntegrationTests.Fixtures;
using Xunit;

namespace Aws2Azure.IntegrationTests.Sqs;

/// <summary>
/// Smoke test for the real-Azure nightly job. Tagged
/// <c>Category=RealAzure</c> so the <c>integration-real-azure</c>
/// workflow can <c>--filter</c> it in isolation from the emulator-backed
/// tests in <see cref="AzuriteBlobRoundTripTests"/> et al.
///
/// <para>Skipped — not failed — when the <c>AZURE_SB_CONNSTR</c>
/// environment variable is unavailable, so fork PRs and local
/// <c>dotnet test</c> runs do not break.</para>
///
/// <para>Today this is intentionally a shape check (connection string
/// parses, proxy host responds). The full real-Azure coverage matrix
/// expands as Slice 5 (FIFO + DLQ) lands and the in-process fakes are
/// cross-checked against real Service Bus.</para>
/// </summary>
[Trait("Category", "RealAzure")]
[Collection(ProxyCollection.Name)]
public sealed class SqsRealAzureSmokeTests
{
    private readonly ProxyHostFixture _proxy;

    public SqsRealAzureSmokeTests(ProxyHostFixture proxy)
    {
        _proxy = proxy;
    }

    [SkippableFact]
    public async Task Connection_string_is_present_and_proxy_host_responds()
    {
        var connStr = Environment.GetEnvironmentVariable("AZURE_SB_CONNSTR");
        Skip.If(string.IsNullOrWhiteSpace(connStr),
            "AZURE_SB_CONNSTR not set — skipping real-Azure smoke. " +
            "Expected on fork PRs and local dev; see " +
            "docs/adr/0001-sb-rest-runtime-protocol.md.");

        Assert.Contains("Endpoint=", connStr, StringComparison.Ordinal);
        Assert.Contains("SharedAccessKey", connStr, StringComparison.Ordinal);

        using var client = _proxy.CreateClient();
        using var resp = await client.GetAsync("/").ConfigureAwait(false);
        // The proxy returns either a 200/204 health-ish response or a
        // 404 for unmatched routes; either confirms the host is up.
        Assert.True((int)resp.StatusCode < 500,
            $"Proxy host returned 5xx: {(int)resp.StatusCode}.");
    }
}
