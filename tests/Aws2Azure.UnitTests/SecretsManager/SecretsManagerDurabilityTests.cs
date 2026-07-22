using System.Net;
using System.Text;
using System.Text.Json;
using Aws2Azure.Core.Azure;
using Aws2Azure.Core.Configuration;
using Aws2Azure.Core.Modules;
using Aws2Azure.Modules.SecretsManager;
using Aws2Azure.Modules.SecretsManager.Operations;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.UnitTests.SecretsManager;

public sealed class SecretsManagerDurabilityTests
{
    [Fact]
    public async Task Concurrent_token_replays_create_one_version_and_release_process_lock()
    {
        var hash = KeyVaultSecretClient.GetPayloadSha256("v2", null);
        using var backend = new DeterministicKeyVaultHandler(
            new FakeVersion("base", "v1", 100, Tags(stages: "AWSCURRENT")));
        using var http = new AzureHttpClient(backend, ownsHandler: false);
        var module = CreateModule(http);
        var requests = Enumerable.Range(0, 12)
            .Select(_ => CreateContext(
                "SecretsManager.PutSecretValue",
                "{\"SecretId\":\"demo\",\"SecretString\":\"v2\",\"ClientRequestToken\":\"shared-token\"}"))
            .ToArray();

        await Task.WhenAll(requests.Select(context => module.HandleAsync(context).AsTask()));

        Assert.All(requests, context => Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode));
        Assert.Equal(1, backend.PutCount);
        Assert.Single(backend.Snapshot(), version =>
            version.Tags.TryGetValue(KeyVaultSecretClient.ClientRequestTokenTag, out var token)
            && token == "shared-token"
            && version.Tags[KeyVaultSecretClient.PayloadSha256Tag] == hash);
        Assert.Equal(0, SecretVersionCoordinator.ActiveLockCount);
    }

    [Fact]
    public async Task Reconciliation_handles_delayed_visibility_pagination_same_second_and_unique_labels()
    {
        using var backend = new DeterministicKeyVaultHandler(
            new FakeVersion("same-a", "a", 100, Tags(stages: "AWSCURRENT")),
            new FakeVersion("same-z", "z", 100, Tags(stages: "AWSCURRENT")),
            new FakeVersion("older", "old", 90, Tags(stages: "AWSPREVIOUS")))
        {
            PageSize = 1,
            NewVersionVisibilityDelay = 2,
        };
        using var http = new AzureHttpClient(backend, ownsHandler: false);
        var context = CreateContext("SecretsManager.PutSecretValue", "{\"SecretId\":\"demo\",\"SecretString\":\"next\"}");

        await CreateModule(http).HandleAsync(context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        var versions = backend.Snapshot();
        Assert.Single(Holders(versions, "AWSCURRENT"));
        Assert.Equal("v0001", Holders(versions, "AWSCURRENT")[0].VersionId);
        Assert.Single(Holders(versions, "AWSPREVIOUS"));
        Assert.Equal("same-z", Holders(versions, "AWSPREVIOUS")[0].VersionId);
        Assert.True(backend.ListRequestCount >= 4);
    }

    [Fact]
    public async Task Reconciliation_repairs_partial_patch_failure_and_preserves_fresh_unrelated_tags()
    {
        using var backend = new DeterministicKeyVaultHandler(
            new FakeVersion("current", "v1", 100, Tags(stages: "AWSCURRENT")))
        {
            FailNextPatchVersionId = "current",
            InterfereOnNextVersionGet = ("current", "operator-tag", "keep-me"),
        };
        using var http = new AzureHttpClient(backend, ownsHandler: false);
        var context = CreateContext("SecretsManager.PutSecretValue", "{\"SecretId\":\"demo\",\"SecretString\":\"v2\"}");

        await CreateModule(http).HandleAsync(context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        var versions = backend.Snapshot();
        var previous = Assert.Single(Holders(versions, "AWSPREVIOUS"));
        Assert.Equal("current", previous.VersionId);
        Assert.Equal("keep-me", previous.Tags["operator-tag"]);
        Assert.Single(Holders(versions, "AWSCURRENT"));
    }

    [Fact]
    public async Task Duplicate_token_versions_choose_deterministic_winner_and_replay_without_put()
    {
        var hash = KeyVaultSecretClient.GetPayloadSha256("same", null);
        var sharedTags = Tags(
            token: "duplicate-token",
            hash: hash,
            stages: "\n",
            intendedStages: "AWSCURRENT",
            defaultTransition: "true");
        using var backend = new DeterministicKeyVaultHandler(
            new FakeVersion("winner-b", "same", 100, new Dictionary<string, string>(sharedTags, StringComparer.Ordinal)),
            new FakeVersion("winner-a", "same", 100, new Dictionary<string, string>(sharedTags, StringComparer.Ordinal)),
            new FakeVersion("base", "old", 90, Tags(stages: "AWSCURRENT")));
        using var http = new AzureHttpClient(backend, ownsHandler: false);
        var context = CreateContext(
            "SecretsManager.PutSecretValue",
            "{\"SecretId\":\"demo\",\"SecretString\":\"same\",\"ClientRequestToken\":\"duplicate-token\"}");

        await CreateModule(http).HandleAsync(context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal(0, backend.PutCount);
        Assert.Equal("winner-a", Assert.Single(Holders(backend.Snapshot(), "AWSCURRENT")).VersionId);
        using var response = JsonDocument.Parse(await ReadBodyAsync(context));
        Assert.Equal("duplicate-token", response.RootElement.GetProperty("VersionId").GetString());
    }

    [Fact]
    public async Task Conflicting_token_versions_return_aws_resource_exists_http_shape()
    {
        using var backend = new DeterministicKeyVaultHandler(
            new FakeVersion("one", "same", 100, Tags(token: "token", hash: "hash-a", stages: "\n")),
            new FakeVersion("two", "other", 100, Tags(token: "token", hash: "hash-b", stages: "\n")),
            new FakeVersion("base", "old", 90, Tags(stages: "AWSCURRENT")));
        using var http = new AzureHttpClient(backend, ownsHandler: false);
        var context = CreateContext(
            "SecretsManager.PutSecretValue",
            "{\"SecretId\":\"demo\",\"SecretString\":\"same\",\"ClientRequestToken\":\"token\"}");

        await CreateModule(http).HandleAsync(context);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Equal("application/x-amz-json-1.1", context.Response.ContentType);
        using var response = JsonDocument.Parse(await ReadBodyAsync(context));
        Assert.Equal("ResourceExistsException", response.RootElement.GetProperty("__type").GetString());
    }

    [Fact]
    public async Task Get_and_describe_share_paginated_inventory_and_report_all_logical_stages()
    {
        var tokenHash = KeyVaultSecretClient.GetPayloadSha256("pending", null);
        using var backend = new DeterministicKeyVaultHandler(
            new FakeVersion("current", "current", 100, Tags(stages: "AWSCURRENT")),
            new FakeVersion("pending", "pending", 101, Tags(token: "pending-token", hash: tokenHash, stages: "AWSPENDING")))
        {
            PageSize = 1,
        };
        using var http = new AzureHttpClient(backend, ownsHandler: false);
        var module = CreateModule(http);
        var get = CreateContext("SecretsManager.GetSecretValue", "{\"SecretId\":\"demo\",\"VersionStage\":\"AWSPENDING\"}");
        var describe = CreateContext("SecretsManager.DescribeSecret", "{\"SecretId\":\"demo\"}");

        await module.HandleAsync(get);
        await module.HandleAsync(describe);

        Assert.Equal(StatusCodes.Status200OK, get.Response.StatusCode);
        using (var getResponse = JsonDocument.Parse(await ReadBodyAsync(get)))
        {
            Assert.Equal("pending-token", getResponse.RootElement.GetProperty("VersionId").GetString());
            Assert.Equal("pending", getResponse.RootElement.GetProperty("SecretString").GetString());
        }

        Assert.Equal(StatusCodes.Status200OK, describe.Response.StatusCode);
        using var describeResponse = JsonDocument.Parse(await ReadBodyAsync(describe));
        var map = describeResponse.RootElement.GetProperty("VersionIdsToStages");
        Assert.Equal("AWSCURRENT", map.GetProperty("current")[0].GetString());
        Assert.Equal("AWSPENDING", map.GetProperty("pending-token")[0].GetString());
    }

    [Fact]
    public async Task UpdateSecret_replays_client_token_through_shared_reconciliation()
    {
        using var backend = new DeterministicKeyVaultHandler(
            new FakeVersion("base", "v1", 100, Tags(stages: "AWSCURRENT")));
        using var http = new AzureHttpClient(backend, ownsHandler: false);
        var module = CreateModule(http);
        const string request = "{\"SecretId\":\"demo\",\"SecretString\":\"v2\",\"ClientRequestToken\":\"update-token\"}";
        var first = CreateContext("SecretsManager.UpdateSecret", request);
        var replay = CreateContext("SecretsManager.UpdateSecret", request);

        await module.HandleAsync(first);
        await module.HandleAsync(replay);

        Assert.Equal(StatusCodes.Status200OK, first.Response.StatusCode);
        Assert.Equal(StatusCodes.Status200OK, replay.Response.StatusCode);
        Assert.Equal(1, backend.PutCount);
        using var response = JsonDocument.Parse(await ReadBodyAsync(replay));
        Assert.Equal("update-token", response.RootElement.GetProperty("VersionId").GetString());
    }

    [Fact]
    public async Task Token_replay_after_later_write_does_not_republish_historical_current_stage()
    {
        using var backend = new DeterministicKeyVaultHandler(
            new FakeVersion("base", "v1", 100, Tags(stages: "AWSCURRENT")));
        using var http = new AzureHttpClient(backend, ownsHandler: false);
        var module = CreateModule(http);
        const string tokenRequest = "{\"SecretId\":\"demo\",\"SecretString\":\"v2\",\"ClientRequestToken\":\"historical-token\"}";
        var first = CreateContext("SecretsManager.PutSecretValue", tokenRequest);
        var laterOne = CreateContext("SecretsManager.PutSecretValue", "{\"SecretId\":\"demo\",\"SecretString\":\"v3\"}");
        var laterTwo = CreateContext("SecretsManager.PutSecretValue", "{\"SecretId\":\"demo\",\"SecretString\":\"v4\"}");
        var replay = CreateContext("SecretsManager.PutSecretValue", tokenRequest);

        await module.HandleAsync(first);
        await module.HandleAsync(laterOne);
        await module.HandleAsync(laterTwo);
        await module.HandleAsync(replay);

        Assert.Equal(StatusCodes.Status200OK, replay.Response.StatusCode);
        var current = Assert.Single(Holders(backend.Snapshot(), "AWSCURRENT"));
        Assert.Equal("v0003", current.VersionId);
        Assert.Equal("v4", current.Value);
    }

    [Fact]
    public async Task Client_token_matching_existing_physical_id_cannot_create_duplicate_logical_version()
    {
        using var backend = new DeterministicKeyVaultHandler(
            new FakeVersion(
                "physical-id",
                "original",
                100,
                Tags(
                    hash: KeyVaultSecretClient.GetPayloadSha256("original", null),
                    stages: "AWSCURRENT")));
        using var http = new AzureHttpClient(backend, ownsHandler: false);
        var context = CreateContext(
            "SecretsManager.PutSecretValue",
            "{\"SecretId\":\"demo\",\"SecretString\":\"different\",\"ClientRequestToken\":\"physical-id\"}");

        await CreateModule(http).HandleAsync(context);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Equal(0, backend.PutCount);
        Assert.Single(backend.Snapshot());
    }

    [Fact]
    public async Task Persistent_backend_outage_remains_retryable_service_error()
    {
        using var backend = new DeterministicKeyVaultHandler(
            new FakeVersion("base", "v1", 100, Tags(stages: "AWSCURRENT")))
        {
            AlwaysPatchStatus = HttpStatusCode.ServiceUnavailable,
        };
        using var http = new AzureHttpClient(backend, ownsHandler: false);
        var context = CreateContext(
            "SecretsManager.PutSecretValue",
            "{\"SecretId\":\"demo\",\"SecretString\":\"v2\"}");

        await CreateModule(http).HandleAsync(context);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        using var response = JsonDocument.Parse(await ReadBodyAsync(context));
        Assert.Equal("InternalServiceError", response.RootElement.GetProperty("__type").GetString());
    }

    [Fact]
    public async Task Published_winner_continues_repair_when_concurrent_writer_reintroduces_current()
    {
        using var backend = new DeterministicKeyVaultHandler(
            new FakeVersion("base", "v1", 100, Tags(stages: "AWSCURRENT")))
        {
            InjectDuplicateCurrentAfterFirstPublication = "base",
        };
        using var http = new AzureHttpClient(backend, ownsHandler: false);
        var context = CreateContext(
            "SecretsManager.PutSecretValue",
            "{\"SecretId\":\"demo\",\"SecretString\":\"v2\"}");

        await CreateModule(http).HandleAsync(context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        var current = Assert.Single(Holders(backend.Snapshot(), "AWSCURRENT"));
        Assert.Equal("v0001", current.VersionId);
        Assert.Equal("base", Assert.Single(Holders(backend.Snapshot(), "AWSPREVIOUS")).VersionId);
    }

    [Fact]
    public async Task Process_lock_reference_is_removed_when_waiter_is_cancelled()
    {
        var lease = await SecretVersionCoordinator.AcquireLockAsync("cancelled", CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var waiting = SecretVersionCoordinator.AcquireLockAsync("cancelled", cancellation.Token).AsTask();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        await lease.DisposeAsync();

        Assert.Equal(0, SecretVersionCoordinator.ActiveLockCount);
    }

    [Fact]
    public async Task Unobserved_label_invariant_returns_explicit_bounded_conflict()
    {
        using var backend = new DeterministicKeyVaultHandler(
            new FakeVersion("base", "v1", 100, Tags(stages: "AWSCURRENT")))
        {
            IgnorePatches = true,
        };
        using var http = new AzureHttpClient(backend, ownsHandler: false);
        var context = CreateContext("SecretsManager.PutSecretValue", "{\"SecretId\":\"demo\",\"SecretString\":\"v2\"}");

        await CreateModule(http).HandleAsync(context);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        var body = await ReadBodyAsync(context);
        Assert.Contains("ResourceExistsException", body, StringComparison.Ordinal);
        Assert.Contains("bounded reconciliation", body, StringComparison.Ordinal);
    }

    private static List<FakeVersion> Holders(IReadOnlyList<FakeVersion> versions, string stage)
        => versions.Where(version =>
                version.Tags.TryGetValue(KeyVaultSecretClient.VersionStagesTag, out var stages)
                && KeyVaultSecretClient.DecodeStoredVersionStages(stages).Contains(stage, StringComparer.Ordinal))
            .ToList();

    private static Dictionary<string, string> Tags(
        string? token = null,
        string? hash = null,
        string? stages = null,
        string? intendedStages = null,
        string? defaultTransition = null)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (token is not null)
        {
            result[KeyVaultSecretClient.ClientRequestTokenTag] = token;
        }

        if (hash is not null)
        {
            result[KeyVaultSecretClient.PayloadSha256Tag] = hash;
        }

        if (stages is not null)
        {
            result[KeyVaultSecretClient.VersionStagesTag] = stages;
        }

        if (intendedStages is not null)
        {
            result[KeyVaultSecretClient.IntendedVersionStagesTag] = intendedStages;
        }

        if (defaultTransition is not null)
        {
            result[KeyVaultSecretClient.DefaultStageTransitionTag] = defaultTransition;
        }

        return result;
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
        using var reader = new StreamReader(context.Response.Body, Encoding.UTF8, leaveOpen: true);
        return await reader.ReadToEndAsync();
    }

    private sealed class DeterministicKeyVaultHandler(params FakeVersion[] initialVersions) : HttpMessageHandler
    {
        private readonly object _sync = new();
        private readonly List<FakeVersion> _versions = initialVersions.Select(version => version.Copy()).ToList();
        private int _nextVersion;
        private int _listRequestCount;
        private bool _patchFailureConsumed;
        private bool _interferenceConsumed;
        private bool _duplicateCurrentInjected;

        public int PageSize { get; init; } = int.MaxValue;
        public int NewVersionVisibilityDelay { get; init; }
        public string? FailNextPatchVersionId { get; init; }
        public (string VersionId, string Key, string Value)? InterfereOnNextVersionGet { get; init; }
        public bool IgnorePatches { get; init; }
        public HttpStatusCode? AlwaysPatchStatus { get; init; }
        public string? InjectDuplicateCurrentAfterFirstPublication { get; init; }
        public int PutCount { get; private set; }
        public int ListRequestCount => Volatile.Read(ref _listRequestCount);

        public IReadOnlyList<FakeVersion> Snapshot()
        {
            lock (_sync)
            {
                return _versions.Select(version => version.Copy()).ToArray();
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsoluteUri.Contains("oauth2/v2.0/token", StringComparison.Ordinal))
            {
                return Json("{\"access_token\":\"token\",\"expires_in\":3600,\"token_type\":\"Bearer\"}");
            }

            var path = request.RequestUri.AbsolutePath;
            if (request.Method == HttpMethod.Get && string.Equals(path, "/secrets/demo", StringComparison.Ordinal))
            {
                lock (_sync)
                {
                    if (_versions.Count == 0)
                    {
                        return Json("{}", HttpStatusCode.NotFound);
                    }

                    return Json(SerializeVersion(_versions[^1], includeValue: true, includeName: true));
                }
            }

            if (request.Method == HttpMethod.Get && string.Equals(path, "/secrets/demo/versions", StringComparison.Ordinal))
            {
                var listCall = Interlocked.Increment(ref _listRequestCount);
                var skip = ReadSkipToken(request.RequestUri);
                lock (_sync)
                {
                    var visible = _versions.Where(version => version.VisibleAfterListCall <= listCall).ToArray();
                    var page = visible.Skip(skip).Take(PageSize).ToArray();
                    var next = skip + page.Length < visible.Length ? skip + page.Length : (int?)null;
                    return Json(SerializeList(page, next));
                }
            }

            if (request.Method == HttpMethod.Put && string.Equals(path, "/secrets/demo", StringComparison.Ordinal))
            {
                var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                using var document = JsonDocument.Parse(body);
                var value = document.RootElement.GetProperty("value").GetString() ?? string.Empty;
                var tags = ReadTags(document.RootElement);
                lock (_sync)
                {
                    PutCount++;
                    var version = new FakeVersion(
                        $"v{++_nextVersion:0000}",
                        value,
                        200,
                        tags,
                        _listRequestCount + NewVersionVisibilityDelay + 1);
                    _versions.Add(version);
                    return Json(SerializeVersion(version, includeValue: false));
                }
            }

            if (path.StartsWith("/secrets/demo/", StringComparison.Ordinal))
            {
                var versionId = Uri.UnescapeDataString(path["/secrets/demo/".Length..]);
                if (request.Method == HttpMethod.Get)
                {
                    lock (_sync)
                    {
                        var version = Find(versionId);
                        if (version is null)
                        {
                            return Json("{}", HttpStatusCode.NotFound);
                        }

                        if (!_interferenceConsumed
                            && InterfereOnNextVersionGet is { } interference
                            && string.Equals(interference.VersionId, versionId, StringComparison.Ordinal))
                        {
                            version.Tags[interference.Key] = interference.Value;
                            version.Revision++;
                            _interferenceConsumed = true;
                        }

                        return Json(
                            SerializeVersion(version, includeValue: true),
                            etag: version.ETag);
                    }
                }

                if (request.Method == HttpMethod.Patch)
                {
                    var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                    using var document = JsonDocument.Parse(body);
                    lock (_sync)
                    {
                        var version = Find(versionId);
                        if (version is null)
                        {
                            return Json("{}", HttpStatusCode.NotFound);
                        }

                        if (AlwaysPatchStatus is { } patchStatus)
                        {
                            return Json("{}", patchStatus);
                        }

                        if (!_patchFailureConsumed
                            && string.Equals(FailNextPatchVersionId, versionId, StringComparison.Ordinal))
                        {
                            _patchFailureConsumed = true;
                            return Json("{}", HttpStatusCode.ServiceUnavailable);
                        }

                        if (IgnorePatches)
                        {
                            return Json(SerializeVersion(version, includeValue: false));
                        }

                        version.Tags.Clear();
                        foreach (var tag in ReadTags(document.RootElement))
                        {
                            version.Tags[tag.Key] = tag.Value;
                        }
                        version.Revision++;

                        if (!_duplicateCurrentInjected
                            && InjectDuplicateCurrentAfterFirstPublication is { } duplicateVersionId
                            && version.Tags.TryGetValue(
                                KeyVaultSecretClient.PublicationStateTag,
                                out var publicationState)
                            && string.Equals(
                                publicationState,
                                "published",
                                StringComparison.Ordinal))
                        {
                            var duplicate = Find(duplicateVersionId);
                            if (duplicate is not null)
                            {
                                duplicate.Tags[KeyVaultSecretClient.VersionStagesTag] =
                                    "AWSCURRENT";
                                duplicate.Revision++;
                                _duplicateCurrentInjected = true;
                            }
                        }

                        return Json(
                            SerializeVersion(version, includeValue: false),
                            etag: version.ETag);
                    }
                }
            }

            return Json("{}", HttpStatusCode.NotFound);
        }

        private FakeVersion? Find(string versionId)
            => _versions.Find(version => string.Equals(version.VersionId, versionId, StringComparison.Ordinal));

        private static int ReadSkipToken(Uri uri)
        {
            const string marker = "$skiptoken=";
            var query = uri.Query;
            var index = query.IndexOf(marker, StringComparison.Ordinal);
            if (index < 0)
            {
                return 0;
            }

            var value = query[(index + marker.Length)..];
            var ampersand = value.IndexOf('&');
            return int.Parse(ampersand < 0 ? value : value[..ampersand], System.Globalization.CultureInfo.InvariantCulture);
        }

        private static string SerializeList(IReadOnlyList<FakeVersion> versions, int? next)
        {
            var builder = new StringBuilder("{\"value\":[");
            for (var i = 0; i < versions.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append(',');
                }

                builder.Append(SerializeVersion(versions[i], includeValue: false));
            }

            builder.Append(']');
            if (next is not null)
            {
                builder.Append(",\"nextLink\":\"https://example.vault.azure.net/secrets/demo/versions?api-version=7.4&$skiptoken=");
                builder.Append(next.Value);
                builder.Append('"');
            }

            builder.Append('}');
            return builder.ToString();
        }

        private static string SerializeVersion(FakeVersion version, bool includeValue, bool includeName = false)
        {
            var fields = new List<string>();
            if (includeValue)
            {
                fields.Add("\"value\":" + JsonSerializer.Serialize(version.Value));
            }

            fields.Add("\"id\":\"https://example.vault.azure.net/secrets/demo/" + version.VersionId + "\"");
            if (includeName)
            {
                fields.Add("\"name\":\"demo\"");
            }

            fields.Add("\"contentType\":\"text/plain\"");
            fields.Add("\"attributes\":{\"created\":" + version.Created + ",\"updated\":" + version.Created + "}");
            fields.Add("\"tags\":" + JsonSerializer.Serialize(version.Tags));
            return "{" + string.Join(',', fields) + "}";
        }

        private static Dictionary<string, string> ReadTags(JsonElement root)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            if (!root.TryGetProperty("tags", out var tags))
            {
                return result;
            }

            foreach (var property in tags.EnumerateObject())
            {
                result[property.Name] = property.Value.GetString() ?? string.Empty;
            }

            return result;
        }

        private static HttpResponseMessage Json(
            string body,
            HttpStatusCode statusCode = HttpStatusCode.OK,
            string? etag = null)
        {
            var response = new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            if (etag is not null)
            {
                response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue(etag);
            }
            return response;
        }
    }

    private sealed record FakeVersion(
        string VersionId,
        string Value,
        long Created,
        Dictionary<string, string> Tags,
        int VisibleAfterListCall = 0)
    {
        public int Revision { get; set; } = 1;
        public string ETag => $"\"{Revision}\"";

        public FakeVersion Copy()
            => new(
                VersionId,
                Value,
                Created,
                new Dictionary<string, string>(Tags, StringComparer.Ordinal),
                VisibleAfterListCall)
            {
                Revision = Revision,
            };
    }
}
