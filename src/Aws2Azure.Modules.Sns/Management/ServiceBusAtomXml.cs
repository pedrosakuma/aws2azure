using System.Globalization;
using System.IO;
using System.Text;
using System.Xml;
using Aws2Azure.Core.Xml;

namespace Aws2Azure.Modules.Sns.Management;

/// <summary>
/// Atom/Service Bus XML serialization and parsing for the Service Bus Topics
/// management API. Extracted from <see cref="ServiceBusTopicsManagementClient"/>
/// so the HTTP/transport client stays focused on request shaping and error
/// mapping while the (verbose, schema-order-sensitive) Atom payload handling
/// lives in one cohesive place.
///
/// <para>Uses <see cref="XmlReader"/>/<see cref="XmlWriter"/> directly — never a
/// reflection-based serializer — to stay Native AOT friendly.</para>
/// </summary>
internal static class ServiceBusAtomXml
{
    private const string AtomNamespace = "http://www.w3.org/2005/Atom";
    private const string ServiceBusNamespace = "http://schemas.microsoft.com/netservices/2010/10/servicebus/connect";
    private const string XmlSchemaInstanceNamespace = "http://www.w3.org/2001/XMLSchema-instance";
    private const string DataServicesMetadataNamespace = "http://schemas.microsoft.com/ado/2007/08/dataservices/metadata";

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

    internal static string BuildTopicDescriptionEntry(bool? requiresDuplicateDetection = null)
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
        if (requiresDuplicateDetection.HasValue)
        {
            writer.WriteElementString("RequiresDuplicateDetection", ServiceBusNamespace, requiresDuplicateDetection.Value ? "true" : "false");
        }

        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndDocument();
        writer.Flush();
        return builder.ToString();
    }

    internal static string BuildSubscriptionDescriptionEntry(string userMetadata)
        => BuildSubscriptionDescriptionEntry(new ServiceBusSubscriptionDescription(
            SubscriptionName: string.Empty,
            UserMetadata: userMetadata,
            LockDuration: ServiceBusTopicsManagementClient.DefaultLockDurationIso8601,
            MaxDeliveryCount: ServiceBusTopicsManagementClient.DefaultMaxDeliveryCount,
            AutoDeleteOnIdle: ServiceBusTopicsManagementClient.LongIdleIso8601));

    internal static string BuildSubscriptionDescriptionEntry(ServiceBusSubscriptionDescription description)
    {
        ArgumentNullException.ThrowIfNull(description);

        var builder = new StringBuilder();
        using var stringWriter = new Utf8StringWriter(builder);
        using var writer = XmlWriter.Create(stringWriter, WriterSettings);
        writer.WriteStartDocument();
        writer.WriteStartElement("entry", AtomNamespace);
        writer.WriteStartElement("content", AtomNamespace);
        writer.WriteAttributeString("type", "application/xml");
        writer.WriteStartElement("SubscriptionDescription", ServiceBusNamespace);
        writer.WriteAttributeString("xmlns", "i", null, XmlSchemaInstanceNamespace);
        if (description.Properties is { Count: > 0 })
        {
            WriteMergedSubscriptionProperties(writer, description);
        }
        else
        {
            writer.WriteElementString("LockDuration", ServiceBusNamespace, string.IsNullOrWhiteSpace(description.LockDuration) ? ServiceBusTopicsManagementClient.DefaultLockDurationIso8601 : description.LockDuration);
            writer.WriteElementString("MaxDeliveryCount", ServiceBusNamespace, description.MaxDeliveryCount <= 0 ? ServiceBusTopicsManagementClient.DefaultMaxDeliveryCount.ToString(CultureInfo.InvariantCulture) : description.MaxDeliveryCount.ToString(CultureInfo.InvariantCulture));
            // UserMetadata must appear BEFORE AutoDeleteOnIdle in the canonical SubscriptionDescription
            // schema order. Real Service Bus rejects out-of-order PUTs with HTTP 400; the SB emulator
            // accepts them but silently drops fields that appear out of position. Keep this order stable.
            writer.WriteElementString("UserMetadata", ServiceBusNamespace, description.UserMetadata ?? string.Empty);
            writer.WriteElementString("AutoDeleteOnIdle", ServiceBusNamespace, string.IsNullOrWhiteSpace(description.AutoDeleteOnIdle) ? ServiceBusTopicsManagementClient.LongIdleIso8601 : description.AutoDeleteOnIdle);
        }

        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndDocument();
        writer.Flush();
        return builder.ToString();
    }

    internal static async ValueTask<IReadOnlyList<string>> ParseTopicNamesAsync(string content, CancellationToken cancellationToken)
    {
        var entries = await ParseFeedEntriesAsync(content, cancellationToken).ConfigureAwait(false);
        var topicNames = new List<string>(entries.Count);
        for (var i = 0; i < entries.Count; i++)
        {
            if (!string.IsNullOrWhiteSpace(entries[i].Title))
            {
                topicNames.Add(entries[i].Title!);
            }
        }

        return topicNames;
    }

    internal static async ValueTask<IReadOnlyList<ServiceBusSubscriptionDescription>> ParseSubscriptionDescriptionsAsync(string content, CancellationToken cancellationToken)
    {
        var entries = await ParseFeedEntriesAsync(content, cancellationToken).ConfigureAwait(false);
        var subscriptions = new List<ServiceBusSubscriptionDescription>(entries.Count);
        for (var i = 0; i < entries.Count; i++)
        {
            if (!string.IsNullOrWhiteSpace(entries[i].Title))
            {
                subscriptions.Add(new ServiceBusSubscriptionDescription(
                    entries[i].Title!,
                    entries[i].UserMetadata,
                    entries[i].LockDuration ?? ServiceBusTopicsManagementClient.DefaultLockDurationIso8601,
                    entries[i].MaxDeliveryCount ?? ServiceBusTopicsManagementClient.DefaultMaxDeliveryCount,
                    entries[i].AutoDeleteOnIdle ?? ServiceBusTopicsManagementClient.LongIdleIso8601,
                    entries[i].ETag,
                    entries[i].SubscriptionProperties));
            }
        }

        return subscriptions;
    }

    internal static async ValueTask<AtomEntryData?> ParseFirstEntryAsync(string content, CancellationToken cancellationToken)
    {
        var entries = await ParseFeedEntriesAsync(content, cancellationToken).ConfigureAwait(false);
        return entries.Count == 0 ? null : entries[0];
    }

    private static async ValueTask<IReadOnlyList<AtomEntryData>> ParseFeedEntriesAsync(string content, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return [];
        }

        var entries = new List<AtomEntryData>();
        using var stringReader = new StringReader(content);
        using var reader = XmlReader.Create(stringReader, ReaderSettings);

        if (!await reader.ReadAsync().ConfigureAwait(false))
        {
            return entries;
        }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (reader.NodeType == XmlNodeType.Element
                && reader.LocalName == "entry"
                && reader.NamespaceURI == AtomNamespace)
            {
                using var entryReader = reader.ReadSubtree();
                entries.Add(await ParseEntryAsync(entryReader, cancellationToken).ConfigureAwait(false));
                reader.Skip();
                if (reader.EOF)
                {
                    break;
                }

                continue;
            }

            if (!await reader.ReadAsync().ConfigureAwait(false))
            {
                break;
            }
        }

        return entries;
    }

    private static async ValueTask<AtomEntryData> ParseEntryAsync(XmlReader reader, CancellationToken cancellationToken)
    {
        string? title = null;
        string? userMetadata = null;
        int? subscriptionCount = null;
        bool? requiresDuplicateDetection = null;
        string? lockDuration = null;
        int? maxDeliveryCount = null;
        string? autoDeleteOnIdle = null;
        string? etag = null;
        var subscriptionDescriptionDepth = -1;
        var subscriptionProperties = new List<ServiceBusSubscriptionProperty>();

        // ReadElementContentAsStringAsync consumes the element AND advances to the
        // next node, so after reading a value we must re-inspect the current node
        // WITHOUT advancing again (otherwise an immediately-following element would
        // be skipped — IgnoreWhitespace removes the text nodes between them).
        // `reRead` records that case and suppresses the next ReadAsync.
        var reRead = false;
        while (true)
        {
            if (!reRead && !await reader.ReadAsync().ConfigureAwait(false))
            {
                break;
            }

            reRead = false;
            cancellationToken.ThrowIfCancellationRequested();

            if (reader.NodeType != XmlNodeType.Element)
            {
                continue;
            }

            if (reader.LocalName == "entry" && reader.NamespaceURI == AtomNamespace)
            {
                etag = reader.GetAttribute("etag", DataServicesMetadataNamespace)
                    ?? reader.GetAttribute("ETag")
                    ?? reader.GetAttribute("etag");
            }

            if (reader.LocalName == "title" && reader.NamespaceURI == AtomNamespace)
            {
                title = await reader.ReadElementContentAsStringAsync().ConfigureAwait(false);
                reRead = true;
                continue;
            }

            if (reader.NamespaceURI == ServiceBusNamespace)
            {
                if (reader.LocalName == "SubscriptionDescription")
                {
                    subscriptionDescriptionDepth = reader.Depth;
                    continue;
                }

                if (subscriptionDescriptionDepth >= 0 && reader.Depth == subscriptionDescriptionDepth + 1)
                {
                    var localName = reader.LocalName;
                    var rawXml = await reader.ReadOuterXmlAsync().ConfigureAwait(false);
                    if (!IsReadOnlySubscriptionProperty(localName))
                    {
                        subscriptionProperties.Add(new ServiceBusSubscriptionProperty(localName, rawXml));
                    }
                    switch (localName)
                    {
                        case "UserMetadata":
                            userMetadata = ReadElementValue(rawXml);
                            break;
                        case "LockDuration":
                            lockDuration = ReadElementValue(rawXml);
                            break;
                        case "MaxDeliveryCount":
                            if (int.TryParse(ReadElementValue(rawXml), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedMaxDeliveryCount))
                            {
                                maxDeliveryCount = parsedMaxDeliveryCount;
                            }
                            break;
                        case "AutoDeleteOnIdle":
                            autoDeleteOnIdle = ReadElementValue(rawXml);
                            break;
                    }

                    reRead = true;
                    continue;
                }

                switch (reader.LocalName)
                {
                    case "SubscriptionCount":
                        if (int.TryParse(await reader.ReadElementContentAsStringAsync().ConfigureAwait(false), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedSubscriptionCount))
                        {
                            subscriptionCount = parsedSubscriptionCount;
                        }

                        reRead = true;
                        continue;
                    case "RequiresDuplicateDetection":
                        if (bool.TryParse(await reader.ReadElementContentAsStringAsync().ConfigureAwait(false), out var parsedRequiresDuplicateDetection))
                        {
                            requiresDuplicateDetection = parsedRequiresDuplicateDetection;
                        }

                        reRead = true;
                        continue;
                }
            }
        }

        return new AtomEntryData(
            title,
            userMetadata,
            subscriptionCount,
            requiresDuplicateDetection,
            lockDuration,
            maxDeliveryCount,
            autoDeleteOnIdle,
            etag,
            subscriptionProperties);
    }

    private static bool IsReadOnlySubscriptionProperty(string localName)
        => localName is
            "AccessedAt"
            or "AvailabilityStatus"
            or "CountDetails"
            or "CreatedAt"
            or "MessageCount"
            or "SizeInBytes"
            or "UpdatedAt";

    private static void WriteMergedSubscriptionProperties(XmlWriter writer, ServiceBusSubscriptionDescription description)
    {
        var wroteUserMetadata = false;
        foreach (var property in description.Properties!)
        {
            if (string.Equals(property.LocalName, "UserMetadata", StringComparison.Ordinal))
            {
                writer.WriteElementString("UserMetadata", ServiceBusNamespace, description.UserMetadata ?? string.Empty);
                wroteUserMetadata = true;
                continue;
            }

            if (!wroteUserMetadata
                && string.Equals(property.LocalName, "AutoDeleteOnIdle", StringComparison.Ordinal))
            {
                writer.WriteElementString("UserMetadata", ServiceBusNamespace, description.UserMetadata ?? string.Empty);
                wroteUserMetadata = true;
            }

            writer.WriteRaw(property.Xml);
        }

        if (!wroteUserMetadata)
        {
            writer.WriteElementString("UserMetadata", ServiceBusNamespace, description.UserMetadata ?? string.Empty);
        }
    }

    private static string ReadElementValue(string rawXml)
    {
        using var stringReader = new StringReader(rawXml);
        using var reader = XmlReader.Create(stringReader, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            IgnoreComments = true,
        });
        return reader.Read() ? reader.ReadElementContentAsString() : string.Empty;
    }

    internal sealed record AtomEntryData(
        string? Title,
        string? UserMetadata,
        int? SubscriptionCount,
        bool? RequiresDuplicateDetection,
        string? LockDuration,
        int? MaxDeliveryCount,
        string? AutoDeleteOnIdle,
        string? ETag,
        IReadOnlyList<ServiceBusSubscriptionProperty> SubscriptionProperties);
}
