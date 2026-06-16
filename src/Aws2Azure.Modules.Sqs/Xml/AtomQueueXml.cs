using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Xml;
using Aws2Azure.Core.Xml;
using Aws2Azure.Modules.Sqs.Internal;

namespace Aws2Azure.Modules.Sqs.Xml;

/// <summary>
/// Hand-rolled XmlReader-based parser for Service Bus Atom responses.
/// XmlSerializer is banned (reflection-heavy), and the SB Atom payload is
/// small and well-known, so a forward-only reader is the right size.
///
/// <para>Supported shapes:</para>
/// <list type="bullet">
///   <item>Single <c>&lt;entry&gt;</c> with embedded <c>&lt;QueueDescription&gt;</c>
///         (CreateQueue response, GetQueue response).</item>
///   <item><c>&lt;feed&gt;</c> with a sequence of <c>&lt;entry&gt;</c>
///         elements (ListQueues response).</item>
/// </list>
/// </summary>
internal static class AtomQueueXmlReader
{
    public const string AtomNs = "http://www.w3.org/2005/Atom";
    public const string SbNs = "http://schemas.microsoft.com/netservices/2010/10/servicebus/connect";

    /// <summary>
    /// Parsed view of an Atom queue entry (used by ListQueues + GetQueue).
    /// </summary>
    public sealed record QueueEntry(
        string Name,
        QueueDescriptionProperties Properties);

    public static QueueEntry? ParseQueueEntry(string xml)
    {
        using var reader = CreateReader(xml);
        return ReadEntry(reader);
    }

    public static IReadOnlyList<QueueEntry> ParseQueueFeed(string xml)
    {
        var entries = new List<QueueEntry>();
        using var reader = CreateReader(xml);
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element
                && reader.LocalName == "entry"
                && reader.NamespaceURI == AtomNs)
            {
                var entry = ReadEntryBody(reader);
                if (entry is not null) entries.Add(entry);
            }
        }
        return entries;
    }

    private static QueueEntry? ReadEntry(XmlReader reader)
    {
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element
                && reader.LocalName == "entry"
                && reader.NamespaceURI == AtomNs)
            {
                return ReadEntryBody(reader);
            }
        }
        return null;
    }

    private static QueueEntry? ReadEntryBody(XmlReader reader)
    {
        string? title = null;
        DateTimeOffset? updated = null;
        DateTimeOffset? published = null;
        QueueDescriptionProperties? props = null;

        if (reader.IsEmptyElement) return null;
        var depth = reader.Depth;

        // ReadElementContentAsString / ReadDate / ReadContent each leave the
        // reader positioned on the node *following* the consumed element.
        // Tracking that with a flag avoids the classic XmlReader bug where
        // the loop's Read() then skips the very next sibling.
        var advanced = false;
        while (advanced || reader.Read())
        {
            advanced = false;
            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth) break;
            if (reader.NodeType != XmlNodeType.Element) continue;

            if (reader.NamespaceURI == AtomNs)
            {
                switch (reader.LocalName)
                {
                    case "title":
                        title = reader.ReadElementContentAsString();
                        advanced = true;
                        break;
                    case "updated":
                        updated = ReadDate(reader);
                        advanced = true;
                        break;
                    case "published":
                        published = ReadDate(reader);
                        advanced = true;
                        break;
                    case "content":
                        props = ReadContent(reader);
                        advanced = true;
                        break;
                }
            }
        }

        if (string.IsNullOrEmpty(title)) return null;
        props ??= new QueueDescriptionProperties();
        props.CreatedAt ??= published;
        props.UpdatedAt ??= updated;
        return new QueueEntry(title, props);
    }

    private static QueueDescriptionProperties? ReadContent(XmlReader reader)
    {
        if (reader.IsEmptyElement) return null;
        var depth = reader.Depth;

        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth) break;
            if (reader.NodeType != XmlNodeType.Element) continue;

            if (reader.LocalName == "QueueDescription" && reader.NamespaceURI == SbNs)
            {
                return ReadQueueDescription(reader);
            }
        }
        return null;
    }

    private static QueueDescriptionProperties ReadQueueDescription(XmlReader reader)
    {
        var p = new QueueDescriptionProperties();
        if (reader.IsEmptyElement) return p;
        var depth = reader.Depth;

        // See ReadEntryBody for why we track `advanced` rather than relying
        // on the loop's Read() to position us — every helper below consumes
        // the element and moves the reader to the following node.
        var advanced = false;
        while (advanced || reader.Read())
        {
            advanced = false;
            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth) break;
            if (reader.NodeType != XmlNodeType.Element) continue;
            if (reader.NamespaceURI != SbNs) continue;

            switch (reader.LocalName)
            {
                case "LockDuration":
                    p.LockDuration = reader.ReadElementContentAsString();
                    p.LockDurationSeconds = ParseIsoDurationSeconds(p.LockDuration);
                    advanced = true;
                    break;
                case "DefaultMessageTimeToLive":
                    p.DefaultMessageTimeToLive = reader.ReadElementContentAsString();
                    p.DefaultMessageTimeToLiveSeconds = ParseIsoDurationSeconds(p.DefaultMessageTimeToLive);
                    advanced = true;
                    break;
                case "MaxMessageSizeInKilobytes":
                {
                    var raw = reader.ReadElementContentAsString();
                    if (long.TryParse(raw, System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out var kb))
                    {
                        p.MaxMessageSizeBytes = checked((int)(kb * 1024));
                    }
                    advanced = true;
                    break;
                }
                case "MessageCount":
                {
                    var raw = reader.ReadElementContentAsString();
                    if (long.TryParse(raw, System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out var n))
                    {
                        p.ApproximateNumberOfMessages = n;
                    }
                    advanced = true;
                    break;
                }
                case "RequiresSession":
                    p.RequiresSession = ReadBool(reader);
                    advanced = true;
                    break;
                case "RequiresDuplicateDetection":
                    p.RequiresDuplicateDetection = ReadBool(reader);
                    advanced = true;
                    break;
                case "MaxDeliveryCount":
                {
                    var raw = reader.ReadElementContentAsString();
                    if (int.TryParse(raw, System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out var mdc))
                    {
                        p.MaxDeliveryCount = mdc;
                    }
                    advanced = true;
                    break;
                }
                case "ForwardDeadLetteredMessagesTo":
                    p.ForwardDeadLetteredMessagesTo = reader.ReadElementContentAsString();
                    advanced = true;
                    break;
                case "CreatedAt":
                    p.CreatedAt = ReadDate(reader);
                    advanced = true;
                    break;
                case "UpdatedAt":
                    p.UpdatedAt = ReadDate(reader);
                    advanced = true;
                    break;
                default:
                    // Skip properties we don't model yet. Slice 4+ will add
                    // SetQueueAttributes round-tripping for the full set.
                    reader.Skip();
                    advanced = true;
                    break;
            }
        }
        return p;
    }

    private static bool? ReadBool(XmlReader reader)
    {
        var raw = reader.ReadElementContentAsString();
        if (string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(raw, "false", StringComparison.OrdinalIgnoreCase)) return false;
        return null;
    }

    private static DateTimeOffset? ReadDate(XmlReader reader)
    {
        var raw = reader.ReadElementContentAsString();
        return DateTimeOffset.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
            out var dt) ? dt : null;
    }

    private static double? ParseIsoDurationSeconds(string? iso)
    {
        if (string.IsNullOrEmpty(iso)) return null;
        try
        {
            return XmlConvert.ToTimeSpan(iso).TotalSeconds;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static XmlReader CreateReader(string xml) =>
        XmlReader.Create(new StringReader(xml), new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            IgnoreWhitespace = true,
            // Banning external resolution keeps the parser AOT-safe and
            // prevents an attacker-controlled response from triggering
            // outbound HTTP calls to resolve an XML entity.
            XmlResolver = null,
        });
}

/// <summary>
/// Writes the SB Atom <c>&lt;entry&gt;</c>/<c>&lt;QueueDescription&gt;</c>
/// payload used by <c>PUT /{queue}</c> (CreateQueue / SetQueueAttributes).
/// </summary>
internal static class AtomQueueXmlWriter
{
    public static string BuildQueueEntry(QueueDescriptionProperties props)
    {
        var sb = new StringBuilder();
        using (var sw = new Utf8StringWriter(sb))
        using (var w = XmlWriter.Create(sw, new XmlWriterSettings
        {
            Indent = false,
            OmitXmlDeclaration = false,
            Encoding = Encoding.UTF8,
            CloseOutput = false,
        }))
        {
            w.WriteStartDocument();
            w.WriteStartElement("entry", AtomQueueXmlReader.AtomNs);
            w.WriteStartElement("content", AtomQueueXmlReader.AtomNs);
            w.WriteAttributeString("type", "application/xml");
            w.WriteStartElement("QueueDescription", AtomQueueXmlReader.SbNs);

            // The property order below matches what the SB management API
            // expects in the legacy schema (some older deployments are
            // sensitive to the order).
            if (props.LockDuration is { } lockDuration)
            {
                w.WriteElementString("LockDuration", AtomQueueXmlReader.SbNs, lockDuration);
            }
            if (props.DefaultMessageTimeToLive is { } ttl)
            {
                w.WriteElementString("DefaultMessageTimeToLive", AtomQueueXmlReader.SbNs, ttl);
            }
            if (props.RequiresDuplicateDetection is { } dedup)
            {
                w.WriteElementString("RequiresDuplicateDetection", AtomQueueXmlReader.SbNs,
                    dedup ? "true" : "false");
            }
            if (props.RequiresSession is { } sess)
            {
                w.WriteElementString("RequiresSession", AtomQueueXmlReader.SbNs,
                    sess ? "true" : "false");
            }
            if (props.MaxMessageSizeBytes is { } mms)
            {
                var kb = (long)Math.Ceiling(mms / 1024.0);
                w.WriteElementString("MaxMessageSizeInKilobytes", AtomQueueXmlReader.SbNs,
                    kb.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            if (props.MaxDeliveryCount is { } mdc)
            {
                w.WriteElementString("MaxDeliveryCount", AtomQueueXmlReader.SbNs,
                    mdc.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            if (!string.IsNullOrEmpty(props.ForwardDeadLetteredMessagesTo))
            {
                w.WriteElementString("ForwardDeadLetteredMessagesTo", AtomQueueXmlReader.SbNs,
                    props.ForwardDeadLetteredMessagesTo);
            }
            w.WriteEndElement(); // QueueDescription
            w.WriteEndElement(); // content
            w.WriteEndElement(); // entry
            w.WriteEndDocument();
            w.Flush();
        }
        return sb.ToString();
    }
}
