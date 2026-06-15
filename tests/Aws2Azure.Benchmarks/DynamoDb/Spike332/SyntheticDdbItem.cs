using System.Globalization;
using System.Text;

namespace Aws2Azure.Benchmarks.DynamoDb.Spike332;

/// <summary>
/// Builds a synthetic DynamoDB <b>item</b> (typed AttributeValue wire form, the
/// input to <c>PutItem</c>) shaped like a realistic record: a sort-key string,
/// a configurable number of S / N user attributes, an optional large string
/// payload, plus a fixed tail of BOOL / NULL / nested M / L. This is the input
/// the production write encoders (<c>WriteCosmosDocument</c> /
/// <c>WriteCosmosDocumentBinary</c>) flatten into the Cosmos document, so the
/// benchmark measures the shipping format path end to end.
/// </summary>
internal static class SyntheticDdbItem
{
    public static string Build(int stringAttrs, int numberAttrs, int payloadBytes)
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.Append('{');

        sb.Append("\"sk\":{\"S\":\"s0001\"},");
        if (payloadBytes > 0)
        {
            sb.Append("\"payload\":{\"S\":\"").Append('x', payloadBytes).Append("\"},");
        }

        for (int i = 0; i < stringAttrs; i++)
        {
            sb.Append("\"str").Append(i.ToString(inv)).Append("\":{\"S\":\"v")
                .Append(i.ToString(inv)).Append("\"},");
        }

        for (int i = 0; i < numberAttrs; i++)
        {
            sb.Append("\"num").Append(i.ToString(inv)).Append("\":{\"N\":\"")
                .Append((i * 31 + 7).ToString(inv)).Append("\"},");
        }

        sb.Append("\"active\":{\"BOOL\":true},");
        sb.Append("\"deleted\":{\"NULL\":true},");
        sb.Append("\"nested\":{\"M\":{\"a\":{\"S\":\"x\"},\"b\":{\"N\":\"7\"},\"c\":{\"BOOL\":false}}},");
        sb.Append("\"tags\":{\"L\":[{\"S\":\"x\"},{\"S\":\"y\"},{\"N\":\"3\"}]}");

        sb.Append('}');
        return sb.ToString();
    }
}
