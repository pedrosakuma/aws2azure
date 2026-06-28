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
    // ---------------- WRITE PATH (DDB → Cosmos) ----------------------

    /// <summary>
    /// Builds the Cosmos document JSON for a PutItem-style write,
    /// flattening every DDB attribute at the root using the inference
    /// rules above. The caller is responsible for having already
    /// validated key-attribute presence/type and reserved-name conflicts
    /// (via <see cref="IsReservedTopLevelName"/>).
    /// </summary>
    public static void WriteCosmosDocument(IBufferWriter<byte> output, string id, string pk, JsonElement item, int? ttlSeconds = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        using var writer = new Utf8JsonWriter(output, WriterOptions);
        WriteCosmosDocumentCore(new Utf8JsonTokenWriter(writer), id, pk, item, ttlSeconds);
    }

    /// <summary>
    /// Builds the Cosmos document as <b>CosmosBinary</b> JSON (the <c>0x80</c>
    /// format), the opt-in write-body encoding of the #332 GO. Drives the exact
    /// same token walk as <see cref="WriteCosmosDocument"/>, differing only in
    /// the <see cref="ITokenWriter"/> back-end, so the two are semantically
    /// symmetric (verified by the decode round-trip golden corpus).
    /// </summary>
    public static void WriteCosmosDocumentBinary(IBufferWriter<byte> output, string id, string pk, JsonElement item, int? ttlSeconds = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        using var writer = new CosmosBinaryWriter(output);
        WriteCosmosDocumentCore(writer, id, pk, item, ttlSeconds);
    }

    /// <summary>
    /// Single-pass variant of <see cref="WriteCosmosDocumentBinary(IBufferWriter{byte},string,string,JsonElement)"/>:
    /// assembles the CosmosBinary body into a self-owned pooled buffer and
    /// returns the writer so the caller can send <see cref="CosmosBinaryWriter.WrittenMemory"/>
    /// directly (no copy into a second buffer). The caller <b>owns the returned
    /// writer</b> and must dispose it once the send completes.
    /// </summary>
    public static CosmosBinaryWriter WriteCosmosDocumentBinary(string id, string pk, JsonElement item, int? ttlSeconds = null)
    {
        var writer = new CosmosBinaryWriter();
        try
        {
            WriteCosmosDocumentCore(writer, id, pk, item, ttlSeconds);
        }
        catch
        {
            writer.Dispose();
            throw;
        }

        return writer;
    }

    // ---------------- SINGLE-PASS WIRE WRITE PATH (#342) -------------------
    //
    // Encodes the Cosmos document straight from the raw UTF-8 bytes of the
    // DynamoDB attribute-map (the request wire payload) via a Utf8JsonReader,
    // emitting tokens directly to the shared ITokenWriter. This avoids the
    // JsonElement traversal + per-attribute GetString() UTF-16 materialization
    // the JsonElement overloads pay (spike #332: that materialization tax — not
    // the formatter — dominates the per-write CPU cost). String values are
    // forwarded as UTF-8 spans (verbatim unescaped, or unescaped once via
    // CopyString when the wire value carried escapes), so the common
    // string-heavy item never allocates per attribute. Numbers, a short ASCII
    // minority, still materialize their raw text once for canonical
    // normalisation — identical output to the JsonElement path.
    //
    // The JsonElement overloads above are retained for callers whose item is
    // synthesized in memory (UpdateItem's read-modify-write) and has no backing
    // wire bytes, and for the string-producing sproc/Transact paths.

    private const int WireUnescapeStackThreshold = 256;

    // The item-level request models deserialize with AllowTrailingCommas (see
    // ItemJsonContext), so the single-pass wire readers must accept the same
    // grammar or a body that deserialized fine could throw mid-encode. Comment
    // handling stays at the serializer default (Disallow).
    internal static readonly JsonReaderOptions WireReaderOptions = new()
    {
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// Single-pass wire variant of <see cref="WriteCosmosDocument(IBufferWriter{byte},string,string,JsonElement)"/>:
    /// encodes the Cosmos document JSON-text body directly from the raw UTF-8
    /// bytes of the DynamoDB attribute-map (<paramref name="itemUtf8"/>), with
    /// no intermediate <see cref="JsonElement"/> or per-attribute string
    /// materialization. Output is byte-identical to the JsonElement overload.
    /// </summary>
    public static void WriteCosmosDocument(IBufferWriter<byte> output, string id, string pk, ReadOnlySpan<byte> itemUtf8, int? ttlSeconds = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        using var writer = new Utf8JsonWriter(output, WriterOptions);
        var reader = new Utf8JsonReader(itemUtf8, WireReaderOptions);
        WriteCosmosDocumentCore(new Utf8JsonTokenWriter(writer), id, pk, ref reader, ttlSeconds);
    }

    /// <summary>
    /// Single-pass wire variant of
    /// <see cref="WriteCosmosDocumentBinary(IBufferWriter{byte},string,string,JsonElement)"/>:
    /// encodes the CosmosBinary (<c>0x80</c>) body directly from the raw UTF-8
    /// attribute-map bytes. Decode round-trip is identical to the JsonElement
    /// overload (golden corpus).
    /// </summary>
    public static void WriteCosmosDocumentBinary(IBufferWriter<byte> output, string id, string pk, ReadOnlySpan<byte> itemUtf8, int? ttlSeconds = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        using var writer = new CosmosBinaryWriter(output);
        var reader = new Utf8JsonReader(itemUtf8, WireReaderOptions);
        WriteCosmosDocumentCore(writer, id, pk, ref reader, ttlSeconds);
    }

    /// <summary>
    /// Self-owned single-pass wire variant of
    /// <see cref="WriteCosmosDocumentBinary(string,string,JsonElement)"/>:
    /// assembles the CosmosBinary body into a pooled buffer exposed via
    /// <see cref="CosmosBinaryWriter.WrittenMemory"/> for a zero-copy send. The
    /// caller owns and must dispose the returned writer once the send completes.
    /// </summary>
    public static CosmosBinaryWriter WriteCosmosDocumentBinary(string id, string pk, ReadOnlySpan<byte> itemUtf8, int? ttlSeconds = null)
    {
        var writer = new CosmosBinaryWriter();
        try
        {
            var reader = new Utf8JsonReader(itemUtf8, WireReaderOptions);
            WriteCosmosDocumentCore(writer, id, pk, ref reader, ttlSeconds);
        }
        catch
        {
            writer.Dispose();
            throw;
        }

        return writer;
    }

    // Cosmos' reserved per-item TTL property: a duration in seconds relative to
    // the document's last-write _ts. Written immediately after the proxy's own
    // reserved fields and before user attributes (#465).
    private static readonly TokenName TtlPropName = new("ttl");

    /// <summary>
    /// Emits the Cosmos reserved <c>ttl</c> property when <paramref name="ttlSeconds"/>
    /// is set. The value is a small positive duration, formatted without an
    /// intermediate string allocation.
    /// </summary>
    private static void WriteItemTtl<TWriter>(TWriter writer, int? ttlSeconds)
        where TWriter : ITokenWriter
    {
        if (!ttlSeconds.HasValue)
        {
            return;
        }

        writer.WritePropertyName(TtlPropName);
        Span<byte> buffer = stackalloc byte[16];
        Utf8Formatter.TryFormat(ttlSeconds.Value, buffer, out int written);
        writer.WriteNumberRaw(buffer[..written]);
    }

    private static void WriteCosmosDocumentCore<TWriter>(TWriter writer, string id, string pk, ref Utf8JsonReader reader, int? ttlSeconds = null)
        where TWriter : ITokenWriter
    {
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            throw new ArgumentException("Item must be a JSON object.", nameof(reader));

        writer.WriteStartObject();
        writer.WriteString(IdPropName, id);
        writer.WriteString(PkPropName, pk);
        writer.WriteString(DiscPropName, DiscValueItemName);
        WriteItemTtl(writer, ttlSeconds);

        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            WriteCheckedPropertyName(writer, ref reader, topLevel: true);
            reader.Read(); // advance onto the attribute value's first token
            EncodeAttributeValueFromReader(writer, ref reader);
        }

        writer.WriteEndObject();
        writer.Flush();
    }

    private static void EncodeAttributeValueFromReader<TWriter>(TWriter writer, ref Utf8JsonReader reader)
        where TWriter : ITokenWriter
    {
        var source = new Utf8DdbAttributeSource(reader);
        EncodeAttributeValueCore<TWriter, Utf8DdbAttributeSource>(writer, ref source);
        reader = source.Reader;
    }

    /// <summary>
    /// Writes the current property name, enforcing the reserved-name rules the
    /// JsonElement walk applies: at the top level the rare <c>id</c> attribute
    /// is shadow-encoded and any other <c>_a2a</c>-namespaced name is rejected;
    /// inside a map any <c>_a2a:</c>-prefixed name is rejected. The unescaped
    /// name bytes are forwarded verbatim (the writer re-escapes for text).
    /// </summary>
    private static void WriteCheckedPropertyName<TWriter>(TWriter writer, ref Utf8JsonReader reader, bool topLevel)
        where TWriter : ITokenWriter
    {
        if (!reader.ValueIsEscaped)
        {
            WriteCheckedPropertyNameCore(writer, reader.ValueSpan, topLevel);
            return;
        }

        int len = reader.ValueSpan.Length;
        byte[]? rented = null;
        Span<byte> buf = len <= WireUnescapeStackThreshold
            ? stackalloc byte[WireUnescapeStackThreshold]
            : (rented = ArrayPool<byte>.Shared.Rent(len));
        try
        {
            int written = reader.CopyString(buf);
            WriteCheckedPropertyNameCore(writer, buf[..written], topLevel);
        }
        finally
        {
            if (rented is not null)
                ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static void WriteCheckedPropertyNameCore<TWriter>(TWriter writer, ReadOnlySpan<byte> name, bool topLevel)
        where TWriter : ITokenWriter
    {
        if (topLevel)
        {
            if (name.SequenceEqual("id"u8))
            {
                // Shadow-encode the rare DDB attr that collides with Cosmos's
                // required "id" field; decoder unmangles.
                writer.WritePropertyName(ShadowIdPropName);
                return;
            }

            if (name.SequenceEqual("ttl"u8))
            {
                // Shadow-encode a user attr named "ttl" so it doesn't collide
                // with Cosmos's native time-to-live field (which the proxy
                // injects on TTL-enabled tables); decoder unmangles.
                writer.WritePropertyName(ShadowTtlPropName);
                return;
            }

            // Any name in the _a2a namespace (other than the shadow-encodable
            // "id"/"ttl") is reserved for proxy use and must be rejected.
            if (name.StartsWith("_a2a"u8))
            {
                throw new ArgumentException(
                    $"Attribute '{Encoding.UTF8.GetString(name)}' uses a reserved name and would collide with proxy metadata.",
                    nameof(name));
            }
        }
        else if (name.StartsWith("_a2a:"u8))
        {
            throw new ArgumentException(
                $"Nested attribute name '{Encoding.UTF8.GetString(name)}' uses the reserved '_a2a:' prefix.",
                nameof(name));
        }

        writer.WritePropertyName(name);
    }

    private static void WriteStringValueFromReader<TWriter>(TWriter writer, ref Utf8JsonReader reader)
        where TWriter : ITokenWriter
    {
        // Common case: the wire string carries no escapes, so its raw value
        // span is already the unescaped UTF-8 — forward it with zero copies.
        if (!reader.ValueIsEscaped)
        {
            writer.WriteStringValue(reader.ValueSpan);
            return;
        }

        int len = reader.ValueSpan.Length;
        byte[]? rented = null;
        Span<byte> buf = len <= WireUnescapeStackThreshold
            ? stackalloc byte[WireUnescapeStackThreshold]
            : (rented = ArrayPool<byte>.Shared.Rent(len));
        try
        {
            int written = reader.CopyString(buf);
            writer.WriteStringValue(buf[..written]);
        }
        finally
        {
            if (rented is not null)
                ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static void ExpectString(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.String)
            throw new ArgumentException(
                "AttributeValue payload must be a JSON string per DDB wire format.", nameof(reader));
    }

    private enum AttrTag
    {
        Unknown = 0,
        String,
        Number,
        Binary,
        Bool,
        Null,
        Map,
        List,
        StringSet,
        NumberSet,
        BinarySet,
    }

    private static AttrTag MatchTag(ReadOnlySpan<byte> tag) => tag.Length switch
    {
        1 => tag[0] switch
        {
            (byte)'S' => AttrTag.String,
            (byte)'N' => AttrTag.Number,
            (byte)'B' => AttrTag.Binary,
            (byte)'M' => AttrTag.Map,
            (byte)'L' => AttrTag.List,
            _ => AttrTag.Unknown,
        },
        2 when tag.SequenceEqual("SS"u8) => AttrTag.StringSet,
        2 when tag.SequenceEqual("NS"u8) => AttrTag.NumberSet,
        2 when tag.SequenceEqual("BS"u8) => AttrTag.BinarySet,
        4 when tag.SequenceEqual("BOOL"u8) => AttrTag.Bool,
        4 when tag.SequenceEqual("NULL"u8) => AttrTag.Null,
        _ => AttrTag.Unknown,
    };

    private static AttrTag MatchTag(string tag) => tag switch
    {
        AttributeValueTypes.String => AttrTag.String,
        AttributeValueTypes.Number => AttrTag.Number,
        AttributeValueTypes.Binary => AttrTag.Binary,
        AttributeValueTypes.Bool => AttrTag.Bool,
        AttributeValueTypes.Null => AttrTag.Null,
        AttributeValueTypes.Map => AttrTag.Map,
        AttributeValueTypes.List => AttrTag.List,
        AttributeValueTypes.StringSet => AttrTag.StringSet,
        AttributeValueTypes.NumberSet => AttrTag.NumberSet,
        AttributeValueTypes.BinarySet => AttrTag.BinarySet,
        _ => AttrTag.Unknown,
    };

    /// <summary>
    /// Resolves the AttributeValue type tag from the current property-name
    /// token. The unescaped fast path forwards the raw span to
    /// <see cref="MatchTag(ReadOnlySpan{byte})"/>; the rare escaped form (a
    /// client may write e.g. <c>{"\u0053":...}</c>) is compared escape-aware via
    /// <see cref="Utf8JsonReader.ValueTextEquals(ReadOnlySpan{byte})"/> so the
    /// wire path accepts exactly what the JsonElement path does.
    /// </summary>
    private static AttrTag MatchTag(ref Utf8JsonReader reader)
    {
        if (!reader.ValueIsEscaped)
            return MatchTag(reader.ValueSpan);

        if (reader.ValueTextEquals("S"u8)) return AttrTag.String;
        if (reader.ValueTextEquals("N"u8)) return AttrTag.Number;
        if (reader.ValueTextEquals("B"u8)) return AttrTag.Binary;
        if (reader.ValueTextEquals("M"u8)) return AttrTag.Map;
        if (reader.ValueTextEquals("L"u8)) return AttrTag.List;
        if (reader.ValueTextEquals("SS"u8)) return AttrTag.StringSet;
        if (reader.ValueTextEquals("NS"u8)) return AttrTag.NumberSet;
        if (reader.ValueTextEquals("BS"u8)) return AttrTag.BinarySet;
        if (reader.ValueTextEquals("BOOL"u8)) return AttrTag.Bool;
        if (reader.ValueTextEquals("NULL"u8)) return AttrTag.Null;
        return AttrTag.Unknown;
    }

    private interface IDdbAttributeSource
    {
        AttrTag ReadTag();

        void EnsureAttributeClosed();

        void ThrowInvalidAttributeValue();

        void ExpectStringPayload(AttrTag tag);

        void WriteStringValue<TWriter>(TWriter writer)
            where TWriter : ITokenWriter;

        string GetStringValue();

        /// <summary>
        /// Exposes the Number payload as raw <b>unescaped</b> UTF-8 bytes without
        /// a copy for the single-pass canonicalizer (#429), avoiding the
        /// per-attribute <c>GetString()</c> transcode. Returns true with a span
        /// valid for the lifetime of the source (the reader's value span) when
        /// the number carries no escapes; false when the caller must copy via
        /// <see cref="CopyNumberUtf8"/> or fall back to the string path.
        /// </summary>
        bool TryGetNumberUtf8Direct(out ReadOnlySpan<byte> utf8);

        /// <summary>
        /// Copies the (possibly escaped) Number payload as UTF-8 into
        /// <paramref name="dest"/>, returning the byte count — used only when
        /// <see cref="TryGetNumberUtf8Direct"/> returned false. Returns -1 when
        /// the source has no zero-string UTF-8 path (the JsonElement source) or
        /// the value does not fit, signalling a string-path fall-back.
        /// </summary>
        int CopyNumberUtf8(scoped Span<byte> dest);

        bool GetBooleanValue();

        void ExpectNullPayload();

        void WriteMap<TWriter>(TWriter writer)
            where TWriter : ITokenWriter;

        void WriteList<TWriter>(TWriter writer)
            where TWriter : ITokenWriter;

        void WriteSetEnvelope<TWriter>(TWriter writer, in TokenName tag)
            where TWriter : ITokenWriter;
    }

    private static void EncodeAttributeValueCore<TWriter, TSource>(TWriter writer, ref TSource source)
        where TWriter : ITokenWriter
        where TSource : IDdbAttributeSource, allows ref struct
    {
        AttrTag tag = source.ReadTag();

        switch (tag)
        {
            case AttrTag.String:
                source.ExpectStringPayload(tag);
                source.WriteStringValue(writer);
                break;

            case AttrTag.Number:
                source.ExpectStringPayload(tag);
                EncodeNumberValue(writer, ref source);
                break;

            case AttrTag.Binary:
                source.ExpectStringPayload(tag);
                writer.WriteStartObject();
                writer.WritePropertyName(EnvelopeBName);
                source.WriteStringValue(writer);
                writer.WriteEndObject();
                break;

            case AttrTag.Bool:
                writer.WriteBooleanValue(source.GetBooleanValue());
                break;

            case AttrTag.Null:
                source.ExpectNullPayload();
                writer.WriteNullValue();
                break;

            case AttrTag.Map:
                source.WriteMap(writer);
                break;

            case AttrTag.List:
                source.WriteList(writer);
                break;

            case AttrTag.StringSet:
                source.WriteSetEnvelope(writer, EnvelopeSSName);
                break;

            case AttrTag.NumberSet:
                source.WriteSetEnvelope(writer, EnvelopeNSName);
                break;

            case AttrTag.BinarySet:
                source.WriteSetEnvelope(writer, EnvelopeBSName);
                break;

            default:
                source.ThrowInvalidAttributeValue();
                break;
        }

        source.EnsureAttributeClosed();
    }

    /// <summary>
    /// Encodes a scalar DDB Number. Prefers the single-pass UTF-8 path (#429) —
    /// canonicalize the reader's value span straight into a stack buffer with no
    /// string materialization — and falls back to the string path only when the
    /// source can't surface raw UTF-8 (the JsonElement source, or a rare escaped
    /// number larger than the scratch buffer).
    /// </summary>
    private static void EncodeNumberValue<TWriter, TSource>(TWriter writer, ref TSource source)
        where TWriter : ITokenWriter
        where TSource : IDdbAttributeSource, allows ref struct
    {
        Span<byte> scratch = stackalloc byte[MaxRawDdbNumberUtf8Length];
        if (source.TryGetNumberUtf8Direct(out var direct))
        {
            EncodeNumberFromRawUtf8(writer, direct);
            return;
        }

        int copied = source.CopyNumberUtf8(scratch);
        if (copied >= 0)
            EncodeNumberFromRawUtf8(writer, scratch[..copied]);
        else
            EncodeNumberFromRaw(writer, source.GetStringValue());
    }

    private static void WriteCosmosDocumentCore<TWriter>(TWriter writer, string id, string pk, JsonElement item, int? ttlSeconds = null)
        where TWriter : ITokenWriter
    {
        if (item.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Item must be a JSON object.", nameof(item));

        writer.WriteStartObject();
        writer.WriteString(IdPropName, id);
        writer.WriteString(PkPropName, pk);
        writer.WriteString(DiscPropName, DiscValueItemName);
        WriteItemTtl(writer, ttlSeconds);

        foreach (var prop in item.EnumerateObject())
        {
            if (IsShadowEncodableName(prop.Name))
            {
                // Shadow-encode the rare DDB attr that collides with a
                // reserved Cosmos field ("id" routing key / "ttl" native
                // time-to-live); decoder unmangles.
                writer.WritePropertyName(ShadowNameFor(prop.Name));
            }
            else if (IsReservedTopLevelName(prop.Name))
            {
                // Names inside the _a2a namespace (other than the
                // shadow-encodable ones) are reserved for proxy use
                // and must be rejected — encoder can't disambiguate
                // a user "_a2a:foo" from an envelope tag.
                throw new ArgumentException(
                    $"Attribute '{prop.Name}' uses a reserved name and would collide with proxy metadata.",
                    nameof(item));
            }
            else
            {
                writer.WritePropertyName(prop.Name);
            }
            EncodeAttributeValue(writer, prop.Value);
        }

        writer.WriteEndObject();
        writer.Flush();
    }

    /// <summary>
    /// String-producing convenience over <see cref="WriteCosmosDocument"/>,
    /// retained for the stored-procedure path that embeds the document as a
    /// JSON string inside a sproc parameter array. The hot HTTP write paths
    /// use <see cref="WriteCosmosDocument"/> directly and never materialize
    /// this string.
    /// </summary>
    public static string BuildCosmosDocument(string id, string pk, JsonElement item)
    {
        var bw = new ArrayBufferWriter<byte>(1024);
        WriteCosmosDocument(bw, id, pk, item);
        return Encoding.UTF8.GetString(bw.WrittenSpan);
    }

    /// <summary>
    /// Encodes a single DDB AttributeValue (e.g. <c>{"S":"foo"}</c>)
    /// into the inferred Cosmos representation. Public so the
    /// UpdateItem read-modify-write path can re-encode attributes
    /// produced by the update executor without round-tripping through
    /// the full doc builder.
    /// </summary>
    public static void EncodeAttributeValue(Utf8JsonWriter writer, JsonElement ddbAttr)
        => EncodeAttributeValue(new Utf8JsonTokenWriter(writer), ddbAttr);

    private static void EncodeAttributeValue<TWriter>(TWriter writer, JsonElement ddbAttr)
        where TWriter : ITokenWriter
    {
        var source = new JsonElementDdbAttributeSource(ddbAttr);
        EncodeAttributeValueCore<TWriter, JsonElementDdbAttributeSource>(writer, ref source);
    }

    private ref struct Utf8DdbAttributeSource : IDdbAttributeSource
    {
        private Utf8JsonReader _reader;

        public Utf8DdbAttributeSource(Utf8JsonReader reader)
        {
            _reader = reader;
        }

        public readonly Utf8JsonReader Reader => _reader;

        public AttrTag ReadTag()
        {
            if (_reader.TokenType != JsonTokenType.StartObject
                || !_reader.Read() || _reader.TokenType != JsonTokenType.PropertyName)
            {
                ThrowInvalidAttributeValue();
            }

            // Type tags (S/N/B/BOOL/NULL/M/L/SS/NS/BS) are pure ASCII and never
            // JSON-escaped on the wire in practice, so the raw value span is the
            // tag — but a client may legally escape the name (e.g. {"\u0053":...}),
            // and the JsonElement path (which sees unescaped prop.Name) accepts it,
            // so match escape-aware to stay byte-identical.
            AttrTag tag = MatchTag(ref _reader);
            _reader.Read(); // advance onto the payload
            return tag;
        }

        public void EnsureAttributeClosed()
        {
            // Single-property discipline: the typed object must close right after
            // its one payload (a second property name would be a malformed value).
            if (!_reader.Read() || _reader.TokenType != JsonTokenType.EndObject)
            {
                ThrowInvalidAttributeValue();
            }
        }

        public void ThrowInvalidAttributeValue()
            => throw new ArgumentException(
                "AttributeValue must be a single-property typed value (e.g. {\"S\":\"x\"}).",
                "reader");

        public void ExpectStringPayload(AttrTag tag)
        {
            if (_reader.TokenType != JsonTokenType.String)
                throw new ArgumentException(
                    "AttributeValue payload must be a JSON string per DDB wire format.", "reader");
        }

        public void WriteStringValue<TWriter>(TWriter writer)
            where TWriter : ITokenWriter
            => WriteStringValueFromReader(writer, ref _reader);

        public string GetStringValue() => _reader.GetString() ?? string.Empty;

        public bool TryGetNumberUtf8Direct(out ReadOnlySpan<byte> utf8)
        {
            // Common case: the number carries no escapes, so its raw value span
            // is already the unescaped UTF-8 digits — forward with zero copies.
            if (!_reader.ValueIsEscaped)
            {
                utf8 = _reader.ValueSpan;
                return true;
            }

            utf8 = default;
            return false;
        }

        public int CopyNumberUtf8(scoped Span<byte> dest)
        {
            // Escaped numbers are not emitted by DDB / the AWS SDK, but stay
            // correct: unescape into the caller's buffer when it fits, else
            // signal a string-path fall-back.
            if (_reader.ValueSpan.Length > dest.Length)
                return -1;
            return _reader.CopyString(dest);
        }

        public bool GetBooleanValue()
        {
            if (_reader.TokenType is not (JsonTokenType.True or JsonTokenType.False))
                throw new ArgumentException("BOOL AttributeValue payload must be a JSON boolean.", "reader");
            return _reader.TokenType == JsonTokenType.True;
        }

        public void ExpectNullPayload()
        {
            if (_reader.TokenType != JsonTokenType.True)
                throw new ArgumentException("NULL AttributeValue payload must be the literal true.", "reader");
        }

        public void WriteMap<TWriter>(TWriter writer)
            where TWriter : ITokenWriter
        {
            if (_reader.TokenType != JsonTokenType.StartObject)
                throw new ArgumentException("M AttributeValue payload must be a JSON object.", "reader");

            writer.WriteStartObject();
            while (_reader.Read() && _reader.TokenType == JsonTokenType.PropertyName)
            {
                WriteCheckedPropertyName(writer, ref _reader, topLevel: false);
                _reader.Read();
                EncodeAttributeValueCore<TWriter, Utf8DdbAttributeSource>(writer, ref this);
            }

            writer.WriteEndObject();
        }

        public void WriteList<TWriter>(TWriter writer)
            where TWriter : ITokenWriter
        {
            if (_reader.TokenType != JsonTokenType.StartArray)
                throw new ArgumentException("L AttributeValue payload must be a JSON array.", "reader");

            writer.WriteStartArray();
            while (_reader.Read() && _reader.TokenType != JsonTokenType.EndArray)
            {
                EncodeAttributeValueCore<TWriter, Utf8DdbAttributeSource>(writer, ref this);
            }

            writer.WriteEndArray();
        }

        public void WriteSetEnvelope<TWriter>(TWriter writer, in TokenName tag)
            where TWriter : ITokenWriter
        {
            if (_reader.TokenType != JsonTokenType.StartArray)
                throw new ArgumentException("Set AttributeValue payload must be a JSON array.", "reader");

            writer.WriteStartObject();
            writer.WritePropertyName(tag);
            writer.WriteStartArray();
            while (_reader.Read() && _reader.TokenType != JsonTokenType.EndArray)
            {
                if (_reader.TokenType != JsonTokenType.String)
                    throw new ArgumentException(
                        "Set members must be JSON strings per DynamoDB wire format.", "reader");
                WriteStringValueFromReader(writer, ref _reader);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }
    }

    private ref struct JsonElementDdbAttributeSource : IDdbAttributeSource
    {
        private readonly JsonElement _attribute;
        private JsonElement _payload;

        public JsonElementDdbAttributeSource(JsonElement attribute)
        {
            _attribute = attribute;
            _payload = default;
        }

        public AttrTag ReadTag()
        {
            if (!ParsedAttributeValue.TryParse(_attribute, out var parsed))
            {
                ThrowInvalidAttributeValue();
            }

            _payload = parsed.Value;
            return MatchTag(parsed.TypeTag);
        }

        public void EnsureAttributeClosed()
        {
        }

        public void ThrowInvalidAttributeValue()
            => throw new ArgumentException(
                "AttributeValue must be a single-property typed value (e.g. {\"S\":\"x\"}).",
                "ddbAttr");

        public void ExpectStringPayload(AttrTag tag)
        {
            if (tag == AttrTag.Number && _payload.ValueKind != JsonValueKind.String)
            {
                throw new ArgumentException(
                    "Number AttributeValue payload must be a JSON string per DDB wire format.",
                    "numberAttrValue");
            }
        }

        public void WriteStringValue<TWriter>(TWriter writer)
            where TWriter : ITokenWriter
            => writer.WriteStringValue(_payload.GetString());

        public string GetStringValue() => _payload.GetString() ?? string.Empty;

        public bool TryGetNumberUtf8Direct(out ReadOnlySpan<byte> utf8)
        {
            // The JsonElement source keeps its existing string path — it is the
            // legacy / correctness-gate path, not the production single-pass hot
            // path, so the per-attribute string is acceptable here.
            utf8 = default;
            return false;
        }

        public int CopyNumberUtf8(scoped Span<byte> dest) => -1;

        public bool GetBooleanValue() => _payload.GetBoolean();

        public void ExpectNullPayload()
        {
        }

        public void WriteMap<TWriter>(TWriter writer)
            where TWriter : ITokenWriter
        {
            if (_payload.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("M must be a JSON object.", "mapEl");

            writer.WriteStartObject();
            foreach (var prop in _payload.EnumerateObject())
            {
                // Nested attributes do NOT collide with top-level routing
                // names, but they could collide with the envelope tag prefix
                // (e.g. "_a2a:N") and confuse the decoder's single-prop
                // detection. Reject defensively — caller should also surface
                // this at validation time so the error is actionable.
                if (prop.Name.StartsWith(EnvelopeTagPrefix, StringComparison.Ordinal))
                    throw new ArgumentException(
                        $"Nested attribute name '{prop.Name}' uses the reserved '_a2a:' prefix.",
                        "mapEl");

                writer.WritePropertyName(prop.Name);
                var child = new JsonElementDdbAttributeSource(prop.Value);
                EncodeAttributeValueCore<TWriter, JsonElementDdbAttributeSource>(writer, ref child);
            }
            writer.WriteEndObject();
        }

        public void WriteList<TWriter>(TWriter writer)
            where TWriter : ITokenWriter
        {
            if (_payload.ValueKind != JsonValueKind.Array)
                throw new ArgumentException("L must be a JSON array.", "listEl");

            writer.WriteStartArray();
            foreach (var item in _payload.EnumerateArray())
            {
                var child = new JsonElementDdbAttributeSource(item);
                EncodeAttributeValueCore<TWriter, JsonElementDdbAttributeSource>(writer, ref child);
            }
            writer.WriteEndArray();
        }

        public void WriteSetEnvelope<TWriter>(TWriter writer, in TokenName tag)
            where TWriter : ITokenWriter
        {
            if (_payload.ValueKind != JsonValueKind.Array)
                throw new ArgumentException("Set value must be a JSON array.", "setEl");

            writer.WriteStartObject();
            writer.WritePropertyName(tag);
            writer.WriteStartArray();
            foreach (var member in _payload.EnumerateArray())
            {
                if (member.ValueKind != JsonValueKind.String)
                    throw new ArgumentException(
                        "Set members must be JSON strings per DynamoDB wire format.",
                        "setEl");
                writer.WriteStringValue(member.GetString());
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
    }

}
