using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using Xunit;

namespace Aws2Azure.IntegrationTests.SecretsManager;

[Trait("Category", "RealAzure")]
[Collection(SecretsManagerRealAzureCollection.Name)]
public sealed class SecretsManagerRealAzureSmokeTests
{
    private readonly SecretsManagerRealAzureProxyFixture _proxy;

    public SecretsManagerRealAzureSmokeTests(SecretsManagerRealAzureProxyFixture proxy)
    {
        _proxy = proxy;
    }

    [SkippableFact]
    public async Task Secret_lifecycle_round_trip_uses_real_key_vault_backend()
    {
        Skip.If(!_proxy.Configured, _proxy.SkipReason ?? "Key Vault real-Azure fixture is not configured.");

        var secretName = "aws2azure-it-" + Guid.NewGuid().ToString("N");

        using var client = _proxy.CreateSecretsManagerClient();

        try
        {
            var created = await client.CreateSecretAsync(new CreateSecretRequest
            {
                Name = secretName,
                SecretString = "smoke-secret-value",
                Description = "aws2azure real-Azure smoke test",
                Tags =
                [
                    new Tag { Key = "env", Value = "integration" },
                ],
            }).ConfigureAwait(false);

            Assert.False(string.IsNullOrWhiteSpace(created.ARN));
            Assert.False(string.IsNullOrWhiteSpace(created.VersionId));

            var described = await client.DescribeSecretAsync(new DescribeSecretRequest
            {
                SecretId = secretName,
            }).ConfigureAwait(false);

            Assert.Equal(secretName, described.Name);
            Assert.Contains(created.VersionId, described.VersionIdsToStages.Keys);

            var value = await client.GetSecretValueAsync(new GetSecretValueRequest
            {
                SecretId = secretName,
            }).ConfigureAwait(false);

            Assert.Equal("smoke-secret-value", value.SecretString);

            var updated = await client.UpdateSecretAsync(new UpdateSecretRequest
            {
                SecretId = secretName,
                SecretString = "smoke-secret-value-updated",
                Description = "aws2azure real-Azure smoke test updated",
            }).ConfigureAwait(false);

            Assert.False(string.IsNullOrWhiteSpace(updated.VersionId));

            var listed = await client.ListSecretsAsync(new ListSecretsRequest()).ConfigureAwait(false);
            Assert.Contains(listed.SecretList, item => string.Equals(item.Name, secretName, StringComparison.Ordinal));

            var updatedValue = await GetSecretValueEventuallyAsync(
                client,
                new GetSecretValueRequest { SecretId = secretName },
                "smoke-secret-value-updated").ConfigureAwait(false);

            Assert.Equal("smoke-secret-value-updated", updatedValue.SecretString);
        }
        finally
        {
            try
            {
                await client.DeleteSecretAsync(new DeleteSecretRequest
                {
                    SecretId = secretName,
                    ForceDeleteWithoutRecovery = true,
                }).ConfigureAwait(false);
            }
            catch
            {
                // Best-effort cleanup for the real-Azure smoke path.
            }
        }
    }

    [SkippableFact]
    public async Task PutSecretValue_versioning_and_idempotency_against_real_key_vault()
    {
        Skip.If(!_proxy.Configured, _proxy.SkipReason ?? "Key Vault real-Azure fixture is not configured.");

        var secretName = "aws2azure-it-put-" + Guid.NewGuid().ToString("N");

        using var client = _proxy.CreateSecretsManagerClient();

        try
        {
            var created = await client.CreateSecretAsync(new CreateSecretRequest
            {
                Name = secretName,
                SecretString = "v1",
            }).ConfigureAwait(false);

            // PutSecretValue moves AWSCURRENT to the new version and demotes the
            // prior current version to AWSPREVIOUS.
            var put = await client.PutSecretValueAsync(new PutSecretValueRequest
            {
                SecretId = secretName,
                SecretString = "v2",
            }).ConfigureAwait(false);

            Assert.False(string.IsNullOrWhiteSpace(put.VersionId));
            Assert.NotEqual(created.VersionId, put.VersionId);
            Assert.Contains("AWSCURRENT", put.VersionStages);

            var current = await GetSecretValueEventuallyAsync(
                client,
                new GetSecretValueRequest { SecretId = secretName },
                "v2").ConfigureAwait(false);
            Assert.Equal("v2", current.SecretString);

            var previous = await client.GetSecretValueAsync(new GetSecretValueRequest
            {
                SecretId = secretName,
                VersionStage = "AWSPREVIOUS",
            }).ConfigureAwait(false);
            Assert.Equal("v1", previous.SecretString);

            // ClientRequestToken idempotency: replaying the same token with the same
            // payload returns the same VersionId without creating a new version.
            var token = Guid.NewGuid().ToString();
            var first = await client.PutSecretValueAsync(new PutSecretValueRequest
            {
                SecretId = secretName,
                SecretString = "v3",
                ClientRequestToken = token,
            }).ConfigureAwait(false);

            var replay = await client.PutSecretValueAsync(new PutSecretValueRequest
            {
                SecretId = secretName,
                SecretString = "v3",
                ClientRequestToken = token,
            }).ConfigureAwait(false);

            Assert.Equal(first.VersionId, replay.VersionId);

            // Same token, different payload must conflict (ResourceExistsException).
            await Assert.ThrowsAsync<ResourceExistsException>(() => client.PutSecretValueAsync(new PutSecretValueRequest
            {
                SecretId = secretName,
                SecretString = "different-payload",
                ClientRequestToken = token,
            })).ConfigureAwait(false);

            // Explicit VersionStages: a custom staging label is persisted on the new
            // version and resolvable via GetSecretValue (the supported path — the
            // proxy resolves stages from Key Vault version tags). DescribeSecret's
            // VersionIdsToStages is intentionally a simplified current-version view
            // and is not asserted here.
            var staged = await client.PutSecretValueAsync(new PutSecretValueRequest
            {
                SecretId = secretName,
                SecretString = "v4",
                VersionStages = ["MYSTAGE"],
            }).ConfigureAwait(false);

            Assert.Contains("MYSTAGE", staged.VersionStages);

            var byStage = await client.GetSecretValueAsync(new GetSecretValueRequest
            {
                SecretId = secretName,
                VersionStage = "MYSTAGE",
            }).ConfigureAwait(false);

            Assert.Equal("v4", byStage.SecretString);
            Assert.Equal(staged.VersionId, byStage.VersionId);
        }
        finally
        {
            try
            {
                await client.DeleteSecretAsync(new DeleteSecretRequest
                {
                    SecretId = secretName,
                    ForceDeleteWithoutRecovery = true,
                }).ConfigureAwait(false);
            }
            catch
            {
                // Best-effort cleanup for the real-Azure smoke path.
            }
        }
    }

    // Azure Key Vault does not guarantee read-your-write on the unversioned
    // GetSecret endpoint immediately after an update: the latest-version pointer
    // can briefly resolve to the prior version (propagation lag). Poll with a
    // short bounded retry until the expected value is observed; on timeout return
    // the last response so the assertion fails with the actual value for
    // diagnostics. See issue #484.
    private static async Task<GetSecretValueResponse> GetSecretValueEventuallyAsync(
        IAmazonSecretsManager client,
        GetSecretValueRequest request,
        string expectedValue)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        GetSecretValueResponse response;
        while (true)
        {
            response = await client.GetSecretValueAsync(request).ConfigureAwait(false);
            if (string.Equals(response.SecretString, expectedValue, StringComparison.Ordinal)
                || DateTime.UtcNow >= deadline)
            {
                return response;
            }

            await Task.Delay(500).ConfigureAwait(false);
        }
    }
}
