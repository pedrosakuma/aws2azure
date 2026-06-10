using System;
using System.IO;
using System.Threading.Tasks;
using Amazon.S3;
using Amazon.S3.Model;
using Aws2Azure.IntegrationTests.Fixtures;
using Xunit;

namespace Aws2Azure.IntegrationTests.S3;

/// <summary>
/// Real-Azure nightly smoke for the S3 module (issue #153): a full
/// CreateBucket → PutObject → GetObject → DeleteObject → DeleteBucket cycle
/// against live Azure Blob Storage. Tagged <c>Category=RealAzure</c> so the
/// <c>integration-real-azure</c> workflow filters it in isolation. Skips when
/// the <c>AZURE_BLOB_*</c> secrets are absent.
/// </summary>
[Trait("Category", "RealAzure")]
[Collection(RealAzureCollection.Name)]
public sealed class S3RealAzureSmokeTests
{
    private readonly RealAzureProxyFixture _fx;

    public S3RealAzureSmokeTests(RealAzureProxyFixture fx)
    {
        _fx = fx;
    }

    [SkippableFact]
    public async Task Object_lifecycle_round_trips_against_real_blob_storage()
    {
        Skip.IfNot(_fx.BlobConfigured,
            "AZURE_BLOB_ACCOUNT/AZURE_BLOB_KEY not set — skipping real-Azure S3 smoke.");

        var bucket = "aws2azure-it-" + Guid.NewGuid().ToString("N")[..12];
        const string key = "smoke/object.txt";
        const string payload = "aws2azure real-Azure S3 smoke payload";

        using var client = _fx.CreateS3Client();
        var bucketCreated = false;

        try
        {
            await client.PutBucketAsync(new PutBucketRequest { BucketName = bucket }).ConfigureAwait(false);
            bucketCreated = true;

            await client.PutObjectAsync(new PutObjectRequest
            {
                BucketName = bucket,
                Key = key,
                ContentBody = payload,
            }).ConfigureAwait(false);

            using var got = await client.GetObjectAsync(new GetObjectRequest
            {
                BucketName = bucket,
                Key = key,
            }).ConfigureAwait(false);

            using var reader = new StreamReader(got.ResponseStream);
            var roundTripped = await reader.ReadToEndAsync().ConfigureAwait(false);
            Assert.Equal(payload, roundTripped);

            await client.DeleteObjectAsync(new DeleteObjectRequest
            {
                BucketName = bucket,
                Key = key,
            }).ConfigureAwait(false);
        }
        finally
        {
            if (bucketCreated)
            {
                try
                {
                    await client.DeleteObjectAsync(new DeleteObjectRequest { BucketName = bucket, Key = key }).ConfigureAwait(false);
                }
                catch
                {
                }

                try
                {
                    await client.DeleteBucketAsync(new DeleteBucketRequest { BucketName = bucket }).ConfigureAwait(false);
                }
                catch
                {
                    // Best-effort cleanup for the real-Azure smoke path.
                }
            }
        }
    }
}
