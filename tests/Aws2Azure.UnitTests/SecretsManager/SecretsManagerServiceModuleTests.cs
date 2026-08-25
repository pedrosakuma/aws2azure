using System.Net;
using System.Net.Http;
using System.Linq;
using System.Text;
using System.Text.Json;
using Aws2Azure.Core.Azure;
using Aws2Azure.Core.Configuration;
using Aws2Azure.Core.Modules;
using Aws2Azure.Modules.SecretsManager;
using Aws2Azure.Modules.SecretsManager.WireProtocol;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.UnitTests.SecretsManager;

public sealed class SecretsManagerServiceModuleTests
{
    [Fact]
    public async Task HandleAsync_GetSecretValue_returns_aws_json_response()
    {
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
                    Content = new StringContent("{\"value\":[{\"id\":\"https://example.vault.azure.net/secrets/demo/versions/abc123\"}]}", Encoding.UTF8, "application/json"),
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"value\":\"super-secret\",\"id\":\"https://example.vault.azure.net/secrets/demo/versions/abc123\",\"contentType\":\"text/plain\",\"attributes\":{\"created\":1710000000}}", Encoding.UTF8, "application/json"),
            });
        }), ownsHandler: false);

        var module = CreateModule(http);
        var context = CreateContext("SecretsManager.GetSecretValue", "{\"SecretId\":\"demo\"}");

        await module.HandleAsync(context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        var body = await ReadBodyAsync(context);
        using var document = JsonDocument.Parse(body);
        Assert.Equal("demo", document.RootElement.GetProperty("Name").GetString());
        Assert.Equal("super-secret", document.RootElement.GetProperty("SecretString").GetString());
        Assert.Equal("abc123", document.RootElement.GetProperty("VersionId").GetString());
        Assert.Equal("arn:aws:secretsmanager:azure:keyvault:secret:demo", document.RootElement.GetProperty("ARN").GetString());
    }

    [Fact]
    public async Task HandleAsync_DescribeSecret_exposes_description_and_version_mapping()
    {
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
                Content = new StringContent("{\"id\":\"https://example.vault.azure.net/secrets/demo/versions/abc123\",\"name\":\"demo\",\"description\":\"account secret\",\"attributes\":{\"created\":1710000000,\"updated\":1710001000}}", Encoding.UTF8, "application/json"),
            });
        }), ownsHandler: false);

        var module = CreateModule(http);
        var context = CreateContext("SecretsManager.DescribeSecret", "{\"SecretId\":\"demo\"}");

        await module.HandleAsync(context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        var body = await ReadBodyAsync(context);
        using var document = JsonDocument.Parse(body);
        Assert.Equal("account secret", document.RootElement.GetProperty("Description").GetString());
        Assert.Equal(1710000000d, document.RootElement.GetProperty("CreatedDate").GetDouble());
        Assert.Equal(1710001000d, document.RootElement.GetProperty("LastChangedDate").GetDouble());
        Assert.Equal("AWSCURRENT", document.RootElement.GetProperty("VersionIdsToStages").GetProperty("abc123")[0].GetString());
    }

    [Fact]
    public async Task HandleAsync_GetSecretValue_uses_key_vault_version_endpoint_when_version_id_is_supplied()
    {
        string? requestedUri = null;
        using var http = new AzureHttpClient(new ScriptedHandler((request, _) =>
        {
            if (request.RequestUri!.AbsoluteUri.Contains("oauth2/v2.0/token"))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"access_token\":\"token\",\"expires_in\":3600,\"token_type\":\"Bearer\"}", Encoding.UTF8, "application/json"),
                });
            }

            requestedUri = request.RequestUri.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"value\":\"super-secret\",\"id\":\"https://example.vault.azure.net/secrets/demo/abc123\",\"contentType\":\"text/plain\",\"attributes\":{\"created\":1710000000}}", Encoding.UTF8, "application/json"),
            });
        }), ownsHandler: false);

        var module = CreateModule(http);
        var context = CreateContext("SecretsManager.GetSecretValue", "{\"SecretId\":\"demo\",\"VersionId\":\"abc123\"}");

        await module.HandleAsync(context);

        Assert.NotNull(requestedUri);
        Assert.Contains("/secrets/demo/abc123?api-version=7.4", requestedUri);
        Assert.DoesNotContain("/versions/", requestedUri);
    }

    [Fact]
    public async Task HandleAsync_GetSecretValue_resolves_version_stage_from_key_vault_version_tags()
    {
        var requestedUris = new List<string>();
        using var http = new AzureHttpClient(new ScriptedHandler((request, _) =>
        {
            if (request.RequestUri!.AbsoluteUri.Contains("oauth2/v2.0/token"))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"access_token\":\"token\",\"expires_in\":3600,\"token_type\":\"Bearer\"}", Encoding.UTF8, "application/json"),
                });
            }

            requestedUris.Add(request.RequestUri.ToString());
            if (request.RequestUri.AbsolutePath.EndsWith("/versions", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"value\":[{\"id\":\"https://example.vault.azure.net/secrets/demo/versions/pending123\",\"tags\":{\"aws2azure-version-stages\":\"AWSPENDING\"}},{\"id\":\"https://example.vault.azure.net/secrets/demo/versions/current123\",\"tags\":{\"aws2azure-version-stages\":\"AWSCURRENT\"}}]}", Encoding.UTF8, "application/json"),
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"value\":\"pending-secret\",\"id\":\"https://example.vault.azure.net/secrets/demo/versions/pending123\",\"contentType\":\"text/plain\",\"attributes\":{\"created\":1710000000},\"tags\":{\"aws2azure-version-stages\":\"AWSPENDING\"}}", Encoding.UTF8, "application/json"),
            });
        }), ownsHandler: false);

        var module = CreateModule(http);
        var context = CreateContext("SecretsManager.GetSecretValue", "{\"SecretId\":\"demo\",\"VersionStage\":\"AWSPENDING\"}");

        await module.HandleAsync(context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Contains(requestedUris, uri => uri.Contains("/secrets/demo/versions?api-version=7.4", StringComparison.Ordinal));
        Assert.Contains(requestedUris, uri => uri.Contains("/secrets/demo/pending123?api-version=7.4", StringComparison.Ordinal));
        var body = await ReadBodyAsync(context);
        using var document = JsonDocument.Parse(body);
        Assert.Equal("pending-secret", document.RootElement.GetProperty("SecretString").GetString());
        Assert.Equal("AWSPENDING", document.RootElement.GetProperty("VersionStages")[0].GetString());
    }

    [Fact]
    public async Task HandleAsync_GetSecretValue_prefers_explicit_current_stage_over_untagged_fallback()
    {
        var requestedUris = new List<string>();
        using var http = new AzureHttpClient(new ScriptedHandler((request, _) =>
        {
            if (request.RequestUri!.AbsoluteUri.Contains("oauth2/v2.0/token"))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"access_token\":\"token\",\"expires_in\":3600,\"token_type\":\"Bearer\"}", Encoding.UTF8, "application/json"),
                });
            }

            requestedUris.Add(request.RequestUri.ToString());
            if (request.RequestUri.AbsolutePath.EndsWith("/versions", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"value\":[{\"id\":\"https://example.vault.azure.net/secrets/demo/versions/legacy\",\"attributes\":{\"created\":1710000000}},{\"id\":\"https://example.vault.azure.net/secrets/demo/versions/current\",\"attributes\":{\"created\":1710000100},\"tags\":{\"aws2azure-version-stages\":\"AWSCURRENT\"}}]}", Encoding.UTF8, "application/json"),
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"value\":\"current-secret\",\"id\":\"https://example.vault.azure.net/secrets/demo/versions/current\",\"contentType\":\"text/plain\",\"attributes\":{\"created\":1710000100},\"tags\":{\"aws2azure-version-stages\":\"AWSCURRENT\"}}", Encoding.UTF8, "application/json"),
            });
        }), ownsHandler: false);

        var module = CreateModule(http);
        var context = CreateContext("SecretsManager.GetSecretValue", "{\"SecretId\":\"demo\"}");

        await module.HandleAsync(context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Contains(requestedUris, uri => uri.Contains("/secrets/demo/current?api-version=7.4", StringComparison.Ordinal));
        var body = await ReadBodyAsync(context);
        using var document = JsonDocument.Parse(body);
        Assert.Equal("current-secret", document.RootElement.GetProperty("SecretString").GetString());
    }

    [Fact]
    public void GetTags_strips_reserved_internal_tags_from_aws_tag_array()
    {
        using var document = JsonDocument.Parse("{\"Tags\":[{\"Key\":\"env\",\"Value\":\"dev\"},{\"Key\":\"aws2azure-client-request-token\",\"Value\":\"spoofed\"}]}");

        var tags = KeyVaultSecretClient.GetTags(document.RootElement);

        Assert.True(tags.ContainsKey("env"));
        Assert.False(tags.ContainsKey("aws2azure-client-request-token"));
    }

    [Fact]
    public async Task HandleAsync_GetSecretValue_accepts_client_request_token_as_version_id()
    {
        var requestedUris = new List<string>();
        using var http = new AzureHttpClient(new ScriptedHandler((request, _) =>
        {
            if (request.RequestUri!.AbsoluteUri.Contains("oauth2/v2.0/token"))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"access_token\":\"token\",\"expires_in\":3600,\"token_type\":\"Bearer\"}", Encoding.UTF8, "application/json"),
                });
            }

            requestedUris.Add(request.RequestUri.ToString());
            if (request.RequestUri.AbsolutePath.EndsWith("/client-token-1", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            if (request.RequestUri.AbsolutePath.EndsWith("/versions", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"value\":[{\"id\":\"https://example.vault.azure.net/secrets/demo/versions/real-kv-version\",\"tags\":{\"aws2azure-client-request-token\":\"client-token-1\",\"aws2azure-version-stages\":\"AWSCURRENT\"}}]}", Encoding.UTF8, "application/json"),
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"value\":\"token-secret\",\"id\":\"https://example.vault.azure.net/secrets/demo/versions/real-kv-version\",\"contentType\":\"text/plain\",\"attributes\":{\"created\":1710000000},\"tags\":{\"aws2azure-client-request-token\":\"client-token-1\",\"aws2azure-version-stages\":\"AWSCURRENT\"}}", Encoding.UTF8, "application/json"),
            });
        }), ownsHandler: false);

        var module = CreateModule(http);
        var context = CreateContext("SecretsManager.GetSecretValue", "{\"SecretId\":\"demo\",\"VersionId\":\"client-token-1\"}");

        await module.HandleAsync(context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Contains(requestedUris, uri => uri.Contains("/secrets/demo/client-token-1?api-version=7.4", StringComparison.Ordinal));
        Assert.Contains(requestedUris, uri => uri.Contains("/secrets/demo/real-kv-version?api-version=7.4", StringComparison.Ordinal));
        var body = await ReadBodyAsync(context);
        using var document = JsonDocument.Parse(body);
        Assert.Equal("client-token-1", document.RootElement.GetProperty("VersionId").GetString());
    }

    [Fact]
    public async Task HandleAsync_GetSecretValue_rejects_mismatched_version_id_and_stage()
    {
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
                Content = new StringContent("{\"value\":\"current-secret\",\"id\":\"https://example.vault.azure.net/secrets/demo/versions/current123\",\"contentType\":\"text/plain\",\"attributes\":{\"created\":1710000000},\"tags\":{\"aws2azure-version-stages\":\"AWSCURRENT\"}}", Encoding.UTF8, "application/json"),
            });
        }), ownsHandler: false);

        var module = CreateModule(http);
        var context = CreateContext("SecretsManager.GetSecretValue", "{\"SecretId\":\"demo\",\"VersionId\":\"current123\",\"VersionStage\":\"AWSPENDING\"}");

        await module.HandleAsync(context);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Equal("application/x-amz-json-1.1", context.Response.ContentType);
        var body = await ReadBodyAsync(context);
        Assert.Contains("InvalidRequestException", body);
    }

    [Fact]
    public async Task HandleAsync_ListSecrets_returns_tags_without_fabricated_versions()
    {
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
                Content = new StringContent("{\"value\":[{\"id\":\"https://example.vault.azure.net/secrets/demo\",\"name\":\"demo\",\"description\":\"account secret\",\"tags\":{\"env\":\"dev\"},\"attributes\":{\"created\":1710000000,\"updated\":1710001000}}],\"nextLink\":\"https://example.vault.azure.net/secrets?api-version=7.4&$skiptoken=abc123&maxresults=25\"}", Encoding.UTF8, "application/json"),
            });
        }), ownsHandler: false);

        var module = CreateModule(http);
        var context = CreateContext("SecretsManager.ListSecrets", string.Empty);

        await module.HandleAsync(context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        var body = await ReadBodyAsync(context);
        using var document = JsonDocument.Parse(body);
        var secret = document.RootElement.GetProperty("SecretList")[0];
        Assert.Equal("demo", secret.GetProperty("Name").GetString());
        Assert.Equal("account secret", secret.GetProperty("Description").GetString());
        Assert.Equal(1710000000d, secret.GetProperty("CreatedDate").GetDouble());
        Assert.Equal(1710001000d, secret.GetProperty("LastChangedDate").GetDouble());
        var tag = secret.GetProperty("Tags")[0];
        Assert.Equal("env", tag.GetProperty("Key").GetString());
        Assert.Equal("dev", tag.GetProperty("Value").GetString());
        Assert.Equal("abc123", document.RootElement.GetProperty("NextToken").GetString());
        Assert.False(secret.TryGetProperty("VersionIdsToStages", out _));
    }

    [Fact]
    public async Task HandleAsync_ListSecrets_preserves_forbidden_without_production_retry()
    {
        var listAttempts = 0;
        using var http = new AzureHttpClient(new ScriptedHandler((request, _) =>
        {
            if (request.RequestUri!.AbsoluteUri.Contains("oauth2/v2.0/token"))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "{\"access_token\":\"token\",\"expires_in\":3600,\"token_type\":\"Bearer\"}",
                        Encoding.UTF8,
                        "application/json"),
                });
            }

            listAttempts++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent(
                    "{\"error\":{\"code\":\"Forbidden\",\"innererror\":{\"code\":\"ForbiddenByRbac\"}}}",
                    Encoding.UTF8,
                    "application/json"),
            });
        }), ownsHandler: false);

        var module = CreateModule(http);
        var context = CreateContext("SecretsManager.ListSecrets", string.Empty);

        await module.HandleAsync(context);

        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        Assert.Contains("AccessDeniedException", await ReadBodyAsync(context));
        Assert.Equal(1, listAttempts);
    }

    [Fact]
    public async Task HandleAsync_ListSecrets_forwards_next_token_as_key_vault_skiptoken()
    {
        string? requestedUri = null;
        using var http = new AzureHttpClient(new ScriptedHandler((request, _) =>
        {
            if (request.RequestUri!.AbsoluteUri.Contains("oauth2/v2.0/token"))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"access_token\":\"token\",\"expires_in\":3600,\"token_type\":\"Bearer\"}", Encoding.UTF8, "application/json"),
                });
            }

            requestedUri = request.RequestUri.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"value\":[]}", Encoding.UTF8, "application/json"),
            });
        }), ownsHandler: false);

        var module = CreateModule(http);
        var context = CreateContext("SecretsManager.ListSecrets", "{\"NextToken\":\"abc123\",\"MaxResults\":10}");

        await module.HandleAsync(context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.NotNull(requestedUri);
        Assert.Contains("$skiptoken=abc123", requestedUri);
        Assert.Contains("maxresults=10", requestedUri);
    }

    [Fact]
    public async Task HandleAsync_ListSecrets_resolves_name_from_id_and_uses_aws_json_1_1()
    {
        using var http = new AzureHttpClient(new ScriptedHandler((request, _) =>
        {
            if (request.RequestUri!.AbsoluteUri.Contains("oauth2/v2.0/token"))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"access_token\":\"token\",\"expires_in\":3600,\"token_type\":\"Bearer\"}", Encoding.UTF8, "application/json"),
                });
            }

            // Real Key Vault list items expose only an id URL (no name field).
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"value\":[{\"id\":\"https://example.vault.azure.net/secrets/prod-db\",\"attributes\":{\"created\":1710000000,\"updated\":1710001000}}]}", Encoding.UTF8, "application/json"),
            });
        }), ownsHandler: false);

        var module = CreateModule(http);
        var context = CreateContext("SecretsManager.ListSecrets", string.Empty);

        await module.HandleAsync(context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal("application/x-amz-json-1.1", context.Response.ContentType);
        var body = await ReadBodyAsync(context);
        using var document = JsonDocument.Parse(body);
        var secret = document.RootElement.GetProperty("SecretList")[0];
        Assert.Equal("prod-db", secret.GetProperty("Name").GetString());
        Assert.Equal("arn:aws:secretsmanager:azure:keyvault:secret:prod-db", secret.GetProperty("ARN").GetString());
        Assert.Equal(JsonValueKind.Array, secret.GetProperty("Tags").ValueKind);
        Assert.Equal(JsonValueKind.Number, secret.GetProperty("CreatedDate").ValueKind);
    }

    [Fact]
    public async Task HandleAsync_CreateSecret_returns_aws_shape_for_key_vault_secret()
    {
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

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"id\":\"https://example.vault.azure.net/secrets/demo/versions/abc123\",\"attributes\":{\"created\":1710000000}}", Encoding.UTF8, "application/json"),
            });
        }), ownsHandler: false);

        var module = CreateModule(http);
        var context = CreateContext("SecretsManager.CreateSecret", "{\"Name\":\"demo\",\"SecretString\":\"super-secret\"}");

        await module.HandleAsync(context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        var body = await ReadBodyAsync(context);
        using var document = JsonDocument.Parse(body);
        Assert.Equal("demo", document.RootElement.GetProperty("Name").GetString());
        Assert.Equal("abc123", document.RootElement.GetProperty("VersionId").GetString());
        Assert.Equal("arn:aws:secretsmanager:azure:keyvault:secret:demo", document.RootElement.GetProperty("ARN").GetString());
    }

    [Fact]
    public async Task HandleAsync_CreateSecret_replays_client_request_token_without_new_put()
    {
        var expectedHash = KeyVaultSecretClient.GetPayloadSha256("super-secret", null);
        var putCount = 0;
        using var http = new AzureHttpClient(new ScriptedHandler((request, _) =>
        {
            if (request.RequestUri!.AbsoluteUri.Contains("oauth2/v2.0/token"))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"access_token\":\"token\",\"expires_in\":3600,\"token_type\":\"Bearer\"}", Encoding.UTF8, "application/json"),
                });
            }

            if (request.Method == HttpMethod.Put)
            {
                putCount++;
            }

            if (request.RequestUri.AbsolutePath.EndsWith("/versions", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent($"{{\"value\":[{{\"id\":\"https://example.vault.azure.net/secrets/demo/versions/real-version\",\"attributes\":{{\"created\":1710000000}},\"tags\":{{\"aws2azure-client-request-token\":\"create-token\",\"aws2azure-payload-sha256\":\"{expectedHash}\",\"aws2azure-version-stages\":\"AWSCURRENT\",\"aws2azure-publication-state\":\"published\"}}}}]}}", Encoding.UTF8, "application/json"),
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"id\":\"https://example.vault.azure.net/secrets/demo\",\"attributes\":{\"created\":1710000000}}", Encoding.UTF8, "application/json"),
            });
        }), ownsHandler: false);

        var module = CreateModule(http);
        var context = CreateContext("SecretsManager.CreateSecret", "{\"Name\":\"demo\",\"SecretString\":\"super-secret\",\"ClientRequestToken\":\"create-token\"}");

        await module.HandleAsync(context);

        Assert.Equal(0, putCount);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        var body = await ReadBodyAsync(context);
        using var document = JsonDocument.Parse(body);
        Assert.Equal("create-token", document.RootElement.GetProperty("VersionId").GetString());
        Assert.Equal("AWSCURRENT", document.RootElement.GetProperty("VersionStages")[0].GetString());
    }

    [Fact]
    public async Task HandleAsync_CreateSecret_replays_client_request_token_after_initial_put_conflict()
    {
        var expectedHash = KeyVaultSecretClient.GetPayloadSha256("super-secret", null);
        var listAttempts = 0;
        using var http = new AzureHttpClient(new ScriptedHandler((request, _) =>
        {
            if (request.RequestUri!.AbsoluteUri.Contains("oauth2/v2.0/token"))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"access_token\":\"token\",\"expires_in\":3600,\"token_type\":\"Bearer\"}", Encoding.UTF8, "application/json"),
                });
            }

            if (request.Method == HttpMethod.Get && request.RequestUri.AbsolutePath == "/secrets/demo")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            if (request.Method == HttpMethod.Put)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict));
            }

            if (request.RequestUri.AbsolutePath.EndsWith("/versions", StringComparison.Ordinal))
            {
                listAttempts++;
                var body = listAttempts == 1
                    ? "{\"value\":[]}"
                    : $"{{\"value\":[{{\"id\":\"https://example.vault.azure.net/secrets/demo/versions/real-version\",\"attributes\":{{\"created\":1710000000}},\"tags\":{{\"aws2azure-client-request-token\":\"create-token\",\"aws2azure-payload-sha256\":\"{expectedHash}\",\"aws2azure-version-stages\":\"AWSCURRENT\",\"aws2azure-publication-state\":\"published\"}}}}]}}";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }), ownsHandler: false);

        var module = CreateModule(http);
        var context = CreateContext("SecretsManager.CreateSecret", "{\"Name\":\"demo\",\"SecretString\":\"super-secret\",\"ClientRequestToken\":\"create-token\"}");

        await module.HandleAsync(context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.True(listAttempts >= 2);
        var body = await ReadBodyAsync(context);
        using var document = JsonDocument.Parse(body);
        Assert.Equal("create-token", document.RootElement.GetProperty("VersionId").GetString());
    }

    [Fact]
    public async Task HandleAsync_CreateSecret_rejects_reused_client_request_token_with_different_payload()
    {
        var secretCreated = false;
        var firstHash = KeyVaultSecretClient.GetPayloadSha256("first", null);
        using var http = new AzureHttpClient(new ScriptedHandler((request, _) =>
        {
            if (request.RequestUri!.AbsoluteUri.Contains("oauth2/v2.0/token"))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"access_token\":\"token\",\"expires_in\":3600,\"token_type\":\"Bearer\"}", Encoding.UTF8, "application/json"),
                });
            }

            if (request.Method == HttpMethod.Get && request.RequestUri.AbsolutePath == "/secrets/demo")
            {
                return Task.FromResult(new HttpResponseMessage(secretCreated ? HttpStatusCode.OK : HttpStatusCode.NotFound)
                {
                    Content = secretCreated
                        ? new StringContent("{\"id\":\"https://example.vault.azure.net/secrets/demo/version-1\",\"attributes\":{\"created\":1710000000}}", Encoding.UTF8, "application/json")
                        : null,
                });
            }

            if (request.Method == HttpMethod.Put && request.RequestUri.AbsolutePath == "/secrets/demo")
            {
                secretCreated = true;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"id\":\"https://example.vault.azure.net/secrets/demo/version-1\",\"attributes\":{\"created\":1710000000}}", Encoding.UTF8, "application/json"),
                });
            }

            if (request.Method == HttpMethod.Get && request.RequestUri.AbsolutePath == "/secrets/demo/versions")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent($"{{\"value\":[{{\"id\":\"https://example.vault.azure.net/secrets/demo/versions/version-1\",\"attributes\":{{\"created\":1710000000}},\"tags\":{{\"aws2azure-client-request-token\":\"create-token\",\"aws2azure-payload-sha256\":\"{firstHash}\",\"aws2azure-version-stages\":\"AWSCURRENT\",\"aws2azure-publication-state\":\"published\"}}}}]}}", Encoding.UTF8, "application/json"),
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }), ownsHandler: false);

        var module = CreateModule(http);
        var firstContext = CreateContext("SecretsManager.CreateSecret", "{\"Name\":\"demo\",\"SecretString\":\"first\",\"ClientRequestToken\":\"create-token\"}");
        await module.HandleAsync(firstContext);
        Assert.Equal(StatusCodes.Status200OK, firstContext.Response.StatusCode);

        var secondContext = CreateContext("SecretsManager.CreateSecret", "{\"Name\":\"demo\",\"SecretString\":\"second\",\"ClientRequestToken\":\"create-token\"}");
        await module.HandleAsync(secondContext);

        Assert.Equal(StatusCodes.Status400BadRequest, secondContext.Response.StatusCode);
        var body = await ReadBodyAsync(secondContext);
        Assert.Contains("ResourceExistsException", body, StringComparison.Ordinal);
        Assert.Contains("ClientRequestToken is already associated with a different secret value.", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HandleAsync_CreateSecret_only_sends_tag_payload_to_key_vault()
    {
        string? requestBody = null;
        using var http = new AzureHttpClient(new ScriptedHandler(async (request, _) =>
        {
            if (request.RequestUri!.AbsoluteUri.Contains("oauth2/v2.0/token"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"access_token\":\"token\",\"expires_in\":3600,\"token_type\":\"Bearer\"}", Encoding.UTF8, "application/json"),
                };
            }

            if (request.Method == HttpMethod.Get)
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            requestBody = await request.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"id\":\"https://example.vault.azure.net/secrets/demo/versions/abc123\",\"attributes\":{\"created\":1710000000}}", Encoding.UTF8, "application/json"),
            };
        }), ownsHandler: false);

        var module = CreateModule(http);
        var context = CreateContext("SecretsManager.CreateSecret", "{\"Name\":\"demo\",\"SecretString\":\"super-secret\",\"Tags\":{\"env\":\"dev\"}}");

        await module.HandleAsync(context);

        Assert.NotNull(requestBody);
        Assert.Contains("\"env\":\"dev\"", requestBody);
        Assert.DoesNotContain("\"SecretString\":\"super-secret\"", requestBody);
        Assert.DoesNotContain("\"Name\":\"demo\"", requestBody);
    }

    [Fact]
    public async Task HandleAsync_CreateSecret_returns_conflict_when_secret_already_exists()
    {
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
                Content = new StringContent("{\"id\":\"https://example.vault.azure.net/secrets/demo\"}", Encoding.UTF8, "application/json"),
            });
        }), ownsHandler: false);

        var module = CreateModule(http);
        var context = CreateContext("SecretsManager.CreateSecret", "{\"Name\":\"demo\",\"SecretString\":\"super-secret\"}");

        await module.HandleAsync(context);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        var body = await ReadBodyAsync(context);
        using var document = JsonDocument.Parse(body);
        Assert.Equal("ResourceExistsException", document.RootElement.GetProperty("__type").GetString());
    }

    [Theory]
    [InlineData("SecretsManager.CreateSecret", "{\"SecretString\":\"value\"}")]
    [InlineData("SecretsManager.GetSecretValue", "{}")]
    [InlineData("SecretsManager.UpdateSecret", "{\"SecretString\":\"value\"}")]
    [InlineData("SecretsManager.DeleteSecret", "{}")]
    [InlineData("SecretsManager.DescribeSecret", "{}")]
    public async Task HandleAsync_returns_invalid_parameter_when_secret_identifier_is_missing(string target, string requestJson)
    {
        // CreateSecret keys off "Name" while the other four operations key off
        // "SecretId"; both paths route through KeyVaultSecretClient.NormalizeSecretName,
        // which throws ArgumentException before any Key Vault HTTP call is issued. This
        // covers issue #708's Tier-3 happy-path matrix precondition that every case's
        // required identifier fields are actually validated, not merely assumed.
        using var http = new AzureHttpClient(new ScriptedHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("Key Vault should not be called when the identifier is missing.", Encoding.UTF8, "text/plain"),
            })), ownsHandler: false);

        var module = CreateModule(http);
        var context = CreateContext(target, requestJson);

        await module.HandleAsync(context);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        var body = await ReadBodyAsync(context);
        using var document = JsonDocument.Parse(body);
        Assert.Equal("InvalidParameterException", document.RootElement.GetProperty("__type").GetString());
    }

    [Fact]
    public async Task HandleAsync_CreateSecret_returns_invalid_parameter_for_malformed_base64()
    {
        using var http = new AzureHttpClient(new ScriptedHandler((request, _) =>
        {
            if (request.RequestUri!.AbsoluteUri.Contains("oauth2/v2.0/token"))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"access_token\":\"token\",\"expires_in\":3600,\"token_type\":\"Bearer\"}", Encoding.UTF8, "application/json"),
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }), ownsHandler: false);

        var module = CreateModule(http);
        var context = CreateContext("SecretsManager.CreateSecret", "{\"Name\":\"demo\",\"SecretBinary\":\"%%%\"}");

        await module.HandleAsync(context);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        var body = await ReadBodyAsync(context);
        using var document = JsonDocument.Parse(body);
        Assert.Equal("InvalidParameterException", document.RootElement.GetProperty("__type").GetString());
    }

    [Fact]
    public async Task HandleAsync_UpdateSecret_returns_aws_shape_for_rewritten_secret()
    {
        using var http = new AzureHttpClient(new InMemoryKeyVaultHandler(
            new SecretVersionState("base", "old-secret", 1_710_000_000, new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["aws2azure-version-stages"] = "AWSCURRENT",
            })), ownsHandler: false);

        var module = CreateModule(http);
        var context = CreateContext("SecretsManager.UpdateSecret", "{\"SecretId\":\"demo\",\"SecretString\":\"new-secret\"}");

        await module.HandleAsync(context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        var body = await ReadBodyAsync(context);
        using var document = JsonDocument.Parse(body);
        Assert.Equal("new-version", document.RootElement.GetProperty("VersionId").GetString());
        Assert.Equal("demo", document.RootElement.GetProperty("Name").GetString());
    }

    [Fact]
    public async Task HandleAsync_UpdateSecret_demotes_prior_current_so_awscurrent_resolves_new_value()
    {
        // Prior AWSCURRENT shares a 1-second created stamp with the update; the
        // resolver must still land on the new version because UpdateSecret demotes
        // the old AWSCURRENT (regression #484).
        using var http = new AzureHttpClient(new InMemoryKeyVaultHandler(
            new SecretVersionState("old-current", "old-secret", 1_710_000_200, new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["aws2azure-version-stages"] = "AWSCURRENT",
            })), ownsHandler: false);

        var module = CreateModule(http);
        var updateContext = CreateContext("SecretsManager.UpdateSecret", "{\"SecretId\":\"demo\",\"SecretString\":\"new-secret\"}");
        await module.HandleAsync(updateContext);
        Assert.Equal(StatusCodes.Status200OK, updateContext.Response.StatusCode);

        var currentContext = CreateContext("SecretsManager.GetSecretValue", "{\"SecretId\":\"demo\",\"VersionStage\":\"AWSCURRENT\"}");
        await module.HandleAsync(currentContext);
        Assert.Equal(StatusCodes.Status200OK, currentContext.Response.StatusCode);
        var currentBody = await ReadBodyAsync(currentContext);
        using var currentDocument = JsonDocument.Parse(currentBody);
        Assert.Equal("new-version", currentDocument.RootElement.GetProperty("VersionId").GetString());
        Assert.Equal("new-secret", currentDocument.RootElement.GetProperty("SecretString").GetString());
    }

    [Fact]
    public async Task HandleAsync_UpdateSecret_returns_not_found_when_secret_is_absent()
    {
        var putAttempted = false;
        using var http = new AzureHttpClient(new ScriptedHandler((request, _) =>
        {
            if (request.RequestUri!.AbsoluteUri.Contains("oauth2/v2.0/token"))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"access_token\":\"token\",\"expires_in\":3600,\"token_type\":\"Bearer\"}", Encoding.UTF8, "application/json"),
                });
            }

            if (request.Method == HttpMethod.Put)
            {
                putAttempted = true;
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }), ownsHandler: false);

        var module = CreateModule(http);
        var context = CreateContext("SecretsManager.UpdateSecret", "{\"SecretId\":\"missing\",\"SecretString\":\"new-secret\"}");

        await module.HandleAsync(context);

        Assert.False(putAttempted);
        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
        var body = await ReadBodyAsync(context);
        using var document = JsonDocument.Parse(body);
        Assert.Equal("ResourceNotFoundException", document.RootElement.GetProperty("__type").GetString());
    }

    [Fact]
    public async Task HandleAsync_UpdateSecret_rejects_metadata_only_description_updates_as_unsupported()
    {
        using var http = new AzureHttpClient(new ScriptedHandler(async (request, _) =>
        {
            if (request.RequestUri!.AbsoluteUri.Contains("oauth2/v2.0/token"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"access_token\":\"token\",\"expires_in\":3600,\"token_type\":\"Bearer\"}", Encoding.UTF8, "application/json"),
                };
            }

            if (request.Method == HttpMethod.Get)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"id\":\"https://example.vault.azure.net/secrets/demo\",\"tags\":{\"env\":\"dev\"},\"attributes\":{\"created\":1710000000}}", Encoding.UTF8, "application/json"),
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"id\":\"https://example.vault.azure.net/secrets/demo\"}", Encoding.UTF8, "application/json"),
            };
        }), ownsHandler: false);

        var module = CreateModule(http);
        var context = CreateContext("SecretsManager.UpdateSecret", "{\"SecretId\":\"demo\",\"Description\":\"rotated creds\"}");

        await module.HandleAsync(context);

        Assert.Equal(StatusCodes.Status501NotImplemented, context.Response.StatusCode);
        var body = await ReadBodyAsync(context);
        Assert.Contains("Metadata-only UpdateSecret requests are not supported", body);
    }

    [Fact]
    public async Task HandleAsync_DeleteSecret_uses_key_vault_delete_schedule_in_response()
    {
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
                Content = new StringContent("{\"deletedDate\":1710000000,\"scheduledPurgeDate\":1710604800}", Encoding.UTF8, "application/json"),
            });
        }), ownsHandler: false);

        var module = CreateModule(http);
        var context = CreateContext("SecretsManager.DeleteSecret", "{\"SecretId\":\"demo\"}");

        await module.HandleAsync(context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        var body = await ReadBodyAsync(context);
        using var document = JsonDocument.Parse(body);
        Assert.Equal("demo", document.RootElement.GetProperty("Name").GetString());
        Assert.Equal("arn:aws:secretsmanager:azure:keyvault:secret:demo", document.RootElement.GetProperty("ARN").GetString());
        Assert.Equal(1710604800d, document.RootElement.GetProperty("DeletionDate").GetDouble());
        Assert.False(document.RootElement.TryGetProperty("DeletedDate", out _));
        Assert.False(document.RootElement.TryGetProperty("VersionId", out _));
    }

    [Fact]
    public async Task HandleAsync_DeleteSecret_accepts_recovery_window_but_returns_key_vault_schedule()
    {
        string? requestedUri = null;
        using var http = new AzureHttpClient(new ScriptedHandler((request, _) =>
        {
            if (request.RequestUri!.AbsoluteUri.Contains("oauth2/v2.0/token"))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"access_token\":\"token\",\"expires_in\":3600,\"token_type\":\"Bearer\"}", Encoding.UTF8, "application/json"),
                });
            }

            requestedUri = request.RequestUri.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"deletedDate\":1710000000,\"scheduledPurgeDate\":1710604800}", Encoding.UTF8, "application/json"),
            });
        }), ownsHandler: false);

        var module = CreateModule(http);
        var context = CreateContext("SecretsManager.DeleteSecret", "{\"SecretId\":\"demo\",\"RecoveryWindowInDays\":30}");

        await module.HandleAsync(context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.NotNull(requestedUri);
        Assert.Contains("/secrets/demo?api-version=7.4", requestedUri, StringComparison.Ordinal);
        var body = await ReadBodyAsync(context);
        using var document = JsonDocument.Parse(body);
        Assert.Equal(1710604800d, document.RootElement.GetProperty("DeletionDate").GetDouble());
    }

    [Fact]
    public async Task HandleAsync_DeleteSecret_force_delete_purges_deleted_secret()
    {
        var requestUris = new List<string>();
        using var http = new AzureHttpClient(new ScriptedHandler((request, _) =>
        {
            if (request.RequestUri!.AbsoluteUri.Contains("oauth2/v2.0/token"))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"access_token\":\"token\",\"expires_in\":3600,\"token_type\":\"Bearer\"}", Encoding.UTF8, "application/json"),
                });
            }

            requestUris.Add(request.RequestUri.ToString());
            if (request.RequestUri.AbsolutePath == "/secrets/demo")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"deletedDate\":1710000000,\"scheduledPurgeDate\":1710604800}", Encoding.UTF8, "application/json"),
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        }), ownsHandler: false);

        var module = CreateModule(http);
        var context = CreateContext("SecretsManager.DeleteSecret", "{\"SecretId\":\"demo\",\"ForceDeleteWithoutRecovery\":true}");

        await module.HandleAsync(context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Contains(requestUris, uri => uri.Contains("/secrets/demo?api-version=7.4", StringComparison.Ordinal));
        Assert.Contains(requestUris, uri => uri.Contains("/deletedsecrets/demo?api-version=7.4", StringComparison.Ordinal));
        var body = await ReadBodyAsync(context);
        using var document = JsonDocument.Parse(body);
        Assert.Equal(1710000000d, document.RootElement.GetProperty("DeletionDate").GetDouble());
    }

    [Fact]
    public async Task HandleAsync_DeleteSecret_force_delete_retries_until_deleted_secret_becomes_purgeable()
    {
        var purgeAttempts = 0;
        using var http = new AzureHttpClient(new ScriptedHandler((request, _) =>
        {
            if (request.RequestUri!.AbsoluteUri.Contains("oauth2/v2.0/token"))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"access_token\":\"token\",\"expires_in\":3600,\"token_type\":\"Bearer\"}", Encoding.UTF8, "application/json"),
                });
            }

            if (request.RequestUri.AbsolutePath == "/secrets/demo")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"deletedDate\":1710000000,\"scheduledPurgeDate\":1710604800}", Encoding.UTF8, "application/json"),
                });
            }

            if (request.RequestUri.AbsolutePath == "/deletedsecrets/demo" && request.Method == HttpMethod.Delete)
            {
                purgeAttempts++;
                return Task.FromResult(new HttpResponseMessage(purgeAttempts <= 8 ? HttpStatusCode.Conflict : HttpStatusCode.NoContent));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"deletedDate\":1710000000,\"scheduledPurgeDate\":1710604800,\"attributes\":{\"recoveryLevel\":\"Recoverable+Purgeable\"}}", Encoding.UTF8, "application/json"),
            });
        }), ownsHandler: false);

        var module = CreateModule(http);
        var context = CreateContext("SecretsManager.DeleteSecret", "{\"SecretId\":\"demo\",\"ForceDeleteWithoutRecovery\":true}");

        await module.HandleAsync(context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.True(purgeAttempts > 8);
    }

    [Fact]
    public async Task HandleAsync_DeleteSecret_force_delete_succeeds_when_secret_is_already_missing()
    {
        var requestUris = new List<string>();
        using var http = new AzureHttpClient(new ScriptedHandler((request, _) =>
        {
            if (request.RequestUri!.AbsoluteUri.Contains("oauth2/v2.0/token"))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"access_token\":\"token\",\"expires_in\":3600,\"token_type\":\"Bearer\"}", Encoding.UTF8, "application/json"),
                });
            }

            requestUris.Add(request.RequestUri.ToString());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }), ownsHandler: false);

        var module = CreateModule(http);
        var context = CreateContext("SecretsManager.DeleteSecret", "{\"SecretId\":\"demo\",\"ForceDeleteWithoutRecovery\":true}");

        await module.HandleAsync(context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Contains(requestUris, uri => uri.Contains("/secrets/demo?api-version=7.4", StringComparison.Ordinal));
        Assert.Contains(requestUris, uri => uri.Contains("/deletedsecrets/demo?api-version=7.4", StringComparison.Ordinal));
    }

    [Fact]
    public async Task HandleAsync_DeleteSecret_force_delete_treats_missing_deleted_secret_as_success_after_delete()
    {
        using var http = new AzureHttpClient(new ScriptedHandler((request, _) =>
        {
            if (request.RequestUri!.AbsoluteUri.Contains("oauth2/v2.0/token"))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"access_token\":\"token\",\"expires_in\":3600,\"token_type\":\"Bearer\"}", Encoding.UTF8, "application/json"),
                });
            }

            if (request.RequestUri.AbsolutePath == "/secrets/demo")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"deletedDate\":1710000000,\"scheduledPurgeDate\":1710604800}", Encoding.UTF8, "application/json"),
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }), ownsHandler: false);

        var module = CreateModule(http);
        var context = CreateContext("SecretsManager.DeleteSecret", "{\"SecretId\":\"demo\",\"ForceDeleteWithoutRecovery\":true}");

        await module.HandleAsync(context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    [Theory]
    [InlineData("{\"SecretId\":\"demo\",\"RecoveryWindowInDays\":7,\"ForceDeleteWithoutRecovery\":true}", "mutually exclusive")]
    public async Task HandleAsync_DeleteSecret_rejects_mutually_exclusive_recovery_options(string requestJson, string expectedMessage)
    {
        var backendCalled = false;
        using var http = new AzureHttpClient(new ScriptedHandler((_, _) =>
        {
            backendCalled = true;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }), ownsHandler: false);

        var module = CreateModule(http);
        var context = CreateContext("SecretsManager.DeleteSecret", requestJson);

        await module.HandleAsync(context);

        Assert.False(backendCalled);
        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        var body = await ReadBodyAsync(context);
        Assert.Contains(expectedMessage, body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(31)]
    public async Task HandleAsync_DeleteSecret_rejects_out_of_range_recovery_windows(int recoveryWindowInDays)
    {
        var backendCalled = false;
        using var http = new AzureHttpClient(new ScriptedHandler((_, _) =>
        {
            backendCalled = true;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }), ownsHandler: false);

        var module = CreateModule(http);
        var context = CreateContext("SecretsManager.DeleteSecret", $"{{\"SecretId\":\"demo\",\"RecoveryWindowInDays\":{recoveryWindowInDays}}}");

        await module.HandleAsync(context);

        Assert.False(backendCalled);
        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        var body = await ReadBodyAsync(context);
        Assert.Contains("between 7 and 30", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HandleAsync_DeleteSecret_rejects_non_int32_recovery_window_values()
    {
        var backendCalled = false;
        using var http = new AzureHttpClient(new ScriptedHandler((_, _) =>
        {
            backendCalled = true;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }), ownsHandler: false);

        var module = CreateModule(http);
        var context = CreateContext("SecretsManager.DeleteSecret", "{\"SecretId\":\"demo\",\"RecoveryWindowInDays\":2147483648}");

        await module.HandleAsync(context);

        Assert.False(backendCalled);
        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        var body = await ReadBodyAsync(context);
        Assert.Contains("must be an integer between 7 and 30", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HandleAsync_DeleteSecret_rejects_non_boolean_force_delete_values()
    {
        var backendCalled = false;
        using var http = new AzureHttpClient(new ScriptedHandler((_, _) =>
        {
            backendCalled = true;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }), ownsHandler: false);

        var module = CreateModule(http);
        var context = CreateContext("SecretsManager.DeleteSecret", "{\"SecretId\":\"demo\",\"ForceDeleteWithoutRecovery\":\"true\"}");

        await module.HandleAsync(context);

        Assert.False(backendCalled);
        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        var body = await ReadBodyAsync(context);
        Assert.Contains("must be a boolean", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HandleAsync_DeleteSecret_force_delete_does_not_require_deleted_secret_get_permission()
    {
        var purgeAttempts = 0;
        using var http = new AzureHttpClient(new ScriptedHandler((request, _) =>
        {
            if (request.RequestUri!.AbsoluteUri.Contains("oauth2/v2.0/token"))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"access_token\":\"token\",\"expires_in\":3600,\"token_type\":\"Bearer\"}", Encoding.UTF8, "application/json"),
                });
            }

            if (request.RequestUri.AbsolutePath == "/secrets/demo")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"deletedDate\":1710000000,\"scheduledPurgeDate\":1710604800}", Encoding.UTF8, "application/json"),
                });
            }

            if (request.RequestUri.AbsolutePath == "/deletedsecrets/demo" && request.Method == HttpMethod.Get)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
            }

            purgeAttempts++;
            return Task.FromResult(new HttpResponseMessage(purgeAttempts == 1 ? HttpStatusCode.Conflict : HttpStatusCode.NoContent));
        }), ownsHandler: false);

        var module = CreateModule(http);
        var context = CreateContext("SecretsManager.DeleteSecret", "{\"SecretId\":\"demo\",\"ForceDeleteWithoutRecovery\":true}");

        await module.HandleAsync(context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal(2, purgeAttempts);
    }

    [Fact]
    public async Task HandleAsync_DeleteSecret_force_delete_reports_non_purgeable_vault_without_deleted_secret_get_permission()
    {
        using var http = new AzureHttpClient(new ScriptedHandler((request, _) =>
        {
            if (request.RequestUri!.AbsoluteUri.Contains("oauth2/v2.0/token"))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"access_token\":\"token\",\"expires_in\":3600,\"token_type\":\"Bearer\"}", Encoding.UTF8, "application/json"),
                });
            }

            if (request.RequestUri.AbsolutePath == "/secrets/demo")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"deletedDate\":1710000000,\"scheduledPurgeDate\":1710604800,\"attributes\":{\"recoveryLevel\":\"Recoverable\"}}", Encoding.UTF8, "application/json"),
                });
            }

            if (request.RequestUri.AbsolutePath == "/deletedsecrets/demo" && request.Method == HttpMethod.Get)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict));
        }), ownsHandler: false);

        var module = CreateModule(http);
        var context = CreateContext("SecretsManager.DeleteSecret", "{\"SecretId\":\"demo\",\"ForceDeleteWithoutRecovery\":true}");

        await module.HandleAsync(context);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        var body = await ReadBodyAsync(context);
        Assert.Contains("enforces soft-delete retention", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, StatusCodes.Status403Forbidden, "requires Key Vault purge permission", null)]
    [InlineData(HttpStatusCode.Conflict, StatusCodes.Status400BadRequest, "enforces soft-delete retention", "{\"deletedDate\":1710000000,\"scheduledPurgeDate\":1710604800,\"attributes\":{\"recoveryLevel\":\"Recoverable\"}}")]
    public async Task HandleAsync_DeleteSecret_reports_force_delete_limitations_honestly(HttpStatusCode purgeStatus, int expectedStatusCode, string expectedMessage, string? deletedSecretResponseJson)
    {
        using var http = new AzureHttpClient(new ScriptedHandler((request, _) =>
        {
            if (request.RequestUri!.AbsoluteUri.Contains("oauth2/v2.0/token"))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"access_token\":\"token\",\"expires_in\":3600,\"token_type\":\"Bearer\"}", Encoding.UTF8, "application/json"),
                });
            }

            if (request.RequestUri.AbsolutePath == "/secrets/demo")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"deletedDate\":1710000000,\"scheduledPurgeDate\":1710604800}", Encoding.UTF8, "application/json"),
                });
            }

            if (request.RequestUri.AbsolutePath == "/deletedsecrets/demo" && request.Method == HttpMethod.Get)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(deletedSecretResponseJson ?? "{\"deletedDate\":1710000000,\"scheduledPurgeDate\":1710604800,\"attributes\":{\"recoveryLevel\":\"Recoverable+Purgeable\"}}", Encoding.UTF8, "application/json"),
                });
            }

            return Task.FromResult(new HttpResponseMessage(purgeStatus));
        }), ownsHandler: false);

        var module = CreateModule(http);
        var context = CreateContext("SecretsManager.DeleteSecret", "{\"SecretId\":\"demo\",\"ForceDeleteWithoutRecovery\":true}");

        await module.HandleAsync(context);

        Assert.Equal(expectedStatusCode, context.Response.StatusCode);
        var body = await ReadBodyAsync(context);
        Assert.Contains(expectedMessage, body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("SecretsManager.getsecretvalue")]
    [InlineData("SecretsManager.GETSECRETVALUE")]
    public async Task HandleAsync_mixed_case_operation_returns_501_not_an_empty_response(string target)
    {
        using var http = new AzureHttpClient(new ScriptedHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK))), ownsHandler: false);

        var module = CreateModule(http);
        var context = CreateContext(target, "{\"SecretId\":\"demo\"}");

        await module.HandleAsync(context);

        // Regression: a non-canonical-case target must not slip past the support
        // gate and fall through the case-sensitive dispatch, leaving the response
        // unwritten (empty body / corrupted connection).
        Assert.Equal(StatusCodes.Status501NotImplemented, context.Response.StatusCode);
        var body = await ReadBodyAsync(context);
        Assert.Contains("NotImplementedException", body);
    }

    [Fact]
    public async Task HandleAsync_RotateSecret_is_rejected_as_unsupported_without_backend_call()
    {
        var backendCalled = false;
        using var http = new AzureHttpClient(new ScriptedHandler((_, _) =>
        {
            backendCalled = true;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }), ownsHandler: false);

        var module = CreateModule(http);
        var context = CreateContext("SecretsManager.RotateSecret", "{\"SecretId\":\"demo\"}");

        await module.HandleAsync(context);

        Assert.Equal(StatusCodes.Status501NotImplemented, context.Response.StatusCode);
        var body = await ReadBodyAsync(context);
        Assert.Contains("NotImplementedException", body);
        Assert.Contains("RotateSecret is not supported", body);
        // Recognised-but-unsupported ops are rejected before any Key Vault request.
        Assert.False(backendCalled);
        // It is a known operation (routed/metered), derived from the action table.
        Assert.Contains("RotateSecret", module.KnownOperations);
    }

    [Fact]
    public async Task HandleAsync_UpdateSecretVersionStage_is_rejected_as_documented_unsupported_without_backend_call()
    {
        var backendCalled = false;
        using var http = new AzureHttpClient(new ScriptedHandler((_, _) =>
        {
            backendCalled = true;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }), ownsHandler: false);

        var module = CreateModule(http);
        var context = CreateContext("SecretsManager.UpdateSecretVersionStage", "{\"SecretId\":\"demo\",\"VersionStage\":\"AWSCURRENT\",\"MoveToVersionId\":\"candidate\"}");

        await module.HandleAsync(context);

        Assert.Equal(StatusCodes.Status501NotImplemented, context.Response.StatusCode);
        var body = await ReadBodyAsync(context);
        Assert.Contains("UpdateSecretVersionStage is recognised but not supported", body);
        Assert.False(backendCalled);
        Assert.Contains("UpdateSecretVersionStage", module.KnownOperations);
    }

    [Fact]
    public void KnownOperations_is_derived_from_the_wire_protocol_action_table()
    {
        using var http = new AzureHttpClient(new ScriptedHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK))), ownsHandler: false);

        var module = CreateModule(http);

        Assert.Equal(
            SecretsManagerOperationNames.Names.OrderBy(name => name, StringComparer.Ordinal),
            module.KnownOperations.OrderBy(name => name, StringComparer.Ordinal));
        Assert.Contains("PutSecretValue", module.KnownOperations);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, StatusCodes.Status404NotFound, "ResourceNotFoundException")]
    [InlineData(HttpStatusCode.Conflict, StatusCodes.Status400BadRequest, "ResourceExistsException")]
    [InlineData(HttpStatusCode.BadRequest, StatusCodes.Status400BadRequest, "InvalidParameterException")]
    [InlineData(HttpStatusCode.Unauthorized, StatusCodes.Status403Forbidden, "AccessDeniedException")]
    [InlineData(HttpStatusCode.Forbidden, StatusCodes.Status403Forbidden, "AccessDeniedException")]
    [InlineData(HttpStatusCode.RequestTimeout, StatusCodes.Status503ServiceUnavailable, "InternalServiceError")]
    [InlineData(HttpStatusCode.TooManyRequests, StatusCodes.Status429TooManyRequests, "ThrottlingException")]
    [InlineData(HttpStatusCode.InternalServerError, StatusCodes.Status503ServiceUnavailable, "InternalServiceError")]
    [InlineData(HttpStatusCode.Accepted, StatusCodes.Status400BadRequest, "InvalidParameterException")]
    public void KeyVault_error_mapping_matches_aws_error_shape(HttpStatusCode backendStatus, int expectedStatus, string expectedCode)
    {
        Assert.Equal(expectedStatus, SecretsManagerServiceModule.MapStatusCode(backendStatus));
        Assert.Equal(expectedCode, SecretsManagerServiceModule.MapErrorCode(backendStatus));
    }

    [Fact]
    public void EpochDateTimeOffsetConverter_round_trips_epoch_seconds()
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(new EpochDateTimeOffsetConverter());
        var expected = DateTimeOffset.FromUnixTimeMilliseconds(1_710_000_000_123);

        var json = JsonSerializer.Serialize(expected, options);
        var actual = JsonSerializer.Deserialize<DateTimeOffset>(json, options);

        Assert.DoesNotContain('"', json);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void NormalizeSecretName_accepts_secret_arn_paths()
    {
        var name = KeyVaultSecretClient.NormalizeSecretName("arn:aws:secretsmanager:us-east-1:123456789012:secret:prod/db/password-AbCdEf");

        Assert.Equal("prod/db/password-AbCdEf", name);
    }

    [Fact]
    public void NormalizeSecretName_round_trips_the_proxy_synthetic_arn_shape()
    {
        var arn = KeyVaultSecretClient.BuildArn("demo");

        Assert.Equal("demo", KeyVaultSecretClient.NormalizeSecretName(arn));
    }

    [Fact]
    public async Task HandleAsync_PutSecretValue_returns_aws_shape_for_new_version()
    {
        var backend = new InMemoryKeyVaultHandler(
            new SecretVersionState("base", "old-secret", 1_710_000_000, new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["aws2azure-version-stages"] = "AWSCURRENT",
            }));
        using var http = new AzureHttpClient(backend, ownsHandler: false);

        var module = CreateModule(http);
        var context = CreateContext("SecretsManager.PutSecretValue", "{\"SecretId\":\"demo\",\"SecretString\":\"new-secret\",\"ClientRequestToken\":\"token-1\",\"VersionStages\":[\"AWSCURRENT\",\"BLUE\"]}");

        await module.HandleAsync(context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.NotNull(backend.LastPutUri);
        Assert.Contains("/secrets/demo?api-version=7.4", backend.LastPutUri);
        Assert.NotNull(backend.LastPutBody);
        Assert.Contains("\"value\":\"new-secret\"", backend.LastPutBody);
        using (var requestDocument = JsonDocument.Parse(backend.LastPutBody))
        {
            var tags = requestDocument.RootElement.GetProperty("tags");
            Assert.Equal("token-1", tags.GetProperty("aws2azure-client-request-token").GetString());
            Assert.Equal("\n", tags.GetProperty("aws2azure-version-stages").GetString());
            Assert.Equal("AWSCURRENT\nBLUE", tags.GetProperty("aws2azure-intended-version-stages").GetString());
            Assert.Equal("pending", tags.GetProperty("aws2azure-publication-state").GetString());
            Assert.False(requestDocument.RootElement.GetProperty("attributes").TryGetProperty("created", out _));
        }

        var body = await ReadBodyAsync(context);
        using var document = JsonDocument.Parse(body);
        Assert.Equal("demo", document.RootElement.GetProperty("Name").GetString());
        Assert.Equal("token-1", document.RootElement.GetProperty("VersionId").GetString());
        Assert.Equal("AWSCURRENT", document.RootElement.GetProperty("VersionStages")[0].GetString());
        Assert.Equal("BLUE", document.RootElement.GetProperty("VersionStages")[1].GetString());
    }

    [Fact]
    public async Task HandleAsync_TagResource_and_UntagResource_round_trip_tags_and_preserve_them_across_new_versions()
    {
        using var http = new AzureHttpClient(new InMemoryKeyVaultHandler(
            new SecretVersionState("base", "old-secret", 1_710_000_000, new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["aws2azure-version-stages"] = "AWSCURRENT",
                ["owner"] = "team-a",
            })), ownsHandler: false);

        var module = CreateModule(http);

        var tagContext = CreateContext("SecretsManager.TagResource", "{\"SecretId\":\"demo\",\"Tags\":[{\"Key\":\"env\",\"Value\":\"dev\"}]}");
        await module.HandleAsync(tagContext);
        Assert.Equal(StatusCodes.Status200OK, tagContext.Response.StatusCode);
        Assert.Equal(string.Empty, await ReadBodyAsync(tagContext));

        var putContext = CreateContext("SecretsManager.PutSecretValue", "{\"SecretId\":\"demo\",\"SecretString\":\"new-secret\"}");
        await module.HandleAsync(putContext);
        Assert.Equal(StatusCodes.Status200OK, putContext.Response.StatusCode);

        var describeContext = CreateContext("SecretsManager.DescribeSecret", "{\"SecretId\":\"demo\"}");
        await module.HandleAsync(describeContext);
        var describeBody = await ReadBodyAsync(describeContext);
        using (var describeDocument = JsonDocument.Parse(describeBody))
        {
            var tags = describeDocument.RootElement.GetProperty("Tags").EnumerateArray().ToArray();
            Assert.Contains(tags, tag => tag.GetProperty("Key").GetString() == "env" && tag.GetProperty("Value").GetString() == "dev");
            Assert.Contains(tags, tag => tag.GetProperty("Key").GetString() == "owner" && tag.GetProperty("Value").GetString() == "team-a");
        }

        var untagContext = CreateContext("SecretsManager.UntagResource", "{\"SecretId\":\"demo\",\"TagKeys\":[\"owner\"]}");
        await module.HandleAsync(untagContext);
        Assert.Equal(StatusCodes.Status200OK, untagContext.Response.StatusCode);
        Assert.Equal(string.Empty, await ReadBodyAsync(untagContext));

        describeContext = CreateContext("SecretsManager.DescribeSecret", "{\"SecretId\":\"demo\"}");
        await module.HandleAsync(describeContext);
        describeBody = await ReadBodyAsync(describeContext);
        using var afterUntagDocument = JsonDocument.Parse(describeBody);
        var afterTags = afterUntagDocument.RootElement.GetProperty("Tags").EnumerateArray().ToArray();
        Assert.Contains(afterTags, tag => tag.GetProperty("Key").GetString() == "env");
        Assert.DoesNotContain(afterTags, tag => tag.GetProperty("Key").GetString() == "owner");
    }

    [Fact]
    public async Task HandleAsync_TagResource_patches_the_current_version_uri()
    {
        string? patchUri = null;
        using var http = new AzureHttpClient(new ScriptedHandler(async (request, _) =>
        {
            if (request.RequestUri!.AbsoluteUri.Contains("oauth2/v2.0/token"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"access_token\":\"token\",\"expires_in\":3600,\"token_type\":\"Bearer\"}", Encoding.UTF8, "application/json"),
                };
            }

            if (request.Method == HttpMethod.Get)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"id\":\"https://example.vault.azure.net/secrets/demo/current123\",\"tags\":{\"aws2azure-version-stages\":\"AWSCURRENT\"},\"attributes\":{\"created\":1710000000}}", Encoding.UTF8, "application/json"),
                };
            }

            patchUri = request.RequestUri!.ToString();
            await request.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"id\":\"https://example.vault.azure.net/secrets/demo/current123\"}", Encoding.UTF8, "application/json"),
            };
        }), ownsHandler: false);

        var module = CreateModule(http);
        var context = CreateContext("SecretsManager.TagResource", "{\"SecretId\":\"demo\",\"Tags\":[{\"Key\":\"env\",\"Value\":\"dev\"}]}");

        await module.HandleAsync(context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.NotNull(patchUri);
        Assert.Contains("/secrets/demo/current123?api-version=7.4", patchUri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HandleAsync_PutSecretValue_default_stage_moves_current_to_previous()
    {
        using var http = new AzureHttpClient(new InMemoryKeyVaultHandler(
            new SecretVersionState("old-current", "old-secret", 1_710_000_000, new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["aws2azure-version-stages"] = "AWSCURRENT",
            }),
            new SecretVersionState("old-previous", "previous-secret", 1_709_999_000, new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["aws2azure-version-stages"] = "AWSPREVIOUS",
            })), ownsHandler: false);

        var module = CreateModule(http);
        var putContext = CreateContext("SecretsManager.PutSecretValue", "{\"SecretId\":\"demo\",\"SecretString\":\"new-secret\"}");

        await module.HandleAsync(putContext);

        Assert.Equal(StatusCodes.Status200OK, putContext.Response.StatusCode);
        var putBody = await ReadBodyAsync(putContext);
        using (var putDocument = JsonDocument.Parse(putBody))
        {
            Assert.Equal("new-version", putDocument.RootElement.GetProperty("VersionId").GetString());
            Assert.Equal("AWSCURRENT", putDocument.RootElement.GetProperty("VersionStages")[0].GetString());
        }

        var oldCurrentContext = CreateContext("SecretsManager.GetSecretValue", "{\"SecretId\":\"demo\",\"VersionId\":\"old-current\",\"VersionStage\":\"AWSCURRENT\"}");
        await module.HandleAsync(oldCurrentContext);

        Assert.Equal(StatusCodes.Status400BadRequest, oldCurrentContext.Response.StatusCode);
        var oldCurrentBody = await ReadBodyAsync(oldCurrentContext);
        Assert.Contains("InvalidRequestException", oldCurrentBody);

        var previousContext = CreateContext("SecretsManager.GetSecretValue", "{\"SecretId\":\"demo\",\"VersionStage\":\"AWSPREVIOUS\"}");
        await module.HandleAsync(previousContext);

        Assert.Equal(StatusCodes.Status200OK, previousContext.Response.StatusCode);
        var previousBody = await ReadBodyAsync(previousContext);
        using (var previousDocument = JsonDocument.Parse(previousBody))
        {
            Assert.Equal("old-current", previousDocument.RootElement.GetProperty("VersionId").GetString());
            Assert.Equal("old-secret", previousDocument.RootElement.GetProperty("SecretString").GetString());
            Assert.Equal("AWSPREVIOUS", previousDocument.RootElement.GetProperty("VersionStages")[0].GetString());
        }

        var currentContext = CreateContext("SecretsManager.GetSecretValue", "{\"SecretId\":\"demo\",\"VersionStage\":\"AWSCURRENT\"}");
        await module.HandleAsync(currentContext);

        Assert.Equal(StatusCodes.Status200OK, currentContext.Response.StatusCode);
        var currentBody = await ReadBodyAsync(currentContext);
        using var currentDocument = JsonDocument.Parse(currentBody);
        Assert.Equal("new-version", currentDocument.RootElement.GetProperty("VersionId").GetString());
        Assert.Equal("new-secret", currentDocument.RootElement.GetProperty("SecretString").GetString());
    }

    [Fact]
    public async Task HandleAsync_PutSecretValue_explicit_stages_move_only_requested_labels()
    {
        using var http = new AzureHttpClient(new InMemoryKeyVaultHandler(
            new SecretVersionState("current", "current-secret", 1_710_000_000, new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["aws2azure-version-stages"] = "AWSCURRENT",
            }),
            new SecretVersionState("blue", "blue-secret", 1_710_000_100, new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["aws2azure-version-stages"] = "BLUE",
            })), ownsHandler: false);

        var module = CreateModule(http);
        var putContext = CreateContext("SecretsManager.PutSecretValue", "{\"SecretId\":\"demo\",\"SecretString\":\"new-secret\",\"VersionStages\":[\"BLUE\"]}");

        await module.HandleAsync(putContext);

        Assert.Equal(StatusCodes.Status200OK, putContext.Response.StatusCode);

        var currentContext = CreateContext("SecretsManager.GetSecretValue", "{\"SecretId\":\"demo\",\"VersionStage\":\"AWSCURRENT\"}");
        await module.HandleAsync(currentContext);
        Assert.Equal(StatusCodes.Status200OK, currentContext.Response.StatusCode);
        var currentBody = await ReadBodyAsync(currentContext);
        using (var currentDocument = JsonDocument.Parse(currentBody))
        {
            Assert.Equal("current", currentDocument.RootElement.GetProperty("VersionId").GetString());
        }

        var blueContext = CreateContext("SecretsManager.GetSecretValue", "{\"SecretId\":\"demo\",\"VersionStage\":\"BLUE\"}");
        await module.HandleAsync(blueContext);
        Assert.Equal(StatusCodes.Status200OK, blueContext.Response.StatusCode);
        var blueBody = await ReadBodyAsync(blueContext);
        using var blueDocument = JsonDocument.Parse(blueBody);
        Assert.Equal("new-version", blueDocument.RootElement.GetProperty("VersionId").GetString());
    }

    [Fact]
    public void PutSecretValue_gap_doc_records_key_vault_only_atomicity_limit()
    {
        var yamlPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../..", "docs/gaps/secretsmanager/PutSecretValue.yaml"));
        var yaml = File.ReadAllText(yamlPath);

        Assert.Contains("strict cross-instance atomicity is structurally impossible", yaml, StringComparison.Ordinal);
        Assert.Contains("intentionally adds no external coordinator", yaml, StringComparison.Ordinal);
        Assert.Contains("status: partial", yaml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HandleAsync_PutSecretValue_replays_existing_client_request_token_without_new_put()
    {
        var expectedHash = KeyVaultSecretClient.GetPayloadSha256("new-secret", null);
        var putCount = 0;
        using var http = new AzureHttpClient(new ScriptedHandler((request, _) =>
        {
            if (request.RequestUri!.AbsoluteUri.Contains("oauth2/v2.0/token"))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"access_token\":\"token\",\"expires_in\":3600,\"token_type\":\"Bearer\"}", Encoding.UTF8, "application/json"),
                });
            }

            if (request.Method == HttpMethod.Put)
            {
                putCount++;
            }

            if (request.RequestUri.AbsolutePath.EndsWith("/versions", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent($"{{\"value\":[{{\"id\":\"https://example.vault.azure.net/secrets/demo/versions/reused123\",\"tags\":{{\"aws2azure-client-request-token\":\"token-1\",\"aws2azure-payload-sha256\":\"{expectedHash}\",\"aws2azure-version-stages\":\"AWSCURRENT\\nBLUE\"}}}}]}}", Encoding.UTF8, "application/json"),
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"id\":\"https://example.vault.azure.net/secrets/demo\",\"attributes\":{\"created\":1710000000}}", Encoding.UTF8, "application/json"),
            });
        }), ownsHandler: false);

        var module = CreateModule(http);
        var context = CreateContext("SecretsManager.PutSecretValue", "{\"SecretId\":\"demo\",\"SecretString\":\"new-secret\",\"ClientRequestToken\":\"token-1\"}");

        await module.HandleAsync(context);

        Assert.Equal(0, putCount);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        var body = await ReadBodyAsync(context);
        using var document = JsonDocument.Parse(body);
        Assert.Equal("token-1", document.RootElement.GetProperty("VersionId").GetString());
        Assert.Equal("BLUE", document.RootElement.GetProperty("VersionStages")[1].GetString());
    }

    [Fact]
    public async Task HandleAsync_PutSecretValue_rejects_client_request_token_with_different_payload()
    {
        using var http = new AzureHttpClient(new ScriptedHandler((request, _) =>
        {
            if (request.RequestUri!.AbsoluteUri.Contains("oauth2/v2.0/token"))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"access_token\":\"token\",\"expires_in\":3600,\"token_type\":\"Bearer\"}", Encoding.UTF8, "application/json"),
                });
            }

            if (request.RequestUri.AbsolutePath.EndsWith("/versions", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"value\":[{\"id\":\"https://example.vault.azure.net/secrets/demo/versions/reused123\",\"tags\":{\"aws2azure-client-request-token\":\"token-1\",\"aws2azure-payload-sha256\":\"different\"}}]}", Encoding.UTF8, "application/json"),
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"id\":\"https://example.vault.azure.net/secrets/demo\",\"attributes\":{\"created\":1710000000}}", Encoding.UTF8, "application/json"),
            });
        }), ownsHandler: false);

        var module = CreateModule(http);
        var context = CreateContext("SecretsManager.PutSecretValue", "{\"SecretId\":\"demo\",\"SecretString\":\"new-secret\",\"ClientRequestToken\":\"token-1\"}");

        await module.HandleAsync(context);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        var body = await ReadBodyAsync(context);
        Assert.Contains("ResourceExistsException", body);
    }

    [Theory]
    [InlineData("SecretsManager.CreateSecret", "{\"Name\":\"demo\",\"SecretString\":\"value\",\"SecretBinary\":\"dmFsdWU=\"}")]
    [InlineData("SecretsManager.PutSecretValue", "{\"SecretId\":\"demo\",\"SecretString\":\"value\",\"SecretBinary\":\"dmFsdWU=\"}")]
    [InlineData("SecretsManager.UpdateSecret", "{\"SecretId\":\"demo\",\"SecretString\":\"value\",\"SecretBinary\":\"dmFsdWU=\"}")]
    [InlineData("SecretsManager.CreateSecret", "{\"Name\":\"demo\"}")]
    [InlineData("SecretsManager.PutSecretValue", "{\"SecretId\":\"demo\"}")]
    public async Task HandleAsync_write_operations_require_exactly_one_secret_value_field(string target, string requestJson)
    {
        var backendCalled = false;
        using var http = new AzureHttpClient(new ScriptedHandler((_, _) =>
        {
            backendCalled = true;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }), ownsHandler: false);

        var module = CreateModule(http);
        var context = CreateContext(target, requestJson);

        await module.HandleAsync(context);

        Assert.False(backendCalled);
        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Contains("Exactly one of SecretString or SecretBinary must be supplied.", await ReadBodyAsync(context));
    }

    [Fact]
    public async Task HandleAsync_CreateSecret_accepts_whitespace_secret_string()
    {
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

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"id\":\"https://example.vault.azure.net/secrets/demo/versions/new-version\",\"attributes\":{\"created\":1710000000}}", Encoding.UTF8, "application/json"),
            });
        }), ownsHandler: false);

        var module = CreateModule(http);
        var context = CreateContext("SecretsManager.CreateSecret", "{\"Name\":\"demo\",\"SecretString\":\" \"}");

        await module.HandleAsync(context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    [Fact]
    public async Task HandleAsync_PutSecretValue_accepts_whitespace_secret_string()
    {
        using var http = new AzureHttpClient(new InMemoryKeyVaultHandler(
            new SecretVersionState("base", "old-secret", 1_710_000_000, new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["aws2azure-version-stages"] = "AWSCURRENT",
            })), ownsHandler: false);

        var module = CreateModule(http);
        var context = CreateContext("SecretsManager.PutSecretValue", "{\"SecretId\":\"demo\",\"SecretString\":\" \"}");

        await module.HandleAsync(context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    [Fact]
    public async Task HandleAsync_ListSecrets_documents_that_filters_sort_and_planned_deletion_are_ignored()
    {
        string? requestedUri = null;
        using var http = new AzureHttpClient(new ScriptedHandler((request, _) =>
        {
            if (request.RequestUri!.AbsoluteUri.Contains("oauth2/v2.0/token"))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"access_token\":\"token\",\"expires_in\":3600,\"token_type\":\"Bearer\"}", Encoding.UTF8, "application/json"),
                });
            }

            requestedUri = request.RequestUri.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"value\":[{\"id\":\"https://example.vault.azure.net/secrets/unmatched\",\"tags\":{\"env\":\"prod\"},\"attributes\":{\"created\":1710000000}}]}", Encoding.UTF8, "application/json"),
            });
        }), ownsHandler: false);

        var module = CreateModule(http);
        var context = CreateContext(
            "SecretsManager.ListSecrets",
            "{\"Filters\":[{\"Key\":\"name\",\"Values\":[\"demo\"]}],\"SortBy\":\"name\",\"SortOrder\":\"desc\",\"IncludePlannedDeletion\":true}");

        await module.HandleAsync(context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.NotNull(requestedUri);
        Assert.DoesNotContain("planned", requestedUri, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sort", requestedUri, StringComparison.OrdinalIgnoreCase);
        var body = await ReadBodyAsync(context);
        using var document = JsonDocument.Parse(body);
        Assert.Equal("unmatched", document.RootElement.GetProperty("SecretList")[0].GetProperty("Name").GetString());
    }

    [Fact]
    public void SecretsManager_gap_docs_capture_the_documented_audit_findings()
    {
        var gapsPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../..", "docs/gaps/secretsmanager"));
        var createYaml = File.ReadAllText(Path.Combine(gapsPath, "CreateSecret.yaml"));
        var deleteYaml = File.ReadAllText(Path.Combine(gapsPath, "DeleteSecret.yaml"));
        var listYaml = File.ReadAllText(Path.Combine(gapsPath, "ListSecrets.yaml"));
        var designYaml = File.ReadAllText(Path.Combine(gapsPath, "_design.yaml"));
        var updateStageYaml = File.ReadAllText(Path.Combine(gapsPath, "UpdateSecretVersionStage.yaml"));

        Assert.Contains("ClientRequestToken is persisted on the first Key Vault version", createYaml, StringComparison.Ordinal);
        Assert.Contains("ForceDeleteWithoutRecovery now maps to Key Vault delete followed by purge", deleteYaml, StringComparison.Ordinal);
        Assert.Contains("Filters, SortBy, SortOrder, and IncludePlannedDeletion are currently ignored", listYaml, StringComparison.Ordinal);
        Assert.Contains("synthetic `arn:aws:secretsmanager:azure:keyvault:secret:{name}` shape", designYaml, StringComparison.Ordinal);
        Assert.Contains("recognised by the wire-protocol router", updateStageYaml, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("SecretsManager.UpdateSecret", "{\"SecretId\":\"demo\",\"SecretString\":\"new-secret\"}")]
    [InlineData("SecretsManager.PutSecretValue", "{\"SecretId\":\"demo\",\"SecretString\":\"new-secret\"}")]
    public async Task HandleAsync_write_operations_preserve_key_vault_created_timestamp(string target, string requestJson)
    {
        const long originalCreated = 1_710_000_000;
        var backend = new InMemoryKeyVaultHandler(
            new SecretVersionState("base", "old-secret", originalCreated, new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["aws2azure-version-stages"] = "AWSCURRENT",
            }));
        using var http = new AzureHttpClient(backend, ownsHandler: false);

        var module = CreateModule(http);
        var writeContext = CreateContext(target, requestJson);
        await module.HandleAsync(writeContext);

        Assert.Equal(StatusCodes.Status200OK, writeContext.Response.StatusCode);
        Assert.NotNull(backend.LastPutBody);
        Assert.DoesNotContain("\"created\"", backend.LastPutBody);

        var describeContext = CreateContext("SecretsManager.DescribeSecret", "{\"SecretId\":\"demo\"}");
        await module.HandleAsync(describeContext);

        Assert.Equal(StatusCodes.Status200OK, describeContext.Response.StatusCode);
        var describeBody = await ReadBodyAsync(describeContext);
        using var document = JsonDocument.Parse(describeBody);
        Assert.Equal(originalCreated, (long)document.RootElement.GetProperty("CreatedDate").GetDouble());
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

    [Theory]
    [InlineData("")]
    [InlineData("   \r\n\t ")]
    public async Task HandleAsync_ListSecrets_treats_blank_non_seekable_body_as_empty_object(string body)
    {
        // Regression: a non-seekable request stream with no Content-Length
        // (the shape Kestrel hands us) must still map an empty or
        // whitespace-only body to "{}" rather than failing JSON parsing.
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
                Content = new StringContent("{\"value\":[]}", Encoding.UTF8, "application/json"),
            });
        }), ownsHandler: false);

        var module = CreateModule(http);
        var context = CreateContext("SecretsManager.ListSecrets", string.Empty);
        context.Request.ContentLength = null;
        context.Request.Body = new NonSeekableStream(Encoding.UTF8.GetBytes(body));

        await module.HandleAsync(context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    private sealed class NonSeekableStream(byte[] data) : Stream
    {
        private readonly MemoryStream _inner = new(data);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => _inner.ReadAsync(buffer, cancellationToken);
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class InMemoryKeyVaultHandler(params SecretVersionState[] initialVersions) : HttpMessageHandler
    {
        private readonly List<SecretVersionState> _versions = [.. initialVersions];
        public string? LastPutUri { get; private set; }
        public string? LastPutBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsoluteUri.Contains("oauth2/v2.0/token", StringComparison.Ordinal))
            {
                return JsonResponse("{\"access_token\":\"token\",\"expires_in\":3600,\"token_type\":\"Bearer\"}");
            }

            var path = request.RequestUri.AbsolutePath;
            if (request.Method == HttpMethod.Get && string.Equals(path, "/secrets/demo", StringComparison.Ordinal))
            {
                var current = ResolveCurrentVersion();
                return JsonResponse(BuildCurrentSecretJson(current));
            }

            if (request.Method == HttpMethod.Get && string.Equals(path, "/secrets/demo/versions", StringComparison.Ordinal))
            {
                var builder = new StringBuilder();
                builder.Append("{\"value\":[");
                for (var i = 0; i < _versions.Count; i++)
                {
                    if (i > 0)
                    {
                        builder.Append(',');
                    }

                    AppendVersionJson(builder, _versions[i], includeValue: false);
                }

                builder.Append("]}");
                return JsonResponse(builder.ToString());
            }

            if (request.Method == HttpMethod.Put && string.Equals(path, "/secrets/demo", StringComparison.Ordinal))
            {
                var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                LastPutUri = request.RequestUri.ToString();
                LastPutBody = body;
                using var document = JsonDocument.Parse(body);
                var value = document.RootElement.TryGetProperty("value", out var valueElement) && valueElement.ValueKind == JsonValueKind.String
                    ? valueElement.GetString() ?? string.Empty
                    : string.Empty;
                var tags = ReadRequestTags(document.RootElement);
                var version = new SecretVersionState("new-version", value, 1_710_000_200, tags);
                _versions.Add(version);
                return JsonResponse(BuildVersionJson(version, includeValue: false));
            }

            if (request.Method == HttpMethod.Patch && string.Equals(path, "/secrets/demo", StringComparison.Ordinal))
            {
                var current = ResolveCurrentVersion();
                var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                using var document = JsonDocument.Parse(body);
                current.Tags.Clear();
                foreach (var tag in ReadRequestTags(document.RootElement))
                {
                    current.Tags[tag.Key] = tag.Value;
                }

                return JsonResponse(BuildCurrentSecretJson(current), etag: "\"test-etag\"");
            }

            if (request.Method == HttpMethod.Patch && path.StartsWith("/secrets/demo/", StringComparison.Ordinal))
            {
                var versionId = Uri.UnescapeDataString(path["/secrets/demo/".Length..]);
                var version = _versions.Find(candidate => string.Equals(candidate.VersionId, versionId, StringComparison.Ordinal));
                if (version is null)
                {
                    return JsonResponse("{\"error\":{\"code\":\"SecretNotFound\"}}", HttpStatusCode.NotFound);
                }

                var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                using var document = JsonDocument.Parse(body);
                version.Tags.Clear();
                foreach (var tag in ReadRequestTags(document.RootElement))
                {
                    version.Tags[tag.Key] = tag.Value;
                }

                return JsonResponse(
                    BuildVersionJson(version, includeValue: false),
                    etag: "\"test-etag\"");
            }

            if (request.Method == HttpMethod.Get && path.StartsWith("/secrets/demo/", StringComparison.Ordinal))
            {
                var versionId = Uri.UnescapeDataString(path["/secrets/demo/".Length..]);
                var version = _versions.Find(candidate => string.Equals(candidate.VersionId, versionId, StringComparison.Ordinal));
                if (version is null)
                {
                    return JsonResponse("{\"error\":{\"code\":\"SecretNotFound\"}}", HttpStatusCode.NotFound);
                }

                return JsonResponse(
                    BuildVersionJson(version, includeValue: true),
                    etag: "\"test-etag\"");
            }

            return JsonResponse("{\"error\":{\"code\":\"Unhandled\"}}", HttpStatusCode.NotFound);
        }

        private static Dictionary<string, string> ReadRequestTags(JsonElement root)
        {
            var tags = new Dictionary<string, string>(StringComparer.Ordinal);
            if (!root.TryGetProperty("tags", out var tagsElement) || tagsElement.ValueKind != JsonValueKind.Object)
            {
                return tags;
            }

            foreach (var property in tagsElement.EnumerateObject())
            {
                tags[property.Name] = property.Value.ValueKind == JsonValueKind.String
                    ? property.Value.GetString() ?? string.Empty
                    : property.Value.ToString();
            }

            return tags;
        }

        private static string BuildVersionJson(SecretVersionState version, bool includeValue)
        {
            var builder = new StringBuilder();
            AppendVersionJson(builder, version, includeValue);
            return builder.ToString();
        }

        private string BuildCurrentSecretJson(SecretVersionState version)
        {
            var builder = new StringBuilder();
            builder.Append("{\"id\":\"https://example.vault.azure.net/secrets/demo/");
            builder.Append(version.VersionId);
            builder.Append("\",\"attributes\":{\"created\":1710000000},\"tags\":");
            builder.Append(JsonSerializer.Serialize(version.Tags));
            builder.Append('}');
            return builder.ToString();
        }

        private SecretVersionState ResolveCurrentVersion()
        {
            var current = _versions
                .Where(version => version.Tags.TryGetValue("aws2azure-version-stages", out var stages)
                    && stages.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Contains("AWSCURRENT", StringComparer.Ordinal))
                .OrderByDescending(version => version.Created)
                .ThenByDescending(version => version.VersionId, StringComparer.Ordinal)
                .FirstOrDefault();
            return current ?? _versions.OrderByDescending(version => version.Created).ThenByDescending(version => version.VersionId, StringComparer.Ordinal).First();
        }

        private static void AppendVersionJson(StringBuilder builder, SecretVersionState version, bool includeValue)
        {
            builder.Append('{');
            if (includeValue)
            {
                builder.Append("\"value\":");
                builder.Append(JsonSerializer.Serialize(version.Value));
                builder.Append(',');
            }

            builder.Append("\"id\":\"https://example.vault.azure.net/secrets/demo/versions/");
            builder.Append(version.VersionId);
            builder.Append("\",\"contentType\":\"text/plain\",\"attributes\":{\"created\":");
            builder.Append(version.Created);
            builder.Append("},\"tags\":");
            builder.Append(JsonSerializer.Serialize(version.Tags));
            builder.Append('}');
        }

        private static HttpResponseMessage JsonResponse(
            string json,
            HttpStatusCode statusCode = HttpStatusCode.OK,
            string? etag = null)
        {
            var response = new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
            if (etag is not null)
            {
                response.Headers.ETag =
                    new System.Net.Http.Headers.EntityTagHeaderValue(etag);
            }
            return response;
        }
    }

    private sealed record SecretVersionState(string VersionId, string Value, long Created, Dictionary<string, string> Tags);

    private sealed class ScriptedHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => responder(request, cancellationToken);
    }
}
