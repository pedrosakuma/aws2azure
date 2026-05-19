using System;
using Aws2Azure.Core.Configuration;
using Aws2Azure.Modules.Sqs.Internal;
using Xunit;

namespace Aws2Azure.UnitTests.Sqs;

public class ServiceBusClientTests
{
    [Fact]
    public void Bare_namespace_resolves_to_public_cloud_hostname()
    {
        var uri = ServiceBusClient.ResolveEndpoint("my-ns");
        Assert.Equal(new Uri("https://my-ns.servicebus.windows.net/"), uri);
    }

    [Fact]
    public void Absolute_url_is_used_verbatim_for_emulator()
    {
        var uri = ServiceBusClient.ResolveEndpoint("http://127.0.0.1:5672");
        Assert.Equal(new Uri("http://127.0.0.1:5672/"), uri);
    }

    [Fact]
    public void Build_uri_appends_path_and_query_under_base_endpoint()
    {
        var creds = new ServiceBusCredentials
        {
            Namespace = "my-ns",
            SasKeyName = "RootManageSharedAccessKey",
            SasKey = Convert.ToBase64String(new byte[] { 1, 2, 3 }),
        };
        var client = new ServiceBusClient(
            new Aws2Azure.Core.Azure.AzureHttpClient(),
            creds);

        var uri = client.BuildUri("myqueue/messages", "timeout=0");
        Assert.Equal("https://my-ns.servicebus.windows.net/myqueue/messages?timeout=0", uri.AbsoluteUri);
    }

    [Fact]
    public void Resolve_endpoint_rejects_namespace_with_path_separator()
    {
        Assert.Throws<ArgumentException>(() =>
            ServiceBusClient.ResolveEndpoint("evil.example/path"));
    }

    [Fact]
    public void Resolve_endpoint_rejects_namespace_with_dots()
    {
        // A dotted "namespace" would be interpolated to evil.com.servicebus.windows.net —
        // technically still a Microsoft subdomain, but the validation enforces
        // single-label namespaces per Azure portal rules.
        Assert.Throws<ArgumentException>(() => ServiceBusClient.ResolveEndpoint("evil.com"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("-leading-hyphen")]
    [InlineData("trailing-hyphen-")]
    [InlineData("1starts-with-digit")]
    [InlineData("has_underscore")]
    public void Resolve_endpoint_rejects_invalid_dns_labels(string value)
    {
        Assert.Throws<ArgumentException>(() => ServiceBusClient.ResolveEndpoint(value));
    }

    [Theory]
    [InlineData("my-ns")]
    [InlineData("a")]
    [InlineData("ns123")]
    public void Resolve_endpoint_accepts_valid_dns_labels(string value)
    {
        var uri = ServiceBusClient.ResolveEndpoint(value);
        Assert.Equal($"https://{value}.servicebus.windows.net/", uri.AbsoluteUri);
    }

    [Fact]
    public void Missing_namespace_throws()
    {
        Assert.Throws<ArgumentException>(() => new ServiceBusClient(
            new Aws2Azure.Core.Azure.AzureHttpClient(),
            new ServiceBusCredentials { SasKeyName = "k", SasKey = "v" }));
    }

    [Fact]
    public void Missing_sas_key_throws()
    {
        Assert.Throws<ArgumentException>(() => new ServiceBusClient(
            new Aws2Azure.Core.Azure.AzureHttpClient(),
            new ServiceBusCredentials { Namespace = "ns", SasKeyName = "k" }));
    }
}
