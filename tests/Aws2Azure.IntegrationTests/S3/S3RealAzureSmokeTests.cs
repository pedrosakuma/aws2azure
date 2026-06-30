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

    [SkippableFact]
    public async Task ObjectLock_retention_and_legal_hold_round_trip()
    {
        Skip.IfNot(_fx.BlobConfigured,
            "AZURE_BLOB_ACCOUNT/AZURE_BLOB_KEY not set — skipping real-Azure object-lock smoke.");

        var bucket = "aws2azure-it-" + Guid.NewGuid().ToString("N")[..12];
        const string key = "lock/object.txt";
        using var client = _fx.CreateS3Client();
        var bucketCreated = false;
        try
        {
            await client.PutBucketAsync(new PutBucketRequest { BucketName = bucket }).ConfigureAwait(false);
            bucketCreated = true;
            await client.PutObjectAsync(new PutObjectRequest
            {
                BucketName = bucket, Key = key, ContentBody = "worm",
            }).ConfigureAwait(false);

            var until = DateTime.UtcNow.AddMinutes(2);
            await client.PutObjectRetentionAsync(new PutObjectRetentionRequest
            {
                BucketName = bucket, Key = key,
                Retention = new ObjectLockRetention { Mode = ObjectLockRetentionMode.Governance, RetainUntilDate = until },
            }).ConfigureAwait(false);
            var ret = await client.GetObjectRetentionAsync(new GetObjectRetentionRequest { BucketName = bucket, Key = key }).ConfigureAwait(false);
            Assert.Equal(ObjectLockRetentionMode.Governance, ret.Retention.Mode);

            await client.PutObjectLegalHoldAsync(new PutObjectLegalHoldRequest
            {
                BucketName = bucket, Key = key,
                LegalHold = new ObjectLockLegalHold { Status = ObjectLockLegalHoldStatus.On },
            }).ConfigureAwait(false);
            var hold = await client.GetObjectLegalHoldAsync(new GetObjectLegalHoldRequest { BucketName = bucket, Key = key }).ConfigureAwait(false);
            Assert.Equal(ObjectLockLegalHoldStatus.On, hold.LegalHold.Status);

            await client.PutObjectLegalHoldAsync(new PutObjectLegalHoldRequest
            {
                BucketName = bucket, Key = key,
                LegalHold = new ObjectLockLegalHold { Status = ObjectLockLegalHoldStatus.Off },
            }).ConfigureAwait(false);
        }
        finally
        {
            if (bucketCreated)
            {
                try { await client.DeleteObjectAsync(new DeleteObjectRequest { BucketName = bucket, Key = key, BypassGovernanceRetention = true }).ConfigureAwait(false); } catch { }
                try { await client.DeleteBucketAsync(new DeleteBucketRequest { BucketName = bucket }).ConfigureAwait(false); } catch { }
            }
        }
    }

    [SkippableFact]
    public async Task Versioning_lists_object_versions_and_reads_by_version_id()
    {
        Skip.IfNot(_fx.BlobConfigured,
            "AZURE_BLOB_ACCOUNT/AZURE_BLOB_KEY not set — skipping real-Azure S3 versioning smoke.");

        var bucket = "aws2azure-it-" + Guid.NewGuid().ToString("N")[..12];
        const string key = "versioned/object.txt";
        using var client = _fx.CreateS3Client();
        var bucketCreated = false;
        try
        {
            await client.PutBucketAsync(new PutBucketRequest { BucketName = bucket }).ConfigureAwait(false);
            bucketCreated = true;

            await client.PutBucketVersioningAsync(new PutBucketVersioningRequest
            {
                BucketName = bucket,
                VersioningConfig = new S3BucketVersioningConfig { Status = VersionStatus.Enabled },
            }).ConfigureAwait(false);

            var versioning = await client.GetBucketVersioningAsync(new GetBucketVersioningRequest { BucketName = bucket }).ConfigureAwait(false);
            Assert.Equal(VersionStatus.Enabled, versioning.VersioningConfig.Status);

            var first = await client.PutObjectAsync(new PutObjectRequest
            {
                BucketName = bucket, Key = key, ContentBody = "v1",
            }).ConfigureAwait(false);
            var second = await client.PutObjectAsync(new PutObjectRequest
            {
                BucketName = bucket, Key = key, ContentBody = "v2",
            }).ConfigureAwait(false);

            Assert.False(string.IsNullOrWhiteSpace(first.VersionId));
            Assert.False(string.IsNullOrWhiteSpace(second.VersionId));
            Assert.NotEqual(first.VersionId, second.VersionId);

            var versions = await client.ListVersionsAsync(new ListVersionsRequest { BucketName = bucket, Prefix = key }).ConfigureAwait(false);
            var entries = versions.Versions.FindAll(v => string.Equals(v.Key, key, StringComparison.Ordinal));
            Assert.True(entries.Count >= 2, $"expected ≥2 versions, got {entries.Count}");
            Assert.Contains(entries, v => v.IsLatest == true);

            using var latest = await client.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = key }).ConfigureAwait(false);
            Assert.Equal("v2", await new StreamReader(latest.ResponseStream).ReadToEndAsync().ConfigureAwait(false));

            using var older = await client.GetObjectAsync(new GetObjectRequest
            {
                BucketName = bucket, Key = key, VersionId = first.VersionId,
            }).ConfigureAwait(false);
            Assert.Equal("v1", await new StreamReader(older.ResponseStream).ReadToEndAsync().ConfigureAwait(false));
        }
        finally
        {
            if (bucketCreated)
            {
                try
                {
                    var remaining = await client.ListVersionsAsync(new ListVersionsRequest { BucketName = bucket, Prefix = key }).ConfigureAwait(false);
                    foreach (var v in remaining.Versions)
                    {
                        try { await client.DeleteObjectAsync(new DeleteObjectRequest { BucketName = bucket, Key = v.Key, VersionId = v.VersionId }).ConfigureAwait(false); } catch { }
                    }
                }
                catch
                {
                }

                try { await client.DeleteObjectAsync(new DeleteObjectRequest { BucketName = bucket, Key = key }).ConfigureAwait(false); } catch { }
                try { await client.DeleteBucketAsync(new DeleteBucketRequest { BucketName = bucket }).ConfigureAwait(false); } catch { }
            }
        }
    }
}
