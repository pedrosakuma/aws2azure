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

    [SkippableFact]
    public async Task GetObject_throughput()
    {
        Skip.IfNot(fixture.Ready, fixture.SkipReason);

        using var client = fixture.CreateClient();
        var payload = new byte[64 * 1024];
        Random.Shared.NextBytes(payload);

        // Pre-seed enough keys that each worker reads a distinct object
        // (avoids Azurite caching skewing the measurement). Seed cost is
        // not counted in the perf window.
        const int seedCount = 64;
        var keys = new string[seedCount];
        for (var i = 0; i < seedCount; i++)
        {
            keys[i] = $"perf-get/{i:D4}-{Guid.NewGuid():N}";
            using var ms = new MemoryStream(payload, writable: false);
            await client.PutObjectAsync(new PutObjectRequest
            {
                BucketName = fixture.Bucket,
                Key = keys[i],
                InputStream = ms,
                UseChunkEncoding = false,
            }).ConfigureAwait(false);
        }

        var result = await PerfRunner.RunAsync(
            scenario: "s3.GetObject (64 KiB)",
            concurrency: 16,
            duration: TimeSpan.FromSeconds(20),
            warmup: TimeSpan.FromSeconds(3),
            action: async (workerId, ct) =>
            {
                var key = keys[Random.Shared.Next(keys.Length)];
                using var resp = await client.GetObjectAsync(new GetObjectRequest
                {
                    BucketName = fixture.Bucket,
                    Key = key,
                }, ct).ConfigureAwait(false);
                // Drain — the SDK gives us the underlying response stream.
                using var sink = new MemoryStream();
                await resp.ResponseStream.CopyToAsync(sink, ct).ConfigureAwait(false);
            });

        PerfReport.Append(result, notes: "S3→Azurite GetObject — 64 KiB random reads");
        result.AssertHealthy(proxyOutput: fixture.ProxyOutput);
        result.AssertNoRegression();
    }

    [SkippableFact]
    public async Task ListObjectsV2_throughput()
    {
        Skip.IfNot(fixture.Ready, fixture.SkipReason);

        using var client = fixture.CreateClient();

        const int seedCount = 500;
        var seedPrefix = $"perf-list/{Guid.NewGuid():N}/";
        var tinyPayload = new byte[16];
        for (var i = 0; i < seedCount; i++)
        {
            using var ms = new MemoryStream(tinyPayload, writable: false);
            await client.PutObjectAsync(new PutObjectRequest
            {
                BucketName = fixture.Bucket,
                Key = $"{seedPrefix}{i:D5}-{Guid.NewGuid():N}",
                InputStream = ms,
                UseChunkEncoding = false,
            }).ConfigureAwait(false);
        }

        var result = await PerfRunner.RunAsync(
            scenario: "s3.ListObjectsV2 (500 keys)",
            concurrency: 16,
            duration: TimeSpan.FromSeconds(20),
            warmup: TimeSpan.FromSeconds(3),
            action: async (workerId, ct) =>
            {
                var resp = await client.ListObjectsV2Async(new ListObjectsV2Request
                {
                    BucketName = fixture.Bucket,
                    Prefix = seedPrefix,
                    MaxKeys = 1000,
                }, ct).ConfigureAwait(false);
                if (resp.S3Objects.Count != seedCount)
                {
                    throw new InvalidOperationException(
                        $"Expected {seedCount} keys, got {resp.S3Objects.Count}.");
                }
            });

        PerfReport.Append(result, notes: "S3→Azurite ListObjectsV2 — 500 keys under a prefix");
        result.AssertHealthy(proxyOutput: fixture.ProxyOutput);
        result.AssertNoRegression();
    }
}
