using Amazon.S3.Model;
using Amazon.SecretsManager.Model;
using Aws2Azure.IntegrationTests.Fixtures;
using Xunit;

namespace Aws2Azure.IntegrationTests.OperationalQualification;

internal static class RealAzureRestartQualification
{
    public static async Task VerifyS3Async(RealAzureProxyFixture fixture)
    {
        var bucket = "aws2azure-restart-" + Guid.NewGuid().ToString("N")[..10];
        const string key = "state";
        using var client = fixture.CreateS3Client();
        try
        {
            await client.PutBucketAsync(bucket).ConfigureAwait(false);
            await client.PutObjectAsync(new PutObjectRequest
            {
                BucketName = bucket,
                Key = key,
                ContentBody = "survives-restart",
            }).ConfigureAwait(false);

            await fixture.RestartAsync().ConfigureAwait(false);

            using var response = await client.GetObjectAsync(bucket, key).ConfigureAwait(false);
            using var reader = new StreamReader(response.ResponseStream);
            Assert.Equal("survives-restart",
                await reader.ReadToEndAsync().ConfigureAwait(false));
        }
        finally
        {
            try { await client.DeleteObjectAsync(bucket, key).ConfigureAwait(false); } catch { }
            try { await client.DeleteBucketAsync(bucket).ConfigureAwait(false); } catch { }
        }
    }

    public static async Task VerifySecretsManagerAsync(
        SecretsManagerRealAzureProxyFixture fixture)
    {
        var secret = "aws2azure-restart-" + Guid.NewGuid().ToString("N");
        using var client = fixture.CreateSecretsManagerClient();
        try
        {
            await client.CreateSecretAsync(new CreateSecretRequest
            {
                Name = secret,
                SecretString = "survives-restart",
            }).ConfigureAwait(false);

            await fixture.RestartAsync().ConfigureAwait(false);

            var response = await client.GetSecretValueAsync(new GetSecretValueRequest
            {
                SecretId = secret,
            }).ConfigureAwait(false);
            Assert.Equal("survives-restart", response.SecretString);
        }
        finally
        {
            try
            {
                await client.DeleteSecretAsync(new DeleteSecretRequest
                {
                    SecretId = secret,
                    ForceDeleteWithoutRecovery = true,
                }).ConfigureAwait(false);
            }
            catch
            {
            }
        }
    }
}
