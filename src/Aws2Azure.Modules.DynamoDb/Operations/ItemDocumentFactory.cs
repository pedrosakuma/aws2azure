using System;
using System.Buffers;
using System.Text.Json;
using Aws2Azure.Core.Buffers;
using Aws2Azure.Modules.DynamoDb.Internal;
using Aws2Azure.Modules.DynamoDb.Persistence;

namespace Aws2Azure.Modules.DynamoDb.Operations;

internal static partial class ItemHandlers
{
    /// <summary>
    /// Composes the Cosmos doc shape used for item writes as a <see cref="string"/>.
    /// Thin wrapper over <see cref="InferredAttributeStorage.BuildCosmosDocument"/>.
    /// Production write paths use the single-pass <see cref="ItemDocumentBody"/>
    /// (HTTP upsert/replace) or <see cref="ItemDocumentBody.CreateText"/> (sproc
    /// params) instead — both avoid the <c>byte[] → string → byte[]</c> round-trip
    /// this overload incurs. Retained for tests.
    /// </summary>
    internal static string BuildItemDocument(string id, string pk, JsonElement item)
        => InferredAttributeStorage.BuildCosmosDocument(id, pk, item);

    /// <summary>
    /// Builds the Cosmos doc shape as a UTF-8 <see cref="byte"/> array (no
    /// <see cref="string"/> / <see cref="StringContent"/> re-encode). Used by
    /// <c>BatchWriteItem</c>, where every unit's body is built upfront and held
    /// live across parallel sends, so a GC-managed array is the leak-safe
    /// representation.
    /// </summary>
    internal static byte[] BuildItemDocumentBytes(string id, string pk, JsonElement item)
        => BuildItemDocumentBytes(id, pk, item, binary: false);

    /// <summary>
    /// Builds the Cosmos doc shape as a UTF-8 <see cref="byte"/> array, choosing
    /// the CosmosBinary (<c>0x80</c>) encoding when <paramref name="binary"/> is
    /// set (#336), else JSON text. Used by <c>BatchWriteItem</c>, where each
    /// unit's standalone document body is built upfront and held live across
    /// parallel sends, so a GC-managed array is the leak-safe representation.
    /// </summary>
    internal static byte[] BuildItemDocumentBytes(string id, string pk, JsonElement item, bool binary, int? ttlSeconds = null, OrderKeyField[]? orderKeys = null)
    {
        DynamoDbMetrics.RecordWriteBodyFormat(binary);
        if (binary)
        {
            using var writer = InferredAttributeStorage.WriteCosmosDocumentBinary(id, pk, item, ttlSeconds, orderKeys);
            return writer.WrittenMemory.ToArray();
        }

        var bw = new System.Buffers.ArrayBufferWriter<byte>(1024);
        InferredAttributeStorage.WriteCosmosDocument(bw, id, pk, item, ttlSeconds, orderKeys);
        return bw.WrittenSpan.ToArray();
    }

    /// <summary>
    /// Owns the encoded body for a standalone Cosmos document write (PutItem
    /// upsert/replace, UpdateItem create/replace), selecting the CosmosBinary
    /// (<c>0x80</c>) encoding when enabled (#336) or JSON text otherwise, behind
    /// a uniform <see cref="Memory"/>. Single-pass for both: text writes straight
    /// into a pooled buffer; binary assembles into its own pooled buffer whose
    /// <see cref="CosmosBinaryWriter.WrittenMemory"/> is sent without a further
    /// copy. The same <see cref="Memory"/> may be re-sent across retry attempts;
    /// dispose once all sends complete. Paths that embed the document as a JSON
    /// value (sproc conditional writes, TransactWriteItems) cannot use binary and
    /// keep text.
    /// </summary>
    internal readonly struct ItemDocumentBody : IDisposable
    {
        private readonly PooledByteBufferWriter? _text;
        private readonly CosmosBinaryWriter? _binary;

        /// <summary>The encoded body, valid until <see cref="Dispose"/>.</summary>
        public ReadOnlyMemory<byte> Memory { get; }

        private ItemDocumentBody(PooledByteBufferWriter text)
        {
            _text = text;
            _binary = null;
            Memory = text.WrittenMemory;
        }

        private ItemDocumentBody(CosmosBinaryWriter binary)
        {
            _binary = binary;
            _text = null;
            Memory = binary.WrittenMemory;
        }

        public static ItemDocumentBody Create(string id, string pk, JsonElement item, bool binary, int? ttlSeconds = null, OrderKeyField[]? orderKeys = null)
        {
            DynamoDbMetrics.RecordWriteBodyFormat(binary);
            if (binary)
            {
                return new ItemDocumentBody(InferredAttributeStorage.WriteCosmosDocumentBinary(id, pk, item, ttlSeconds, orderKeys));
            }

            var text = new PooledByteBufferWriter();
            try
            {
                InferredAttributeStorage.WriteCosmosDocument(text, id, pk, item, ttlSeconds, orderKeys);
            }
            catch
            {
                text.Dispose();
                throw;
            }

            return new ItemDocumentBody(text);
        }

        /// <summary>
        /// Item-bytes overload: encodes the Cosmos body straight from the
        /// caller-sliced item attribute-map UTF-8 (<paramref name="itemUtf8"/>),
        /// with no <see cref="JsonElement"/> traversal and no
        /// <see cref="TryLocateItemBytes"/> re-scan — the caller already holds the
        /// item's exact byte range (the request <c>JsonRange</c>). Output is
        /// byte-identical to the <see cref="JsonElement"/> overload.
        /// </summary>
        public static ItemDocumentBody CreateFromItemBytes(string id, string pk, ReadOnlySpan<byte> itemUtf8, bool binary, int? ttlSeconds = null, OrderKeyField[]? orderKeys = null)
        {
            DynamoDbMetrics.RecordWriteBodyFormat(binary);
            if (binary)
            {
                return new ItemDocumentBody(InferredAttributeStorage.WriteCosmosDocumentBinary(id, pk, itemUtf8, ttlSeconds, orderKeys));
            }

            var text = new PooledByteBufferWriter();
            try
            {
                InferredAttributeStorage.WriteCosmosDocument(text, id, pk, itemUtf8, ttlSeconds, orderKeys);
            }
            catch
            {
                text.Dispose();
                throw;
            }

            return new ItemDocumentBody(text);
        }

        /// <summary>
        /// Text-only item-bytes overload for the stored-procedure write path
        /// (CosmosBinary does not apply to sproc input). Encodes straight from the
        /// caller-sliced item UTF-8, byte-identical to the <see cref="JsonElement"/>
        /// path. Does not emit the write-body-format metric, which tracks only
        /// standalone-document writes (#336), not sproc params.
        /// </summary>
        public static ItemDocumentBody CreateTextFromItemBytes(string id, string pk, ReadOnlySpan<byte> itemUtf8, int? ttlSeconds = null, OrderKeyField[]? orderKeys = null)
        {
            var text = new PooledByteBufferWriter();
            try
            {
                InferredAttributeStorage.WriteCosmosDocument(text, id, pk, itemUtf8, ttlSeconds, orderKeys);
            }
            catch
            {
                text.Dispose();
                throw;
            }

            return new ItemDocumentBody(text);
        }
        /// recoverable from the original request <paramref name="body"/>, encodes
        /// the Cosmos body straight from those bytes (no JsonElement traversal /
        /// per-attribute GetString), else falls back to the
        /// <see cref="JsonElement"/> overload (e.g. a synthesized item with no
        /// backing wire bytes). Output is byte-identical either way.
        /// </summary>
        public static ItemDocumentBody Create(
            string id, string pk, ReadOnlySpan<byte> body, JsonElement item, bool binary)
        {
            if (!TryLocateItemBytes(body, out int start, out int length))
            {
                return Create(id, pk, item, binary);
            }

            ReadOnlySpan<byte> itemUtf8 = body.Slice(start, length);
            DynamoDbMetrics.RecordWriteBodyFormat(binary);
            if (binary)
            {
                return new ItemDocumentBody(InferredAttributeStorage.WriteCosmosDocumentBinary(id, pk, itemUtf8));
            }

            var text = new PooledByteBufferWriter();
            try
            {
                InferredAttributeStorage.WriteCosmosDocument(text, id, pk, itemUtf8);
            }
            catch
            {
                text.Dispose();
                throw;
            }

            return new ItemDocumentBody(text);
        }

        /// <summary>
        /// Text-only single-pass overload for the stored-procedure write paths,
        /// where the document is embedded as a raw JSON value in the sproc
        /// parameter list (CosmosBinary does not apply to sproc input). Encodes
        /// straight from the item's wire bytes when recoverable from
        /// <paramref name="body"/> (else the <see cref="JsonElement"/> path),
        /// byte-identical either way. Does not emit the write-body-format metric,
        /// which tracks only standalone-document writes (#336), not sproc params.
        /// </summary>
        public static ItemDocumentBody CreateText(
            string id, string pk, ReadOnlySpan<byte> body, JsonElement item)
        {
            var text = new PooledByteBufferWriter();
            try
            {
                if (TryLocateItemBytes(body, out int start, out int length))
                {
                    InferredAttributeStorage.WriteCosmosDocument(text, id, pk, body.Slice(start, length));
                }
                else
                {
                    InferredAttributeStorage.WriteCosmosDocument(text, id, pk, item);
                }
            }
            catch
            {
                text.Dispose();
                throw;
            }

            return new ItemDocumentBody(text);
        }

        public void Dispose()
        {
            _text?.Dispose();
            _binary?.Dispose();
        }
    }

    /// <summary>
    /// Locates the byte range of the top-level <c>"Item"</c> attribute-map value
    /// inside the raw request <paramref name="body"/> with a single no-alloc
    /// <see cref="Utf8JsonReader"/> scan, so the write path can encode the Cosmos
    /// document directly from those wire bytes (#342). Returns false (caller
    /// falls back to the parsed <see cref="JsonElement"/>) when the request is
    /// not a top-level object or carries no <c>"Item"</c> object — e.g. a
    /// non-canonical casing the encoder need not optimize.
    /// </summary>
    internal static bool TryLocateItemBytes(ReadOnlySpan<byte> body, out int start, out int length)
    {
        start = 0;
        length = 0;

        var reader = new Utf8JsonReader(body, InferredAttributeStorage.WireReaderOptions);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            return false;
        }

        // The request deserializes case-insensitively and last-wins, so req.Item
        // binds to the LAST property whose name equals "Item" ignoring case. We
        // only optimize when exactly one such property exists AND it is the
        // canonical "Item" casing whose value object we can slice; any other
        // shape (a different-case variant, or more than one match) falls back to
        // the parsed JsonElement, which is always correct.
        int caseInsensitiveItems = 0;
        bool haveCanonical = false;
        int canonicalStart = 0;
        int canonicalLength = 0;

        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            bool canonical = reader.ValueTextEquals("Item"u8);
            bool isItem = canonical || NameEqualsItemIgnoreCase(ref reader);
            if (!reader.Read())
            {
                return false;
            }

            if (isItem)
            {
                caseInsensitiveItems++;
                if (canonical && reader.TokenType == JsonTokenType.StartObject)
                {
                    canonicalStart = (int)reader.TokenStartIndex;
                    reader.Skip();
                    canonicalLength = (int)reader.BytesConsumed - canonicalStart;
                    haveCanonical = true;
                    continue;
                }
            }

            reader.Skip();
        }

        if (caseInsensitiveItems == 1 && haveCanonical)
        {
            start = canonicalStart;
            length = canonicalLength;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Case-insensitive ASCII comparison of the current property-name token to
    /// <c>"Item"</c> — the slow companion to the <c>ValueTextEquals("Item"u8)</c>
    /// exact check, used only to detect non-canonical duplicates that
    /// case-insensitive deserialization would bind <c>req.Item</c> to. Handles
    /// escaped names via <see cref="Utf8JsonReader.CopyString(Span{byte})"/>.
    /// </summary>
    private static bool NameEqualsItemIgnoreCase(ref Utf8JsonReader reader)
    {
        if (!reader.ValueIsEscaped)
        {
            ReadOnlySpan<byte> name = reader.ValueSpan;
            return name.Length == 4 && AsciiEqualsItem(name);
        }

        int raw = reader.ValueSpan.Length;
        if (raw < 4)
        {
            return false;
        }

        byte[] rented = ArrayPool<byte>.Shared.Rent(raw);
        try
        {
            int written = reader.CopyString(rented);
            return written == 4 && AsciiEqualsItem(rented);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static bool AsciiEqualsItem(ReadOnlySpan<byte> name) =>
        (name[0] | 0x20) == 'i'
        && (name[1] | 0x20) == 't'
        && (name[2] | 0x20) == 'e'
        && (name[3] | 0x20) == 'm';
}
