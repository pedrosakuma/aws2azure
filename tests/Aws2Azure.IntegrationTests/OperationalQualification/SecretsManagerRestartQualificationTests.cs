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
        await RealAzureRestartQualification.VerifySecretsManagerAsync(fixture)
            .ConfigureAwait(false);
    }
}
