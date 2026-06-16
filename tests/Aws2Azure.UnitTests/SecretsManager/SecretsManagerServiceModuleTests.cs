using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Aws2Azure.Core.Azure;
using Aws2Azure.Core.Configuration;
using Aws2Azure.Core.Modules;
using Aws2Azure.Modules.SecretsManager;
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
