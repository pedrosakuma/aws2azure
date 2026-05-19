using Aws2Azure.Modules.Sqs.Internal;
using Aws2Azure.Modules.Sqs.Xml;
using Xunit;

namespace Aws2Azure.UnitTests.Sqs;

public class AtomQueueXmlDlqTests
{
    private const string SbNs = AtomQueueXmlReader.SbNs;
    private const string AtomNs = AtomQueueXmlReader.AtomNs;

    [Fact]
    public void Writer_emits_max_delivery_count_and_forward_dlq_target()
    {
        var props = new QueueDescriptionProperties
        {
            MaxDeliveryCount = 7,
            ForwardDeadLetteredMessagesTo = "my-dlq",
        };
        var xml = AtomQueueXmlWriter.BuildQueueEntry(props);
        Assert.Contains("<MaxDeliveryCount", xml);
        Assert.Contains(">7<", xml);
        Assert.Contains("<ForwardDeadLetteredMessagesTo", xml);
        Assert.Contains(">my-dlq<", xml);
    }

    [Fact]
    public void Reader_parses_dlq_fields_when_present()
    {
        var wrappedXml =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
            "<entry xmlns=\"" + AtomNs + "\">" +
              "<title>q1</title>" +
              "<content type=\"application/xml\">" +
                "<QueueDescription xmlns=\"" + SbNs + "\">" +
                  "<LockDuration>PT30S</LockDuration>" +
                  "<MaxDeliveryCount>9</MaxDeliveryCount>" +
                  "<ForwardDeadLetteredMessagesTo>some-dlq</ForwardDeadLetteredMessagesTo>" +
                "</QueueDescription>" +
              "</content>" +
            "</entry>";
        var entry = AtomQueueXmlReader.ParseQueueEntry(wrappedXml);
        Assert.NotNull(entry);
        Assert.Equal(9, entry!.Properties.MaxDeliveryCount);
        Assert.Equal("some-dlq", entry.Properties.ForwardDeadLetteredMessagesTo);
    }

    [Fact]
    public void Round_trip_preserves_dlq_fields()
    {
        // The writer emits a bare <entry><content><QueueDescription/> with
        // no <title>; the reader requires a title. Wrap the produced body
        // so we can prove the QueueDescription is parsed back identically.
        var props = new QueueDescriptionProperties
        {
            LockDuration = "PT30S",
            LockDurationSeconds = 30,
            MaxDeliveryCount = 5,
            ForwardDeadLetteredMessagesTo = "rt-dlq",
        };
        var rawEntry = AtomQueueXmlWriter.BuildQueueEntry(props);
        var qdStart = rawEntry.IndexOf("<QueueDescription", System.StringComparison.Ordinal);
        var qdEnd = rawEntry.IndexOf("</QueueDescription>", System.StringComparison.Ordinal)
                    + "</QueueDescription>".Length;
        var qd = rawEntry.Substring(qdStart, qdEnd - qdStart);
        var wrapped =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
            "<entry xmlns=\"" + AtomNs + "\">" +
              "<title>rt</title>" +
              "<content type=\"application/xml\">" +
                qd +
              "</content>" +
            "</entry>";
        var parsed = AtomQueueXmlReader.ParseQueueEntry(wrapped);
        Assert.NotNull(parsed);
        Assert.Equal(5, parsed!.Properties.MaxDeliveryCount);
        Assert.Equal("rt-dlq", parsed.Properties.ForwardDeadLetteredMessagesTo);
    }
}
