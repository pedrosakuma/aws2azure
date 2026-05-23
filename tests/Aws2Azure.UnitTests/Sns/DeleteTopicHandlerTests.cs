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

public sealed class DeleteTopicHandlerTests
{
    [Fact]
    public async Task HandleAsync_deletes_topic_from_arn()
    {
        var managementClient = NewManagementClient((request, _) =>
        {
            Assert.Equal("https://myns.servicebus.windows.net/orders?api-version=2021-05", request.RequestUri!.ToString());
            Assert.True(request.Headers.TryGetValues("Authorization", out var authorization));
            Assert.Equal("TestAuth", Assert.Single(authorization));
            if (request.Method == HttpMethod.Get)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(SnsManagementClientTestSupport.BuildTopicEntry("orders"), Encoding.UTF8, "application/atom+xml"),
                });
            }
            Assert.Equal(HttpMethod.Delete, request.Method);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });

        var context = NewContext();
        await DeleteTopicHandler.HandleAsync(
            context,
            NewParseResult("arn:aws:sns:us-west-2:000000000000:orders"),
            NewCredentials(),
            managementClient,
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Contains("<DeleteTopicResponse", ReadBody(context));
    }

    [Fact]
    public async Task HandleAsync_treats_not_found_as_success()
    {
        var managementClient = NewManagementClient((request, _) =>
        {
            // Probe-before-delete: a 404 on the GET probe is sufficient; DELETE must not fire.
            Assert.Equal(HttpMethod.Get, request.Method);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        });
        var context = NewContext();

        await DeleteTopicHandler.HandleAsync(
            context,
            NewParseResult("arn:aws:sns:us-west-2:000000000000:orders"),
            NewCredentials(),
            managementClient,
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Contains("<DeleteTopicResponse", ReadBody(context));
    }

    [Theory]
    [InlineData("")]
    [InlineData("arn:aws:sns:us-west-2:000000000000")]
    [InlineData("not-an-arn")]
    public async Task HandleAsync_rejects_invalid_topic_arn(string topicArn)
    {
        var managementClient = NewManagementClient((_, _) => throw new InvalidOperationException("HTTP should not be called."));
        var context = NewContext();

        await DeleteTopicHandler.HandleAsync(
            context,
            NewParseResult(topicArn),
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

    private static SnsParseResult NewParseResult(string topicArn)
        => new(SnsOperation.DeleteTopic, new Dictionary<string, string> { ["TopicArn"] = topicArn }, null);

    private static DefaultHttpContext NewContext()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
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
