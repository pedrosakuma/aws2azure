using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using Aws2Azure.Modules.DynamoDb.Expressions;

namespace Aws2Azure.Modules.DynamoDb.Operations;

/// <summary>
/// Serialises a Cosmos SQL query body (<c>{"query": ..., "parameters":
/// [...]}</c>) with strongly-typed parameter values. Centralises the
/// JSON writer options + parameter-emission logic shared by
/// <see cref="QueryHandler"/> and <see cref="ScanHandler"/>.
/// </summary>
internal static class CosmosQueryBody
{
    public static string Build(string sql, IReadOnlyList<CosmosSqlParameter> parameters)
    {
        using var ms = new MemoryStream();
        var options = new JsonWriterOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        using (var writer = new Utf8JsonWriter(ms, options))
        {
            writer.WriteStartObject();
            writer.WriteString("query", sql);
            writer.WritePropertyName("parameters");
            writer.WriteStartArray();
            foreach (var p in parameters)
            {
                writer.WriteStartObject();
                writer.WriteString("name", p.Name);
                writer.WritePropertyName("value");
                p.Value.WriteTo(writer);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>
    /// Wraps a plain string as a <see cref="JsonElement"/> for use as a
    /// Cosmos SQL parameter value — KeyCondition values (HASH/RANGE
    /// formatted strings) all flow through this helper.
    /// </summary>
    public static JsonElement StringValue(string s)
    {
        // System.Text.Json doesn't expose a JsonElement factory for raw
        // strings; round-trip through Utf8JsonWriter (AOT-safe — no
        // reflection-based JsonSerializer).
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStringValue(s);
        }
        var doc = JsonDocument.Parse(ms.ToArray());
        try { return doc.RootElement.Clone(); }
        finally { doc.Dispose(); }
    }
}
