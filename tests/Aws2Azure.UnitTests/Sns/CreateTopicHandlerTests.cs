using System.Net;
using System.Net.Http;
using System.Text;
using Aws2Azure.Core.Azure;
using Aws2Azure.Core.Configuration;
using Aws2Azure.Modules.Sns;
using Aws2Azure.Modules.Sns.Management;
using Aws2Azure.Modules.Sns.Operations;
using Aws2Azure.Modules.Sns.WireProtocol;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aws2Azure.UnitTests.Sns;

public sealed class CreateTopicHandlerTests
{
    [Fact]
    public async Task HandleAsync_creates_topic_and_returns_topic_arn()
    {
        var managementClient = NewManagementClient(async (request, _) =>
        {
            Assert.Equal(HttpMethod.Put, request.Method);
            Assert.Equal("https://myns.servicebus.windows.net/orders?api-version=2021-05", request.RequestUri!.ToString());
            Assert.True(request.Headers.TryGetValues("Authorization", out var authorization));
            Assert.Equal("TestAuth", Assert.Single(authorization));

            var body = await request.Content!.ReadAsStringAsync().ConfigureAwait(false);
            Assert.Contains("<entry xmlns=\"http://www.w3.org/2005/Atom\">", body);
            Assert.Contains("TopicDescription", body);
            Assert.Contains("http://schemas.microsoft.com/netservices/2010/10/servicebus/connect", body);

            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent(string.Empty, Encoding.UTF8, "application/atom+xml"),
            };
        });

        var context = NewContext();
        await CreateTopicHandler.HandleAsync(
            context,
            NewParseResult("orders"),
            NewCredentials(),
            managementClient,
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        var body = ReadBody(context);
        Assert.Contains("<CreateTopicResponse", body);
        Assert.Contains("arn:aws:sns:us-west-2:000000000000:orders", body);
        Assert.Contains("<RequestId", body);
    }

    [Fact]
    public async Task HandleAsync_treats_existing_topic_as_idempotent()
    {
        var managementClient = NewManagementClient((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        var context = NewContext();

        await CreateTopicHandler.HandleAsync(
            context,
            NewParseResult("orders"),
            NewCredentials(),
            managementClient,
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Contains("arn:aws:sns:us-west-2:000000000000:orders", ReadBody(context));
    }

    [Theory]
    [InlineData("")]
    [InlineData("orders.fifo")]
    [InlineData("orders!")]
    public async Task HandleAsync_validates_topic_name(string topicName)
    {
        var managementClient = NewManagementClient((_, _) => throw new InvalidOperationException("HTTP should not be called."));
        var context = NewContext();

        await CreateTopicHandler.HandleAsync(
            context,
            NewParseResult(topicName),
            NewCredentials(),
            managementClient,
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Contains("InvalidParameter", ReadBody(context));
    }

    private static ServiceBusTopicsCredentials NewCredentials() => new()
    {
        Namespace = "myns",
        SasKeyName = "RootManageSharedAccessKey",
        SasKey = "secret",
    };

    private static SnsParseResult NewParseResult(string topicName)
        => new(SnsOperation.CreateTopic, new Dictionary<string, string> { ["Name"] = topicName }, null);

    private static DefaultHttpContext NewContext()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Headers.Authorization = "AWS4-HMAC-SHA256 Credential=AKIAEXAMPLE/20250101/us-west-2/sns/aws4_request, SignedHeaders=content-type;host;x-amz-date, Signature=deadbeef";
        context.Request.Host = new HostString("sns.us-west-2.amazonaws.com");
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static string ReadBody(HttpContext context)
    {
        context.Response.Body.Position = 0;
        return new StreamReader(context.Response.Body, Encoding.UTF8).ReadToEnd();
    }

    private static ServiceBusTopicsManagementClient NewManagementClient(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
    {
        var handler = new ScriptedHandler(responder);
        var httpClient = new AzureHttpClient(handler, ownsHandler: false);
        return new ServiceBusTopicsManagementClient(
            httpClient,
            new TestAuthenticator(),
            NullLogger<ServiceBusTopicsManagementClient>.Instance);
    }

    private sealed class TestAuthenticator : IServiceBusTopicsAuthenticator
    {
        public ValueTask AuthenticateAsync(HttpRequestMessage request, ServiceBusTopicsCredentials credentials, CancellationToken cancellationToken = default)
        {
            request.Headers.TryAddWithoutValidation("Authorization", "TestAuth");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ScriptedHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => responder(request, cancellationToken);
    }
}
