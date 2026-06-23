using System.Net;
using System.Net.Http;
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

        Assert.Equal(StatusCodes.Status409Conflict, context.Response.StatusCode);
        var body = await ReadBodyAsync(context);
        using var document = JsonDocument.Parse(body);
        Assert.Equal("ResourceExistsException", document.RootElement.GetProperty("__type").GetString());
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
                Content = new StringContent("{\"id\":\"https://example.vault.azure.net/secrets/demo/versions/def456\",\"attributes\":{\"created\":1710000000}}", Encoding.UTF8, "application/json"),
            });
        }), ownsHandler: false);

        var module = CreateModule(http);
        var context = CreateContext("SecretsManager.UpdateSecret", "{\"SecretId\":\"demo\",\"SecretString\":\"new-secret\"}");

        await module.HandleAsync(context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        var body = await ReadBodyAsync(context);
        using var document = JsonDocument.Parse(body);
        Assert.Equal("def456", document.RootElement.GetProperty("VersionId").GetString());
        Assert.Equal("demo", document.RootElement.GetProperty("Name").GetString());
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
    public async Task HandleAsync_DeleteSecret_returns_aws_shape_without_version_id()
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

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }), ownsHandler: false);

        var module = CreateModule(http);
        var context = CreateContext("SecretsManager.DeleteSecret", "{\"SecretId\":\"demo\"}");

        await module.HandleAsync(context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        var body = await ReadBodyAsync(context);
        using var document = JsonDocument.Parse(body);
        Assert.Equal("demo", document.RootElement.GetProperty("Name").GetString());
        Assert.Equal("arn:aws:secretsmanager:azure:keyvault:secret:demo", document.RootElement.GetProperty("ARN").GetString());
        var deletedDate = DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Round(document.RootElement.GetProperty("DeletedDate").GetDouble() * 1000d));
        Assert.True(deletedDate <= DateTimeOffset.UtcNow.AddSeconds(5));
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
    [InlineData(HttpStatusCode.Conflict, StatusCodes.Status409Conflict, "ResourceExistsException")]
    [InlineData(HttpStatusCode.BadRequest, StatusCodes.Status400BadRequest, "InvalidParameterException")]
    [InlineData(HttpStatusCode.Unauthorized, StatusCodes.Status403Forbidden, "AccessDeniedException")]
    [InlineData(HttpStatusCode.Forbidden, StatusCodes.Status403Forbidden, "AccessDeniedException")]
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
    public async Task HandleAsync_PutSecretValue_returns_aws_shape_for_new_version()
    {
        string? requestedUri = null;
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
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"id\":\"https://example.vault.azure.net/secrets/demo\",\"attributes\":{\"created\":1710000000}}", Encoding.UTF8, "application/json"),
                };
            }

            requestedUri = request.RequestUri.ToString();
            requestBody = await request.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"id\":\"https://example.vault.azure.net/secrets/demo/versions/put789\",\"attributes\":{\"created\":1710000000}}", Encoding.UTF8, "application/json"),
            };
        }), ownsHandler: false);

        var module = CreateModule(http);
        var context = CreateContext("SecretsManager.PutSecretValue", "{\"SecretId\":\"demo\",\"SecretString\":\"new-secret\",\"ClientRequestToken\":\"token-1\",\"VersionStages\":[\"AWSCURRENT\",\"BLUE\"]}");

        await module.HandleAsync(context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.NotNull(requestedUri);
        Assert.Contains("/secrets/demo?api-version=7.4", requestedUri);
        Assert.NotNull(requestBody);
        Assert.Contains("\"value\":\"new-secret\"", requestBody);
        using (var requestDocument = JsonDocument.Parse(requestBody))
        {
            var tags = requestDocument.RootElement.GetProperty("tags");
            Assert.Equal("token-1", tags.GetProperty("aws2azure-client-request-token").GetString());
            Assert.Equal("AWSCURRENT\nBLUE", tags.GetProperty("aws2azure-version-stages").GetString());
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

        Assert.Equal(StatusCodes.Status409Conflict, context.Response.StatusCode);
        var body = await ReadBodyAsync(context);
        Assert.Contains("ResourceExistsException", body);
    }

    [Theory]
    [InlineData("SecretsManager.UpdateSecret", "{\"SecretId\":\"demo\",\"SecretString\":\"new-secret\"}")]
    [InlineData("SecretsManager.PutSecretValue", "{\"SecretId\":\"demo\",\"SecretString\":\"new-secret\"}")]
    public async Task HandleAsync_write_operations_preserve_key_vault_created_timestamp(string target, string requestJson)
    {
        const long originalCreated = 1_710_000_000;
        long currentCreated = originalCreated;
        string? putBody = null;
        using var http = new AzureHttpClient(new ScriptedHandler(async (request, _) =>
        {
            if (request.RequestUri!.AbsoluteUri.Contains("oauth2/v2.0/token"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"access_token\":\"token\",\"expires_in\":3600,\"token_type\":\"Bearer\"}", Encoding.UTF8, "application/json"),
                };
            }

            if (request.Method == HttpMethod.Put)
            {
                putBody = await request.Content!.ReadAsStringAsync();
                if (putBody.Contains("\"created\"", StringComparison.Ordinal))
                {
                    currentCreated = 1_710_009_999;
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent($"{{\"id\":\"https://example.vault.azure.net/secrets/demo/versions/def456\",\"attributes\":{{\"created\":{currentCreated},\"updated\":1710001000}}}}", Encoding.UTF8, "application/json"),
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($"{{\"id\":\"https://example.vault.azure.net/secrets/demo/versions/def456\",\"name\":\"demo\",\"attributes\":{{\"created\":{currentCreated},\"updated\":1710001000}}}}", Encoding.UTF8, "application/json"),
            };
        }), ownsHandler: false);

        var module = CreateModule(http);
        var writeContext = CreateContext(target, requestJson);
        await module.HandleAsync(writeContext);

        Assert.Equal(StatusCodes.Status200OK, writeContext.Response.StatusCode);
        Assert.NotNull(putBody);
        Assert.DoesNotContain("\"created\"", putBody);

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

    private sealed class ScriptedHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => responder(request, cancellationToken);
    }
}
