using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Aws2Azure.Core.Azure;
using Aws2Azure.Core.Configuration;
using Aws2Azure.Modules.DynamoDb.Internal;
using Aws2Azure.Modules.DynamoDb.Operations;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Aws2Azure.UnitTests.DynamoDb;

/// <summary>
/// Coverage for the Slice-9 stub handlers (TransactWriteItems,
/// Describe/UpdateTimeToLive, Tag/Untag/ListTagsOfResource). The goal
/// of these tests is to pin the wire-format contracts so SDK callers
/// don't break — not to assert real Azure behaviour, which the gap docs
/// explicitly mark as unsupported.
/// </summary>
[Collection(DynamoDbTestCollection.Name)]
public class StubHandlersTests
{
    public StubHandlersTests()
    {
        // Clear metadata cache at test start to ensure isolation
        CosmosOpsShared.MetadataCache.Clear();
    }

    private static CosmosClient BuildClient()
    {
        var http = new AzureHttpClient(new HttpClientHandler(), ownsHandler: true,
            new AzureHttpClientOptions { MaxAttempts = 1 });
        var creds = new CosmosCredentials
        {
            Endpoint = "https://example.documents.azure.com/",
            PrimaryKey = "MDEyMzQ1Njc4OWFiY2RlZjAxMjM0NTY3ODlhYmNkZWY=",
            DatabaseName = "main",
        };
        return new CosmosClient(http, creds, new MasterKeyCosmosAuthenticator(creds.PrimaryKey));
    }

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

    // ----- TransactWriteItems: see TransactWriteItemsHandlerTests for full coverage. -----

    // ----- TTL: see TimeToLiveHandlersTests for full coverage of the now-implemented
    //       Update/DescribeTimeToLive control plane. -----

    // ----- TagResource -----

    [Fact]
    public async Task TagResource_missing_arn_is_rejected()
    {
        var (ctx, body) = NewCtx();
        var req = "{\"Tags\":[{\"Key\":\"env\",\"Value\":\"prod\"}]}";

        await TaggingHandlers.HandleTagResourceAsync(ctx, Encoding.UTF8.GetBytes(req), BuildClient(), default);

        Assert.Equal(400, ctx.Response.StatusCode);
        Assert.Contains("ResourceArn", ReadResponse(body));
    }

    [Fact]
    public async Task TagResource_empty_tags_is_rejected()
    {
        var (ctx, body) = NewCtx();
        var req = "{\"ResourceArn\":\"arn:aws:dynamodb:::table/orders\",\"Tags\":[]}";

        await TaggingHandlers.HandleTagResourceAsync(ctx, Encoding.UTF8.GetBytes(req), BuildClient(), default);

        Assert.Equal(400, ctx.Response.StatusCode);
        Assert.Contains("Tags", ReadResponse(body));
    }

    // ----- UntagResource -----

    [Fact]
    public async Task UntagResource_empty_keys_is_rejected()
    {
        var (ctx, _) = NewCtx();
        var req = "{\"ResourceArn\":\"arn:aws:dynamodb:::table/orders\",\"TagKeys\":[]}";

        await TaggingHandlers.HandleUntagResourceAsync(ctx, Encoding.UTF8.GetBytes(req), BuildClient(), default);

        Assert.Equal(400, ctx.Response.StatusCode);
    }

    // ----- ListTagsOfResource -----

    [Fact]
    public async Task ListTagsOfResource_missing_arn_is_rejected()
    {
        var (ctx, _) = NewCtx();
        await TaggingHandlers.HandleListTagsOfResourceAsync(ctx, Encoding.UTF8.GetBytes("{}"), BuildClient(), default);

        Assert.Equal(400, ctx.Response.StatusCode);
    }
}
