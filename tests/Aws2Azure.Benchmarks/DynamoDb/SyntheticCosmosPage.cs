using System.Globalization;
using System.Text;

namespace Aws2Azure.Benchmarks.DynamoDb;

/// <summary>
/// Builds synthetic Cosmos query-page JSON shaped like a real DynamoDB→Cosmos
/// <c>Query</c> response (a <c>Documents</c> array of items wrapped in the
/// Cosmos envelope). Mirrors the generator used by the closed-loop perf suite
/// so the two harnesses exercise comparable payloads.
/// </summary>
internal static class SyntheticCosmosPage
{
    /// <summary>
    /// Builds a page of <paramref name="documentCount"/> items. Each item carries
    /// the base attributes plus an optional <c>payload</c> string of
    /// <paramref name="payloadBytes"/> bytes and <paramref name="extraAttributes"/>
    /// additional small scalar fields. Large <c>payload</c> strings are
    /// near-identical in both encodings (so they dilute CosmosBinary's wire win);
    /// many small attributes are where CosmosBinary tokenization helps most.
    /// </summary>
    public static string Build(int documentCount, int payloadBytes, int extraAttributes = 0)
    {
        var payload = new string('x', payloadBytes);
        var sb = new StringBuilder();
        sb.Append("{\"_rid\":\"synthetic\",\"Documents\":[");
        for (int i = 0; i < documentCount; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }

            sb.Append("{\"id\":\"item-").Append(i.ToString("D4", CultureInfo.InvariantCulture))
                .Append("\",\"_a2a\":\"item\",\"pk\":\"p")
                .Append((i % 10).ToString("D2", CultureInfo.InvariantCulture))
                .Append("\",\"sk\":\"s")
                .Append(i.ToString("D4", CultureInfo.InvariantCulture))
                .Append('"');

            if (payloadBytes > 0)
            {
                sb.Append(",\"payload\":\"").Append(payload).Append('"');
            }

            for (int a = 0; a < extraAttributes; a++)
            {
                sb.Append(",\"attr").Append(a.ToString(CultureInfo.InvariantCulture))
                    .Append("\":").Append((i * 31 + a).ToString(CultureInfo.InvariantCulture));
            }

            sb.Append(",\"n\":").Append(i.ToString(CultureInfo.InvariantCulture)).Append('}');
        }

        sb.Append("],\"_count\":").Append(documentCount.ToString(CultureInfo.InvariantCulture)).Append('}');
        return sb.ToString();
    }
}
