using System;
using System.Buffers;
using System.Buffers.Text;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Aws2Azure.Modules.DynamoDb.Internal;
using Aws2Azure.Modules.DynamoDb.Operations;

namespace Aws2Azure.Modules.DynamoDb.Persistence;

internal static partial class InferredAttributeStorage
{
    // ---------------- READ PATH (Cosmos → DDB) -----------------------

    /// <summary>
    /// Extracts the DDB item map from a Cosmos document body. Iterates
    /// the root properties, skips routing/discriminator metadata, and
    /// decodes every remaining property back to its typed DDB
    /// representation. Returns <c>null</c> when the body is not a JSON
    /// object (defensive — should never happen for a real Cosmos doc).
    /// </summary>
    public static Dictionary<string, JsonElement>? ExtractItem(Stream cosmosDocBody)
    {
        using var doc = JsonDocument.Parse(cosmosDocBody);
        return ExtractItem(doc.RootElement);
    }

    /// <summary>
    /// Streams the DynamoDB <c>GetItem</c> response envelope
    /// (<c>{"Item":{...}}</c>) for a found Cosmos document directly into
    /// <paramref name="writer"/>, reusing the same reserved-name skip,
    /// shadow-<c>id</c> unmangling, and <see cref="WriteAttributeValue"/>
    /// per-value encoder as <see cref="ExtractItem(JsonElement)"/>. This is
    /// the allocation-lean read path: it eliminates the intermediate
    /// <see cref="Dictionary{TKey,TValue}"/>, the per-attribute
    /// <see cref="JsonElement.Clone"/> calls, the response model object, and
    /// the model re-serialization that the <c>ExtractItem → model →
    /// SerializeAsync</c> path pays.
    ///
    /// <para><b>Encoder contract:</b> <paramref name="writer"/> MUST be
    /// constructed with the default <see cref="JavaScriptEncoder"/> (i.e. a
    /// <see cref="JsonWriterOptions"/> with no <c>Encoder</c> override) so the
    /// emitted bytes are identical to the model-based path, which terminates
    /// in <see cref="JsonSerializer"/> using <c>JavaScriptEncoder.Default</c>.
    /// Passing this module's relaxed-escaping <c>WriterOptions</c> would
    /// silently diverge on non-ASCII and HTML-sensitive characters.</para>
    ///
    /// <para>When <paramref name="docRoot"/> is not a JSON object the
    /// envelope collapses to <c>{}</c>, matching a null
    /// <c>GetItemResponse.Item</c> serialized with
    /// <c>DefaultIgnoreCondition=WhenWritingNull</c>.</para>
    /// </summary>
    public static void WriteGetItemEnvelope(Utf8JsonWriter writer, JsonElement docRoot)
    {
        writer.WriteStartObject();
        if (docRoot.ValueKind == JsonValueKind.Object)
        {
            writer.WritePropertyName(ItemPropEncoded);
            writer.WriteStartObject();
            foreach (var prop in docRoot.EnumerateObject())
            {
                string targetName;
                if (prop.Name.Equals(ShadowEncodedIdName, StringComparison.Ordinal))
                {
                    // Unmangle the shadow-encoded "id" attribute.
                    targetName = IdProperty;
                }
                else if (prop.Name.Equals(ShadowEncodedTtlName, StringComparison.Ordinal))
                {
                    // Unmangle the shadow-encoded "ttl" attribute.
                    targetName = TtlProperty;
                }
                else if (IsReservedTopLevelName(prop.Name) || IsCosmosSystemField(prop.Name))
                {
                    // Routing fields, discriminator, other reserved
                    // _a2a-namespace props, Cosmos system fields
                    // (_rid/_self/_etag/_ts/_attachments/...), and the injected
                    // native "ttl" — never user data.
                    continue;
                }
                else
                {
                    targetName = prop.Name;
                }

                writer.WritePropertyName(targetName);
                WriteAttributeValue(writer, prop.Value);
            }
            writer.WriteEndObject();
        }
        writer.WriteEndObject();
    }

    // ---- UTF-8 streaming GetItem envelope (no DOM / no String) ----------
    //
    // Forward-only Utf8JsonReader → Utf8JsonWriter pump that emits the same
    // {"Item":{...}} envelope as WriteGetItemEnvelope(JsonElement) but never
    // materializes a JsonDocument, a JsonElement, or a System.String. Keys
    // and values are copied as raw UTF-8 spans (ValueSpan), unescaped only
    // when the source token actually carries escapes (ValueIsEscaped). This
    // is the realization of the original transform-on-the-fly premise and
    // eliminates the per-string UTF8→UTF16→UTF8 round-trip that dominates
    // allocation for large / string-heavy items.
    //
    // Envelope-vs-map disambiguation needs no lookahead: the encoder forbids
    // any stored object — top-level or nested map — from carrying a key with
    // the "_a2a:" prefix unless it is a genuine single-property envelope
    // (BuildCosmosDocument / EncodeMap reject such names at write time), so a
    // first property name under that prefix is deterministically an envelope.

    private static ReadOnlySpan<byte> ShadowEncodedIdNameU8 => "_a2a$id"u8;
    private static ReadOnlySpan<byte> ShadowEncodedTtlNameU8 => "_a2a$ttl"u8;
    private static ReadOnlySpan<byte> IdNameU8 => "id"u8;
    private static ReadOnlySpan<byte> TtlNameU8 => "ttl"u8;
    private static ReadOnlySpan<byte> DiscriminatorPrefixU8 => "_a2a"u8;
    private static ReadOnlySpan<byte> EnvelopeTagPrefixU8 => "_a2a:"u8;
    private static ReadOnlySpan<byte> EnvelopeTagNU8 => "_a2a:N"u8;
    private static ReadOnlySpan<byte> EnvelopeTagBU8 => "_a2a:B"u8;
    private static ReadOnlySpan<byte> EnvelopeTagSSU8 => "_a2a:SS"u8;
    private static ReadOnlySpan<byte> EnvelopeTagNSU8 => "_a2a:NS"u8;
    private static ReadOnlySpan<byte> EnvelopeTagBSU8 => "_a2a:BS"u8;

    /// <summary>
    /// Streaming equivalent of <see cref="WriteGetItemEnvelope(Utf8JsonWriter, JsonElement)"/>
    /// that reads the Cosmos document straight from its UTF-8 bytes. Produces
    /// byte-identical output (pinned by the golden corpus) with zero DOM,
    /// JsonElement or String allocation.
    /// </summary>
    public static void WriteGetItemEnvelope(Utf8JsonWriter writer, ReadOnlySpan<byte> cosmosDocUtf8)
    {
        var reader = new Utf8JsonTokenReader(cosmosDocUtf8);
        WriteGetItemEnvelope(writer, ref reader);
    }

    /// <summary>
    /// Reader-agnostic GetItem envelope transform. Driven by any
    /// <see cref="ITokenReader"/> — <see cref="Utf8JsonTokenReader"/> for the
    /// text path, <c>CosmosBinaryReader</c> for the fused binary path — so the
    /// transform is written once and serves both encodings.
    /// </summary>
    public static void WriteGetItemEnvelope<TReader>(Utf8JsonWriter writer, scoped ref TReader reader)
        where TReader : ITokenReader, allows ref struct
    {
        writer.WriteStartObject();
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            // Non-object root collapses to {} (null GetItemResponse.Item).
            writer.WriteEndObject();
            return;
        }

        writer.WritePropertyName(ItemPropEncoded);
        WriteTransformedItem(writer, ref reader);
        writer.WriteEndObject();
    }

    // ---- Projection-filtered streaming envelope (top-level only) ----------
    //
    // WriteProjectedGetItemEnvelope is the projected twin of
    // WriteGetItemEnvelope: a single forward walk of the Cosmos document that
    // emits only the top-level attributes named by the projection, straight to
    // the Utf8JsonWriter. Unlike the materialized path (ReadAndExtractItem →
    // Projection.Apply), it builds no intermediate Dictionary and never
    // materializes the discarded attributes at all — a non-projected attribute
    // is skipped with reader.Skip() (its value subtree is walked, not
    // allocated). Restricted to non-nested projections: a nested path would
    // need bounded lookahead to honour DynamoDB's "omit a path that does not
    // exist" semantics, so those still take the materialized Apply path.

    /// <summary>
    /// Streaming projected GetItem envelope over the Cosmos document's UTF-8
    /// bytes. Emits <c>{"Item":{…}}</c> keeping only the projection's top-level
    /// attributes. The projection must be non-nested
    /// (<see cref="Expressions.Projection.HasNestedPaths"/> is <c>false</c>).
    /// </summary>
    public static void WriteProjectedGetItemEnvelope(
        Utf8JsonWriter writer, ReadOnlySpan<byte> cosmosDocUtf8, Expressions.Projection projection)
    {
        var reader = new Utf8JsonTokenReader(cosmosDocUtf8);
        WriteProjectedGetItemEnvelope(writer, ref reader, projection);
    }

    /// <summary>
    /// Reader-agnostic projected GetItem envelope transform — the projected twin
    /// of <see cref="WriteGetItemEnvelope{TReader}(Utf8JsonWriter, ref TReader)"/>,
    /// driven by either the text (<see cref="Utf8JsonTokenReader"/>) or fused
    /// binary (<c>CosmosBinaryReader</c>) encoding.
    /// </summary>
    public static void WriteProjectedGetItemEnvelope<TReader>(
        Utf8JsonWriter writer, scoped ref TReader reader, Expressions.Projection projection)
        where TReader : ITokenReader, allows ref struct
    {
        writer.WriteStartObject();
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            // Non-object root collapses to {} (null GetItemResponse.Item).
            writer.WriteEndObject();
            return;
        }

        writer.WritePropertyName(ItemPropEncoded);
        WriteProjectedItem(writer, ref reader, projection);
        writer.WriteEndObject();
    }

    /// <summary>
    /// Writes the projected DynamoDB attribute-map object for the Cosmos document
    /// the reader is currently positioned on (its <see cref="JsonTokenType.StartObject"/>
    /// token). Keeps only the top-level attributes named by the projection; every
    /// other attribute (including reserved routing and Cosmos system fields) is
    /// skipped. The shadow-encoded <c>id</c>/<c>ttl</c> attributes are unmangled
    /// and kept only when the projection names <c>id</c>/<c>ttl</c> respectively.
    /// </summary>
    private static void WriteProjectedItem<TReader>(
        Utf8JsonWriter writer, scoped ref TReader reader, Expressions.Projection projection)
        where TReader : ITokenReader, allows ref struct
    {
        byte[][] rootNames = projection.RootNamesUtf8;

        // A user attribute literally named "id"/"ttl" is stored shadow-encoded;
        // decide up front whether the projection asked for it (the shadow token
        // never equals the projected plain name).
        bool keepId = false;
        bool keepTtl = false;
        foreach (byte[] name in rootNames)
        {
            if (name.AsSpan().SequenceEqual(IdNameU8))
            {
                keepId = true;
            }
            else if (name.AsSpan().SequenceEqual(TtlNameU8))
            {
                keepTtl = true;
            }
        }

        writer.WriteStartObject();
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (reader.ValueTextEquals(ShadowEncodedIdNameU8))
            {
                reader.Read();
                if (keepId)
                {
                    writer.WritePropertyName(IdPropEncoded);
                    WriteAttributeValue(writer, ref reader);
                }
                else
                {
                    reader.Skip();
                }
            }
            else if (reader.ValueTextEquals(ShadowEncodedTtlNameU8))
            {
                reader.Read();
                if (keepTtl)
                {
                    writer.WritePropertyName(TtlPropEncoded);
                    WriteAttributeValue(writer, ref reader);
                }
                else
                {
                    reader.Skip();
                }
            }
            else if (IsReservedTopLevelNameToken(ref reader) || IsCosmosSystemFieldToken(ref reader))
            {
                reader.Read();
                reader.Skip();
            }
            else if (MatchesProjectedRoot(ref reader, rootNames))
            {
                WriteCurrentPropertyName(writer, ref reader);
                reader.Read();
                WriteAttributeValue(writer, ref reader);
            }
            else
            {
                reader.Read();
                reader.Skip();
            }
        }
        writer.WriteEndObject();
    }

    private static bool MatchesProjectedRoot<TReader>(scoped ref TReader reader, byte[][] rootNames)
        where TReader : ITokenReader, allows ref struct
    {
        // ValueTextEquals transparently handles an escaped property-name token,
        // so the comparison is against the unescaped attribute name.
        foreach (byte[] name in rootNames)
        {
            if (reader.ValueTextEquals(name))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Writes the transformed DynamoDB attribute-map object for the Cosmos
    /// document the reader is currently positioned on (its
    /// <see cref="JsonTokenType.StartObject"/> token). On entry the reader is on
    /// that StartObject; on return it is on the matching EndObject. The emitted
    /// object is byte-identical to serializing
    /// <see cref="ExtractItem(JsonElement)"/> — routing/discriminator/Cosmos
    /// system fields stripped, the shadow-encoded <c>id</c> attribute
    /// unmangled, every value rendered as its typed AttributeValue. This is the
    /// shared per-item core behind the GetItem <c>{"Item":{…}}</c> envelope and
    /// the Query/Scan <c>"Items":[…]</c> array elements, so the transform is
    /// written once and serves single- and multi-item read paths over either
    /// the text (<see cref="Utf8JsonTokenReader"/>) or fused binary
    /// (<c>CosmosBinaryReader</c>) encoding.
    /// </summary>
    public static void WriteTransformedItem<TReader>(Utf8JsonWriter writer, scoped ref TReader reader)
        where TReader : ITokenReader, allows ref struct
    {
        writer.WriteStartObject();
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (reader.ValueTextEquals(ShadowEncodedIdNameU8))
            {
                // Unmangle the shadow-encoded "id" attribute.
                writer.WritePropertyName(IdPropEncoded);
                reader.Read();
                WriteAttributeValue(writer, ref reader);
            }
            else if (reader.ValueTextEquals(ShadowEncodedTtlNameU8))
            {
                // Unmangle the shadow-encoded "ttl" attribute.
                writer.WritePropertyName(TtlPropEncoded);
                reader.Read();
                WriteAttributeValue(writer, ref reader);
            }
            else if (IsReservedTopLevelNameToken(ref reader) || IsCosmosSystemFieldToken(ref reader))
            {
                // Routing fields, discriminator, other reserved _a2a props,
                // the injected native "ttl", and Cosmos system fields
                // (_rid/_self/_etag/_ts/...).
                reader.Read();
                reader.Skip();
            }
            else
            {
                WriteCurrentPropertyName(writer, ref reader);
                reader.Read();
                WriteAttributeValue(writer, ref reader);
            }
        }
        writer.WriteEndObject();
    }

    private static ReadOnlySpan<byte> DocumentsNameU8 => "Documents"u8;

    /// <summary>
    /// Reader-agnostic single-document map extraction: materializes the same
    /// <c>Dictionary&lt;string, JsonElement&gt;</c> AttributeValue map as
    /// <see cref="ExtractItem(JsonElement)"/>, but driven straight off any
    /// <see cref="ITokenReader"/> — in particular <c>CosmosBinaryReader</c> over
    /// a binary Cosmos body — so callers that still need a materialized map
    /// (condition evaluation, projection, update application, response grouping)
    /// can skip the redundant binary→text page decode + <see cref="JsonDocument"/>
    /// DOM build. The small re-serialize-and-parse the map itself requires (the
    /// typed AttributeValue values must be <see cref="JsonElement"/>) is shared
    /// with the <see cref="JsonElement"/> overload, so the returned map is
    /// element-for-element identical to <see cref="ExtractItem(JsonElement)"/>.
    ///
    /// <para>On entry the reader is positioned before the document (the first
    /// <see cref="reader.Read()"/> must land on its <see cref="JsonTokenType.StartObject"/>);
    /// a non-object root returns <c>null</c>, matching the <see cref="JsonElement"/>
    /// overload.</para>
    /// </summary>
    public static Dictionary<string, JsonElement>? ExtractItemFused<TReader>(scoped ref TReader reader)
        where TReader : ITokenReader, allows ref struct
    {
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            return null;
        }

        // Re-emit the transformed item into a single batch buffer, parse once,
        // then clone children — identical tail to ExtractItem(JsonElement).
        var bw = new ArrayBufferWriter<byte>(1024);
        using (var writer = new Utf8JsonWriter(bw, WriterOptions))
        {
            WriteTransformedItem(writer, ref reader);
            writer.Flush();
        }

        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        using var batch = JsonDocument.Parse(bw.WrittenMemory);
        foreach (var prop in batch.RootElement.EnumerateObject())
        {
            result[prop.Name] = prop.Value.Clone();
        }
        return result;
    }

    /// <summary>
    /// Streams the <c>Documents</c> array of a Cosmos query/feed response into
    /// per-document AttributeValue maps, appending each
    /// <c>Dictionary&lt;string, JsonElement&gt;</c> (element-for-element
    /// identical to <see cref="ExtractItem(JsonElement)"/>) to
    /// <paramref name="sink"/> in document order. Reader-agnostic, so a Cosmos
    /// page can be materialized straight off a <c>CosmosBinaryReader</c> with no
    /// binary→text page decode and no full-page <see cref="JsonDocument"/> DOM —
    /// only the same small per-document re-serialize-and-parse the map itself
    /// requires (shared with <see cref="ExtractItemFused"/>). Non-object array
    /// elements terminate the walk (Cosmos feeds only ever contain objects),
    /// matching <see cref="WriteTransformedDocuments"/>. Returns the number of
    /// documents appended.
    /// </summary>
    public static int ExtractItemsFused<TReader>(
        scoped ref TReader reader, List<Dictionary<string, JsonElement>> sink)
        where TReader : ITokenReader, allows ref struct
    {
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            return 0;
        }

        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (reader.ValueTextEquals(DocumentsNameU8))
            {
                if (!reader.Read() || reader.TokenType != JsonTokenType.StartArray)
                {
                    reader.Skip();
                    return 0;
                }

                int count = 0;
                var bw = new ArrayBufferWriter<byte>(1024);
                while (reader.Read() && reader.TokenType == JsonTokenType.StartObject)
                {
                    bw.ResetWrittenCount();
                    using (var writer = new Utf8JsonWriter(bw, WriterOptions))
                    {
                        WriteTransformedItem(writer, ref reader);
                        writer.Flush();
                    }

                    var map = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                    using (var batch = JsonDocument.Parse(bw.WrittenMemory))
                    {
                        foreach (var prop in batch.RootElement.EnumerateObject())
                        {
                            map[prop.Name] = prop.Value.Clone();
                        }
                    }
                    sink.Add(map);
                    count++;
                }
                return count;
            }

            reader.Read();
            reader.Skip();
        }

        return 0;
    }

    /// <summary>
    /// Correlation-aware counterpart to <see cref="ExtractItemsFused"/>: streams
    /// the <c>Documents</c> array of a Cosmos query/feed response into per-document
    /// AttributeValue maps <i>and</i> surfaces each document's reserved Cosmos
    /// <c>id</c> (the correlation key) via <paramref name="sink"/>, in feed order.
    /// The BatchGetItem grouped-query read needs the <c>id</c> to map each document
    /// back to its request-key index — <see cref="ExtractItemsFused"/> strips it.
    /// Reader-agnostic, so the page is materialized straight off any
    /// <see cref="ITokenReader"/> with no whole-page <see cref="JsonDocument"/> DOM —
    /// only the same small per-document re-serialize-and-parse the map itself
    /// requires (shared with <see cref="ExtractItemFused"/>). Each emitted map is
    /// element-for-element identical to <c>JsonDocument.Parse → ExtractItem(JsonElement)</c>.
    /// Non-object array elements terminate the walk (Cosmos feeds only ever contain
    /// objects). Returns the number of documents passed to <paramref name="sink"/>.
    /// </summary>
    public static int ExtractItemsFusedWithId<TReader, TSink>(
        scoped ref TReader reader, TSink sink)
        where TReader : ITokenReader, allows ref struct
        where TSink : struct, IFusedItemWithIdSink
    {
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            return 0;
        }

        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (reader.ValueTextEquals(DocumentsNameU8))
            {
                if (!reader.Read() || reader.TokenType != JsonTokenType.StartArray)
                {
                    reader.Skip();
                    return 0;
                }

                int count = 0;
                var bw = new ArrayBufferWriter<byte>(1024);
                while (reader.Read() && reader.TokenType == JsonTokenType.StartObject)
                {
                    bw.ResetWrittenCount();
                    string? id;
                    using (var writer = new Utf8JsonWriter(bw, WriterOptions))
                    {
                        WriteTransformedItemCapturingId(writer, ref reader, out id);
                        writer.Flush();
                    }

                    var map = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                    using (var batch = JsonDocument.Parse(bw.WrittenMemory))
                    {
                        foreach (var prop in batch.RootElement.EnumerateObject())
                        {
                            map[prop.Name] = prop.Value.Clone();
                        }
                    }
                    sink.Accept(id, map);
                    count++;
                }
                return count;
            }

            reader.Read();
            reader.Skip();
        }

        return 0;
    }

    /// <summary>
    /// Byte-streaming counterpart to <see cref="ExtractItemsFusedWithId{TReader,TSink}"/>:
    /// surfaces each document's reserved Cosmos <c>id</c> alongside the <i>transformed
    /// DDB item bytes</i> (a complete <c>{…}</c> object) instead of a materialized
    /// <see cref="Dictionary{TKey,TValue}"/> map. The BatchGetItem grouped read
    /// (no <c>ProjectionExpression</c>) keeps these bytes and splices them straight
    /// into the response <c>Responses</c> array, skipping the per-document
    /// <see cref="JsonDocument.Parse"/> + dictionary + per-attribute
    /// <see cref="JsonElement.Clone"/> the map path pays (issue #443).
    ///
    /// <para><b>Lifetime:</b> the span handed to <see cref="IFusedItemBytesWithIdSink.Accept"/>
    /// is the shared scratch buffer, reused on the next iteration; the sink MUST
    /// copy any bytes it intends to retain past the call.</para>
    ///
    /// <para><b>Encoder contract:</b> each item is written with the default
    /// <see cref="JavaScriptEncoder"/> (a plain <see cref="Utf8JsonWriter"/> with no
    /// <c>Encoder</c> override), <i>not</i> this module's relaxed-escaping
    /// <c>WriterOptions</c>, so the spliced bytes are identical to the model-based
    /// path that terminates in <see cref="JsonSerializer"/> using
    /// <c>JavaScriptEncoder.Default</c> — matching the <see cref="WriteGetItemEnvelope"/>
    /// contract. Using the relaxed encoder would silently diverge on non-ASCII and
    /// HTML-sensitive characters.</para>
    /// </summary>
    public static int ExtractItemsFusedWithIdBytes<TReader, TSink>(
        scoped ref TReader reader, TSink sink)
        where TReader : ITokenReader, allows ref struct
        where TSink : struct, IFusedItemBytesWithIdSink
    {
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            return 0;
        }

        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (reader.ValueTextEquals(DocumentsNameU8))
            {
                if (!reader.Read() || reader.TokenType != JsonTokenType.StartArray)
                {
                    reader.Skip();
                    return 0;
                }

                int count = 0;
                var bw = new ArrayBufferWriter<byte>(1024);
                while (reader.Read() && reader.TokenType == JsonTokenType.StartObject)
                {
                    bw.ResetWrittenCount();
                    string? id;
                    // Default encoder (no WriterOptions) — see the encoder contract
                    // above: spliced bytes must match the model serializer's output.
                    using (var writer = new Utf8JsonWriter(bw))
                    {
                        WriteTransformedItemCapturingId(writer, ref reader, out id);
                        writer.Flush();
                    }

                    sink.Accept(id, bw.WrittenSpan);
                    count++;
                }
                return count;
            }

            reader.Read();
            reader.Skip();
        }

        return 0;
    }

    /// <summary>
    /// <see cref="WriteTransformedItem{TReader}"/> variant that additionally
    /// captures the document's reserved Cosmos <c>id</c> value into
    /// <paramref name="id"/>. The <c>id</c> stays stripped from the emitted DDB
    /// item (byte-identical output to <see cref="WriteTransformedItem{TReader}"/>) —
    /// it is only surfaced, never written. <paramref name="id"/> is <c>null</c> when
    /// the document carries no string <c>id</c> property.
    /// </summary>
    public static void WriteTransformedItemCapturingId<TReader>(
        Utf8JsonWriter writer, scoped ref TReader reader, out string? id)
        where TReader : ITokenReader, allows ref struct
    {
        id = null;
        writer.WriteStartObject();
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (reader.ValueTextEquals(ShadowEncodedIdNameU8))
            {
                // Unmangle the shadow-encoded "id" attribute.
                writer.WritePropertyName(IdPropEncoded);
                reader.Read();
                WriteAttributeValue(writer, ref reader);
            }
            else if (reader.ValueTextEquals(ShadowEncodedTtlNameU8))
            {
                // Unmangle the shadow-encoded "ttl" attribute.
                writer.WritePropertyName(TtlPropEncoded);
                reader.Read();
                WriteAttributeValue(writer, ref reader);
            }
            else if (reader.ValueTextEquals(IdNameU8))
            {
                // Reserved Cosmos document id: capture for correlation but keep
                // it stripped from the emitted item (same as the reserved-name
                // branch in WriteTransformedItem, which routes "id" to Skip).
                reader.Read();
                id = reader.TokenType == JsonTokenType.String ? MaterializeString(ref reader) : null;
                reader.Skip();
            }
            else if (IsReservedTopLevelNameToken(ref reader) || IsCosmosSystemFieldToken(ref reader))
            {
                reader.Read();
                reader.Skip();
            }
            else
            {
                WriteCurrentPropertyName(writer, ref reader);
                reader.Read();
                WriteAttributeValue(writer, ref reader);
            }
        }
        writer.WriteEndObject();
    }

    /// <summary>
    /// Streams the <c>Documents</c> array of a Cosmos query/feed response
    /// (<c>{"_rid":…,"Documents":[ {doc}, … ],"_count":N}</c>) into
    /// <paramref name="writer"/> as transformed DynamoDB item objects, writing
    /// each array element via <see cref="WriteTransformedItem{TReader}"/>.
    /// <paramref name="writer"/> must already be positioned inside an open JSON
    /// array (the response's <c>Items</c> array); this method writes only the
    /// element objects, never the surrounding <c>[</c>/<c>]</c>. Returns the
    /// number of documents emitted.
    ///
    /// <para>Reader-agnostic (text <see cref="Utf8JsonTokenReader"/> or fused
    /// <c>CosmosBinaryReader</c>), so a no-FilterExpression / no-ProjectionExpression
    /// Query or Scan page can be pumped straight to the wire with no
    /// JsonDocument DOM, no AttributeValue map, and no per-item model
    /// re-serialization. The reader is consumed through the end of the
    /// <c>Documents</c> array; trailing properties (e.g. <c>_count</c>) are left
    /// unread.</para>
    /// </summary>
    public static int WriteTransformedDocuments<TReader>(Utf8JsonWriter writer, scoped ref TReader reader)
        where TReader : ITokenReader, allows ref struct
    {
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            return 0;
        }

        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (reader.ValueTextEquals(DocumentsNameU8))
            {
                if (!reader.Read() || reader.TokenType != JsonTokenType.StartArray)
                {
                    // "Documents" present but not an array — nothing to emit.
                    reader.Skip();
                    return 0;
                }

                int count = 0;
                while (reader.Read() && reader.TokenType == JsonTokenType.StartObject)
                {
                    WriteTransformedItem(writer, ref reader);
                    count++;
                }
                // reader is now on the array's EndArray token.
                return count;
            }

            // Any other top-level property (_rid, _count, ...): skip its value.
            reader.Read();
            reader.Skip();
        }

        return 0;
    }

    /// <summary>Reserved top-level name check over the reader's current
    /// PropertyName token (shadow-id is handled separately by the caller).</summary>
    private static bool IsReservedTopLevelNameToken<TReader>(scoped ref TReader reader)
        where TReader : ITokenReader, allows ref struct
    {
        if (reader.ValueTextEquals(IdNameU8)) return true;
        // Cosmos's native "ttl" (injected by the proxy on TTL-enabled tables)
        // is reserved and stripped on read; a user attr named "ttl" is stored
        // shadow-encoded as "_a2a$ttl" and unmangled by the caller before this.
        if (reader.ValueTextEquals(TtlNameU8)) return true;
        // Proxy metadata names (_a2a, _a2a_pk, ...) are always emitted
        // canonically — never escaped — so an escaped name cannot be ours.
        if (reader.ValueIsEscaped || reader.HasValueSequence) return false;
        return reader.ValueSpan.StartsWith(DiscriminatorPrefixU8);
    }

    /// <summary>Streaming counterpart of <see cref="IsCosmosSystemField"/> over
    /// the reader's current PropertyName token. Must stay in sync with that
    /// switch. <see cref="Utf8JsonReader.ValueTextEquals(ReadOnlySpan{byte})"/>
    /// transparently handles escaped names, so no escape guard is needed.</summary>
    private static bool IsCosmosSystemFieldToken<TReader>(scoped ref TReader reader)
        where TReader : ITokenReader, allows ref struct
        => reader.ValueTextEquals("_rid"u8)
        || reader.ValueTextEquals("_self"u8)
        || reader.ValueTextEquals("_etag"u8)
        || reader.ValueTextEquals("_ts"u8)
        || reader.ValueTextEquals("_attachments"u8)
        || reader.ValueTextEquals("_lsn"u8)
        || reader.ValueTextEquals("_metadata"u8);

    /// <summary>
    /// Writes the typed DDB AttributeValue for the JSON value the reader is
    /// currently positioned on. On entry the reader is on the value's first
    /// token; on return it is on the value's last token (Skip contract).
    /// </summary>
    private static void WriteAttributeValue<TReader>(Utf8JsonWriter writer, scoped ref TReader reader)
        where TReader : ITokenReader, allows ref struct
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                writer.WriteStartObject();
                writer.WritePropertyName(TagS);
                WriteStringTokenValue(writer, ref reader);
                writer.WriteEndObject();
                break;

            case JsonTokenType.Number:
                writer.WriteStartObject();
                writer.WritePropertyName(TagN);
                WriteNumberTokenAsString(writer, ref reader);
                writer.WriteEndObject();
                break;

            case JsonTokenType.True:
            case JsonTokenType.False:
                writer.WriteStartObject();
                writer.WriteBoolean(TagBool, reader.TokenType == JsonTokenType.True);
                writer.WriteEndObject();
                break;

            case JsonTokenType.Null:
                writer.WriteStartObject();
                writer.WriteBoolean(TagNull, true);
                writer.WriteEndObject();
                break;

            case JsonTokenType.StartArray:
                writer.WriteStartObject();
                writer.WritePropertyName(TagL);
                writer.WriteStartArray();
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    WriteAttributeValue(writer, ref reader);
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
                break;

            case JsonTokenType.StartObject:
                WriteObjectAsAttributeValue(writer, ref reader);
                break;

            default:
                throw new InvalidOperationException(
                    $"Cannot decode Cosmos token {reader.TokenType}.");
        }
    }

    /// <summary>
    /// Object disambiguation for the streaming path. Reader is on StartObject;
    /// peeks the first property name (no count lookahead needed — see the
    /// encoder invariant above) and either unwraps an envelope or emits an M.
    /// On return the reader is on the matching EndObject.
    /// </summary>
    private static void WriteObjectAsAttributeValue<TReader>(Utf8JsonWriter writer, scoped ref TReader reader)
        where TReader : ITokenReader, allows ref struct
    {
        if (!reader.Read())
            throw new InvalidOperationException("Truncated Cosmos object.");

        if (reader.TokenType == JsonTokenType.EndObject)
        {
            // Empty object → empty map.
            writer.WriteStartObject();
            writer.WritePropertyName(TagM);
            writer.WriteStartObject();
            writer.WriteEndObject();
            writer.WriteEndObject();
            return;
        }

        // reader is on the first PropertyName.
        if (!reader.ValueIsEscaped && !reader.HasValueSequence &&
            reader.ValueSpan.StartsWith(EnvelopeTagPrefixU8))
        {
            if (reader.ValueTextEquals(EnvelopeTagNU8))
            {
                reader.Read();
                if (reader.TokenType != JsonTokenType.String)
                    throw new InvalidOperationException("'_a2a:N' envelope payload must be a string.");
                writer.WriteStartObject();
                writer.WritePropertyName(TagN);
                WriteStringTokenValue(writer, ref reader);
                writer.WriteEndObject();
                reader.Read(); // consume wrapper EndObject
                return;
            }
            if (reader.ValueTextEquals(EnvelopeTagBU8))
            {
                reader.Read();
                if (reader.TokenType != JsonTokenType.String)
                    throw new InvalidOperationException("'_a2a:B' envelope payload must be a string.");
                writer.WriteStartObject();
                writer.WritePropertyName(TagB);
                WriteStringTokenValue(writer, ref reader);
                writer.WriteEndObject();
                reader.Read();
                return;
            }
            if (reader.ValueTextEquals(EnvelopeTagSSU8)) { WriteUnwrappedSet(writer, TagSS, ref reader); reader.Read(); return; }
            if (reader.ValueTextEquals(EnvelopeTagNSU8)) { WriteUnwrappedSet(writer, TagNS, ref reader); reader.Read(); return; }
            if (reader.ValueTextEquals(EnvelopeTagBSU8)) { WriteUnwrappedSet(writer, TagBS, ref reader); reader.Read(); return; }

            throw new InvalidOperationException("Unknown '_a2a:' envelope tag in Cosmos document.");
        }

        // Plain map. The first PropertyName has already been read.
        writer.WriteStartObject();
        writer.WritePropertyName(TagM);
        writer.WriteStartObject();
        while (true)
        {
            WriteCurrentPropertyName(writer, ref reader);
            reader.Read();
            WriteAttributeValue(writer, ref reader);
            if (!reader.Read() || reader.TokenType == JsonTokenType.EndObject)
                break;
        }
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    /// <summary>Reader is on the envelope's PropertyName; reads the array
    /// payload and writes the unwrapped set. On return the reader is on the
    /// array's EndArray token.</summary>
    private static void WriteUnwrappedSet<TReader>(Utf8JsonWriter writer, JsonEncodedText tag, scoped ref TReader reader)
        where TReader : ITokenReader, allows ref struct
    {
        reader.Read();
        if (reader.TokenType != JsonTokenType.StartArray)
            throw new InvalidOperationException($"Envelope {tag} payload must be a JSON array.");

        writer.WriteStartObject();
        writer.WritePropertyName(tag);
        writer.WriteStartArray();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType != JsonTokenType.String)
                throw new InvalidOperationException("Set members must be JSON strings.");
            WriteStringTokenValue(writer, ref reader);
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    /// <summary>Copies the current String/PropertyName token's value to the
    /// writer as a JSON string value, raw-UTF-8 when unescaped, otherwise via
    /// a pooled unescape buffer. Never allocates a System.String.</summary>
    private static void WriteStringTokenValue<TReader>(Utf8JsonWriter writer, scoped ref TReader reader)
        where TReader : ITokenReader, allows ref struct
    {
        if (!reader.ValueIsEscaped && !reader.HasValueSequence)
        {
            writer.WriteStringValue(reader.ValueSpan);
            return;
        }

        int max = reader.HasValueSequence ? checked((int)reader.ValueSequence.Length) : reader.ValueSpan.Length;
        byte[]? rented = max > 256 ? ArrayPool<byte>.Shared.Rent(max) : null;
        Span<byte> buf = rented ?? stackalloc byte[256];
        int written = reader.CopyString(buf);
        writer.WriteStringValue(buf[..written]);
        if (rented is not null) ArrayPool<byte>.Shared.Return(rented);
    }

    /// <summary>Writes the current PropertyName token as a property name,
    /// raw-UTF-8 when unescaped, otherwise via a pooled unescape buffer.</summary>
    private static void WriteCurrentPropertyName<TReader>(Utf8JsonWriter writer, scoped ref TReader reader)
        where TReader : ITokenReader, allows ref struct
    {
        if (!reader.ValueIsEscaped && !reader.HasValueSequence)
        {
            writer.WritePropertyName(reader.ValueSpan);
            return;
        }

        int max = reader.HasValueSequence ? checked((int)reader.ValueSequence.Length) : reader.ValueSpan.Length;
        byte[]? rented = max > 256 ? ArrayPool<byte>.Shared.Rent(max) : null;
        Span<byte> buf = rented ?? stackalloc byte[256];
        int written = reader.CopyString(buf);
        writer.WritePropertyName(buf[..written]);
        if (rented is not null) ArrayPool<byte>.Shared.Return(rented);
    }

    /// <summary>Materializes the current String token as an unescaped UTF-16
    /// string, parity with <see cref="JsonElement.GetString"/>. Used to capture
    /// the reserved Cosmos <c>id</c> correlation key in
    /// <see cref="WriteTransformedItemCapturingId{TReader}"/>.</summary>
    private static string MaterializeString<TReader>(scoped ref TReader reader)
        where TReader : ITokenReader, allows ref struct
    {
        int max = reader.HasValueSequence ? checked((int)reader.ValueSequence.Length) : reader.ValueSpan.Length;
        byte[]? rented = max > 256 ? ArrayPool<byte>.Shared.Rent(max) : null;
        Span<byte> buf = rented ?? stackalloc byte[256];
        int written = reader.CopyString(buf);
        string s = Encoding.UTF8.GetString(buf[..written]);
        if (rented is not null) ArrayPool<byte>.Shared.Return(rented);
        return s;
    }

    /// <summary>Writes the current Number token as a DDB N string value
    /// (<c>{"N":"&lt;digits&gt;"}</c>). Number tokens never carry escapes.</summary>
    private static void WriteNumberTokenAsString<TReader>(Utf8JsonWriter writer, scoped ref TReader reader)
        where TReader : ITokenReader, allows ref struct
    {
        if (!reader.HasValueSequence)
        {
            writer.WriteStringValue(reader.ValueSpan);
            return;
        }
        int len = checked((int)reader.ValueSequence.Length);
        byte[] rented = ArrayPool<byte>.Shared.Rent(len);
        reader.ValueSequence.CopyTo(rented);
        writer.WriteStringValue(rented.AsSpan(0, len));
        ArrayPool<byte>.Shared.Return(rented);
    }

    /// <summary>
    /// Same as <see cref="ExtractItem(Stream)"/> but takes an already-
    /// parsed <see cref="JsonElement"/> rooted at the Cosmos doc.
    /// </summary>
    public static Dictionary<string, JsonElement>? ExtractItem(JsonElement docRoot)
    {
        if (docRoot.ValueKind != JsonValueKind.Object) return null;

        // Batch decode: encode every attribute into a single buffer
        // wrapped as one JSON object, parse once, then clone children.
        // This avoids N separate JsonDocument.Parse / Clone pairs that
        // dominate per-attribute cost at small item sizes.
        var bw = new ArrayBufferWriter<byte>(1024);
        using (var writer = new Utf8JsonWriter(bw, WriterOptions))
        {
            writer.WriteStartObject();
            foreach (var prop in docRoot.EnumerateObject())
            {
                string targetName;
                if (prop.Name.Equals(ShadowEncodedIdName, StringComparison.Ordinal))
                {
                    // Unmangle the shadow-encoded "id" attribute.
                    targetName = IdProperty;
                }
                else if (prop.Name.Equals(ShadowEncodedTtlName, StringComparison.Ordinal))
                {
                    // Unmangle the shadow-encoded "ttl" attribute.
                    targetName = TtlProperty;
                }
                else if (IsReservedTopLevelName(prop.Name) || IsCosmosSystemField(prop.Name))
                {
                    // Routing fields, discriminator, other reserved
                    // _a2a-namespace props, Cosmos system fields
                    // (_rid/_self/_etag/_ts/_attachments/...), and the injected
                    // native "ttl" — never user data.
                    continue;
                }
                else
                {
                    targetName = prop.Name;
                }

                writer.WritePropertyName(targetName);
                WriteAttributeValue(writer, prop.Value);
            }
            writer.WriteEndObject();
        }

        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        using var batch = JsonDocument.Parse(bw.WrittenMemory);
        foreach (var prop in batch.RootElement.EnumerateObject())
        {
            result[prop.Name] = prop.Value.Clone();
        }
        return result;
    }

    /// <summary>
    /// Decodes a single Cosmos JSON value back to the corresponding DDB
    /// AttributeValue (e.g. <c>{"S":"foo"}</c>), applying the inference
    /// rules in reverse. The returned <see cref="JsonElement"/> is
    /// detached from any parent <see cref="JsonDocument"/>.
    /// </summary>
    public static JsonElement DecodeToAttributeValue(JsonElement value)
    {
        var bw = new ArrayBufferWriter<byte>(64);
        using (var writer = new Utf8JsonWriter(bw, WriterOptions))
        {
            WriteAttributeValue(writer, value);
        }
        // Parse-and-clone so the returned element owns its buffer.
        using var doc = JsonDocument.Parse(bw.WrittenMemory);
        return doc.RootElement.Clone();
    }

    /// <summary>
    /// Writes the typed DDB AttributeValue for <paramref name="value"/>
    /// into <paramref name="writer"/>. Used by both the per-attribute
    /// decoder above and any caller that wants to stream a whole map
    /// without intermediate allocations.
    /// </summary>
    public static void WriteAttributeValue(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                writer.WriteStartObject();
                // Copy the raw UTF-8 string token straight through instead of
                // GetString() (UTF8→UTF16) + WriteString (UTF16→UTF8). WriteTo
                // re-escapes via the destination writer's encoder, so byte
                // parity is preserved (pinned by the golden corpus).
                writer.WritePropertyName(TagS);
                value.WriteTo(writer);
                writer.WriteEndObject();
                break;

            case JsonValueKind.Number:
                writer.WriteStartObject();
                writer.WritePropertyName(TagN);
                writer.WriteStringValue(value.GetRawText());
                writer.WriteEndObject();
                break;

            case JsonValueKind.True:
            case JsonValueKind.False:
                writer.WriteStartObject();
                writer.WriteBoolean(TagBool, value.GetBoolean());
                writer.WriteEndObject();
                break;

            case JsonValueKind.Null:
                writer.WriteStartObject();
                writer.WriteBoolean(TagNull, true);
                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                writer.WriteStartObject();
                writer.WritePropertyName(TagL);
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray())
                {
                    WriteAttributeValue(writer, item);
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
                break;

            case JsonValueKind.Object:
                WriteObjectAsAttributeValue(writer, value);
                break;

            case JsonValueKind.Undefined:
            default:
                throw new InvalidOperationException(
                    $"Cannot decode Cosmos value with kind {value.ValueKind}.");
        }
    }

    /// <summary>
    /// Object disambiguation: peek the first (and only) property name;
    /// if it matches a known envelope tag, unwrap; else decode as M
    /// (recurse).
    /// </summary>
    private static void WriteObjectAsAttributeValue(Utf8JsonWriter writer, JsonElement obj)
    {
        // Try the envelope short-circuit first. We require exactly one
        // property because the encoder never emits multi-prop envelopes.
        if (TryGetSingleProperty(obj, out var only) && only.Name.StartsWith(EnvelopeTagPrefix, StringComparison.Ordinal))
        {
            switch (only.Name)
            {
                case EnvelopeTagN:
                    writer.WriteStartObject();
                    writer.WritePropertyName(TagN);
                    if (only.Value.ValueKind != JsonValueKind.String)
                        throw new InvalidOperationException("'_a2a:N' envelope payload must be a string.");
                    only.Value.WriteTo(writer);
                    writer.WriteEndObject();
                    return;

                case EnvelopeTagB:
                    writer.WriteStartObject();
                    writer.WritePropertyName(TagB);
                    only.Value.WriteTo(writer);
                    writer.WriteEndObject();
                    return;

                case EnvelopeTagSS:
                    WriteUnwrappedSet(writer, TagSS, only.Value);
                    return;

                case EnvelopeTagNS:
                    WriteUnwrappedSet(writer, TagNS, only.Value);
                    return;

                case EnvelopeTagBS:
                    WriteUnwrappedSet(writer, TagBS, only.Value);
                    return;

                default:
                    throw new InvalidOperationException(
                        $"Unknown envelope tag '{only.Name}' in Cosmos document.");
            }
        }

        // Plain map.
        writer.WriteStartObject();
        writer.WritePropertyName(TagM);
        writer.WriteStartObject();
        foreach (var prop in obj.EnumerateObject())
        {
            writer.WritePropertyName(prop.Name);
            WriteAttributeValue(writer, prop.Value);
        }
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static void WriteUnwrappedSet(Utf8JsonWriter writer, JsonEncodedText tag, JsonElement arr)
    {
        if (arr.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException(
                $"Envelope {tag} payload must be a JSON array.");

        writer.WriteStartObject();
        writer.WritePropertyName(tag);
        writer.WriteStartArray();
        foreach (var member in arr.EnumerateArray())
        {
            if (member.ValueKind != JsonValueKind.String)
                throw new InvalidOperationException("Set members must be JSON strings.");
            member.WriteTo(writer);
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static bool TryGetSingleProperty(JsonElement obj, out JsonProperty only)
    {
        only = default;
        int count = 0;
        foreach (var p in obj.EnumerateObject())
        {
            count++;
            if (count > 1) return false;
            only = p;
        }
        return count == 1;
    }
}

/// <summary>
/// Per-document callback for
/// <see cref="InferredAttributeStorage.ExtractItemsFusedWithId{TReader,TSink}"/>.
/// Receives one transformed Cosmos document at a time: its raw Cosmos <c>id</c>
/// value (the correlation key, <c>null</c> when the document carried no string
/// <c>id</c>) alongside the AttributeValue map. Implemented as a value type
/// (<c>struct</c> constraint on the consumer) so the generic page walk
/// monomorphizes and the JIT devirtualizes the callback — no delegate, closure,
/// or intermediate collection allocation on the page-streaming read path.
/// </summary>
internal interface IFusedItemWithIdSink
{
    /// <summary>Receives one transformed document.</summary>
    void Accept(string? id, Dictionary<string, JsonElement> map);
}

/// <summary>
/// Byte-streaming per-document callback for
/// <see cref="InferredAttributeStorage.ExtractItemsFusedWithIdBytes{TReader,TSink}"/>.
/// Receives each document's correlation <c>id</c> (<c>null</c> when absent) and the
/// transformed DDB item bytes — a complete <c>{…}</c> object written with the
/// default <see cref="JavaScriptEncoder"/>. The span is the shared scratch buffer,
/// reused after the call returns; an implementation that retains the bytes MUST
/// copy them. A value type (<c>struct</c> constraint) so the generic page walk
/// monomorphizes and the JIT devirtualizes the callback with no allocation.
/// </summary>
internal interface IFusedItemBytesWithIdSink
{
    /// <summary>Receives one transformed document's bytes.</summary>
    void Accept(string? id, ReadOnlySpan<byte> itemBytes);
}
