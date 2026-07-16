using Amazon.SecretsManager.Model;
using Xunit;

namespace Aws2Azure.IntegrationTests.SecretsManager;

[Trait("Category", "RealAzure")]
[Collection(SecretsManagerRealAzureCollection.Name)]
public sealed class SecretsManagerRealAzureConformanceTests(SecretsManagerRealAzureProxyFixture fixture)
{
    [SkippableFact]
    public async Task ListSecrets_paginates_against_real_key_vault()
    {
        Skip.If(!fixture.Configured,
            fixture.SkipReason ?? "Key Vault real-Azure fixture is not configured.");

        var prefix = "aws2azure-page-" + Guid.NewGuid().ToString("N")[..12];
        var names = Enumerable.Range(0, 3).Select(i => $"{prefix}-{i}").ToArray();
        using var client = fixture.CreateSecretsManagerClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));

        try
        {
            foreach (var name in names)
            {
                await client.CreateSecretAsync(new CreateSecretRequest
                {
                    Name = name,
                    SecretString = "pagination-value",
                }, timeout.Token).ConfigureAwait(false);
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            string? nextToken = null;
            var pages = 0;
            do
            {
                var response = await client.ListSecretsAsync(new ListSecretsRequest
                {
                    MaxResults = 2,
                    NextToken = nextToken,
                }, timeout.Token).ConfigureAwait(false);
                pages++;
                Assert.InRange(pages, 1, 20);
                foreach (var secret in response.SecretList)
                {
                    if (secret.Name.StartsWith(prefix, StringComparison.Ordinal))
                    {
                        seen.Add(secret.Name);
                    }
                }

                nextToken = response.NextToken;
            } while (!string.IsNullOrWhiteSpace(nextToken));

            Assert.True(pages > 1);
            Assert.Equal(names.Order(StringComparer.Ordinal).ToArray(), seen.Order(StringComparer.Ordinal).ToArray());
        }
        finally
        {
            foreach (var name in names)
            {
                try
                {
                    await client.DeleteSecretAsync(new DeleteSecretRequest
                    {
                        SecretId = name,
                        ForceDeleteWithoutRecovery = true,
                    }).ConfigureAwait(false);
                }
                catch
                {
                }
            }
        }
    }
}
