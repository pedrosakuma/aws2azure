using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Aws2Azure.Core.Azure;
using Aws2Azure.Core.Configuration;
using Aws2Azure.Modules.DynamoDb.Internal;
using Aws2Azure.Modules.DynamoDb.Operations;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Aws2Azure.UnitTests.DynamoDb;

/// <summary>
/// Coverage for <see cref="TransactWriteItemsHandler"/>:
/// <list type="bullet">
///   <item>Validation: 100-item cap, exactly-one-of Put/Delete/ConditionCheck,
///   Update rejected, ConditionCheck requires a ConditionExpression.</item>
///   <item>Single-table / single-partition / duplicate-target rejection.</item>
///   <item>Stored-procedures-disabled rejection.</item>
///   <item>Sproc request shape, success (200), condition failure
///   (TransactionCanceledException + positional reasons), and error mapping.</item>
/// </list>
/// </summary>
[Collection(DynamoDbTestCollection.Name)]
public class TransactWriteItemsHandlerTests
{
    public TransactWriteItemsHandlerTests()
    {
        CosmosOpsShared.MetadataCache.Clear();
    }

    // pk (HASH) + sk (RANGE) so multiple items can share a partition.
    private static readonly string MetaPkSk =
        "{\"id\":\"__aws2azure_table_meta__\",\"_a2a_pk\":\"__aws2azure_table_meta__\",\"_meta\":\"table\","
        + "\"tableName\":\"orders\","
        + "\"attributeDefinitions\":[{\"name\":\"pk\",\"type\":\"S\"},{\"name\":\"sk\",\"type\":\"S\"}],"
        + "\"keySchema\":[{\"name\":\"pk\",\"keyType\":\"HASH\"},{\"name\":\"sk\",\"keyType\":\"RANGE\"}],"
        + "\"billingMode\":\"PAY_PER_REQUEST\"}";

    private static CosmosClient BuildClient(ScriptedHandler handler)
    {
        var http = new AzureHttpClient(handler, ownsHandler: false,
            new AzureHttpClientOptions { MaxAttempts = 1 });
        var creds = new CosmosCredentials
        {
            Endpoint = "https://example.documents.azure.com/",
            PrimaryKey = "MDEyMzQ1Njc4OWFiY2RlZjAxMjM0NTY3ODlhYmNkZWY=",
            DatabaseName = "main",
        };
        return new CosmosClient(http, creds, new MasterKeyCosmosAuthenticator(creds.PrimaryKey));
    }

    private static SprocContext EnabledSproc()
        => new(StoredProcedureMode.Preferred, new SprocManager(NullLogger<SprocManager>.Instance));

    private static (DefaultHttpContext ctx, MemoryStream body) NewCtx()
    {
        var ctx = new DefaultHttpContext();
        var ms = new MemoryStream();
        ctx.Response.Body = ms;
        return (ctx, ms);
    }

    private static string ReadResponse(MemoryStream body)
    {
        body.Position = 0;
        return new StreamReader(body).ReadToEnd();
    }

    private static HttpResponseMessage CosmosOk(string body)
        => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage CosmosCreated(string body = "{}")
        => new(HttpStatusCode.Created) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage CosmosStatus(HttpStatusCode code, string body = "{}")
        => new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static Task Run(DefaultHttpContext ctx, CosmosClient cosmos, SprocContext? sproc, string req)
        => TransactWriteItemsHandler.HandleTransactWriteItemsAsync(
            ctx, Encoding.UTF8.GetBytes(req), cosmos, sproc, CancellationToken.None);

    private static string PutOp(string sk, string? condition = null)
    {
        var cond = condition is null
            ? string.Empty
            : ",\"ConditionExpression\":\"" + condition + "\"";
        return "{\"Put\":{\"TableName\":\"orders\",\"Item\":{\"pk\":{\"S\":\"a\"},\"sk\":{\"S\":\""
            + sk + "\"},\"v\":{\"N\":\"1\"}}" + cond + "}}";
    }

    [Fact]
    public async Task Disabled_sprocs_rejected()
    {
        var (ctx, body) = NewCtx();
        var cosmos = BuildClient(new ScriptedHandler());
        var disabled = new SprocContext(StoredProcedureMode.Disabled, null);

        await Run(ctx, cosmos, disabled, "{\"TransactItems\":[" + PutOp("1") + "]}");

        Assert.Equal(400, ctx.Response.StatusCode);
        Assert.Contains("ValidationException", ReadResponse(body));
        Assert.Contains("stored procedures", ReadResponse(body), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Empty_transact_items_rejected()
    {
        var (ctx, body) = NewCtx();
        var cosmos = BuildClient(new ScriptedHandler());

        await Run(ctx, cosmos, EnabledSproc(), "{\"TransactItems\":[]}");

        Assert.Equal(400, ctx.Response.StatusCode);
        Assert.Contains("ValidationException", ReadResponse(body));
    }

    [Fact]
    public async Task Update_operation_rejected_as_gap()
    {
        var (ctx, body) = NewCtx();
        var cosmos = BuildClient(new ScriptedHandler());
        var req = "{\"TransactItems\":[{\"Update\":{\"TableName\":\"orders\",\"Key\":{\"pk\":{\"S\":\"a\"},\"sk\":{\"S\":\"1\"}}," +
            "\"UpdateExpression\":\"SET v = :x\",\"ExpressionAttributeValues\":{\":x\":{\"N\":\"5\"}}}}]}";

        await Run(ctx, cosmos, EnabledSproc(), req);

        Assert.Equal(400, ctx.Response.StatusCode);
        var resp = ReadResponse(body);
        Assert.Contains("ValidationException", resp);
        Assert.Contains("Update", resp);
    }

    [Fact]
    public async Task Item_with_two_operations_rejected()
    {
        var (ctx, body) = NewCtx();
        var cosmos = BuildClient(new ScriptedHandler());
        var req = "{\"TransactItems\":[{" +
            "\"Put\":{\"TableName\":\"orders\",\"Item\":{\"pk\":{\"S\":\"a\"},\"sk\":{\"S\":\"1\"}}}," +
            "\"Delete\":{\"TableName\":\"orders\",\"Key\":{\"pk\":{\"S\":\"a\"},\"sk\":{\"S\":\"1\"}}}}]}";

        await Run(ctx, cosmos, EnabledSproc(), req);

        Assert.Equal(400, ctx.Response.StatusCode);
        Assert.Contains("exactly one", ReadResponse(body));
    }

    [Theory]
    [InlineData("""{"TransactItems":[{"Put":null}]}""", "Put must be an object")]
    [InlineData(
        """{"TransactItems":[{"Put":{"TableName":"orders","Item":{"pk":{"S":"a"},"sk":{"S":"1"}}},"Delete":"invalid"}]}""",
        "Delete must be an object")]
    [InlineData("""{"TransactItems":[{"Update":null}]}""", "Update is not supported")]
    [InlineData(
        """{"TransactItems":[{"Put":{"TableName":"orders","Item":{"pk":{"S":"a"},"sk":{"S":"1"}}},"Unknown":{}}]}""",
        "unsupported action")]
    public async Task Present_non_object_actions_are_rejected(
        string request,
        string expected)
    {
        var (ctx, body) = NewCtx();
        var handler = new ScriptedHandler();

        await Run(ctx, BuildClient(handler), EnabledSproc(), request);

        Assert.Equal(400, ctx.Response.StatusCode);
        Assert.Contains(expected, ReadResponse(body));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Cross_table_rejected()
    {
        var (ctx, body) = NewCtx();
        var handler = new ScriptedHandler { Responses = { CosmosOk(MetaPkSk) } };
        var cosmos = BuildClient(handler);
        var req = "{\"TransactItems\":[" + PutOp("1") + "," +
            "{\"Put\":{\"TableName\":\"others\",\"Item\":{\"pk\":{\"S\":\"a\"},\"sk\":{\"S\":\"2\"}}}}]}";

        await Run(ctx, cosmos, EnabledSproc(), req);

        Assert.Equal(400, ctx.Response.StatusCode);
        Assert.Contains("same table", ReadResponse(body));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Cross_partition_rejected()
    {
        var (ctx, body) = NewCtx();
        var handler = new ScriptedHandler { Responses = { CosmosOk(MetaPkSk) } };
        var cosmos = BuildClient(handler);
        var req = "{\"TransactItems\":[" + PutOp("1") + "," +
            "{\"Put\":{\"TableName\":\"orders\",\"Item\":{\"pk\":{\"S\":\"b\"},\"sk\":{\"S\":\"2\"}}}}]}";

        await Run(ctx, cosmos, EnabledSproc(), req);

        Assert.Equal(400, ctx.Response.StatusCode);
        Assert.Contains("partition-key", ReadResponse(body));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Duplicate_target_rejected()
    {
        var (ctx, body) = NewCtx();
        var handler = new ScriptedHandler { Responses = { CosmosOk(MetaPkSk) } };
        var cosmos = BuildClient(handler);
        var req = "{\"TransactItems\":[" + PutOp("1") + "," + PutOp("1") + "]}";

        await Run(ctx, cosmos, EnabledSproc(), req);

        Assert.Equal(400, ctx.Response.StatusCode);
        Assert.Contains("multiple operations on one item", ReadResponse(body));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ConditionCheck_requires_expression()
    {
        var (ctx, body) = NewCtx();
        var handler = new ScriptedHandler { Responses = { CosmosOk(MetaPkSk) } };
        var cosmos = BuildClient(handler);
        var req = "{\"TransactItems\":[{\"ConditionCheck\":{\"TableName\":\"orders\"," +
            "\"Key\":{\"pk\":{\"S\":\"a\"},\"sk\":{\"S\":\"1\"}}}}]}";

        await Run(ctx, cosmos, EnabledSproc(), req);

        Assert.Equal(400, ctx.Response.StatusCode);
        Assert.Contains("ConditionExpression is required", ReadResponse(body));
    }

    [Fact]
    public async Task Condition_on_reserved_attribute_rejected()
    {
        var (ctx, body) = NewCtx();
        var handler = new ScriptedHandler { Responses = { CosmosOk(MetaPkSk) } };
        var cosmos = BuildClient(handler);
        // "ttl" is shadow-encoded / injected as Cosmos' native TTL, so a
        // transaction condition on it cannot be faithfully evaluated server-side.
        var req = "{\"TransactItems\":[" + PutOp("1", condition: "attribute_not_exists(ttl)") + "]}";

        await Run(ctx, cosmos, EnabledSproc(), req);

        Assert.Equal(400, ctx.Response.StatusCode);
        var resp = ReadResponse(body);
        Assert.Contains("ValidationException", resp);
        Assert.Contains("ttl", resp);
    }

    [Fact]
    public async Task Client_request_token_is_rejected_instead_of_ignored()
    {
        var (ctx, body) = NewCtx();
        var handler = new ScriptedHandler();
        var cosmos = BuildClient(handler);
        var req = "{\"ClientRequestToken\":\"token-1\",\"TransactItems\":["
            + PutOp("1") + "]}";

        await Run(ctx, cosmos, EnabledSproc(), req);

        Assert.Equal(400, ctx.Response.StatusCode);
        var response = ReadResponse(body);
        Assert.Contains("ClientRequestToken", response);
        Assert.Contains("durable", response);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(
        """{"TransactItems":[{"Put":{"TableName":"orders","Item":{"pk":{"S":"a"},"sk":{"S":"1"}},"Expected":{"v":{"Exists":false}}}}]}""",
        "Expected")]
    [InlineData(
        """{"TransactItems":[{"Delete":{"TableName":"orders","Key":{"pk":{"S":"a"},"sk":{"S":"1"}},"ConditionalOperator":"AND"}}]}""",
        "ConditionalOperator")]
    [InlineData(
        """{"TransactItems":[{"ConditionCheck":{"TableName":"orders","Key":{"pk":{"S":"a"},"sk":{"S":"1"}},"ConditionExpression":"attribute_exists(pk)","ReturnValuesOnConditionCheckFailure":"ALL_OLD"}}]}""",
        "ReturnValuesOnConditionCheckFailure")]
    public async Task Legacy_and_unsupported_condition_members_are_rejected_before_cosmos(
        string request,
        string expected)
    {
        var (ctx, body) = NewCtx();
        var handler = new ScriptedHandler();

        await Run(ctx, BuildClient(handler), EnabledSproc(), request);

        Assert.Equal(400, ctx.Response.StatusCode);
        Assert.Contains(expected, ReadResponse(body));
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(
        """{"TransactItems":[{"Put":{"TableName":"orders","Item":{"pk":{"S":"a"},"sk":{"S":"1"}},"ConditionExpression":"m = :v","ExpressionAttributeValues":{":v":{"M":{"x":{"S":"y"}}}}}}]}""")]
    [InlineData(
        """{"TransactItems":[{"Put":{"TableName":"orders","Item":{"pk":{"S":"a"},"sk":{"S":"1"}},"ConditionExpression":"xs = :v","ExpressionAttributeValues":{":v":{"L":[{"S":"y"}]}}}}]}""")]
    [InlineData(
        """{"TransactItems":[{"Put":{"TableName":"orders","Item":{"pk":{"S":"a"},"sk":{"S":"1"}},"ConditionExpression":"blob = :v","ExpressionAttributeValues":{":v":{"B":"AQID"}}}}]}""")]
    [InlineData(
        """{"TransactItems":[{"Put":{"TableName":"orders","Item":{"pk":{"S":"a"},"sk":{"S":"1"}},"ConditionExpression":"tags = :v","ExpressionAttributeValues":{":v":{"SS":["x"]}}}}]}""")]
    [InlineData(
        """{"TransactItems":[{"Put":{"TableName":"orders","Item":{"pk":{"S":"a"},"sk":{"S":"1"}},"ConditionExpression":"n = :v","ExpressionAttributeValues":{":v":{"N":"9007199254740993"}}}}]}""")]
    [InlineData(
        """{"TransactItems":[{"Put":{"TableName":"orders","Item":{"pk":{"S":"a"},"sk":{"S":"1"}},"ConditionExpression":"m.x = :v","ExpressionAttributeValues":{":v":{"S":"y"}}}}]}""")]
    [InlineData(
        """{"TransactItems":[{"Put":{"TableName":"orders","Item":{"pk":{"S":"a"},"sk":{"S":"1"}},"ConditionExpression":"xs[0] = :v","ExpressionAttributeValues":{":v":{"S":"y"}}}}]}""")]
    [InlineData(
        """{"TransactItems":[{"Put":{"TableName":"orders","Item":{"pk":{"S":"a"},"sk":{"S":"1"}},"ConditionExpression":"contains(xs, :v)","ExpressionAttributeValues":{":v":{"S":"y"}}}}]}""")]
    [InlineData(
        """{"TransactItems":[{"Put":{"TableName":"orders","Item":{"pk":{"S":"a"},"sk":{"S":"1"}},"ConditionExpression":"size(xs) > :v","ExpressionAttributeValues":{":v":{"N":"0"}}}}]}""")]
    [InlineData(
        """{"TransactItems":[{"Put":{"TableName":"orders","Item":{"pk":{"S":"a"},"sk":{"S":"1"}},"ConditionExpression":"v = other"}}]}""")]
    public async Task Unsupported_transaction_condition_shapes_fail_before_sproc(
        string request)
    {
        var (ctx, body) = NewCtx();
        var handler = new ScriptedHandler
        {
            Responses = { CosmosOk(MetaPkSk) },
        };

        await Run(ctx, BuildClient(handler), EnabledSproc(), request);

        Assert.Equal(400, ctx.Response.StatusCode);
        var response = ReadResponse(body);
        Assert.Contains("supported transaction subset", response);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Over_100_items_rejected()
    {
        var (ctx, body) = NewCtx();
        var cosmos = BuildClient(new ScriptedHandler());
        var sb = new StringBuilder("{\"TransactItems\":[");
        for (int i = 0; i < 101; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(PutOp(i.ToString()));
        }
        sb.Append("]}");

        await Run(ctx, cosmos, EnabledSproc(), sb.ToString());

        Assert.Equal(400, ctx.Response.StatusCode);
        Assert.Contains("at most 100", ReadResponse(body));
    }

    [Fact]
    public async Task Malformed_numeric_condition_value_rejected_with_validation_error()
    {
        var (ctx, body) = NewCtx();
        var handler = new ScriptedHandler { Responses = { CosmosOk(MetaPkSk) } };
        var cosmos = BuildClient(handler);
        // {"N":"not-a-number"} parses as a condition operand but serializes to a
        // raw, invalid JSON token — the handler must surface a ValidationException
        // (400), not throw / 500, when the embedded condition is re-validated.
        var req = "{\"TransactItems\":[{\"Put\":{\"TableName\":\"orders\",\"Item\":" +
            "{\"pk\":{\"S\":\"a\"},\"sk\":{\"S\":\"1\"},\"v\":{\"N\":\"1\"}}," +
            "\"ConditionExpression\":\"v = :bad\"," +
            "\"ExpressionAttributeValues\":{\":bad\":{\"N\":\"not-a-number\"}}}}]}";

        await Run(ctx, cosmos, EnabledSproc(), req);

        Assert.Equal(400, ctx.Response.StatusCode);
        var resp = ReadResponse(body);
        Assert.Contains("ValidationException", resp);
        Assert.Contains("JavaScript-safe", resp);
    }

    [Fact]
    public async Task Whitespace_condition_expression_is_rejected_not_dropped()
    {
        var (ctx, body) = NewCtx();
        var handler = new ScriptedHandler
        {
            Responses = { CosmosOk(MetaPkSk) },
        };
        var request =
            """
            {
              "TransactItems": [
                {
                  "Put": {
                    "TableName": "orders",
                    "Item": {
                      "pk": { "S": "a" },
                      "sk": { "S": "1" }
                    },
                    "ConditionExpression": "   "
                  }
                }
              ]
            }
            """;

        await Run(ctx, BuildClient(handler), EnabledSproc(), request);

        Assert.Equal(400, ctx.Response.StatusCode);
        Assert.Contains("must not be empty", ReadResponse(body));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Success_returns_empty_200_and_sends_sproc()
    {
        var (ctx, body) = NewCtx();
        var handler = new ScriptedHandler
        {
            Responses =
            {
                CosmosOk(MetaPkSk),
                CosmosCreated(),
                CosmosOk("{\"success\":true}"),
            },
        };
        var cosmos = BuildClient(handler);
        var req = "{\"TransactItems\":[" + PutOp("1") + "," + PutOp("2") + "]}";

        await Run(ctx, cosmos, EnabledSproc(), req);

        Assert.Equal(200, ctx.Response.StatusCode);
        Assert.Equal("{}", ReadResponse(body));

        // Last request must be the sproc execution, carrying both PUT ops.
        var exec = handler.Requests[^1];
        Assert.Equal(HttpMethod.Post, exec.Method);
        Assert.Contains("/sprocs/" + SprocManager.TransactSprocId, exec.Uri.AbsolutePath);
        Assert.NotNull(exec.Body);
        Assert.Contains("\"type\":\"PUT\"", exec.Body!);
        // Params array wraps the operations array: [ [ {..}, {..} ] ]
        Assert.StartsWith("[[", exec.Body!.Replace(" ", string.Empty));
    }

    [Fact]
    public async Task Condition_failure_returns_transaction_cancelled_with_positional_reasons()
    {
        var (ctx, body) = NewCtx();
        var handler = new ScriptedHandler
        {
            Responses =
            {
                CosmosOk(MetaPkSk),
                CosmosCreated(),
                CosmosOk("{\"success\":false,\"reasons\":[{\"code\":\"None\"},{\"code\":\"ConditionalCheckFailed\"}]}"),
            },
        };
        var cosmos = BuildClient(handler);
        var req = "{\"TransactItems\":[" + PutOp("1") + "," +
            PutOp("2", "attribute_not_exists(pk)") + "]}";

        await Run(ctx, cosmos, EnabledSproc(), req);

        Assert.Equal(400, ctx.Response.StatusCode);
        using var doc = JsonDocument.Parse(ReadResponse(body));
        Assert.Contains("TransactionCanceledException", doc.RootElement.GetProperty("__type").GetString());
        var reasons = doc.RootElement.GetProperty("CancellationReasons");
        Assert.Equal(2, reasons.GetArrayLength());
        Assert.Equal("None", reasons[0].GetProperty("Code").GetString());
        Assert.Equal("ConditionalCheckFailed", reasons[1].GetProperty("Code").GetString());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"success\":true,\"reasons\":[]}")]
    [InlineData("{\"success\":\"true\"}")]
    public async Task Malformed_2xx_sproc_response_fails_closed(string response)
    {
        var (ctx, body) = NewCtx();
        var handler = new ScriptedHandler
        {
            Responses =
            {
                CosmosOk(MetaPkSk),
                CosmosCreated(),
                CosmosOk(response),
            },
        };

        await Run(
            ctx,
            BuildClient(handler),
            EnabledSproc(),
            "{\"TransactItems\":[" + PutOp("1") + "]}");

        Assert.Equal(500, ctx.Response.StatusCode);
        Assert.Contains("malformed", ReadResponse(body), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("{\"success\":false,\"reasons\":[]}")]
    [InlineData("{\"success\":false,\"reasons\":[{\"code\":\"Unknown\"}]}")]
    [InlineData("{\"success\":false,\"reasons\":[{\"code\":\"ConditionalCheckFailed\"}]}")]
    public async Task Malformed_or_unjustified_cancellation_reasons_fail_closed(
        string response)
    {
        var (ctx, body) = NewCtx();
        var handler = new ScriptedHandler
        {
            Responses =
            {
                CosmosOk(MetaPkSk),
                CosmosCreated(),
                CosmosOk(response),
            },
        };

        await Run(
            ctx,
            BuildClient(handler),
            EnabledSproc(),
            "{\"TransactItems\":[" + PutOp("1") + "]}");

        Assert.Equal(500, ctx.Response.StatusCode);
        Assert.Contains("cancellation reasons", ReadResponse(body), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Sproc_execution_error_maps_to_internal_error()
    {
        var (ctx, body) = NewCtx();
        var handler = new ScriptedHandler
        {
            Responses =
            {
                CosmosOk(MetaPkSk),
                CosmosCreated(),
                CosmosStatus(HttpStatusCode.InternalServerError, "{\"message\":\"boom\"}"),
            },
        };
        var cosmos = BuildClient(handler);
        var req = "{\"TransactItems\":[" + PutOp("1") + "]}";

        await Run(ctx, cosmos, EnabledSproc(), req);

        Assert.Equal(500, ctx.Response.StatusCode);
        Assert.Contains("InternalServerError", ReadResponse(body));
    }

    [Fact]
    public async Task Delete_operation_builds_delete_op()
    {
        var (ctx, body) = NewCtx();
        var handler = new ScriptedHandler
        {
            Responses =
            {
                CosmosOk(MetaPkSk),
                CosmosCreated(),
                CosmosOk("{\"success\":true}"),
            },
        };
        var cosmos = BuildClient(handler);
        var req = "{\"TransactItems\":[{\"Delete\":{\"TableName\":\"orders\"," +
            "\"Key\":{\"pk\":{\"S\":\"a\"},\"sk\":{\"S\":\"1\"}}}}]}";

        await Run(ctx, cosmos, EnabledSproc(), req);

        Assert.Equal(200, ctx.Response.StatusCode);
        var exec = handler.Requests[^1];
        Assert.Contains("\"type\":\"DELETE\"", exec.Body!);
    }

    [Fact]
    public async Task Missing_table_returns_resource_not_found()
    {
        var (ctx, body) = NewCtx();
        var handler = new ScriptedHandler
        {
            Responses = { CosmosStatus(HttpStatusCode.NotFound, "{\"code\":\"NotFound\"}") },
        };
        var cosmos = BuildClient(handler);
        var req = "{\"TransactItems\":[" + PutOp("1") + "]}";

        await Run(ctx, cosmos, EnabledSproc(), req);

        Assert.Equal(400, ctx.Response.StatusCode);
        Assert.Contains("ResourceNotFoundException", ReadResponse(body));
    }

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        public List<HttpResponseMessage> Responses { get; } = new();
        public List<CapturedRequest> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string? bodyText = null;
            if (request.Content is not null)
            {
                bodyText = await request.Content.ReadAsStringAsync(ct);
            }
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var h in request.Headers) headers[h.Key] = string.Join(",", h.Value);
            if (request.Content is not null)
            {
                foreach (var h in request.Content.Headers) headers[h.Key] = string.Join(",", h.Value);
            }
            lock (Requests)
            {
                Requests.Add(new CapturedRequest(request.Method, request.RequestUri!, headers, bodyText));
            }
            HttpResponseMessage? next;
            lock (Responses)
            {
                if (Responses.Count == 0)
                    return new HttpResponseMessage(HttpStatusCode.InternalServerError);
                next = Responses[0];
                Responses.RemoveAt(0);
            }
            return next;
        }
    }

    private sealed record CapturedRequest(
        HttpMethod Method, Uri Uri, Dictionary<string, string> Headers, string? Body);
}
