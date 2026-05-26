using Amazon.S3.Model;
using Xunit;

namespace Aws2Azure.PerfTests;

[Collection(S3PerfCollection.Name)]
public sealed class S3PerfTests(S3PerfFixture fixture)
{
    [SkippableFact]
    public async Task PutObject_throughput()
    {
        Skip.IfNot(fixture.Ready, fixture.SkipReason);

        using var client = fixture.CreateClient();
        var payload = new byte[4 * 1024];
        Random.Shared.NextBytes(payload);

        var result = await PerfRunner.RunAsync(
            scenario: "s3.PutObject (4 KiB)",
            concurrency: 16,
            duration: TimeSpan.FromSeconds(20),
            warmup: TimeSpan.FromSeconds(3),
            action: async (workerId, ct) =>
            {
                using var ms = new MemoryStream(payload, writable: false);
                await client.PutObjectAsync(new PutObjectRequest
                {
                    BucketName = fixture.Bucket,
                    Key = $"perf/w{workerId:D2}/{Guid.NewGuid():N}",
                    InputStream = ms,
                    UseChunkEncoding = false,
                }, ct).ConfigureAwait(false);
            });

        PerfReport.Append(result, notes: "S3→Azurite (blob REST)");
        result.AssertHealthy(proxyOutput: fixture.ProxyOutput);
        result.AssertNoRegression();
    }
}
