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

    [SkippableTheory]
    [InlineData("1 KiB", 1 * 1024, 16)]
    [InlineData("1 MiB", 1 * 1024 * 1024, 8)]
    [InlineData("10 MiB", 10 * 1024 * 1024, 4)]
    public async Task PutObject_size_throughput(string label, int size, int concurrency)
    {
        Skip.IfNot(fixture.Ready, fixture.SkipReason);

        using var client = fixture.CreateClient();
        var payload = new byte[size];
        Random.Shared.NextBytes(payload);

        var result = await PerfRunner.RunAsync(
            scenario: $"s3.PutObject ({label})",
            concurrency: concurrency,
            duration: TimeSpan.FromSeconds(20),
            warmup: TimeSpan.FromSeconds(3),
            action: async (workerId, ct) =>
            {
                using var ms = new MemoryStream(payload, writable: false);
                await client.PutObjectAsync(new PutObjectRequest
                {
                    BucketName = fixture.Bucket,
                    Key = $"perf-put/{label.Replace(' ', '_')}/w{workerId:D2}/{Guid.NewGuid():N}",
                    InputStream = ms,
                    UseChunkEncoding = false,
                }, ct).ConfigureAwait(false);
            });

        PerfReport.Append(result, notes: $"S3→Azurite PutObject — {label} streaming upload (concurrency {concurrency})");
        result.AssertHealthy(proxyOutput: fixture.ProxyOutput);
        result.AssertNoRegression();
    }

    [SkippableFact]
    public async Task DeleteObject_throughput()
    {
        Skip.IfNot(fixture.Ready, fixture.SkipReason);

        using var client = fixture.CreateClient();

        var result = await PerfRunner.RunAsync(
            scenario: "s3.DeleteObject (idempotent)",
            concurrency: 16,
            duration: TimeSpan.FromSeconds(20),
            warmup: TimeSpan.FromSeconds(3),
            action: async (workerId, ct) =>
            {
                // S3 DeleteObject is idempotent — deleting a missing key returns
                // 204 — and the proxy's blob DELETE translation cost is the same
                // whether or not the blob exists. Deleting unique non-existent
                // keys avoids seed-pool exhaustion over a 20 s run.
                await client.DeleteObjectAsync(new DeleteObjectRequest
                {
                    BucketName = fixture.Bucket,
                    Key = $"perf-del/w{workerId:D2}/{Guid.NewGuid():N}",
                }, ct).ConfigureAwait(false);
            });

        PerfReport.Append(result, notes: "S3→Azurite DeleteObject — idempotent delete of unique non-existent keys (204 no-op; cost independent of existence)");
        result.AssertHealthy(proxyOutput: fixture.ProxyOutput);
        result.AssertNoRegression();
    }

    [SkippableFact]
    public async Task DeleteObjects_100_throughput()
    {
        Skip.IfNot(fixture.Ready, fixture.SkipReason);

        using var client = fixture.CreateClient();

        var result = await PerfRunner.RunAsync(
            scenario: "s3.DeleteObjects (100 keys)",
            concurrency: 8,
            duration: TimeSpan.FromSeconds(20),
            warmup: TimeSpan.FromSeconds(3),
            action: async (workerId, ct) =>
            {
                // Batch-delete 100 unique non-existent keys per call. S3
                // DeleteObjects reports a missing key as Deleted (never an
                // error), so this measures the per-key fan-out + XML response
                // assembly path without needing a seed pool.
                var objects = new List<KeyVersion>(100);
                for (var i = 0; i < 100; i++)
                {
                    objects.Add(new KeyVersion { Key = $"perf-delb/w{workerId:D2}/{Guid.NewGuid():N}" });
                }
                var resp = await client.DeleteObjectsAsync(new DeleteObjectsRequest
                {
                    BucketName = fixture.Bucket,
                    Objects = objects,
                    Quiet = false,
                }, ct).ConfigureAwait(false);
                if (resp.DeleteErrors is { Count: > 0 } errs)
                {
                    throw new InvalidOperationException($"DeleteObjects reported {errs.Count} errors — first: {errs[0].Code} {errs[0].Message}");
                }
            });

        PerfReport.Append(result, notes: "S3→Azurite DeleteObjects — 100 keys/call, idempotent (missing keys reported as Deleted)");
        result.AssertHealthy(proxyOutput: fixture.ProxyOutput);
        result.AssertNoRegression();
    }

    [SkippableFact]
    public async Task CopyObject_throughput()
    {
        Skip.IfNot(fixture.Ready, fixture.SkipReason);

        using var client = fixture.CreateClient();

        // Seed one source object — server-side copy reads it on every call.
        var sourceKey = $"perf-copy-src/{Guid.NewGuid():N}";
        var payload = new byte[64 * 1024];
        Random.Shared.NextBytes(payload);
        using (var seed = new MemoryStream(payload, writable: false))
        {
            await client.PutObjectAsync(new PutObjectRequest
            {
                BucketName = fixture.Bucket,
                Key = sourceKey,
                InputStream = seed,
                UseChunkEncoding = false,
            }).ConfigureAwait(false);
        }

        var result = await PerfRunner.RunAsync(
            scenario: "s3.CopyObject (64 KiB)",
            concurrency: 16,
            duration: TimeSpan.FromSeconds(20),
            warmup: TimeSpan.FromSeconds(3),
            action: async (workerId, ct) =>
            {
                // Server-side copy to a unique destination each call (overwrite
                // is idempotent). Exercises the Azure blob copy translation path
                // — no payload crosses the client.
                await client.CopyObjectAsync(new CopyObjectRequest
                {
                    SourceBucket = fixture.Bucket,
                    SourceKey = sourceKey,
                    DestinationBucket = fixture.Bucket,
                    DestinationKey = $"perf-copy-dst/w{workerId:D2}/{Guid.NewGuid():N}",
                }, ct).ConfigureAwait(false);
            });

        PerfReport.Append(result, notes: "S3→Azurite CopyObject — server-side copy of a 64 KiB source to unique destinations");
        result.AssertHealthy(proxyOutput: fixture.ProxyOutput);
        result.AssertNoRegression();
    }
}
