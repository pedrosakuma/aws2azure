using System.IO;
using System.Text;
using System.Threading.Tasks;
using Aws2Azure.Core.Configuration;
using Aws2Azure.Core.Modules;
using Aws2Azure.Modules.Sns;
using Aws2Azure.Modules.Sns.Amqp;
using Aws2Azure.Modules.Sns.EventGrid;
using Aws2Azure.Modules.Sns.Management;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aws2Azure.UnitTests.Sns;

/// <summary>
/// Unit coverage for the request-validation surfaces the new SNS Tier-3
/// happy-path cases exercise: Subscribe/Unsubscribe protocol and ARN
/// validation, GetSubscriptionAttributes/SetSubscriptionAttributes attribute
/// handling, ListSubscriptions/ListSubscriptionsByTopic required parameters,
/// and the GetTopicAttributes/SetTopicAttributes round-trip these new
/// conformance cases rely on. These assert the module's existing production
/// validation logic (see SnsSubscriptionSupport, SnsTopicSupport) rather than
/// introducing new rules.
/// </summary>
public sealed class SnsSubscriptionOperationsModuleTests
{
    [Theory]
    [InlineData("email")]
    [InlineData("sms")]
    [InlineData("lambda")]
    public async Task Subscribe_rejects_unsupported_protocols(string protocol)
    {
        var module = NewModule();
        var ctx = NewContext(
            $"Action=Subscribe&Version=2010-03-31&TopicArn=arn%3Aaws%3Asns%3Aus-east-1%3A000000000000%3Aorders&Protocol={protocol}&Endpoint=stub-endpoint");
        ctx.Items["aws2azure.accessKeyId"] = "AKIAEXAMPLE";

        await module.HandleAsync(ctx);

        Assert.Equal(StatusCodes.Status400BadRequest, ctx.Response.StatusCode);
        Assert.Contains("InvalidParameter", ReadBody(ctx));
    }

    [Fact]
    public async Task Subscribe_succeeds_for_the_sqs_protocol_and_returns_a_subscription_arn()
    {
        var management = new FakeManagementClient();
        var module = NewModule(management);
        var ctx = NewContext(
            "Action=Subscribe&Version=2010-03-31&TopicArn=arn%3Aaws%3Asns%3Aus-east-1%3A000000000000%3Aorders" +
            "&Protocol=sqs&Endpoint=arn%3Aaws%3Asqs%3Aus-east-1%3A000000000000%3Astub-queue");
        ctx.Items["aws2azure.accessKeyId"] = "AKIAEXAMPLE";

        await module.HandleAsync(ctx);

        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
        Assert.Contains("<SubscriptionArn>", ReadBody(ctx));
        Assert.Single(management.Subscriptions);
    }

    [Fact]
    public async Task GetSubscriptionAttributes_requires_a_subscription_arn()
    {
        var module = NewModule();
        var ctx = NewContext("Action=GetSubscriptionAttributes&Version=2010-03-31");
        ctx.Items["aws2azure.accessKeyId"] = "AKIAEXAMPLE";

        await module.HandleAsync(ctx);

        Assert.Equal(StatusCodes.Status400BadRequest, ctx.Response.StatusCode);
        Assert.Contains("InvalidParameter", ReadBody(ctx));
    }

    [Fact]
    public async Task GetSubscriptionAttributes_returns_NotFound_when_the_subscription_is_missing()
    {
        var module = NewModule(new FakeManagementClient());
        var ctx = NewContext(
            "Action=GetSubscriptionAttributes&Version=2010-03-31&SubscriptionArn=arn%3Aaws%3Asns%3Aus-east-1%3A000000000000%3Aorders%3Aabc123");
        ctx.Items["aws2azure.accessKeyId"] = "AKIAEXAMPLE";

        await module.HandleAsync(ctx);

        Assert.Equal(StatusCodes.Status404NotFound, ctx.Response.StatusCode);
        Assert.Contains("NotFound", ReadBody(ctx));
    }

    [Fact]
    public async Task SetSubscriptionAttributes_rejects_unknown_attribute_names()
    {
        var management = new FakeManagementClient();
        management.Subscriptions["orders/abc123"] = new ServiceBusSubscriptionDescription(
            "abc123", null, "PT1M", 10, "PT10M");
        var module = NewModule(management);
        var ctx = NewContext(
            "Action=SetSubscriptionAttributes&Version=2010-03-31" +
            "&SubscriptionArn=arn%3Aaws%3Asns%3Aus-east-1%3A000000000000%3Aorders%3Aabc123" +
            "&AttributeName=NotARealAttribute&AttributeValue=x");
        ctx.Items["aws2azure.accessKeyId"] = "AKIAEXAMPLE";

        await module.HandleAsync(ctx);

        Assert.Equal(StatusCodes.Status400BadRequest, ctx.Response.StatusCode);
        Assert.Contains("InvalidParameter", ReadBody(ctx));
    }

    [Fact]
    public async Task SetSubscriptionAttributes_toggles_RawMessageDelivery_and_GetSubscriptionAttributes_observes_it()
    {
        var management = new FakeManagementClient();
        management.Subscriptions["orders/abc123"] = new ServiceBusSubscriptionDescription(
            "abc123", null, "PT1M", 10, "PT10M");
        var module = NewModule(management);

        var setCtx = NewContext(
            "Action=SetSubscriptionAttributes&Version=2010-03-31" +
            "&SubscriptionArn=arn%3Aaws%3Asns%3Aus-east-1%3A000000000000%3Aorders%3Aabc123" +
            "&AttributeName=RawMessageDelivery&AttributeValue=true");
        setCtx.Items["aws2azure.accessKeyId"] = "AKIAEXAMPLE";
        await module.HandleAsync(setCtx);
        Assert.Equal(StatusCodes.Status200OK, setCtx.Response.StatusCode);

        var getCtx = NewContext(
            "Action=GetSubscriptionAttributes&Version=2010-03-31" +
            "&SubscriptionArn=arn%3Aaws%3Asns%3Aus-east-1%3A000000000000%3Aorders%3Aabc123");
        getCtx.Items["aws2azure.accessKeyId"] = "AKIAEXAMPLE";
        await module.HandleAsync(getCtx);

        Assert.Equal(StatusCodes.Status200OK, getCtx.Response.StatusCode);
        var body = ReadBody(getCtx);
        Assert.Contains("<key>RawMessageDelivery</key><value>true</value>", body);
    }

    [Fact]
    public async Task Unsubscribe_requires_a_subscription_arn()
    {
        var module = NewModule();
        var ctx = NewContext("Action=Unsubscribe&Version=2010-03-31");
        ctx.Items["aws2azure.accessKeyId"] = "AKIAEXAMPLE";

        await module.HandleAsync(ctx);

        Assert.Equal(StatusCodes.Status400BadRequest, ctx.Response.StatusCode);
        Assert.Contains("InvalidParameter", ReadBody(ctx));
    }

    [Fact]
    public async Task ListSubscriptionsByTopic_requires_a_topic_arn()
    {
        var module = NewModule();
        var ctx = NewContext("Action=ListSubscriptionsByTopic&Version=2010-03-31");
        ctx.Items["aws2azure.accessKeyId"] = "AKIAEXAMPLE";

        await module.HandleAsync(ctx);

        Assert.Equal(StatusCodes.Status400BadRequest, ctx.Response.StatusCode);
        Assert.Contains("InvalidParameter", ReadBody(ctx));
    }

    [Fact]
    public async Task ListSubscriptions_does_not_require_any_parameters()
    {
        var module = NewModule(new FakeManagementClient());
        var ctx = NewContext("Action=ListSubscriptions&Version=2010-03-31");
        ctx.Items["aws2azure.accessKeyId"] = "AKIAEXAMPLE";

        await module.HandleAsync(ctx);

        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
        Assert.Contains("<ListSubscriptionsResult", ReadBody(ctx));
    }

    [Fact]
    public async Task SetTopicAttributes_rejects_unknown_attribute_names()
    {
        var management = new FakeManagementClient();
        management.Topics["orders"] = new ServiceBusTopicDescription("orders", 0, false, null, null, null);
        var module = NewModule(management);
        var ctx = NewContext(
            "Action=SetTopicAttributes&Version=2010-03-31" +
            "&TopicArn=arn%3Aaws%3Asns%3Aus-east-1%3A000000000000%3Aorders" +
            "&AttributeName=NotARealAttribute&AttributeValue=x");
        ctx.Items["aws2azure.accessKeyId"] = "AKIAEXAMPLE";

        await module.HandleAsync(ctx);

        Assert.Equal(StatusCodes.Status400BadRequest, ctx.Response.StatusCode);
        Assert.Contains("InvalidParameter", ReadBody(ctx));
    }

    [Fact]
    public async Task SetTopicAttributes_updates_DisplayName_and_GetTopicAttributes_observes_it()
    {
        var management = new FakeManagementClient();
        management.Topics["orders"] = new ServiceBusTopicDescription("orders", 0, false, null, null, null);
        var module = NewModule(management);

        var setCtx = NewContext(
            "Action=SetTopicAttributes&Version=2010-03-31" +
            "&TopicArn=arn%3Aaws%3Asns%3Aus-east-1%3A000000000000%3Aorders" +
            "&AttributeName=DisplayName&AttributeValue=Updated");
        setCtx.Items["aws2azure.accessKeyId"] = "AKIAEXAMPLE";
        await module.HandleAsync(setCtx);
        Assert.Equal(StatusCodes.Status200OK, setCtx.Response.StatusCode);

        var getCtx = NewContext(
            "Action=GetTopicAttributes&Version=2010-03-31" +
            "&TopicArn=arn%3Aaws%3Asns%3Aus-east-1%3A000000000000%3Aorders");
        getCtx.Items["aws2azure.accessKeyId"] = "AKIAEXAMPLE";
        await module.HandleAsync(getCtx);

        Assert.Equal(StatusCodes.Status200OK, getCtx.Response.StatusCode);
        var body = ReadBody(getCtx);
        Assert.Contains("<key>DisplayName</key><value>Updated</value>", body);
    }

    private static SnsServiceModule NewModule(IServiceBusTopicsManagementClient? managementClient = null)
        => new(
            GetResolver(),
            new SnsSettings(),
            managementClient ?? new FakeManagementClient(),
            new NoopSender(),
            new NoopEventGridPublisher(),
            NullLogger<SnsServiceModule>.Instance,
            new CapabilityMatrix("sns", []));

    private static ICredentialResolver GetResolver()
    {
        var azure = new AzureCredentials
        {
            ServiceBusTopics = new ServiceBusTopicsCredentials
            {
                Namespace = "myns",
                SasKeyName = "RootManageSharedAccessKey",
                SasKey = "ZGVhZGJlZWY=",
            },
        };

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

    private sealed class NoopSender : ISnsAmqpSender
    {
        public Task SendAsync(ServiceBusTopicsCredentials credentials, string namespaceFqdn, string topicName, SnsAmqpSendMessage message, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task<SnsBatchSendResult> SendBatchAsync(ServiceBusTopicsCredentials credentials, string namespaceFqdn, string topicName, IReadOnlyList<SnsAmqpSendMessage> messages, CancellationToken cancellationToken)
            => Task.FromResult(new SnsBatchSendResult(messages.Select(_ => new SnsBatchSendOutcome(true, null, null, false)).ToArray()));
    }

    private sealed class NoopEventGridPublisher : IEventGridPublisher
    {
        public Task PublishAsync(EventGridPublishDestination destination, EventGridPublishMessage message, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task<SnsBatchSendResult> PublishBatchAsync(EventGridPublishDestination destination, IReadOnlyList<EventGridPublishMessage> messages, CancellationToken cancellationToken)
            => Task.FromResult(new SnsBatchSendResult(messages.Select(_ => new SnsBatchSendOutcome(true, null, null, false)).ToArray()));
    }

    /// <summary>
    /// Minimal stateful fake standing in for the real Service Bus management
    /// REST client so Subscribe/Get/Set/List/Unsubscribe round-trips can be
    /// exercised without a live Service Bus namespace.
    /// </summary>
    private sealed class FakeManagementClient : IServiceBusTopicsManagementClient
    {
        public Dictionary<string, ServiceBusTopicDescription> Topics { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, ServiceBusSubscriptionDescription> Subscriptions { get; } = new(StringComparer.Ordinal);

        public ValueTask CreateTopicAsync(ServiceBusTopicsCredentials credentials, string namespaceFqdn, ServiceBusTopicDescription description, CancellationToken cancellationToken)
        {
            Topics[description.TopicName] = description;
            return ValueTask.CompletedTask;
        }

        public ValueTask DeleteTopicAsync(ServiceBusTopicsCredentials credentials, string namespaceFqdn, string topicName, CancellationToken cancellationToken)
        {
            Topics.Remove(topicName);
            return ValueTask.CompletedTask;
        }

        public ValueTask<ServiceBusTopicPage> ListTopicsAsync(ServiceBusTopicsCredentials credentials, string namespaceFqdn, int skip, int top, CancellationToken cancellationToken)
            => ValueTask.FromResult(new ServiceBusTopicPage(Topics.Keys.Skip(skip).Take(top).ToArray()));

        public ValueTask<ServiceBusTopicDescription?> GetTopicAsync(ServiceBusTopicsCredentials credentials, string namespaceFqdn, string topicName, CancellationToken cancellationToken)
            => ValueTask.FromResult(Topics.TryGetValue(topicName, out var topic) ? topic : null);

        public ValueTask UpdateTopicAsync(ServiceBusTopicsCredentials credentials, string namespaceFqdn, ServiceBusTopicDescription description, CancellationToken cancellationToken)
        {
            Topics[description.TopicName] = description;
            return ValueTask.CompletedTask;
        }

        public ValueTask CreateSubscriptionAsync(ServiceBusTopicsCredentials credentials, string namespaceFqdn, string topicName, string subscriptionName, string userMetadata, CancellationToken cancellationToken)
        {
            Subscriptions[Key(topicName, subscriptionName)] = new ServiceBusSubscriptionDescription(
                subscriptionName, userMetadata, "PT1M", 10, "PT10M");
            return ValueTask.CompletedTask;
        }

        public ValueTask DeleteSubscriptionAsync(ServiceBusTopicsCredentials credentials, string namespaceFqdn, string topicName, string subscriptionName, CancellationToken cancellationToken)
        {
            Subscriptions.Remove(Key(topicName, subscriptionName));
            return ValueTask.CompletedTask;
        }

        public ValueTask<ServiceBusSubscriptionPage> ListSubscriptionsAsync(ServiceBusTopicsCredentials credentials, string namespaceFqdn, string topicName, int skip, int top, CancellationToken cancellationToken)
        {
            var prefix = topicName + "/";
            var matches = Subscriptions
                .Where(kvp => kvp.Key.StartsWith(prefix, StringComparison.Ordinal))
                .Select(kvp => kvp.Value)
                .Skip(skip)
                .Take(top)
                .ToArray();
            return ValueTask.FromResult(new ServiceBusSubscriptionPage(matches));
        }

        public ValueTask<ServiceBusSubscriptionDescription?> GetSubscriptionAsync(ServiceBusTopicsCredentials credentials, string namespaceFqdn, string topicName, string subscriptionName, CancellationToken cancellationToken)
            => ValueTask.FromResult(Subscriptions.TryGetValue(Key(topicName, subscriptionName), out var sub) ? sub : null);

        public ValueTask UpdateSubscriptionAsync(ServiceBusTopicsCredentials credentials, string namespaceFqdn, string topicName, ServiceBusSubscriptionDescription description, CancellationToken cancellationToken)
        {
            Subscriptions[Key(topicName, description.SubscriptionName)] = description;
            return ValueTask.CompletedTask;
        }

        public ValueTask PutSubscriptionRuleAsync(ServiceBusTopicsCredentials credentials, string namespaceFqdn, string topicName, string subscriptionName, ServiceBusSubscriptionRuleDescription description, bool updateExisting, CancellationToken cancellationToken)
            => ValueTask.CompletedTask;

        public ValueTask DeleteSubscriptionRuleAsync(ServiceBusTopicsCredentials credentials, string namespaceFqdn, string topicName, string subscriptionName, string ruleName, CancellationToken cancellationToken)
            => ValueTask.CompletedTask;

        private static string Key(string topicName, string subscriptionName) => topicName + "/" + subscriptionName;
    }
}
