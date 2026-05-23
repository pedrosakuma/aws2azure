using System.IO;
using System.Text;
using System.Threading.Tasks;
using Aws2Azure.Core.Configuration;
using Aws2Azure.Core.Modules;
using Aws2Azure.Modules.Sns;
using Aws2Azure.Modules.Sns.Amqp;
using Aws2Azure.Modules.Sns.Management;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

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

    [Fact]
    public async Task GetTopicAttributes_is_no_longer_stubbed()
    {
        var module = NewModule();
        var ctx = NewContext("Action=GetTopicAttributes&Version=2010-03-31&TopicArn=arn%3Aaws%3Asns%3Aus-east-1%3A000000000000%3Aorders");
        ctx.Items["aws2azure.accessKeyId"] = "AKIAEXAMPLE";

        await module.HandleAsync(ctx);

        Assert.Equal(StatusCodes.Status404NotFound, ctx.Response.StatusCode);
        Assert.Contains("NotFound", ReadBody(ctx));
    }

    [Fact]
    public async Task SetTopicAttributes_is_no_longer_stubbed()
    {
        var module = NewModule();
        var ctx = NewContext("Action=SetTopicAttributes&Version=2010-03-31&TopicArn=arn%3Aaws%3Asns%3Aus-east-1%3A000000000000%3Aorders&AttributeName=DisplayName&AttributeValue=test");
        ctx.Items["aws2azure.accessKeyId"] = "AKIAEXAMPLE";

        await module.HandleAsync(ctx);

        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
        Assert.Contains("<SetTopicAttributesResponse", ReadBody(ctx));
    }

    [Fact]
    public async Task Publish_routes_to_amqp_sender()
    {
        var sender = new RecordingSender();
        var module = NewModule(sender: sender);
        var ctx = NewContext("Action=Publish&Version=2010-03-31&TopicArn=arn%3Aaws%3Asns%3Aus-east-1%3A000000000000%3Aorders&Message=hello");
        ctx.Items["aws2azure.accessKeyId"] = "AKIAEXAMPLE";

        await module.HandleAsync(ctx);

        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
        Assert.NotNull(sender.SingleCall);
        Assert.Equal("orders", sender.SingleCall!.Value.TopicName);
    }

    [Fact]
    public async Task PublishBatch_routes_to_amqp_sender()
    {
        var sender = new RecordingSender();
        var module = NewModule(sender: sender);
        var ctx = NewContext("Action=PublishBatch&Version=2010-03-31&TopicArn=arn%3Aaws%3Asns%3Aus-east-1%3A000000000000%3Aorders&PublishBatchRequestEntries.member.1.Id=a&PublishBatchRequestEntries.member.1.Message=hello");
        ctx.Items["aws2azure.accessKeyId"] = "AKIAEXAMPLE";

        await module.HandleAsync(ctx);

        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
        Assert.NotNull(sender.BatchCall);
        Assert.Single(sender.BatchCall!.Value.Messages);
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
    public async Task EventGrid_only_credentials_do_not_enable_topic_crud_or_publish()
    {
        var module = NewModule(includeTopicCreds: false, includeEventGridCreds: true);
        var ctx = NewContext("Action=Publish&Version=2010-03-31&TopicArn=arn%3Aaws%3Asns%3Aus-east-1%3A000000000000%3Aorders&Message=hello");
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

    private static SnsServiceModule NewModule(bool includeTopicCreds = true, bool includeEventGridCreds = false, ISnsAmqpSender? sender = null)
        => new(
            GetResolver(includeTopicCreds, includeEventGridCreds),
            new NoopManagementClient(),
            sender ?? new RecordingSender(),
            NullLogger<SnsServiceModule>.Instance,
            new CapabilityMatrix("sns", []));

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

    private sealed class NoopManagementClient : IServiceBusTopicsManagementClient
    {
        public ValueTask CreateTopicAsync(ServiceBusTopicsCredentials credentials, string namespaceFqdn, string topicName, CancellationToken cancellationToken)
            => ValueTask.CompletedTask;

        public ValueTask DeleteTopicAsync(ServiceBusTopicsCredentials credentials, string namespaceFqdn, string topicName, CancellationToken cancellationToken)
            => ValueTask.CompletedTask;

        public ValueTask<ServiceBusTopicPage> ListTopicsAsync(ServiceBusTopicsCredentials credentials, string namespaceFqdn, int skip, int top, CancellationToken cancellationToken)
            => ValueTask.FromResult(new ServiceBusTopicPage([]));

        public ValueTask<ServiceBusTopicDescription?> GetTopicAsync(ServiceBusTopicsCredentials credentials, string namespaceFqdn, string topicName, CancellationToken cancellationToken)
            => ValueTask.FromResult<ServiceBusTopicDescription?>(null);

        public ValueTask CreateSubscriptionAsync(ServiceBusTopicsCredentials credentials, string namespaceFqdn, string topicName, string subscriptionName, string userMetadata, CancellationToken cancellationToken)
            => ValueTask.CompletedTask;

        public ValueTask DeleteSubscriptionAsync(ServiceBusTopicsCredentials credentials, string namespaceFqdn, string topicName, string subscriptionName, CancellationToken cancellationToken)
            => ValueTask.CompletedTask;

        public ValueTask<ServiceBusSubscriptionPage> ListSubscriptionsAsync(ServiceBusTopicsCredentials credentials, string namespaceFqdn, string topicName, int skip, int top, CancellationToken cancellationToken)
            => ValueTask.FromResult(new ServiceBusSubscriptionPage([]));

        public ValueTask<ServiceBusSubscriptionDescription?> GetSubscriptionAsync(ServiceBusTopicsCredentials credentials, string namespaceFqdn, string topicName, string subscriptionName, CancellationToken cancellationToken)
            => ValueTask.FromResult<ServiceBusSubscriptionDescription?>(null);

        public ValueTask UpdateSubscriptionAsync(ServiceBusTopicsCredentials credentials, string namespaceFqdn, string topicName, ServiceBusSubscriptionDescription description, CancellationToken cancellationToken)
            => ValueTask.CompletedTask;
    }

    private sealed class RecordingSender : ISnsAmqpSender
    {
        public (string NamespaceFqdn, string TopicName, SnsAmqpSendMessage Message)? SingleCall { get; private set; }
        public (string NamespaceFqdn, string TopicName, IReadOnlyList<SnsAmqpSendMessage> Messages)? BatchCall { get; private set; }

        public Task SendAsync(ServiceBusTopicsCredentials credentials, string namespaceFqdn, string topicName, SnsAmqpSendMessage message, CancellationToken cancellationToken)
        {
            SingleCall = (namespaceFqdn, topicName, message);
            return Task.CompletedTask;
        }

        public Task<SnsBatchSendResult> SendBatchAsync(ServiceBusTopicsCredentials credentials, string namespaceFqdn, string topicName, IReadOnlyList<SnsAmqpSendMessage> messages, CancellationToken cancellationToken)
        {
            BatchCall = (namespaceFqdn, topicName, messages);
            return Task.FromResult(new SnsBatchSendResult(messages.Select(_ => new SnsBatchSendOutcome(true, null, null, false)).ToArray()));
        }
    }
}
