using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Aws2Azure.Core.Azure;
using Aws2Azure.Core.Configuration;
using Aws2Azure.Core.Modules;
using Aws2Azure.Modules.SecretsManager;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.UnitTests.SecretsManager;

/// <summary>
/// Covers issue #962: AWS Secrets Manager names allow <c>/_+=.@-</c> (hierarchical
/// names such as Airflow's <c>SecretsManagerBackend</c> default convention
/// <c>airflow/connections/my_conn</c> are common), but Azure Key Vault secret
/// names must match <c>^[0-9a-zA-Z-]+$</c>. Without translation, such names 404
/// against real Key Vault with a raw IIS error instead of a clean AWS error.
/// </summary>
public sealed class KeyVaultSecretNameEncodingTests
{
    private static readonly Regex KeyVaultLegalName = new("^[0-9a-zA-Z-]+$", RegexOptions.Compiled);

    [Theory]
    [InlineData("plain-name")]
    [InlineData("Already123Legal")]
    [InlineData("a")]
    public void EncodeVaultSecretName_passes_through_already_legal_names_unchanged(string name)
    {
        Assert.Equal(name, KeyVaultSecretClient.EncodeVaultSecretName(name));
    }

    [Theory]
    [InlineData("airflow/connections/my_conn")]
    [InlineData("a/b")]
    [InlineData("a_b")]
    [InlineData("path/with/many/slashes/and_underscores.and.dots")]
    [InlineData("/////")]
    public void EncodeVaultSecretName_produces_key_vault_legal_names(string name)
    {
        var encoded = KeyVaultSecretClient.EncodeVaultSecretName(name);

        Assert.Matches(KeyVaultLegalName, encoded);
        Assert.True(encoded.Length <= 127);
    }

    [Fact]
    public void EncodeVaultSecretName_is_deterministic()
    {
        const string name = "airflow/connections/my_conn";

        var first = KeyVaultSecretClient.EncodeVaultSecretName(name);
        var second = KeyVaultSecretClient.EncodeVaultSecretName(name);

        Assert.Equal(first, second);
    }

    [Fact]
    public void EncodeVaultSecretName_avoids_collisions_between_names_sharing_a_sanitized_prefix()
    {
        // These three names all sanitize to the same "a-b" prefix once '/' and
        // '_' are replaced, so the hash suffix must be the thing that keeps
        // them distinct.
        var encodedSlash = KeyVaultSecretClient.EncodeVaultSecretName("a/b");
        var encodedUnderscore = KeyVaultSecretClient.EncodeVaultSecretName("a_b");
        var encodedDash = KeyVaultSecretClient.EncodeVaultSecretName("a-b");

        Assert.Equal("a-b", encodedDash); // already legal: passes through unchanged
        Assert.NotEqual(encodedSlash, encodedUnderscore);
        Assert.NotEqual(encodedSlash, encodedDash);
        Assert.NotEqual(encodedUnderscore, encodedDash);
    }

    [Fact]
    public void EncodeVaultSecretName_truncates_long_names_but_stays_within_key_vault_limit()
    {
        var longName = "airflow/connections/" + new string('x', 500);

        var encoded = KeyVaultSecretClient.EncodeVaultSecretName(longName);

        Assert.Matches(KeyVaultLegalName, encoded);
        Assert.True(encoded.Length <= 127);
    }

    [Fact]
    public void EncodeVaultSecretName_falls_back_to_a_placeholder_prefix_when_sanitizing_strips_everything()
    {
        var encoded = KeyVaultSecretClient.EncodeVaultSecretName("/////");

        Assert.StartsWith("secret-", encoded, StringComparison.Ordinal);
        Assert.Matches(KeyVaultLegalName, encoded);
    }

    [Fact]
    public async Task HandleAsync_CreateSecret_with_slash_name_targets_encoded_key_vault_path_and_tags_raw_name()
    {
        const string rawName = "airflow/connections/my_conn";
        var encodedName = KeyVaultSecretClient.EncodeVaultSecretName(rawName);
        var expectedPath = "/secrets/" + encodedName;
        string? putBody = null;
        string? putPath = null;

        using var http = new AzureHttpClient(new ScriptedHandler((request, _) =>
        {
            if (request.RequestUri!.AbsoluteUri.Contains("oauth2/v2.0/token"))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"access_token\":\"token\",\"expires_in\":3600,\"token_type\":\"Bearer\"}", Encoding.UTF8, "application/json"),
                });
            }

            if (request.Method == HttpMethod.Get)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            putPath = request.RequestUri!.AbsolutePath;
            putBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $"{{\"id\":\"https://example.vault.azure.net/secrets/{encodedName}/versions/abc123\",\"attributes\":{{\"created\":1710000000}}}}",
                    Encoding.UTF8,
                    "application/json"),
            });
        }), ownsHandler: false);

        var module = CreateModule(http);
        var context = CreateContext("SecretsManager.CreateSecret", $"{{\"Name\":\"{rawName}\",\"SecretString\":\"super-secret\"}}");

        await module.HandleAsync(context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal(expectedPath, putPath);
        Assert.NotNull(putBody);
        using var putDocument = JsonDocument.Parse(putBody!);
        Assert.Equal(
            rawName,
            putDocument.RootElement.GetProperty("tags").GetProperty(KeyVaultSecretClient.AwsSecretNameTag).GetString());

        var body = await ReadBodyAsync(context);
        using var responseDocument = JsonDocument.Parse(body);
        Assert.Equal(rawName, responseDocument.RootElement.GetProperty("Name").GetString());
        Assert.Equal($"arn:aws:secretsmanager:azure:keyvault:secret:{rawName}", responseDocument.RootElement.GetProperty("ARN").GetString());
    }

    [Fact]
    public async Task HandleAsync_GetSecretValue_with_slash_name_resolves_via_encoded_key_vault_path()
    {
        const string rawName = "airflow/connections/my_conn";
        var encodedName = KeyVaultSecretClient.EncodeVaultSecretName(rawName);

        using var http = new AzureHttpClient(new ScriptedHandler((request, _) =>
        {
            if (request.RequestUri!.AbsoluteUri.Contains("oauth2/v2.0/token"))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"access_token\":\"token\",\"expires_in\":3600,\"token_type\":\"Bearer\"}", Encoding.UTF8, "application/json"),
                });
            }

            if (request.RequestUri!.AbsolutePath.EndsWith("/versions", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        $"{{\"value\":[{{\"id\":\"https://example.vault.azure.net/secrets/{encodedName}/versions/abc123\",\"attributes\":{{\"created\":1710000000}},\"tags\":{{\"aws2azure-version-stages\":\"AWSCURRENT\",\"aws2azure-secret-name\":\"{rawName}\"}}}}]}}",
                        Encoding.UTF8,
                        "application/json"),
                });
            }

            Assert.Equal("/secrets/" + encodedName + "/abc123", request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $"{{\"value\":\"super-secret\",\"id\":\"https://example.vault.azure.net/secrets/{encodedName}/versions/abc123\",\"contentType\":\"text/plain\",\"attributes\":{{\"created\":1710000000}}}}",
                    Encoding.UTF8,
                    "application/json"),
            });
        }), ownsHandler: false);

        var module = CreateModule(http);
        var context = CreateContext("SecretsManager.GetSecretValue", $"{{\"SecretId\":\"{rawName}\"}}");

        await module.HandleAsync(context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        var body = await ReadBodyAsync(context);
        using var document = JsonDocument.Parse(body);
        Assert.Equal(rawName, document.RootElement.GetProperty("Name").GetString());
        Assert.Equal("super-secret", document.RootElement.GetProperty("SecretString").GetString());
    }

    [Fact]
    public async Task HandleAsync_ListSecrets_recovers_raw_slash_name_from_internal_tag_instead_of_encoded_id()
    {
        const string rawName = "airflow/connections/my_conn";
        var encodedName = KeyVaultSecretClient.EncodeVaultSecretName(rawName);

        using var http = new AzureHttpClient(new ScriptedHandler((request, _) =>
        {
            if (request.RequestUri!.AbsoluteUri.Contains("oauth2/v2.0/token"))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"access_token\":\"token\",\"expires_in\":3600,\"token_type\":\"Bearer\"}", Encoding.UTF8, "application/json"),
                });
            }

            // Key Vault's list-secrets response items expose only an "id" URL
            // (no "name" field), so without the internal tag the handler would
            // fall back to the (encoded, non-AWS) name embedded in the id.
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $"{{\"value\":[{{\"id\":\"https://example.vault.azure.net/secrets/{encodedName}\",\"attributes\":{{\"created\":1710000000}},\"tags\":{{\"aws2azure-secret-name\":\"{rawName}\"}}}}]}}",
                    Encoding.UTF8,
                    "application/json"),
            });
        }), ownsHandler: false);

        var module = CreateModule(http);
        var context = CreateContext("SecretsManager.ListSecrets", "{}");

        await module.HandleAsync(context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        var body = await ReadBodyAsync(context);
        using var document = JsonDocument.Parse(body);
        var name = document.RootElement.GetProperty("SecretList")[0].GetProperty("Name").GetString();
        Assert.Equal(rawName, name);
        Assert.NotEqual(encodedName, name);
    }

    [Fact]
    public async Task HandleAsync_ListSecrets_falls_back_to_id_derived_name_when_tag_is_absent()
    {
        // Pre-existing/manually-created Key Vault secrets whose names are
        // already Key-Vault-legal (i.e. equal to what would be their own AWS
        // name) never had a chance to carry the internal tag; GetSecretNameFromId
        // must remain a correct fallback for them.
        using var http = new AzureHttpClient(new ScriptedHandler((request, _) =>
        {
            if (request.RequestUri!.AbsoluteUri.Contains("oauth2/v2.0/token"))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"access_token\":\"token\",\"expires_in\":3600,\"token_type\":\"Bearer\"}", Encoding.UTF8, "application/json"),
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"value\":[{\"id\":\"https://example.vault.azure.net/secrets/plain-name\",\"attributes\":{\"created\":1710000000}}]}",
                    Encoding.UTF8,
                    "application/json"),
            });
        }), ownsHandler: false);

        var module = CreateModule(http);
        var context = CreateContext("SecretsManager.ListSecrets", "{}");

        await module.HandleAsync(context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        var body = await ReadBodyAsync(context);
        using var document = JsonDocument.Parse(body);
        Assert.Equal("plain-name", document.RootElement.GetProperty("SecretList")[0].GetProperty("Name").GetString());
    }

    [Fact]
    public async Task HandleAsync_CreateSecret_omits_raw_name_tag_when_name_exceeds_key_vault_tag_value_limit()
    {
        // AWS Secrets Manager names allow up to 512 characters, but Key Vault
        // tag values are capped at 256. A name in that gap cannot be
        // round-tripped via the internal tag; CreateSecret must still succeed
        // and simply skip writing the (would-be-truncated) tag.
        var rawName = "airflow/connections/" + new string('x', 480);
        Assert.True(rawName.Length > 256);
        string? putBody = null;

        using var http = new AzureHttpClient(new ScriptedHandler((request, _) =>
        {
            if (request.RequestUri!.AbsoluteUri.Contains("oauth2/v2.0/token"))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"access_token\":\"token\",\"expires_in\":3600,\"token_type\":\"Bearer\"}", Encoding.UTF8, "application/json"),
                });
            }

            if (request.Method == HttpMethod.Get)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            putBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"id\":\"https://example.vault.azure.net/secrets/whatever/versions/abc123\",\"attributes\":{\"created\":1710000000}}",
                    Encoding.UTF8,
                    "application/json"),
            });
        }), ownsHandler: false);

        var module = CreateModule(http);
        var context = CreateContext("SecretsManager.CreateSecret", $"{{\"Name\":\"{rawName}\",\"SecretString\":\"super-secret\"}}");

        await module.HandleAsync(context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.NotNull(putBody);
        using var putDocument = JsonDocument.Parse(putBody!);
        Assert.False(putDocument.RootElement.GetProperty("tags").TryGetProperty(KeyVaultSecretClient.AwsSecretNameTag, out _));

        var body = await ReadBodyAsync(context);
        using var responseDocument = JsonDocument.Parse(body);
        Assert.Equal(rawName, responseDocument.RootElement.GetProperty("Name").GetString());
    }

    private static SecretsManagerServiceModule CreateModule(AzureHttpClient http)
    {
        var config = new ProxyConfig
        {
            Credentials =
            {
                new CredentialEntry
                {
                    AwsAccessKeyId = "AKIA1",
                    AwsSecretAccessKey = "secret",
                    Azure = new AzureCredentials
                    {
                        KeyVault = new KeyVaultCredentials
                        {
                            VaultUrl = "https://example.vault.azure.net/",
                            TenantId = "tenant",
                            ClientId = "client",
                            ClientSecret = "secret",
                        },
                    },
                },
            },
        };

        return new SecretsManagerServiceModule(http, new StaticCredentialResolver(config), new CapabilityMatrix("secretsmanager", []));
    }

    private static DefaultHttpContext CreateContext(string target, string body)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = "/";
        context.Request.ContentType = "application/x-amz-json-1.0";
        context.Request.Headers["X-Amz-Target"] = target;
        context.Items["aws2azure.accessKeyId"] = "AKIA1";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static async Task<string> ReadBodyAsync(DefaultHttpContext context)
    {
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        return await reader.ReadToEndAsync();
    }

    private sealed class ScriptedHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => responder(request, cancellationToken);
    }
}
