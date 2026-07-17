using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using Xunit;

namespace Aws2Azure.IntegrationTests.S3;

[Trait("Category", "RealAzure")]
[Collection(RealAzureCollection.Name)]
public sealed class S3RealAzureConformanceTests(RealAzureProxyFixture fixture)
{
    [SkippableFact]
    public async Task HeadObject_and_read_shape_conform_against_real_blob_storage()
    {
        Skip.IfNot(fixture.BlobConfigured,
            "AZURE_BLOB_ACCOUNT/AZURE_BLOB_KEY not set — skipping real-Azure S3 conformance.");

        var bucket = "aws2azure-read-" + Guid.NewGuid().ToString("N")[..12];
        const string key = "read/object.txt";
        const string missingCleanupKey = "read/missing-cleanup.txt";
        const string payload = "0123456789abcdefghijklmnopqrstuvwxyz";
        const string contentType = "text/plain; charset=utf-8";
        const string metadataKey = "x-amz-meta-certification";
        const string metadataValue = "head-read-shape";
        const string nonMatchingEtag = "\"00000000000000000000000000000000\"";
        using var client = fixture.CreateS3Client();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var bucketCreated = false;

        try
        {
            await client.PutBucketAsync(
                new PutBucketRequest { BucketName = bucket },
                timeout.Token).ConfigureAwait(false);
            bucketCreated = true;

            await client.DeleteObjectAsync(
                new DeleteObjectRequest { BucketName = bucket, Key = missingCleanupKey },
                timeout.Token).ConfigureAwait(false);
            await client.DeleteObjectAsync(
                new DeleteObjectRequest { BucketName = bucket, Key = missingCleanupKey },
                timeout.Token).ConfigureAwait(false);

            var putRequest = new PutObjectRequest
            {
                BucketName = bucket,
                Key = key,
                ContentBody = payload,
                ContentType = contentType,
            };
            putRequest.Metadata.Add(metadataKey, metadataValue);
            var put = await client.PutObjectAsync(putRequest, timeout.Token).ConfigureAwait(false);

            var head = await client.GetObjectMetadataAsync(new GetObjectMetadataRequest
            {
                BucketName = bucket,
                Key = key,
            }, timeout.Token).ConfigureAwait(false);

            Assert.Equal(payload.Length, head.ContentLength);
            Assert.Equal(contentType, head.Headers.ContentType);
            Assert.Equal(put.ETag, head.ETag);
            Assert.Equal(metadataValue, head.Metadata[metadataKey]);

            using (var ranged = await client.GetObjectAsync(new GetObjectRequest
            {
                BucketName = bucket,
                Key = key,
                ByteRange = new ByteRange(7, 18),
            }, timeout.Token).ConfigureAwait(false))
            {
                Assert.Equal(HttpStatusCode.PartialContent, ranged.HttpStatusCode);
                using var reader = new StreamReader(ranged.ResponseStream);
                Assert.Equal(payload[7..19], await reader.ReadToEndAsync(timeout.Token).ConfigureAwait(false));
            }

            var matchingHead = await client.GetObjectMetadataAsync(new GetObjectMetadataRequest
            {
                BucketName = bucket,
                Key = key,
                EtagToMatch = head.ETag,
            }, timeout.Token).ConfigureAwait(false);
            Assert.Equal(head.ETag, matchingHead.ETag);

            var failedHead = await Assert.ThrowsAsync<AmazonS3Exception>(() =>
                client.GetObjectMetadataAsync(new GetObjectMetadataRequest
                {
                    BucketName = bucket,
                    Key = key,
                    EtagToMatch = nonMatchingEtag,
                }, timeout.Token)).ConfigureAwait(false);
            Assert.Equal(HttpStatusCode.PreconditionFailed, failedHead.StatusCode);

            var notModified = await Assert.ThrowsAsync<AmazonS3Exception>(() =>
                client.GetObjectAsync(new GetObjectRequest
                {
                    BucketName = bucket,
                    Key = key,
                    EtagToNotMatch = head.ETag,
                }, timeout.Token)).ConfigureAwait(false);
            Assert.Equal(HttpStatusCode.NotModified, notModified.StatusCode);

            using (var nonMatching = await client.GetObjectAsync(new GetObjectRequest
            {
                BucketName = bucket,
                Key = key,
                EtagToNotMatch = nonMatchingEtag,
            }, timeout.Token).ConfigureAwait(false))
            using (var reader = new StreamReader(nonMatching.ResponseStream))
            {
                Assert.Equal(payload, await reader.ReadToEndAsync(timeout.Token).ConfigureAwait(false));
            }

            await client.DeleteObjectAsync(
                new DeleteObjectRequest { BucketName = bucket, Key = key },
                timeout.Token).ConfigureAwait(false);
            await client.DeleteBucketAsync(
                new DeleteBucketRequest { BucketName = bucket },
                timeout.Token).ConfigureAwait(false);
            bucketCreated = false;
        }
        finally
        {
            if (bucketCreated)
            {
                await DeleteBucketBestEffortAsync(client, bucket, [key]).ConfigureAwait(false);
            }
        }
    }

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
