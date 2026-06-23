using Aws2Azure.Modules.Sqs.Internal;
using Aws2Azure.Modules.Sqs.Xml;
using Xunit;

namespace Aws2Azure.UnitTests.Sqs;

public class AtomQueueXmlTests
{
    private const string SbNs = AtomQueueXmlReader.SbNs;
    private const string AtomNs = AtomQueueXmlReader.AtomNs;

    [Fact]
    public void Writer_emits_utf8_declaration_and_queue_description()
    {
        var props = new QueueDescriptionProperties
        {
            LockDuration = "PT45S",
            DefaultMessageTimeToLive = "PT1H",
            MaxMessageSizeBytes = 200000,
            RequiresSession = true,
            RequiresDuplicateDetection = true,
        };

        var xml = AtomQueueXmlWriter.BuildQueueEntry(props);
        Assert.Contains("encoding=\"utf-8\"", xml);
        Assert.Contains("<QueueDescription", xml);
        Assert.Contains("<LockDuration", xml);
        Assert.Contains("<RequiresSession", xml);
    }

    [Fact]
    public void Writer_emits_user_metadata_before_forward_dead_lettered_messages_to()
    {
        var props = new QueueDescriptionProperties
        {
            LockDuration = "PT30S",
            DefaultMessageTimeToLive = "P14D",
            RequiresDuplicateDetection = false,
            RequiresSession = false,
            MaxMessageSizeBytes = 1048576,
            MaxDeliveryCount = 10,
            UserMetadata = "owned-by-aws2azure",
            ForwardDeadLetteredMessagesTo = "dead-letter-target",
        };

        var xml = AtomQueueXmlWriter.BuildQueueEntry(props);

        Assert.True(
            xml.IndexOf("<MaxDeliveryCount", System.StringComparison.Ordinal) <
            xml.IndexOf("<UserMetadata", System.StringComparison.Ordinal));
        Assert.True(
            xml.IndexOf("<UserMetadata", System.StringComparison.Ordinal) <
            xml.IndexOf("<ForwardDeadLetteredMessagesTo", System.StringComparison.Ordinal));
    }

    [Fact]
    public void Reader_parses_single_queue_entry()
    {
        var wrappedXml =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
            "<entry xmlns=\"" + AtomNs + "\">" +
              "<title>q1</title>" +
              "<content type=\"application/xml\">" +
                "<QueueDescription xmlns=\"" + SbNs + "\">" +
                  "<LockDuration>PT45S</LockDuration>" +
                  "<DefaultMessageTimeToLive>PT1H</DefaultMessageTimeToLive>" +
                  "<MaxMessageSizeInKilobytes>196</MaxMessageSizeInKilobytes>" +
                  "<RequiresDuplicateDetection>true</RequiresDuplicateDetection>" +
                  "<RequiresSession>true</RequiresSession>" +
                "</QueueDescription>" +
              "</content>" +
            "</entry>";
        var entry = AtomQueueXmlReader.ParseQueueEntry(wrappedXml);
        Assert.NotNull(entry);
        Assert.Equal("q1", entry!.Name);
        Assert.Equal(45.0, entry.Properties.LockDurationSeconds);
        Assert.Equal(3600.0, entry.Properties.DefaultMessageTimeToLiveSeconds);
        Assert.Equal(196 * 1024, entry.Properties.MaxMessageSizeBytes);
        Assert.True(entry.Properties.RequiresSession);
        Assert.True(entry.Properties.RequiresDuplicateDetection);
    }

    [Fact]
    public void Feed_parser_returns_every_queue_entry()
    {
        var feedXml =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
            "<feed xmlns=\"" + AtomNs + "\">" +
              "<entry>" +
                "<title>a</title>" +
                "<content type=\"application/xml\">" +
                  "<QueueDescription xmlns=\"" + SbNs + "\">" +
                    "<LockDuration>PT30S</LockDuration>" +
                  "</QueueDescription>" +
                "</content>" +
              "</entry>" +
              "<entry>" +
                "<title>b.fifo</title>" +
                "<content type=\"application/xml\">" +
                  "<QueueDescription xmlns=\"" + SbNs + "\">" +
                    "<RequiresSession>true</RequiresSession>" +
                  "</QueueDescription>" +
                "</content>" +
              "</entry>" +
            "</feed>";
        var entries = AtomQueueXmlReader.ParseQueueFeed(feedXml);
        Assert.Equal(2, entries.Count);
        Assert.Equal("a", entries[0].Name);
        Assert.Equal("b.fifo", entries[1].Name);
        Assert.True(entries[1].Properties.RequiresSession);
    }

    [Fact]
    public void Empty_feed_yields_zero_entries()
    {
        var feedXml = "<feed xmlns=\"" + AtomNs + "\"></feed>";
        Assert.Empty(AtomQueueXmlReader.ParseQueueFeed(feedXml));
    }

    [Fact]
    public void Parser_does_not_resolve_external_entities()
    {
        // SB responses are trusted, but the Atom parser must still refuse
        // to resolve external DTDs — keeps us defence-in-depth against
        // upstream compromise. DtdProcessing=Prohibit must throw on the
        // DOCTYPE.
        var dangerous =
            "<?xml version=\"1.0\"?>" +
            "<!DOCTYPE foo [<!ENTITY xxe SYSTEM \"file:///nonexistent/aws2azure-xxe-canary\">]>" +
            "<feed xmlns=\"" + AtomNs + "\"><entry><title>&xxe;</title></entry></feed>";
        Assert.ThrowsAny<System.Xml.XmlException>(
            () => AtomQueueXmlReader.ParseQueueFeed(dangerous));
    }
}
