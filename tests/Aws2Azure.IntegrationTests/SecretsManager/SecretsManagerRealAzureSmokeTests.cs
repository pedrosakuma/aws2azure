using System;
using System.Collections.Generic;
using System.Threading.Tasks;
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
        var vaultUrl = Environment.GetEnvironmentVariable("AZURE_KEYVAULT_URL");
        var tenantId = Environment.GetEnvironmentVariable("AZURE_KEYVAULT_TENANT_ID");
        var clientId = Environment.GetEnvironmentVariable("AZURE_KEYVAULT_CLIENT_ID");
        var clientSecret = Environment.GetEnvironmentVariable("AZURE_KEYVAULT_CLIENT_SECRET");

        Skip.If(string.IsNullOrWhiteSpace(vaultUrl) || string.IsNullOrWhiteSpace(tenantId)
            || string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret),
            "AZURE_KEYVAULT_* env vars not set — skipping real-Azure Secrets Manager smoke.");

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

            var updatedValue = await client.GetSecretValueAsync(new GetSecretValueRequest
            {
                SecretId = secretName,
            }).ConfigureAwait(false);

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
}
