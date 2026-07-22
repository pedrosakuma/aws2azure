using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
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

    [SkippableFact]
    public async Task PutSecretValue_converges_across_instances_and_preserves_out_of_band_tags()
        {
            Skip.If(!_proxy.Configured, _proxy.SkipReason ?? "Key Vault real-Azure fixture is not configured.");
            var secretName = "aws2azure-it-durable-" + Guid.NewGuid().ToString("N");
            var secondInstance = await _proxy.StartAdditionalRuntimeAsync(Aws2Azure.TestSupport.OperationalQualification.SealedRuntimeRole.Candidate).ConfigureAwait(false);
            using var firstClient = _proxy.CreateSecretsManagerClient(maxErrorRetry: 0);
            using var secondClient = _proxy.CreateSecretsManagerClient(secondInstance.ServiceUrl, maxErrorRetry: 0);

            try
            {
                await firstClient.CreateSecretAsync(new CreateSecretRequest
                {
                    Name = secretName,
                    SecretString = "base",
                }).ConfigureAwait(false);

                var sharedToken = Guid.NewGuid().ToString();
                var writes = await Task.WhenAll(
                    PutOrConflictAsync(firstClient, secretName, "shared", sharedToken),
                    PutOrConflictAsync(secondClient, secretName, "shared", sharedToken)).ConfigureAwait(false);
                Assert.Contains(writes, result => result is not null);
                Assert.All(writes.Where(result => result is not null), result => Assert.Equal(sharedToken, result!.VersionId));

                var current = await firstClient.GetSecretValueAsync(new GetSecretValueRequest
                {
                    SecretId = secretName,
                }).ConfigureAwait(false);
                Assert.Equal("shared", current.SecretString);
                Assert.Equal(sharedToken, current.VersionId);

                var rawVersion = await firstClient.PutSecretValueAsync(new PutSecretValueRequest
                {
                    SecretId = secretName,
                    SecretString = "tagged",
                }).ConfigureAwait(false);
                await AddKeyVaultVersionTagAsync(secretName, rawVersion.VersionId, "operator-tag", "preserved").ConfigureAwait(false);

                await firstClient.PutSecretValueAsync(new PutSecretValueRequest
                {
                    SecretId = secretName,
                    SecretString = "after-tag",
                }).ConfigureAwait(false);
                Assert.Equal(
                    "preserved",
                    await ReadKeyVaultVersionTagAsync(secretName, rawVersion.VersionId, "operator-tag").ConfigureAwait(false));

                await _proxy.RestartAsync().ConfigureAwait(false);
                using var restartedClient = _proxy.CreateSecretsManagerClient(maxErrorRetry: 0);
                var replay = await restartedClient.PutSecretValueAsync(new PutSecretValueRequest
                {
                    SecretId = secretName,
                    SecretString = "shared",
                    ClientRequestToken = sharedToken,
                }).ConfigureAwait(false);
                Assert.Equal(sharedToken, replay.VersionId);
            }
            finally
            {
                try
                {
                    using var cleanup = _proxy.CreateSecretsManagerClient(maxErrorRetry: 0);
                    await cleanup.DeleteSecretAsync(new DeleteSecretRequest
                    {
                        SecretId = secretName,
                        ForceDeleteWithoutRecovery = true,
                    }).ConfigureAwait(false);
                }
                catch
                {
                }

                await _proxy.StopProxyInstanceAsync(secondInstance).ConfigureAwait(false);
            }
        }

    private static async Task<PutSecretValueResponse?> PutOrConflictAsync(
            IAmazonSecretsManager client,
            string secretName,
            string value,
            string token)
        {
            try
            {
                return await client.PutSecretValueAsync(new PutSecretValueRequest
                {
                    SecretId = secretName,
                    SecretString = value,
                    ClientRequestToken = token,
                }).ConfigureAwait(false);
            }
            catch (ResourceExistsException)
            {
                return null;
            }
        }

    private static async Task AddKeyVaultVersionTagAsync(string name, string versionId, string key, string value)
        {
            using var client = await CreateKeyVaultHttpClientAsync().ConfigureAwait(false);
            var uri = BuildKeyVaultVersionUri(name, versionId);
            using var response = await client.GetAsync(uri).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
            var tags = new Dictionary<string, string>(StringComparer.Ordinal);
            if (document.RootElement.TryGetProperty("tags", out var existingTags))
            {
                foreach (var tag in existingTags.EnumerateObject())
                {
                    tags[tag.Name] = tag.Value.GetString() ?? string.Empty;
                }
            }

            tags[key] = value;
            using var patch = new HttpRequestMessage(HttpMethod.Patch, uri)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { tags }), System.Text.Encoding.UTF8, "application/json"),
            };
            using var patchResponse = await client.SendAsync(patch).ConfigureAwait(false);
            patchResponse.EnsureSuccessStatusCode();
        }

    private static async Task<string?> ReadKeyVaultVersionTagAsync(string name, string versionId, string key)
        {
            using var client = await CreateKeyVaultHttpClientAsync().ConfigureAwait(false);
            using var response = await client.GetAsync(BuildKeyVaultVersionUri(name, versionId)).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
            return document.RootElement.TryGetProperty("tags", out var tags)
                   && tags.TryGetProperty(key, out var value)
                ? value.GetString()
                : null;
        }

    private static async Task<HttpClient> CreateKeyVaultHttpClientAsync()
        {
            var tenantId = Environment.GetEnvironmentVariable("AZURE_TENANT_ID")
                ?? throw new InvalidOperationException("AZURE_TENANT_ID is required.");
            var clientId = Environment.GetEnvironmentVariable("AZURE_CLIENT_ID")
                ?? throw new InvalidOperationException("AZURE_CLIENT_ID is required.");
            var tokenFile = Environment.GetEnvironmentVariable("AZURE_FEDERATED_TOKEN_FILE")
                ?? throw new InvalidOperationException("AZURE_FEDERATED_TOKEN_FILE is required.");
            var assertion = await File.ReadAllTextAsync(tokenFile).ConfigureAwait(false);
            using var tokenClient = new HttpClient();
            using var tokenResponse = await tokenClient.PostAsync(
                $"https://login.microsoftonline.com/{tenantId}/oauth2/v2.0/token",
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = clientId,
                    ["scope"] = "https://vault.azure.net/.default",
                    ["grant_type"] = "client_credentials",
                    ["client_assertion_type"] = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer",
                    ["client_assertion"] = assertion.Trim(),
                })).ConfigureAwait(false);
            tokenResponse.EnsureSuccessStatusCode();
            using var tokenDocument = JsonDocument.Parse(await tokenResponse.Content.ReadAsStringAsync().ConfigureAwait(false));
            var accessToken = tokenDocument.RootElement.GetProperty("access_token").GetString()
                ?? throw new InvalidOperationException("Token response omitted access_token.");
            var client = new HttpClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            return client;
        }

    private static string BuildKeyVaultVersionUri(string name, string versionId)
        {
            var vaultUrl = Environment.GetEnvironmentVariable("AZURE_KEYVAULT_URL")
                ?? throw new InvalidOperationException("AZURE_KEYVAULT_URL is required.");
            return $"{vaultUrl.TrimEnd('/')}/secrets/{Uri.EscapeDataString(name)}/{Uri.EscapeDataString(versionId)}?api-version=7.4";
        }
    // Azure Key Vault does not guarantee read-your-write on the unversioned
    // GetSecret endpoint immediately after an update: the latest-version pointer
    // can briefly resolve to the prior version (propagation lag, occasionally
    // tens of seconds). Poll with a bounded backoff until the expected value is
    // observed; on timeout return the last response so the assertion fails with
    // the actual value for diagnostics. See issue #484.
    private static async Task<GetSecretValueResponse> GetSecretValueEventuallyAsync(
        IAmazonSecretsManager client,
        GetSecretValueRequest request,
        string expectedValue)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        var delay = TimeSpan.FromMilliseconds(500);
        var maxDelay = TimeSpan.FromSeconds(2);
        GetSecretValueResponse response;
        while (true)
        {
            response = await client.GetSecretValueAsync(request).ConfigureAwait(false);
            if (string.Equals(response.SecretString, expectedValue, StringComparison.Ordinal)
                || DateTime.UtcNow >= deadline)
            {
                return response;
            }

            await Task.Delay(delay).ConfigureAwait(false);
            delay = delay < maxDelay ? delay + delay : maxDelay;
        }
    }
}
