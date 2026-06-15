using System;
using System.Buffers;
using System.Collections.Generic;
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
    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Encodes the query body straight into a pooled UTF-8 buffer the caller
    /// owns and sends zero-copy (no <c>bytes → string → StringContent</c>
    /// round-trip). The caller MUST dispose the returned writer once the body
    /// has been sent (its <see cref="PooledByteBufferWriter.WrittenMemory"/> is
    /// read once per attempt across retries / pagination).
    /// </summary>
    public static PooledByteBufferWriter Build(string sql, IReadOnlyList<CosmosSqlParameter> parameters)
    {
        var buffer = new PooledByteBufferWriter(Math.Max(256, sql.Length + (parameters.Count * 24) + 64));
        try
        {
            using var writer = new Utf8JsonWriter(buffer, WriterOptions);
            writer.WriteStartObject();
            writer.WriteString("query", sql);
            writer.WritePropertyName("parameters");
            writer.WriteStartArray();
            foreach (var p in parameters)
            {
                writer.WriteStartObject();
                writer.WriteString("name", p.Name);
                writer.WritePropertyName("value");
                p.WriteValueTo(writer);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        catch
        {
            buffer.Dispose();
            throw;
        }

        return buffer;
    }
}
