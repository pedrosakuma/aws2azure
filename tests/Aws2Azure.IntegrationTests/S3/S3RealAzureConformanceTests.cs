using Amazon.S3.Model;
using Xunit;

namespace Aws2Azure.IntegrationTests.S3;

[Trait("Category", "RealAzure")]
[Collection(RealAzureCollection.Name)]
public sealed class S3RealAzureConformanceTests(RealAzureProxyFixture fixture)
{
    [SkippableFact]
    public async Task ListObjectsV2_paginates_against_real_blob_storage()
    {
        Skip.IfNot(fixture.BlobConfigured,
            "AZURE_BLOB_ACCOUNT/AZURE_BLOB_KEY not set — skipping real-Azure S3 conformance.");

        var bucket = "aws2azure-page-" + Guid.NewGuid().ToString("N")[..12];
        var keys = Enumerable.Range(0, 5).Select(i => $"page/item-{i:D2}").ToArray();
        using var client = fixture.CreateS3Client();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var bucketCreated = false;

        try
        {
            await client.PutBucketAsync(new PutBucketRequest { BucketName = bucket }, timeout.Token).ConfigureAwait(false);
            bucketCreated = true;
            foreach (var key in keys)
            {
                await client.PutObjectAsync(new PutObjectRequest
                {
                    BucketName = bucket,
                    Key = key,
                    ContentBody = key,
                }, timeout.Token).ConfigureAwait(false);
            }

            var seen = new List<string>();
            string? continuationToken = null;
            var pageCount = 0;
            do
            {
                var response = await client.ListObjectsV2Async(new ListObjectsV2Request
                {
                    BucketName = bucket,
                    Prefix = "page/",
                    MaxKeys = 2,
                    ContinuationToken = continuationToken,
                }, timeout.Token).ConfigureAwait(false);

                pageCount++;
                Assert.InRange(pageCount, 1, 4);
                seen.AddRange(response.S3Objects.Select(item => item.Key));
                continuationToken = response.IsTruncated == true
                    ? response.NextContinuationToken
                    : null;
                if (response.IsTruncated == true)
                {
                    Assert.False(string.IsNullOrWhiteSpace(continuationToken));
                }
            } while (continuationToken is not null);

            Assert.Equal(3, pageCount);
            Assert.Equal(keys, seen);
        }
        finally
        {
            if (bucketCreated)
            {
                await DeleteBucketBestEffortAsync(client, bucket, keys).ConfigureAwait(false);
            }
        }
    }

    [SkippableFact]
    public async Task DeleteObjects_reports_real_blob_batch_results()
    {
        Skip.IfNot(fixture.BlobConfigured,
            "AZURE_BLOB_ACCOUNT/AZURE_BLOB_KEY not set — skipping real-Azure S3 conformance.");

        var bucket = "aws2azure-batch-" + Guid.NewGuid().ToString("N")[..12];
        string[] existingKeys = ["batch/a", "batch/b"];
        const string missingKey = "batch/missing";
        using var client = fixture.CreateS3Client();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var bucketCreated = false;

        try
        {
            await client.PutBucketAsync(new PutBucketRequest { BucketName = bucket }, timeout.Token).ConfigureAwait(false);
            bucketCreated = true;
            foreach (var key in existingKeys)
            {
                await client.PutObjectAsync(new PutObjectRequest
                {
                    BucketName = bucket,
                    Key = key,
                    ContentBody = key,
                }, timeout.Token).ConfigureAwait(false);
            }

            var response = await client.DeleteObjectsAsync(new DeleteObjectsRequest
            {
                BucketName = bucket,
                Objects =
                [
                    new KeyVersion { Key = existingKeys[0] },
                    new KeyVersion { Key = existingKeys[1] },
                    new KeyVersion { Key = missingKey },
                ],
            }, timeout.Token).ConfigureAwait(false);

            Assert.True(response.DeleteErrors is null or { Count: 0 });
            Assert.Equal(
                existingKeys.Append(missingKey).Order(StringComparer.Ordinal).ToArray(),
                (response.DeletedObjects ?? []).Select(item => item.Key).Order(StringComparer.Ordinal).ToArray());

            var listed = await client.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = bucket,
                Prefix = "batch/",
            }, timeout.Token).ConfigureAwait(false);
            Assert.True(listed.S3Objects is null or { Count: 0 });
        }
        finally
        {
            if (bucketCreated)
            {
                await DeleteBucketBestEffortAsync(client, bucket, existingKeys).ConfigureAwait(false);
            }
        }
    }

    private static async Task DeleteBucketBestEffortAsync(
        Amazon.S3.IAmazonS3 client,
        string bucket,
        IEnumerable<string> keys)
    {
        foreach (var key in keys)
        {
            try
            {
                await client.DeleteObjectAsync(new DeleteObjectRequest { BucketName = bucket, Key = key }).ConfigureAwait(false);
            }
            catch
            {
            }
        }

        try
        {
            await client.DeleteBucketAsync(new DeleteBucketRequest { BucketName = bucket }).ConfigureAwait(false);
        }
        catch
        {
        }
    }
}
