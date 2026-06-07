using System.Net;
using System.Text;
using Aws2Azure.Core.Azure;
using Xunit;

namespace Aws2Azure.PerfTests;

/// <summary>
/// In-process microbenchmark for <c>EntraIdTokenProvider.GetTokenAsync</c> on the
/// cache-hit fast path under high concurrency. It isolates the cost of the single
/// global lock that guards the token cache — the data point behind the
/// "is double-checked locking / <c>ConcurrentDictionary</c> worth it here?" question.
///
/// <para>These are pure in-process scenarios (no proxy, no emulator), still gated by
/// <c>AWS2AZURE_PERF=1</c> so a default <c>dotnet test</c> does not run them. The
/// reported throughput INCLUDES the <see cref="PerfRunner"/> per-op overhead
/// (Stopwatch + ConcurrentQueue enqueue + Interlocked), so treat the absolute number
/// as a lower bound on achievable lookup throughput, not the bare lock cost.</para>
///
/// <para>The useful signals are (1) the comparison between the single-hot-key and
/// distinct-key-per-worker scenarios — identical harness overhead, the only
/// difference being contention on one cache entry vs many under the same global lock
/// — and (2) the sheer magnitude relative to any real Azure round-trip
/// (hundreds/sec) the token actually enables. If lookup throughput is orders of
/// magnitude above the downstream call rate, the lock is not a bottleneck and DCL is
/// premature.</para>
/// </summary>
public sealed class TokenProviderPerfTests
{
    private const int Concurrency = 64;

    // Bound the total work so PerfRunner's per-op latency queue stays at a few million
    // entries instead of growing unbounded for the whole duration window (these ops
    // are nanosecond-scale, not network-scale).
    private const int MaxOps = 2_000_000;

    [SkippableFact]
    public async Task GetToken_cache_hit_single_key_throughput()
    {
        Skip.IfNot(PerfGate.Enabled, "Set AWS2AZURE_PERF=1 to run perf scenarios.");

        var handler = new FreshTokenHandler();
        using var http = new AzureHttpClient(handler, ownsHandler: true);
        var provider = new EntraIdTokenProvider(http, authority: new Uri("https://login.test/"));

        // Pre-warm so the measured window is pure cache hits (one hot key, contended).
        await provider.GetTokenAsync("tenant", "client", "secret", "scope-0");

        var result = await PerfRunner.RunAsync(
            scenario: "entra.GetToken (cache hit, 1 key, c=64)",
            concurrency: Concurrency,
            duration: TimeSpan.FromSeconds(10),
            warmup: TimeSpan.FromSeconds(1),
            maxOps: MaxOps,
            action: async (workerId, ct) =>
            {
                _ = await provider.GetTokenAsync("tenant", "client", "secret", "scope-0").ConfigureAwait(false);
            });

        PerfReport.Append(result, notes: $"in-proc token cache-hit, 1 hot key under the global lock; token-endpoint fetches={handler.Calls}");
        result.AssertHealthy();
        result.AssertNoRegression();

        // The measured window must never have left the cache-hit path: exactly the one
        // pre-warm fetch, no refreshes. Guards the experiment against silently
        // measuring token-endpoint round-trips instead of the lock.
        Assert.Equal(1, handler.Calls);
    }

    [SkippableFact]
    public async Task GetToken_cache_hit_distinct_keys_throughput()
    {
        Skip.IfNot(PerfGate.Enabled, "Set AWS2AZURE_PERF=1 to run perf scenarios.");

        var handler = new FreshTokenHandler();
        using var http = new AzureHttpClient(handler, ownsHandler: true);
        var provider = new EntraIdTokenProvider(http, authority: new Uri("https://login.test/"));

        // One distinct (scope-keyed) cache entry per worker; pre-warm all of them so
        // the measured window is cache hits with no per-entry contention — only the
        // single global lock is shared.
        var scopes = new string[Concurrency];
        for (var i = 0; i < Concurrency; i++)
        {
            scopes[i] = $"scope-{i}";
            await provider.GetTokenAsync("tenant", "client", "secret", scopes[i]);
        }

        var result = await PerfRunner.RunAsync(
            scenario: "entra.GetToken (cache hit, 64 keys, c=64)",
            concurrency: Concurrency,
            duration: TimeSpan.FromSeconds(10),
            warmup: TimeSpan.FromSeconds(1),
            maxOps: MaxOps,
            action: async (workerId, ct) =>
            {
                var idx = workerId < 0 ? 0 : workerId % Concurrency;
                _ = await provider.GetTokenAsync("tenant", "client", "secret", scopes[idx]).ConfigureAwait(false);
            });

        PerfReport.Append(result, notes: $"in-proc token cache-hit, distinct key per worker; token-endpoint fetches={handler.Calls}");
        result.AssertHealthy();
        result.AssertNoRegression();

        // One fetch per distinct key, all during pre-warm; the measured window stayed
        // on the cache-hit path throughout.
        Assert.Equal(Concurrency, handler.Calls);
    }

    private sealed class FreshTokenHandler : HttpMessageHandler
    {
        private long _calls;
        public long Calls => Interlocked.Read(ref _calls);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            // Long-lived token so the entire measured window stays on the cache-hit path.
            const string json = "{\"access_token\":\"perf-token\",\"token_type\":\"Bearer\",\"expires_in\":3600}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }
}
