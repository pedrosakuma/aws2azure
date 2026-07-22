using System;
using System.IO;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
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
        var legalHoldEnabled = false;
        var retentionExpiresAt = DateTime.MinValue;
        string? versionId = null;
        try
        {
            await client.PutBucketAsync(new PutBucketRequest { BucketName = bucket }).ConfigureAwait(false);
            bucketCreated = true;
            var put = await client.PutObjectAsync(new PutObjectRequest
            {
                BucketName = bucket, Key = key, ContentBody = "worm",
            }).ConfigureAwait(false);
            versionId = put.VersionId;
            Assert.False(string.IsNullOrWhiteSpace(versionId));

            retentionExpiresAt = DateTime.UtcNow.AddSeconds(15);
            await client.PutObjectRetentionAsync(new PutObjectRetentionRequest
            {
                BucketName = bucket, Key = key,
                Retention = new ObjectLockRetention
                {
                    Mode = ObjectLockRetentionMode.Governance,
                    RetainUntilDate = retentionExpiresAt,
                },
            }).ConfigureAwait(false);
            var ret = await client.GetObjectRetentionAsync(new GetObjectRetentionRequest { BucketName = bucket, Key = key }).ConfigureAwait(false);
            Assert.Equal(ObjectLockRetentionMode.Governance, ret.Retention.Mode);

            await client.PutObjectLegalHoldAsync(new PutObjectLegalHoldRequest
            {
                BucketName = bucket, Key = key,
                LegalHold = new ObjectLockLegalHold { Status = ObjectLockLegalHoldStatus.On },
            }).ConfigureAwait(false);
            legalHoldEnabled = true;
            var hold = await client.GetObjectLegalHoldAsync(new GetObjectLegalHoldRequest { BucketName = bucket, Key = key }).ConfigureAwait(false);
            Assert.Equal(ObjectLockLegalHoldStatus.On, hold.LegalHold.Status);

            await client.PutObjectLegalHoldAsync(new PutObjectLegalHoldRequest
            {
                BucketName = bucket, Key = key,
                LegalHold = new ObjectLockLegalHold { Status = ObjectLockLegalHoldStatus.Off },
            }).ConfigureAwait(false);
            legalHoldEnabled = false;
        }
        finally
        {
            if (bucketCreated)
            {
                if (legalHoldEnabled)
                {
                    try
                    {
                        await client.PutObjectLegalHoldAsync(new PutObjectLegalHoldRequest
                        {
                            BucketName = bucket,
                            Key = key,
                            LegalHold = new ObjectLockLegalHold { Status = ObjectLockLegalHoldStatus.Off },
                        }).ConfigureAwait(false);
                    }
                    catch
                    {
                    }
                }

                var retentionDelay = retentionExpiresAt - DateTime.UtcNow + TimeSpan.FromSeconds(2);
                if (retentionDelay > TimeSpan.Zero)
                {
                    await Task.Delay(retentionDelay).ConfigureAwait(false);
                }

                try
                {
                    await client.DeleteObjectAsync(new DeleteObjectRequest
                    {
                        BucketName = bucket,
                        Key = key,
                        VersionId = versionId,
                        BypassGovernanceRetention = true,
                    }).ConfigureAwait(false);
                }
                catch
                {
                }
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

    [SkippableFact]
    public async Task Bucket_compatibility_intents_survive_real_proxy_restart()
        {
            Skip.IfNot(_fx.BlobConfigured,
                "AZURE_BLOB_ACCOUNT/AZURE_BLOB_KEY not set — skipping real-Azure S3 metadata restart smoke.");

            var bucket = "aws2azure-it-" + Guid.NewGuid().ToString("N")[..12];
            using var client = _fx.CreateS3Client();
            using var http = new HttpClient();
            var bucketCreated = false;
            try
            {
                await client.PutBucketAsync(new PutBucketRequest { BucketName = bucket }).ConfigureAwait(false);
                bucketCreated = true;

                await AssertSignedSuccessAsync(http, HttpMethod.Put, $"{bucket}?ownershipControls",
                    "<OwnershipControls><Rule><ObjectOwnership>BucketOwnerEnforced</ObjectOwnership></Rule></OwnershipControls>");
                await AssertSignedSuccessAsync(http, HttpMethod.Put, $"{bucket}?publicAccessBlock",
                    "<PublicAccessBlockConfiguration>"
                    + "<BlockPublicAcls>true</BlockPublicAcls>"
                    + "<IgnorePublicAcls>true</IgnorePublicAcls>"
                    + "<BlockPublicPolicy>true</BlockPublicPolicy>"
                    + "<RestrictPublicBuckets>true</RestrictPublicBuckets>"
                    + "</PublicAccessBlockConfiguration>");
                await AssertSignedSuccessAsync(http, HttpMethod.Put, $"{bucket}?encryption",
                    "<ServerSideEncryptionConfiguration><Rule><ApplyServerSideEncryptionByDefault>"
                    + "<SSEAlgorithm>AES256</SSEAlgorithm>"
                    + "</ApplyServerSideEncryptionByDefault></Rule></ServerSideEncryptionConfiguration>");

                await _fx.RestartAsync().ConfigureAwait(false);

                Assert.Contains("BucketOwnerEnforced",
                    await GetSignedBodyAsync(http, $"{bucket}?ownershipControls").ConfigureAwait(false));
                Assert.Contains("<RestrictPublicBuckets>true</RestrictPublicBuckets>",
                    await GetSignedBodyAsync(http, $"{bucket}?publicAccessBlock").ConfigureAwait(false));
                Assert.Contains("<SSEAlgorithm>AES256</SSEAlgorithm>",
                    await GetSignedBodyAsync(http, $"{bucket}?encryption").ConfigureAwait(false));
            }
            finally
            {
                if (bucketCreated)
                {
                    try { await client.DeleteBucketAsync(new DeleteBucketRequest { BucketName = bucket }).ConfigureAwait(false); } catch { }
                }
            }
        }

        [SkippableFact]
        public async Task Version_specific_tagging_and_acl_target_blob_versions()
        {
            Skip.IfNot(_fx.BlobConfigured,
                "AZURE_BLOB_ACCOUNT/AZURE_BLOB_KEY not set — skipping real-Azure S3 version-tagging smoke.");

            var bucket = "aws2azure-it-" + Guid.NewGuid().ToString("N")[..12];
            const string key = "versioned/tagged.txt";
            using var client = _fx.CreateS3Client();
            using var http = new HttpClient();
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

                var first = await client.PutObjectAsync(new PutObjectRequest
                {
                    BucketName = bucket,
                    Key = key,
                    ContentBody = "v1",
                }).ConfigureAwait(false);
                var second = await client.PutObjectAsync(new PutObjectRequest
                {
                    BucketName = bucket,
                    Key = key,
                    ContentBody = "v2",
                }).ConfigureAwait(false);
                Assert.False(string.IsNullOrWhiteSpace(first.VersionId));
                Assert.False(string.IsNullOrWhiteSpace(second.VersionId));

                await AssertSignedSuccessAsync(http, HttpMethod.Put,
                    $"{bucket}/{key}?tagging&versionId={Uri.EscapeDataString(first.VersionId!)}",
                    "<Tagging><TagSet><Tag><Key>version</Key><Value>one</Value></Tag></TagSet></Tagging>");
                await AssertSignedSuccessAsync(http, HttpMethod.Put,
                    $"{bucket}/{key}?tagging&versionId={Uri.EscapeDataString(second.VersionId!)}",
                    "<Tagging><TagSet><Tag><Key>version</Key><Value>two</Value></Tag></TagSet></Tagging>");

                Assert.Contains("<Value>one</Value>", await GetSignedBodyAsync(
                    http, $"{bucket}/{key}?tagging&versionId={Uri.EscapeDataString(first.VersionId!)}").ConfigureAwait(false));
                Assert.Contains("<Value>two</Value>", await GetSignedBodyAsync(
                    http, $"{bucket}/{key}?tagging&versionId={Uri.EscapeDataString(second.VersionId!)}").ConfigureAwait(false));

                var acl = await SendSignedAsync(
                    http,
                    HttpMethod.Get,
                    $"{bucket}/{key}?acl&versionId={Uri.EscapeDataString(first.VersionId!)}",
                    Array.Empty<byte>()).ConfigureAwait(false);
                using (acl)
                {
                    Assert.Equal(HttpStatusCode.OK, acl.StatusCode);
                    Assert.Equal(first.VersionId, acl.Headers.GetValues("x-amz-version-id").Single());
                    Assert.Contains("FULL_CONTROL", await acl.Content.ReadAsStringAsync().ConfigureAwait(false));
                }
            }
            finally
            {
                if (bucketCreated)
                {
                    try
                    {
                        var remaining = await client.ListVersionsAsync(
                            new ListVersionsRequest { BucketName = bucket, Prefix = key }).ConfigureAwait(false);
                        foreach (var version in remaining.Versions)
                        {
                            try
                            {
                                await client.DeleteObjectAsync(new DeleteObjectRequest
                                {
                                    BucketName = bucket,
                                    Key = version.Key,
                                    VersionId = version.VersionId,
                                }).ConfigureAwait(false);
                            }
                            catch
                            {
                            }
                    }
                    }
                    catch
                    {
                    }
                    try { await client.DeleteBucketAsync(new DeleteBucketRequest { BucketName = bucket }).ConfigureAwait(false); } catch { }
                }
            }
        }

    private async Task AssertSignedSuccessAsync(
        HttpClient http, HttpMethod method, string pathAndQuery, string xml)
    {
        using var response = await SendSignedAsync(
            http, method, pathAndQuery, Encoding.UTF8.GetBytes(xml), "application/xml").ConfigureAwait(false);
        Assert.True(response.IsSuccessStatusCode,
            $"{pathAndQuery} → {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync().ConfigureAwait(false)}");
    }

    private async Task<string> GetSignedBodyAsync(HttpClient http, string pathAndQuery)
    {
        using var response = await SendSignedAsync(
            http, HttpMethod.Get, pathAndQuery, Array.Empty<byte>()).ConfigureAwait(false);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendSignedAsync(
        HttpClient http,
        HttpMethod method,
        string pathAndQuery,
        byte[] body,
        string? contentType = null)
    {
        var request = new HttpRequestMessage(
            method,
            new Uri(new Uri(_fx.S3ServiceUrl.TrimEnd('/') + "/"), pathAndQuery));
        if (body.Length > 0 || method == HttpMethod.Put)
        {
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentLength = body.Length;
            if (contentType is not null)
            {
                request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
            }
        }
        TestSigV4Signer.SignHeader(
            request,
            body,
            RealAzureProxyFixture.AwsAccessKey,
            RealAzureProxyFixture.AwsSecret);
        return await http.SendAsync(request).ConfigureAwait(false);
    }
}
