using System.IO;
using System.Text;
using System.Threading.Tasks;
using Aws2Azure.Core.Configuration;
using Aws2Azure.Core.Modules;
using Aws2Azure.Modules.Sns;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.UnitTests.Sns;

public class SnsServiceModuleTests
{
    [Theory]
    [InlineData("sns.us-east-1.amazonaws.com", true)]
    [InlineData("SNS.eu-west-1.amazonaws.com", true)]
    [InlineData("sns", true)]
    [InlineData("sns-fips.us-gov-west-1.amazonaws.com", false)]
    [InlineData("sqs.us-east-1.amazonaws.com", false)]
    [InlineData("", false)]
    public void MatchesHost_recognises_sns_hosts(string host, bool expected)
    {
        var module = NewModule();
        Assert.Equal(expected, module.MatchesHost(host));
    }

    [Fact]
    public void Module_requires_sigv4_and_buffers_body()
    {
        var module = NewModule();

        Assert.True(module.RequiresSigV4);
        Assert.True(module.BuffersRequestBodyForSigV4);
        Assert.Empty(module.RequiredSignedHeaders);
    }

    [Theory]
    [InlineData("CreateTopic")]
    [InlineData("DeleteTopic")]
    [InlineData("ListTopics")]
    [InlineData("Publish")]
    [InlineData("PublishBatch")]
    [InlineData("Subscribe")]
    [InlineData("Unsubscribe")]
    [InlineData("ListSubscriptions")]
    [InlineData("ListSubscriptionsByTopic")]
    [InlineData("GetTopicAttributes")]
    [InlineData("SetTopicAttributes")]
    public async Task Recognised_operations_return_structured_501_stub(string action)
    {
        var module = NewModule();
        var ctx = NewContext($"Action={action}&Version=2010-03-31");
        ctx.Items["aws2azure.accessKeyId"] = "AKIAEXAMPLE";

        await module.HandleAsync(ctx);

        var body = ReadBody(ctx);
        Assert.Equal(StatusCodes.Status501NotImplemented, ctx.Response.StatusCode);
        Assert.Contains("text/xml", ctx.Response.ContentType);
        Assert.Contains("<ErrorResponse", body);
        Assert.Contains("Receiver", body);
        Assert.Contains("InternalFailure", body);
        Assert.Contains(action, body);
        Assert.Contains("<RequestId", body);
    }

    [Fact]
    public async Task Unknown_operation_returns_InvalidAction()
    {
        var module = NewModule();
        var ctx = NewContext("Action=Nope&Version=2010-03-31");
        ctx.Items["aws2azure.accessKeyId"] = "AKIAEXAMPLE";

        await module.HandleAsync(ctx);

        Assert.Equal(StatusCodes.Status400BadRequest, ctx.Response.StatusCode);
        Assert.Contains("InvalidAction", ReadBody(ctx));
    }

    [Fact]
    public async Task Missing_azure_credentials_returns_AuthorizationError()
    {
        var module = NewModule(includeTopicCreds: false, includeEventGridCreds: false);
        var ctx = NewContext("Action=ListTopics&Version=2010-03-31");
        ctx.Items["aws2azure.accessKeyId"] = "AKIAEXAMPLE";

        await module.HandleAsync(ctx);

        Assert.Equal(StatusCodes.Status403Forbidden, ctx.Response.StatusCode);
        Assert.Contains("AuthorizationError", ReadBody(ctx));
    }

    [Fact]
    public async Task Missing_access_key_returns_MissingAuthenticationToken()
    {
        var module = NewModule();
        var ctx = NewContext("Action=ListTopics&Version=2010-03-31");

        await module.HandleAsync(ctx);

        Assert.Equal(StatusCodes.Status403Forbidden, ctx.Response.StatusCode);
        Assert.Contains("MissingAuthenticationToken", ReadBody(ctx));
    }

    private static SnsServiceModule NewModule(bool includeTopicCreds = true, bool includeEventGridCreds = false)
        => new(GetResolver(includeTopicCreds, includeEventGridCreds), new CapabilityMatrix("sns", []));

    private static ICredentialResolver GetResolver(bool includeTopicCreds, bool includeEventGridCreds)
    {
        var azure = new AzureCredentials();
        if (includeTopicCreds)
        {
            azure.ServiceBusTopics = new ServiceBusTopicsCredentials
            {
                Namespace = "myns",
                SasKeyName = "RootManageSharedAccessKey",
                SasKey = "ZGVhZGJlZWY=",
            };
        }

        if (includeEventGridCreds)
        {
            azure.EventGrid = new EventGridCredentials
            {
                Endpoint = "https://example.westus2-1.eventgrid.azure.net/api/events",
                AccessKey = "secret",
            };
        }

        return new StaticCredentialResolver(new ProxyConfig
        {
            Credentials =
            {
                new CredentialEntry
                {
                    AwsAccessKeyId = "AKIAEXAMPLE",
                    AwsSecretAccessKey = "secret",
                    Azure = azure,
                },
            },
        });
    }

    private static DefaultHttpContext NewContext(string body)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = HttpMethods.Post;
        ctx.Request.ContentType = "application/x-www-form-urlencoded";
        var bytes = Encoding.UTF8.GetBytes(body);
        ctx.Request.Body = new MemoryStream(bytes);
        ctx.Request.ContentLength = bytes.Length;
        ctx.Response.Body = new MemoryStream();
        return ctx;
    }

    private static string ReadBody(HttpContext context)
    {
        context.Response.Body.Position = 0;
        return new StreamReader(context.Response.Body, Encoding.UTF8).ReadToEnd();
    }
}
