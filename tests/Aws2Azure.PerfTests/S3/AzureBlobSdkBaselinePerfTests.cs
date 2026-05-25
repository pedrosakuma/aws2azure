using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Aws2Azure.IntegrationTests.Fixtures;
using Xunit;

namespace Aws2Azure.PerfTests.S3;

/// <summary>
/// Baseline that hits Azurite directly with <c>Azure.Storage.Blobs</c> —
/// the proxy is left idle. Mirrors <see cref="S3PerfTests.PutObject_throughput"/>
/// shape (c=16, 20s, 4 KiB) so the two rows in baseline-latest.md are
/// directly comparable and surface the "proxy tax" (issue #131).
/// </summary>
[Collection(S3PerfCollection.Name)]
public sealed class AzureBlobSdkBaselinePerfTests(S3PerfFixture fixture)
{
    [SkippableFact]
    public async Task UploadAsync_throughput_AzureSdk()
    {
        Skip.IfNot(fixture.Ready, fixture.SkipReason);

        var connStr =
            $"DefaultEndpointsProtocol=http;AccountName={AzuriteFixture.AccountName};"
            + $"AccountKey={AzuriteFixture.AccountKey};BlobEndpoint={fixture.BlobEndpoint};";

        var service = new BlobServiceClient(connStr);
        var containerName = "sdk-baseline-" + Guid.NewGuid().ToString("N")[..8];
        var container = service.GetBlobContainerClient(containerName);
        await container.CreateIfNotExistsAsync().ConfigureAwait(false);

        var payload = new byte[4 * 1024];
        Random.Shared.NextBytes(payload);

        var result = await PerfRunner.RunAsync(
            scenario: "azure-sdk.Blob.UploadAsync (4 KiB)",
            concurrency: 16,
            duration: TimeSpan.FromSeconds(20),
            warmup: TimeSpan.FromSeconds(3),
            action: async (workerId, ct) =>
            {
                var blob = container.GetBlobClient($"perf/w{workerId:D2}/{Guid.NewGuid():N}");
                using var ms = new MemoryStream(payload, writable: false);
                await blob.UploadAsync(ms, overwrite: true, ct).ConfigureAwait(false);
            });

        PerfReport.Append(result, notes: "Azure SDK baseline — direct BlobClient.UploadAsync against Azurite (no proxy)");
        result.AssertHealthy(proxyOutput: fixture.ProxyOutput);
    }
}
