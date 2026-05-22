using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Xml;
using Aws2Azure.Core.Azure;
using Aws2Azure.Core.Configuration;
using Microsoft.Extensions.Logging;

namespace Aws2Azure.Modules.Sns.Management;

public interface IServiceBusTopicsManagementClient
{
    ValueTask CreateTopicAsync(
        ServiceBusTopicsCredentials credentials,
        string namespaceFqdn,
        string topicName,
        CancellationToken cancellationToken);

    ValueTask DeleteTopicAsync(
        ServiceBusTopicsCredentials credentials,
        string namespaceFqdn,
        string topicName,
        CancellationToken cancellationToken);

    ValueTask<ServiceBusTopicPage> ListTopicsAsync(
        ServiceBusTopicsCredentials credentials,
        string namespaceFqdn,
        int skip,
        int top,
        CancellationToken cancellationToken);
}

public sealed class ServiceBusTopicsManagementClient : IServiceBusTopicsManagementClient
{
    public const string ApiVersion = "2021-05";

    private const string AtomNamespace = "http://www.w3.org/2005/Atom";
    private const string ServiceBusNamespace = "http://schemas.microsoft.com/netservices/2010/10/servicebus/connect";
    private const string XmlSchemaInstanceNamespace = "http://www.w3.org/2001/XMLSchema-instance";

    private static readonly XmlReaderSettings ReaderSettings = new()
    {
        Async = true,
        DtdProcessing = DtdProcessing.Prohibit,
        IgnoreComments = true,
        IgnoreWhitespace = true,
    };

    private static readonly XmlWriterSettings WriterSettings = new()
    {
        Encoding = Encoding.UTF8,
        Indent = false,
        OmitXmlDeclaration = false,
        CloseOutput = false,
    };

    private readonly AzureHttpClient _httpClient;
    private readonly IServiceBusTopicsAuthenticator _authenticator;
    private readonly ILogger<ServiceBusTopicsManagementClient> _logger;

    public ServiceBusTopicsManagementClient(
        AzureHttpClient httpClient,
        IServiceBusTopicsAuthenticator authenticator,
        ILogger<ServiceBusTopicsManagementClient> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(authenticator);
        ArgumentNullException.ThrowIfNull(logger);
        _httpClient = httpClient;
        _authenticator = authenticator;
        _logger = logger;
    }

    public async ValueTask CreateTopicAsync(
        ServiceBusTopicsCredentials credentials,
        string namespaceFqdn,
        string topicName,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentException.ThrowIfNullOrWhiteSpace(namespaceFqdn);
        ArgumentException.ThrowIfNullOrWhiteSpace(topicName);

        var requestUri = BuildTopicUri(credentials, namespaceFqdn, topicName);
        ServiceBusTopicsManagementClientLog.CreatingTopic(_logger, namespaceFqdn, topicName);

        using var request = new HttpRequestMessage(HttpMethod.Put, requestUri);
        request.Headers.TryAddWithoutValidation("Accept", "application/atom+xml");
        request.Content = new StringContent(BuildTopicDescriptionEntry(), Encoding.UTF8, "application/atom+xml");
        request.Content.Headers.ContentType!.Parameters.Add(new NameValueHeaderValue("type", "entry"));
        await _authenticator.AuthenticateAsync(request, credentials, cancellationToken).ConfigureAwait(false);

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
            .ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created)
        {
            return;
        }

        var errorBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        ServiceBusTopicsManagementClientLog.TopicRequestFailed(_logger, nameof(CreateTopicAsync), namespaceFqdn, topicName, (int)response.StatusCode);
        throw new ServiceBusTopicsManagementException(response.StatusCode, errorBody);
    }

    public async ValueTask DeleteTopicAsync(
        ServiceBusTopicsCredentials credentials,
        string namespaceFqdn,
        string topicName,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentException.ThrowIfNullOrWhiteSpace(namespaceFqdn);
        ArgumentException.ThrowIfNullOrWhiteSpace(topicName);

        var requestUri = BuildTopicUri(credentials, namespaceFqdn, topicName);
        ServiceBusTopicsManagementClientLog.DeletingTopic(_logger, namespaceFqdn, topicName);

        using var request = new HttpRequestMessage(HttpMethod.Delete, requestUri);
        await _authenticator.AuthenticateAsync(request, credentials, cancellationToken).ConfigureAwait(false);

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
            .ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent or HttpStatusCode.NotFound)
        {
            return;
        }

        var errorBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        ServiceBusTopicsManagementClientLog.TopicRequestFailed(_logger, nameof(DeleteTopicAsync), namespaceFqdn, topicName, (int)response.StatusCode);
        throw new ServiceBusTopicsManagementException(response.StatusCode, errorBody);
    }

    public async ValueTask<ServiceBusTopicPage> ListTopicsAsync(
        ServiceBusTopicsCredentials credentials,
        string namespaceFqdn,
        int skip,
        int top,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentException.ThrowIfNullOrWhiteSpace(namespaceFqdn);
        ArgumentOutOfRangeException.ThrowIfNegative(skip);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(top);

        var requestUri = BuildListTopicsUri(credentials, namespaceFqdn, skip, top);
        ServiceBusTopicsManagementClientLog.ListingTopics(_logger, namespaceFqdn, skip, top);

        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.TryAddWithoutValidation("Accept", "application/atom+xml");
        await _authenticator.AuthenticateAsync(request, credentials, cancellationToken).ConfigureAwait(false);

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            ServiceBusTopicsManagementClientLog.TopicRequestFailed(_logger, nameof(ListTopicsAsync), namespaceFqdn, "$Resources/topics", (int)response.StatusCode);
            throw new ServiceBusTopicsManagementException(response.StatusCode, errorBody);
        }

        var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var topicNames = await ParseTopicNamesAsync(content, cancellationToken).ConfigureAwait(false);
        return new ServiceBusTopicPage(topicNames);
    }

    internal static string BuildTopicDescriptionEntry()
    {
        var builder = new StringBuilder();
        using var stringWriter = new Utf8StringWriter(builder);
        using var writer = XmlWriter.Create(stringWriter, WriterSettings);
        writer.WriteStartDocument();
        writer.WriteStartElement("entry", AtomNamespace);
        writer.WriteStartElement("content", AtomNamespace);
        writer.WriteAttributeString("type", "application/xml");
        writer.WriteStartElement("TopicDescription", ServiceBusNamespace);
        writer.WriteAttributeString("xmlns", "i", null, XmlSchemaInstanceNamespace);
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndDocument();
        writer.Flush();
        return builder.ToString();
    }

    internal static async ValueTask<IReadOnlyList<string>> ParseTopicNamesAsync(string content, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return [];
        }

        var topicNames = new List<string>();
        using var stringReader = new StringReader(content);
        using var reader = XmlReader.Create(stringReader, ReaderSettings);

        var entryDepth = -1;
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (reader.NodeType == XmlNodeType.Element
                && reader.Depth == 1
                && reader.LocalName == "entry"
                && reader.NamespaceURI == AtomNamespace)
            {
                entryDepth = reader.Depth;
                continue;
            }

            if (reader.NodeType == XmlNodeType.EndElement
                && reader.Depth == entryDepth
                && reader.LocalName == "entry"
                && reader.NamespaceURI == AtomNamespace)
            {
                entryDepth = -1;
                continue;
            }

            if (entryDepth >= 0
                && reader.NodeType == XmlNodeType.Element
                && reader.Depth == entryDepth + 1
                && reader.LocalName == "title"
                && reader.NamespaceURI == AtomNamespace)
            {
                var title = await reader.ReadElementContentAsStringAsync().ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(title))
                {
                    topicNames.Add(title);
                }
            }
        }

        return topicNames;
    }

    private static Uri BuildTopicUri(ServiceBusTopicsCredentials credentials, string namespaceFqdn, string topicName)
    {
        var scheme = ResolveManagementScheme(credentials);
        return new Uri($"{scheme}://{namespaceFqdn.TrimEnd('/')}/{Uri.EscapeDataString(topicName)}?api-version={ApiVersion}", UriKind.Absolute);
    }

    private static Uri BuildListTopicsUri(ServiceBusTopicsCredentials credentials, string namespaceFqdn, int skip, int top)
    {
        var scheme = ResolveManagementScheme(credentials);
        return new Uri($"{scheme}://{namespaceFqdn.TrimEnd('/')}/$Resources/topics?api-version={ApiVersion}&$skip={skip}&$top={top}", UriKind.Absolute);
    }

    private static string ResolveManagementScheme(ServiceBusTopicsCredentials credentials)
    {
        if (!string.IsNullOrWhiteSpace(credentials.Endpoint)
            && Uri.TryCreate(credentials.Endpoint, UriKind.Absolute, out var endpointUri)
            && string.Equals(endpointUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
        {
            return Uri.UriSchemeHttp;
        }

        return Uri.UriSchemeHttps;
    }

    private sealed class Utf8StringWriter(StringBuilder builder) : StringWriter(builder)
    {
        public override Encoding Encoding => Encoding.UTF8;
    }
}

public sealed record ServiceBusTopicPage(IReadOnlyList<string> TopicNames);

public sealed class ServiceBusTopicsManagementException : Exception
{
    public ServiceBusTopicsManagementException(HttpStatusCode statusCode, string? responseBody)
        : base($"Service Bus Topics management API returned {(int)statusCode}.")
    {
        StatusCode = statusCode;
        ResponseBody = responseBody;
    }

    public HttpStatusCode StatusCode { get; }
    public string? ResponseBody { get; }
}

internal static partial class ServiceBusTopicsManagementClientLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Debug,
        Message = "Creating Service Bus topic for namespace '{NamespaceFqdn}' and entity '{TopicName}'")]
    public static partial void CreatingTopic(ILogger logger, string namespaceFqdn, string topicName);

    [LoggerMessage(EventId = 2, Level = LogLevel.Debug,
        Message = "Deleting Service Bus topic for namespace '{NamespaceFqdn}' and entity '{TopicName}'")]
    public static partial void DeletingTopic(ILogger logger, string namespaceFqdn, string topicName);

    [LoggerMessage(EventId = 3, Level = LogLevel.Debug,
        Message = "Listing Service Bus topics for namespace '{NamespaceFqdn}' with skip={Skip} top={Top}")]
    public static partial void ListingTopics(ILogger logger, string namespaceFqdn, int skip, int top);

    [LoggerMessage(EventId = 4, Level = LogLevel.Warning,
        Message = "Service Bus Topics request '{Operation}' for namespace '{NamespaceFqdn}' and entity '{EntityName}' failed with HTTP {StatusCode}")]
    public static partial void TopicRequestFailed(ILogger logger, string operation, string namespaceFqdn, string entityName, int statusCode);
}
