using Amazon.SecretsManager.Model;
using Aws2Azure.IntegrationTests.Fixtures;
using Xunit;

namespace Aws2Azure.IntegrationTests.OperationalQualification;

[Trait("Category", "RealAzure")]
[Collection(SecretsManagerRealAzureCollection.Name)]
public sealed class SecretsManagerRestartQualificationTests(
    SecretsManagerRealAzureProxyFixture fixture)
{
    [SkippableFact]
    public async Task Secret_remains_readable_after_proxy_restart()
    {
        Skip.If(!fixture.Configured, fixture.SkipReason ?? "Real Azure Key Vault is not configured.");
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
