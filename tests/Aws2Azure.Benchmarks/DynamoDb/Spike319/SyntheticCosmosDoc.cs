using System.Globalization;
using System.Text;

namespace Aws2Azure.Benchmarks.DynamoDb.Spike319;

/// <summary>
/// Builds a single synthetic Cosmos GetItem document (point-read body) shaped
/// like a real DynamoDB→Cosmos item: routing/discriminator/system fields that
/// the transform strips, a shadow-encoded <c>id</c>, and a configurable number
/// of representative user attributes (S, N-integer, BOOL, NULL, nested M, L).
/// Used by the #319 fusion spike; the integer-only number choice keeps the DDB
/// <c>N</c> rendering byte-identical between the production transform and the
/// minimal spike decoder.
/// </summary>
internal static class SyntheticCosmosDoc
{
    public static string Build(int extraStringAttrs, int extraNumberAttrs, int payloadBytes)
    {
        var sb = new StringBuilder();
        sb.Append('{');

        // Reserved / system fields the transform strips on read.
        sb.Append("\"id\":\"route-0001\",");
        sb.Append("\"_a2a_pk\":\"p00\",");
        sb.Append("\"_a2a\":\"item\",");
        sb.Append("\"_rid\":\"abc==\",");
        sb.Append("\"_self\":\"dbs/x/colls/y/docs/z/\",");
        sb.Append("\"_etag\":\"\\\"0000\\\"\",");
        sb.Append("\"_ts\":1700000000,");
        sb.Append("\"_attachments\":\"attachments/\",");

        // Shadow-encoded user "id" attribute (transform unmangles to "id").
        sb.Append("\"_a2a$id\":\"user-id-0001\",");

        // Representative user attributes.
        sb.Append("\"sk\":\"s0001\",");
        if (payloadBytes > 0)
        {
            sb.Append("\"payload\":\"").Append('x', payloadBytes).Append("\",");
        }

        for (int i = 0; i < extraStringAttrs; i++)
        {
            sb.Append("\"str").Append(i.ToString(CultureInfo.InvariantCulture))
                .Append("\":\"v").Append(i.ToString(CultureInfo.InvariantCulture)).Append("\",");
        }

        for (int i = 0; i < extraNumberAttrs; i++)
        {
            sb.Append("\"num").Append(i.ToString(CultureInfo.InvariantCulture))
                .Append("\":").Append((i * 31 + 7).ToString(CultureInfo.InvariantCulture)).Append(',');
        }

        sb.Append("\"active\":true,");
        sb.Append("\"deleted\":null,");
        sb.Append("\"nested\":{\"a\":\"x\",\"b\":7,\"c\":false},");
        sb.Append("\"tags\":[\"x\",\"y\",3]");

        sb.Append('}');
        return sb.ToString();
    }
}
