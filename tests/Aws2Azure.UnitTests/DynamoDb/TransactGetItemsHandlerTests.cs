using System.Net;
using System.Text;
using System.Text.Json;
using Aws2Azure.Core.Azure;
using Aws2Azure.Core.Configuration;
using Aws2Azure.Modules.DynamoDb.Internal;
using Aws2Azure.Modules.DynamoDb.Operations;
using Aws2Azure.Modules.DynamoDb.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aws2Azure.UnitTests.DynamoDb;

[Collection(DynamoDbTestCollection.Name)]
public sealed class TransactGetItemsHandlerTests
{
    private const string MetaPkSk =
        "{\"id\":\"__aws2azure_table_meta__\",\"_a2a_pk\":\"__aws2azure_table_meta__\",\"_meta\":\"table\","
        + "\"tableName\":\"orders\","
        + "\"attributeDefinitions\":[{\"name\":\"pk\",\"type\":\"S\"},{\"name\":\"sk\",\"type\":\"S\"}],"
        + "\"keySchema\":[{\"name\":\"pk\",\"keyType\":\"HASH\"},{\"name\":\"sk\",\"keyType\":\"RANGE\"}],"
        + "\"billingMode\":\"PAY_PER_REQUEST\"}";

    public TransactGetItemsHandlerTests()
    {
        CosmosOpsShared.MetadataCache.Clear();
    }

    [Fact]
    public async Task Snapshot_returns_positional_projected_responses_without_point_reads()
    {
        var first = ItemDocument(
            "a",
            "a",
            "{\"pk\":{\"S\":\"a\"},\"sk\":{\"S\":\"1\"},\"v\":{\"N\":\"1\"},\"hidden\":{\"S\":\"x\"}}");
        var handler = new ScriptedHandler
        {
            Responses =
            {
                CosmosOk(MetaPkSk),
                CosmosCreated(),
                CosmosOk(Snapshot(first, null)),
            },
        };
        var (context, responseBody) = NewContext();

        await RunAsync(
            context,
            BuildClient(handler),
            EnabledSproc(),
            """
            {
              "TransactItems": [
                {
                  "Get": {
                    "TableName": "orders",
                    "Key": { "pk": { "S": "a" }, "sk": { "S": "1" } },
                    "ProjectionExpression": "pk, sk, #v",
                    "ExpressionAttributeNames": { "#v": "v" }
                  }
                },
                {
                  "Get": {
                    "TableName": "orders",
                    "Key": { "pk": { "S": "a" }, "sk": { "S": "2" } }
                  }
                }
              ]
            }
            """);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        using var response = JsonDocument.Parse(ReadResponse(responseBody));
        var items = response.RootElement.GetProperty("Responses");
        Assert.Equal(2, items.GetArrayLength());
        var projected = items[0].GetProperty("Item");
        Assert.Equal("1", projected.GetProperty("v").GetProperty("N").GetString());
        Assert.False(projected.TryGetProperty("hidden", out _));
        Assert.False(items[1].TryGetProperty("Item", out _));

        Assert.DoesNotContain(
            handler.Requests,
            request => request.Method == HttpMethod.Get
                && !request.Uri.AbsolutePath.EndsWith(
                    "/docs/__aws2azure_table_meta__",
                    StringComparison.Ordinal));
        var execution = Assert.Single(
            handler.Requests,
            request => request.Uri.AbsolutePath.EndsWith(
                "/sprocs/" + SprocManager.TransactGetSprocId,
                StringComparison.Ordinal));
        Assert.Equal(HttpMethod.Post, execution.Method);
        Assert.Equal("[\"61\"]", execution.Headers["x-ms-documentdb-partitionkey"]);
        Assert.StartsWith("[[", execution.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cross_table_is_rejected_before_any_cosmos_request()
    {
        var handler = new ScriptedHandler();
        var (context, body) = NewContext();

        await RunAsync(
            context,
            BuildClient(handler),
            EnabledSproc(),
            """
            {
              "TransactItems": [
                { "Get": { "TableName": "orders", "Key": { "pk": { "S": "a" }, "sk": { "S": "1" } } } },
                { "Get": { "TableName": "other", "Key": { "pk": { "S": "a" }, "sk": { "S": "2" } } } }
              ]
            }
            """);

        AssertValidation(context, body, "same table");
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Cross_partition_is_rejected_before_snapshot_execution()
    {
        var handler = new ScriptedHandler
        {
            Responses = { CosmosOk(MetaPkSk) },
        };
        var (context, body) = NewContext();

        await RunAsync(
            context,
            BuildClient(handler),
            EnabledSproc(),
            """
            {
              "TransactItems": [
                { "Get": { "TableName": "orders", "Key": { "pk": { "S": "a" }, "sk": { "S": "1" } } } },
                { "Get": { "TableName": "orders", "Key": { "pk": { "S": "b" }, "sk": { "S": "2" } } } }
              ]
            }
            """);

        AssertValidation(context, body, "partition-key");
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Duplicate_target_is_rejected_before_snapshot_execution()
    {
        var handler = new ScriptedHandler
        {
            Responses = { CosmosOk(MetaPkSk) },
        };
        var (context, body) = NewContext();
        const string request =
            """
            {
              "TransactItems": [
                { "Get": { "TableName": "orders", "Key": { "pk": { "S": "a" }, "sk": { "S": "1" } } } },
                { "Get": { "TableName": "orders", "Key": { "pk": { "S": "a" }, "sk": { "S": "1" } } } }
              ]
            }
            """;

        await RunAsync(context, BuildClient(handler), EnabledSproc(), request);

        AssertValidation(context, body, "multiple operations on one item");
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Disabled_stored_procedures_are_rejected()
    {
        var handler = new ScriptedHandler();
        var (context, body) = NewContext();

        await RunAsync(
            context,
            BuildClient(handler),
            new SprocContext(StoredProcedureMode.Disabled, null),
            SingleGet());

        AssertValidation(context, body, "snapshot");
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Malformed_snapshot_success_response_fails_closed()
    {
        var handler = new ScriptedHandler
        {
            Responses =
            {
                CosmosOk(MetaPkSk),
                CosmosCreated(),
                CosmosOk("{}"),
            },
        };
        var (context, body) = NewContext();

        await RunAsync(context, BuildClient(handler), EnabledSproc(), SingleGet());

        Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
        Assert.Contains("malformed", ReadResponse(body), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Misaligned_snapshot_item_count_fails_closed()
    {
        var handler = new ScriptedHandler
        {
            Responses =
            {
                CosmosOk(MetaPkSk),
                CosmosCreated(),
                CosmosOk(Snapshot()),
            },
        };
        var (context, body) = NewContext();

        await RunAsync(context, BuildClient(handler), EnabledSproc(), SingleGet());

        Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
        Assert.Contains("positional", ReadResponse(body), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(
        """{"TransactItems":[{"Get":{"TableName":"orders","Key":{"pk":{"S":"a"},"sk":{"S":"1"}},"ConsistentRead":true}}]}""",
        "unsupported member")]
    [InlineData(
        """{"TransactItems":[{"Get":{"TableName":"orders","Key":{"pk":{"S":"a"},"sk":{"S":"1"}}},"Put":{}}]}""",
        "unsupported action")]
    [InlineData(
        """{"TransactItems":[{"Get":{"TableName":"orders","Key":{"pk":{"S":"a"},"sk":{"S":"1"}},"ExpressionAttributeNames":{"#p":"pk"}}}]}""",
        "requires ProjectionExpression")]
    [InlineData(
        """{"TransactItems":[{"Get":{"TableName":"orders","Key":{"pk":{"S":"a"},"sk":{"S":"1"}}}}],"ReturnConsumedCapacity":"TOTAL"}""",
        "ReturnConsumedCapacity")]
    public async Task Unsupported_request_shapes_are_rejected_without_data_reads(
        string request,
        string expected)
    {
        var handler = new ScriptedHandler
        {
            Responses = { CosmosOk(MetaPkSk) },
        };
        var (context, body) = NewContext();

        await RunAsync(context, BuildClient(handler), EnabledSproc(), request);

        AssertValidation(context, body, expected);
        Assert.True(handler.Requests.Count <= 1);
    }

    [Fact]
    public async Task Missing_table_returns_resource_not_found()
    {
        var handler = new ScriptedHandler
        {
            Responses =
            {
                CosmosStatus(
                    HttpStatusCode.NotFound,
                    "{\"code\":\"NotFound\",\"message\":\"x-ms-substatus: 1003\"}"),
            },
        };
        var (context, body) = NewContext();

        await RunAsync(context, BuildClient(handler), EnabledSproc(), SingleGet());

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Contains("ResourceNotFoundException", ReadResponse(body));
    }

    [Fact]
    public async Task More_than_100_items_is_rejected_without_cosmos_requests()
    {
        var request = new StringBuilder("{\"TransactItems\":[");
        for (var index = 0; index < 101; index++)
        {
            if (index > 0)
            {
                request.Append(',');
            }
            request.Append(
                "{\"Get\":{\"TableName\":\"orders\",\"Key\":{\"pk\":{\"S\":\"a\"},\"sk\":{\"S\":\"")
                .Append(index)
                .Append("\"}}}}");
        }
        request.Append("]}");
        var handler = new ScriptedHandler();
        var (context, body) = NewContext();

        await RunAsync(
            context,
            BuildClient(handler),
            EnabledSproc(),
            request.ToString());

        AssertValidation(context, body, "100");
        Assert.Empty(handler.Requests);
    }

    private static string SingleGet() =>
        """
        {
          "TransactItems": [
            {
              "Get": {
                "TableName": "orders",
                "Key": { "pk": { "S": "a" }, "sk": { "S": "1" } }
              }
            }
          ]
        }
        """;

    private static string Snapshot(params string?[] documents)
    {
        var builder = new StringBuilder("{\"success\":true,\"items\":[");
        for (var index = 0; index < documents.Length; index++)
        {
            if (index > 0)
            {
                builder.Append(',');
            }
            builder.Append(documents[index] ?? "null");
        }
        return builder.Append("]}").ToString();
    }

    private static string ItemDocument(string id, string partitionKey, string item)
    {
        using var document = JsonDocument.Parse(item);
        return InferredAttributeStorage.BuildCosmosDocument(
            id,
            partitionKey,
            document.RootElement);
    }

    private static Task RunAsync(
        DefaultHttpContext context,
        CosmosClient cosmos,
        SprocContext? sproc,
        string request)
        => TransactGetItemsHandler.HandleTransactGetItemsAsync(
            context,
            Encoding.UTF8.GetBytes(request),
            cosmos,
            sproc,
            CancellationToken.None);

    private static SprocContext EnabledSproc()
        => new(
            StoredProcedureMode.Preferred,
            new SprocManager(NullLogger<SprocManager>.Instance));

    private static CosmosClient BuildClient(ScriptedHandler handler)
    {
        var http = new AzureHttpClient(
            handler,
            ownsHandler: false,
            new AzureHttpClientOptions { MaxAttempts = 1 });
        var credentials = new CosmosCredentials
        {
            Endpoint = "https://example.documents.azure.com/",
            PrimaryKey =
                "MDEyMzQ1Njc4OWFiY2RlZjAxMjM0NTY3ODlhYmNkZWY=",
            DatabaseName = "main",
        };
        return new CosmosClient(
            http,
            credentials,
            new MasterKeyCosmosAuthenticator(credentials.PrimaryKey));
    }

    private static (DefaultHttpContext Context, MemoryStream Body) NewContext()
    {
        var context = new DefaultHttpContext();
        var body = new MemoryStream();
        context.Response.Body = body;
        return (context, body);
    }

    private static string ReadResponse(MemoryStream body)
    {
        body.Position = 0;
        return new StreamReader(body).ReadToEnd();
    }

    private static void AssertValidation(
        DefaultHttpContext context,
        MemoryStream body,
        string expected)
    {
        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        var response = ReadResponse(body);
        Assert.Contains("ValidationException", response);
        Assert.Contains(expected, response, StringComparison.OrdinalIgnoreCase);
    }

    private static HttpResponseMessage CosmosOk(string body)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

    private static HttpResponseMessage CosmosCreated()
        => new(HttpStatusCode.Created)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };

    private static HttpResponseMessage CosmosStatus(
        HttpStatusCode status,
        string body)
        => new(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        public List<HttpResponseMessage> Responses { get; } = [];
        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            var headers = new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var header in request.Headers)
            {
                headers[header.Key] = string.Join(",", header.Value);
            }
            if (request.Content is not null)
            {
                foreach (var header in request.Content.Headers)
                {
                    headers[header.Key] = string.Join(",", header.Value);
                }
            }
            Requests.Add(new CapturedRequest(
                request.Method,
                request.RequestUri!,
                headers,
                body));

            if (Responses.Count == 0)
            {
                return new HttpResponseMessage(
                    HttpStatusCode.InternalServerError)
                {
                    Content = new StringContent("{}"),
                };
            }

            var response = Responses[0];
            Responses.RemoveAt(0);
            return response;
        }
    }

    private sealed record CapturedRequest(
        HttpMethod Method,
        Uri Uri,
        Dictionary<string, string> Headers,
        string? Body);
}
