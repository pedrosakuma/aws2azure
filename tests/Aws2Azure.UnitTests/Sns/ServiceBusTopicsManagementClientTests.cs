using System.Net;
using System.Net.Http;
using System.Text;
using Aws2Azure.Modules.Sns.Management;

namespace Aws2Azure.UnitTests.Sns;

public sealed class ServiceBusTopicsManagementClientTests
{
    [Fact]
    public async Task PutSubscriptionRuleAsync_retries_transient_forbidden_response()
    {
        var attempts = 0;
        var delays = new List<TimeSpan>();
        var managementClient = SnsManagementClientTestSupport.NewManagementClient(
            (_, _) =>
            {
                attempts++;
                return Task.FromResult(new HttpResponseMessage(attempts == 1 ? HttpStatusCode.Forbidden : HttpStatusCode.OK)
                {
                    Content = new StringContent(string.Empty, Encoding.UTF8, "application/atom+xml"),
                });
            },
            (delay, _) =>
            {
                delays.Add(delay);
                return ValueTask.CompletedTask;
            });

        await managementClient.PutSubscriptionRuleAsync(
            SnsManagementClientTestSupport.NewCredentials(),
            "myns.servicebus.windows.net",
            "orders",
            "sub123",
            new ServiceBusSubscriptionRuleDescription("aws2azure", "1=1"),
            updateExisting: false,
            CancellationToken.None);

        Assert.Equal(2, attempts);
        Assert.Equal(new[] { TimeSpan.FromSeconds(2) }, delays);
    }

    [Fact]
    public async Task PutSubscriptionRuleAsync_throws_after_exhausting_authorization_retries()
    {
        var attempts = 0;
        var delays = new List<TimeSpan>();
        var managementClient = SnsManagementClientTestSupport.NewManagementClient(
            (_, _) =>
            {
                attempts++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)
                {
                    Content = new StringContent("nope", Encoding.UTF8, "application/atom+xml"),
                });
            },
            (delay, _) =>
            {
                delays.Add(delay);
                return ValueTask.CompletedTask;
            });

        var exception = await Assert.ThrowsAsync<ServiceBusTopicsManagementException>(() =>
            managementClient.PutSubscriptionRuleAsync(
                SnsManagementClientTestSupport.NewCredentials(),
                "myns.servicebus.windows.net",
                "orders",
                "sub123",
                new ServiceBusSubscriptionRuleDescription("aws2azure", "1=1"),
                updateExisting: false,
                CancellationToken.None).AsTask());

        Assert.Equal(HttpStatusCode.Forbidden, exception.StatusCode);
        Assert.Equal("nope", exception.ResponseBody);
        Assert.Equal(6, attempts);
        Assert.Equal(
            new[] { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(16), TimeSpan.FromSeconds(30) },
            delays);
    }

    [Fact]
    public async Task DeleteSubscriptionAsync_retries_transient_forbidden_probe_before_delete()
    {
        var attempts = 0;
        var delays = new List<TimeSpan>();
        var managementClient = SnsManagementClientTestSupport.NewManagementClient(
            (_, _) =>
            {
                attempts++;
                return Task.FromResult(attempts switch
                {
                    1 => new HttpResponseMessage(HttpStatusCode.Forbidden)
                    {
                        Content = new StringContent("warming up", Encoding.UTF8, "application/atom+xml"),
                    },
                    2 => new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(
                            SnsManagementClientTestSupport.BuildSubscriptionEntry("sub123", "{}"),
                            Encoding.UTF8,
                            "application/atom+xml"),
                    },
                    _ => new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(string.Empty, Encoding.UTF8, "application/atom+xml"),
                    }
                });
            },
            (delay, _) =>
            {
                delays.Add(delay);
                return ValueTask.CompletedTask;
            });

        await managementClient.DeleteSubscriptionAsync(
            SnsManagementClientTestSupport.NewCredentials(),
            "myns.servicebus.windows.net",
            "orders",
            "sub123",
            CancellationToken.None);

        Assert.Equal(3, attempts);
        Assert.Equal(new[] { TimeSpan.FromSeconds(2) }, delays);
    }

    [Fact]
    public async Task ListTopicsAsync_retries_transient_forbidden_response()
    {
        var attempts = 0;
        var delays = new List<TimeSpan>();
        var managementClient = SnsManagementClientTestSupport.NewManagementClient(
            (_, _) =>
            {
                attempts++;
                return Task.FromResult(attempts == 1
                    ? new HttpResponseMessage(HttpStatusCode.Forbidden)
                    {
                        Content = new StringContent("warming up", Encoding.UTF8, "application/atom+xml"),
                    }
                    : new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(
                            SnsManagementClientTestSupport.BuildTopicsFeed("orders"),
                            Encoding.UTF8,
                            "application/atom+xml"),
                    });
            },
            (delay, _) =>
            {
                delays.Add(delay);
                return ValueTask.CompletedTask;
            });

        var page = await managementClient.ListTopicsAsync(
            SnsManagementClientTestSupport.NewCredentials(),
            "myns.servicebus.windows.net",
            skip: 0,
            top: 10,
            CancellationToken.None);

        Assert.Single(page.TopicNames, "orders");
        Assert.Equal(2, attempts);
        Assert.Equal(new[] { TimeSpan.FromSeconds(2) }, delays);
    }

    [Fact]
    public async Task ListSubscriptionsAsync_retries_transient_forbidden_response()
    {
        var attempts = 0;
        var delays = new List<TimeSpan>();
        var managementClient = SnsManagementClientTestSupport.NewManagementClient(
            (_, _) =>
            {
                attempts++;
                return Task.FromResult(attempts == 1
                    ? new HttpResponseMessage(HttpStatusCode.Forbidden)
                    {
                        Content = new StringContent("warming up", Encoding.UTF8, "application/atom+xml"),
                    }
                    : new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(
                            SnsManagementClientTestSupport.BuildSubscriptionsFeed(("sub123", "{}")),
                            Encoding.UTF8,
                            "application/atom+xml"),
                    });
            },
            (delay, _) =>
            {
                delays.Add(delay);
                return ValueTask.CompletedTask;
            });

        var page = await managementClient.ListSubscriptionsAsync(
            SnsManagementClientTestSupport.NewCredentials(),
            "myns.servicebus.windows.net",
            "orders",
            skip: 0,
            top: 10,
            CancellationToken.None);

        Assert.Single(page.Subscriptions);
        Assert.Equal("sub123", page.Subscriptions[0].SubscriptionName);
        Assert.Equal(2, attempts);
        Assert.Equal(new[] { TimeSpan.FromSeconds(2) }, delays);
    }
}
