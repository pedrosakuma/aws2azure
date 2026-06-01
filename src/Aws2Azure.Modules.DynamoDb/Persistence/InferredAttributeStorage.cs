using System;
using System.Buffers;
using System.Buffers.Text;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Aws2Azure.Modules.DynamoDb.Operations;

namespace Aws2Azure.Modules.DynamoDb.Persistence;

/// <summary>
/// DynamoDB ↔ Cosmos document persistence using <b>type inference</b>
/// driven by <see cref="JsonValueKind"/>, replacing the v1 byte-for-byte
/// item-envelope.
///
/// <para><b>Cosmos doc shape</b> (flat):</para>
/// <code>
/// {
///   "id":   "&lt;formatted sort-key&gt;",
///   "pk":   "&lt;formatted partition-key&gt;",
///   "_a2a": "item",
///   "&lt;attrName&gt;": &lt;inferred-or-envelope value&gt;,
///   ...
/// }
/// </code>
///
/// <para><b>Encoding rules</b> (DDB AttributeValue → Cosmos JSON):</para>
/// <list type="bullet">
///   <item><c>{S:v}</c> → bare string <c>"v"</c>.</item>
///   <item><c>{N:v}</c> → bare JSON number <c>v</c> if it round-trips
///     through <see cref="decimal"/> losslessly; otherwise envelope
///     <c>{"_a2a:N":"v"}</c> (preserves DDB 38-digit precision).</item>
///   <item><c>{BOOL:v}</c> → bare <c>true</c>/<c>false</c>.</item>
///   <item><c>{NULL:true}</c> → bare <c>null</c>.</item>
///   <item><c>{M:{...}}</c> → bare JSON object (recurse).</item>
///   <item><c>{L:[...]}</c> → bare JSON array (recurse).</item>
///   <item><c>{B:"&lt;b64&gt;"}</c> → envelope <c>{"_a2a:B":"&lt;b64&gt;"}</c>
///     (disambiguates from S).</item>
///   <item><c>{SS:[...]}</c> → envelope <c>{"_a2a:SS":[...]}</c>.</item>
///   <item><c>{NS:[...]}</c> → envelope <c>{"_a2a:NS":[...]}</c>.</item>
///   <item><c>{BS:[...]}</c> → envelope <c>{"_a2a:BS":[...]}</c>.</item>
/// </list>
///
/// <para><b>Decoding rules</b> (Cosmos JSON → DDB AttributeValue):</para>
/// <list type="bullet">
///   <item><see cref="JsonValueKind.String"/> → <c>{S:v}</c>.</item>
///   <item><see cref="JsonValueKind.Number"/> → <c>{N:rawText}</c>.</item>
///   <item><see cref="JsonValueKind.True"/>/<see cref="JsonValueKind.False"/>
///     → <c>{BOOL:v}</c>.</item>
///   <item><see cref="JsonValueKind.Null"/> → <c>{NULL:true}</c>.</item>
///   <item><see cref="JsonValueKind.Object"/>: if single property named
///     <c>_a2a:N</c>/<c>_a2a:B</c>/<c>_a2a:SS</c>/<c>_a2a:NS</c>/<c>_a2a:BS</c>
///     unwrap to corresponding typed attribute value; else <c>{M:...}</c>.</item>
///   <item><see cref="JsonValueKind.Array"/> → <c>{L:[...]}</c> (each
///     element recursively decoded).</item>
/// </list>
///
/// <para><b>Reserved top-level attribute names</b> in <c>PutItem</c>/
/// <c>UpdateItem</c> input: <c>id</c>, <c>pk</c>, <c>_a2a</c>, and any
/// name starting with <c>_a2a:</c>. <see cref="IsReservedTopLevelName"/>
/// returns true for these.</para>
/// </summary>
internal static class InferredAttributeStorage
{
    // Reserved Cosmos doc top-level property names. These collide with
    // routing/discriminator metadata or with envelope-tag syntax and must
    // be rejected at write time, never round-tripped.
    //
    // <para>Design note on naming: Cosmos requires the document
    // identifier field to be named exactly <c>id</c>. The partition-key
    // path is configurable per-collection — we use <c>/_a2a_pk</c> so
    // the much more common DDB attribute name <c>pk</c> stays available
    // for user data. <c>id</c> is the only DDB attr name that collides
    // with a Cosmos hard requirement; PutItem / UpdateItem shadow-encode
    // such an attribute under the <see cref="ShadowPrefix"/> namespace
    // ("_a2a$id") so the user's <c>id</c> attribute round-trips losslessly
    // without clobbering the routing field.</para>
    public const string IdProperty = "id";
    public const string PkProperty = "_a2a_pk";
    public const string DiscriminatorProperty = "_a2a";
    public const string DiscriminatorValueItem = "item";

    // Shadow-encoding namespace for DDB attribute names that collide
    // with reserved Cosmos doc property names (currently only "id").
    // Distinct from the envelope-tag prefix ("_a2a:") so encoder/decoder
    // can disambiguate purely by character.
    public const string ShadowPrefix = "_a2a$";
    public const string ShadowEncodedIdName = "_a2a$id";

    // Envelope tag prefix. All five ambiguous-type tags live under this
    // namespace so detection is a single substring check.
    public const string EnvelopeTagPrefix = "_a2a:";
    public const string EnvelopeTagN = "_a2a:N";
    public const string EnvelopeTagB = "_a2a:B";
    public const string EnvelopeTagSS = "_a2a:SS";
    public const string EnvelopeTagNS = "_a2a:NS";
    public const string EnvelopeTagBS = "_a2a:BS";

    // Pre-encoded property names — avoids JS-escape work and per-call
    // allocations on the hot path.
    private static readonly JsonEncodedText IdPropEncoded = JsonEncodedText.Encode(IdProperty);
    private static readonly JsonEncodedText PkPropEncoded = JsonEncodedText.Encode(PkProperty);
    private static readonly JsonEncodedText DiscPropEncoded = JsonEncodedText.Encode(DiscriminatorProperty);
    private static readonly JsonEncodedText DiscValueItemEncoded = JsonEncodedText.Encode(DiscriminatorValueItem);
    private static readonly JsonEncodedText ShadowIdPropEncoded = JsonEncodedText.Encode(ShadowEncodedIdName);
    private static readonly JsonEncodedText TagS = JsonEncodedText.Encode(AttributeValueTypes.String);
    private static readonly JsonEncodedText TagN = JsonEncodedText.Encode(AttributeValueTypes.Number);
    private static readonly JsonEncodedText TagBool = JsonEncodedText.Encode(AttributeValueTypes.Bool);
    private static readonly JsonEncodedText TagNull = JsonEncodedText.Encode(AttributeValueTypes.Null);
    private static readonly JsonEncodedText TagM = JsonEncodedText.Encode(AttributeValueTypes.Map);
    private static readonly JsonEncodedText TagL = JsonEncodedText.Encode(AttributeValueTypes.List);
    private static readonly JsonEncodedText TagB = JsonEncodedText.Encode(AttributeValueTypes.Binary);
    private static readonly JsonEncodedText TagSS = JsonEncodedText.Encode(AttributeValueTypes.StringSet);
    private static readonly JsonEncodedText TagNS = JsonEncodedText.Encode(AttributeValueTypes.NumberSet);
    private static readonly JsonEncodedText TagBS = JsonEncodedText.Encode(AttributeValueTypes.BinarySet);
    private static readonly JsonEncodedText EnvelopeNEncoded = JsonEncodedText.Encode(EnvelopeTagN);
    private static readonly JsonEncodedText EnvelopeBEncoded = JsonEncodedText.Encode(EnvelopeTagB);
    private static readonly JsonEncodedText EnvelopeSSEncoded = JsonEncodedText.Encode(EnvelopeTagSS);
    private static readonly JsonEncodedText EnvelopeNSEncoded = JsonEncodedText.Encode(EnvelopeTagNS);
    private static readonly JsonEncodedText EnvelopeBSEncoded = JsonEncodedText.Encode(EnvelopeTagBS);

    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        SkipValidation = false,
    };

    /// <summary>
    /// True if <paramref name="name"/> is a reserved Cosmos doc top-level
    /// property name — i.e. it collides with routing (<see cref="IdProperty"/>,
    /// <see cref="PkProperty"/>), the discriminator (<see cref="DiscriminatorProperty"/>),
    /// any envelope tag (<see cref="EnvelopeTagPrefix"/>...), or any
    /// shadow-encoded name (<see cref="ShadowPrefix"/>...). PutItem /
    /// UpdateItem must reject attributes that target these names so the
    /// read path can safely skip them. The only DDB attribute name a
    /// caller would naturally pick that lands here is <c>id</c>; for
    /// that case the encoder transparently shadow-encodes the attribute
    /// rather than rejecting the write.
    /// </summary>
    public static bool IsReservedTopLevelName(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        if (name == IdProperty || name == PkProperty || name == DiscriminatorProperty)
            return true;
        // Any name in the _a2a namespace (envelope tags, shadow names,
        // future reserved props) — keep the user out of it entirely so
        // we have freedom to extend the schema without breaking writes.
        return name.StartsWith(DiscriminatorProperty, StringComparison.Ordinal);
    }

    /// <summary>
    /// True if the attribute name can be written directly at the root
    /// without shadow-encoding. The only collision the encoder rewrites
    /// transparently is <c>id</c>; every other reserved name is a hard
    /// validation failure (those would be names the user actively chose
    /// to put under the <c>_a2a</c> namespace).
    /// </summary>
    public static bool IsShadowEncodableName(string name)
        => string.Equals(name, IdProperty, StringComparison.Ordinal);

    // ---------------- WRITE PATH (DDB → Cosmos) ----------------------

    /// <summary>
    /// Builds the Cosmos document JSON for a PutItem-style write,
    /// flattening every DDB attribute at the root using the inference
    /// rules above. The caller is responsible for having already
    /// validated key-attribute presence/type and reserved-name conflicts
    /// (via <see cref="IsReservedTopLevelName"/>).
    /// </summary>
    public static string BuildCosmosDocument(string id, string pk, JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Item must be a JSON object.", nameof(item));

        var bw = new ArrayBufferWriter<byte>(1024);
        using (var writer = new Utf8JsonWriter(bw, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteString(IdPropEncoded, id);
            writer.WriteString(PkPropEncoded, pk);
            writer.WriteString(DiscPropEncoded, DiscValueItemEncoded);

            foreach (var prop in item.EnumerateObject())
            {
                if (IsShadowEncodableName(prop.Name))
                {
                    // Shadow-encode the rare DDB attr that collides with
                    // Cosmos's required "id" field; decoder unmangles.
                    writer.WritePropertyName(ShadowIdPropEncoded);
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
        }
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
    {
        if (!ParsedAttributeValue.TryParse(ddbAttr, out var parsed))
            throw new ArgumentException(
                "AttributeValue must be a single-property typed value (e.g. {\"S\":\"x\"}).",
                nameof(ddbAttr));

        switch (parsed.TypeTag)
        {
            case AttributeValueTypes.String:
                writer.WriteStringValue(parsed.Value.GetString());
                break;

            case AttributeValueTypes.Number:
                EncodeNumber(writer, parsed.Value);
                break;

            case AttributeValueTypes.Bool:
                writer.WriteBooleanValue(parsed.Value.GetBoolean());
                break;

            case AttributeValueTypes.Null:
                writer.WriteNullValue();
                break;

            case AttributeValueTypes.Map:
                EncodeMap(writer, parsed.Value);
                break;

            case AttributeValueTypes.List:
                EncodeList(writer, parsed.Value);
                break;

            case AttributeValueTypes.Binary:
                writer.WriteStartObject();
                writer.WriteString(EnvelopeBEncoded, parsed.Value.GetString());
                writer.WriteEndObject();
                break;

            case AttributeValueTypes.StringSet:
                WriteSetEnvelope(writer, EnvelopeSSEncoded, parsed.Value);
                break;

            case AttributeValueTypes.NumberSet:
                WriteSetEnvelope(writer, EnvelopeNSEncoded, parsed.Value);
                break;

            case AttributeValueTypes.BinarySet:
                WriteSetEnvelope(writer, EnvelopeBSEncoded, parsed.Value);
                break;

            default:
                throw new ArgumentException(
                    $"Unknown DDB type tag '{parsed.TypeTag}'.", nameof(ddbAttr));
        }
    }

    private static void EncodeMap(Utf8JsonWriter writer, JsonElement mapEl)
    {
        if (mapEl.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("M must be a JSON object.", nameof(mapEl));

        writer.WriteStartObject();
        foreach (var prop in mapEl.EnumerateObject())
        {
            // Nested attributes do NOT collide with top-level routing
            // names, but they could collide with the envelope tag prefix
            // (e.g. "_a2a:N") and confuse the decoder's single-prop
            // detection. Reject defensively — caller should also surface
            // this at validation time so the error is actionable.
            if (prop.Name.StartsWith(EnvelopeTagPrefix, StringComparison.Ordinal))
                throw new ArgumentException(
                    $"Nested attribute name '{prop.Name}' uses the reserved '_a2a:' prefix.",
                    nameof(mapEl));

            writer.WritePropertyName(prop.Name);
            EncodeAttributeValue(writer, prop.Value);
        }
        writer.WriteEndObject();
    }

    private static void EncodeList(Utf8JsonWriter writer, JsonElement listEl)
    {
        if (listEl.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("L must be a JSON array.", nameof(listEl));

        writer.WriteStartArray();
        foreach (var item in listEl.EnumerateArray())
        {
            EncodeAttributeValue(writer, item);
        }
        writer.WriteEndArray();
    }

    private static void WriteSetEnvelope(Utf8JsonWriter writer, JsonEncodedText tag, JsonElement setEl)
    {
        if (setEl.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("Set value must be a JSON array.", nameof(setEl));

        writer.WriteStartObject();
        writer.WritePropertyName(tag);
        writer.WriteStartArray();
        foreach (var member in setEl.EnumerateArray())
        {
            if (member.ValueKind != JsonValueKind.String)
                throw new ArgumentException(
                    "Set members must be JSON strings per DynamoDB wire format.",
                    nameof(setEl));
            writer.WriteStringValue(member.GetString());
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    // -- Number encoding -----------------------------------------------

    // Bounds:
    // - DDB: up to 38 significant digits, magnitude in [1e-130, 9.99...e+125].
    // - Cosmos SQL: bare JSON numbers go through int64 or IEEE 754 double,
    //   so reliably preserves only what round-trips through a double.
    // We write bare when the normalised form is exactly representable in
    // IEEE 754 double AND parses+formats back to itself (so Cosmos won't
    // renormalise it to scientific notation on read). Everything else
    // — large magnitudes, high precision — goes through the
    // `{"_a2a:N":"<normalised>"}` envelope (Cosmos preserves strings
    // byte-identical). Values outside DDB's 38-digit / 1e±125 range raise
    // ValidationException — match real DDB.
    internal const int MaxDdbNumberSignificantDigits = 38;
    internal const int MaxDdbNumberDecimalExponent = 125;
    internal const int MinDdbNumberDecimalExponent = -130;

    /// <summary>
    /// Encodes a DDB Number attribute. Normalises the input to canonical
    /// DDB decimal form (no leading zeros, no trailing zeros, no
    /// exponent, no <c>-0</c>) — matching real DynamoDB's documented
    /// normalisation, so callers get back the same digits real DDB would
    /// return. Writes bare iff ≤15 significant digits (Cosmos
    /// double-precision safe range); otherwise wraps in the
    /// <c>{"_a2a:N":"&lt;normalised&gt;"}</c> envelope so 16-38 digit
    /// values survive the Cosmos round-trip as a string. Throws
    /// <see cref="ArgumentException"/> on inputs outside DDB's range.
    /// </summary>
    private static void EncodeNumber(Utf8JsonWriter writer, JsonElement numberAttrValue)
    {
        if (numberAttrValue.ValueKind != JsonValueKind.String)
            throw new ArgumentException(
                "Number AttributeValue payload must be a JSON string per DDB wire format.",
                nameof(numberAttrValue));

        var raw = numberAttrValue.GetString() ?? string.Empty;
        if (!TryNormalizeDdbNumber(raw, out var normalised, out var significantDigits, out var error))
            throw new ArgumentException(error, nameof(numberAttrValue));

        if (CanRoundTripAsBareJsonNumber(normalised))
        {
            // Bare: normalised form survives Cosmos's JSON-number
            // double conversion byte-identical.
            writer.WriteRawValue(normalised, skipInputValidation: false);
            return;
        }

        // Envelope: high precision or magnitude — Cosmos would silently
        // truncate / renormalise via double on bare storage. String
        // preserves digits.
        writer.WriteStartObject();
        writer.WriteString(EnvelopeNEncoded, normalised);
        writer.WriteEndObject();
    }

    /// <summary>
    /// Returns true iff <paramref name="canonical"/> parses to an IEEE 754
    /// double whose canonical re-print (via <c>R</c>) matches the input
    /// byte-for-byte. This is the strongest portable guarantee that
    /// Cosmos, after reading the value back into a JSON number, will emit
    /// the same lexical form (no scientific renormalisation, no
    /// precision loss). Inputs whose magnitude or precision exceed
    /// double's safe range fail this check and route to the envelope.
    /// </summary>
    internal static bool CanRoundTripAsBareJsonNumber(string canonical)
    {
        if (!double.TryParse(canonical, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var value))
        {
            return false;
        }
        if (double.IsNaN(value) || double.IsInfinity(value)) return false;
        var roundTrip = value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        return string.Equals(roundTrip, canonical, StringComparison.Ordinal);
    }

    /// <summary>
    /// Normalises a raw DDB Number string to its canonical decimal form
    /// (matching DynamoDB's documented behaviour: strip leading zeros,
    /// strip trailing zeros from the fraction, expand exponent notation
    /// to plain decimal, collapse <c>-0</c> to <c>0</c>) AND validates
    /// it against DDB's published bounds (≤38 significant digits,
    /// magnitude in <c>[1e-130, 9.99...e+125]</c>). Returns false with
    /// <paramref name="error"/> populated on any malformed input or
    /// out-of-range value. The normalised string is what
    /// <c>GetItem</c> echoes back to the client.
    /// </summary>
    /// <summary>
    /// Structural decomposition of a validated DDB Number, shared by the
    /// canonical emitter (<see cref="TryNormalizeDdbNumber"/>) and the
    /// order-preserving key encoder. <see cref="Digits"/> holds the
    /// significant digits MSD-first with no leading/trailing zeros (empty
    /// when <see cref="IsZero"/>). <see cref="MsdExponent"/> is the decimal
    /// exponent of the most-significant digit; for valid inputs it lies in
    /// <c>[MinDdbNumberDecimalExponent + sig - 1, MaxDdbNumberDecimalExponent]</c>
    /// ⊆ <c>[-130, 125]</c>.
    /// </summary>
    internal readonly struct DdbNumberParts
    {
        public bool IsZero { get; }
        public bool Negative { get; }
        public int MsdExponent { get; }
        public int SignificantDigits { get; }
        public string Digits { get; }

        public DdbNumberParts(bool isZero, bool negative, int msdExponent, int significantDigits, string digits)
        {
            IsZero = isZero;
            Negative = negative;
            MsdExponent = msdExponent;
            SignificantDigits = significantDigits;
            Digits = digits;
        }
    }

    /// <summary>
    /// Normalises a raw DDB Number string to its canonical decimal form —
    /// see <see cref="TryParseDdbNumber"/> for the parse + validation
    /// rules. The normalised string is what <c>GetItem</c> echoes back to
    /// the client.
    /// </summary>
    internal static bool TryNormalizeDdbNumber(
        string raw, out string normalised, out int significantDigits, out string error)
    {
        normalised = string.Empty;
        significantDigits = 0;
        if (!TryParseDdbNumber(raw, out var parts, out error))
            return false;

        significantDigits = parts.SignificantDigits;
        if (parts.IsZero)
        {
            normalised = "0";
            return true;
        }
        normalised = EmitCanonical(parts);
        return true;
    }

    /// <summary>
    /// Emits the canonical decimal layout from a parsed, non-zero number.
    ///   msdExp &gt;= 0 → [-]&lt;intPart&gt;[.&lt;fracPart&gt;]  (e.g. 1500, 1.5, 1.005)
    ///   msdExp &lt;  0 → [-]0.&lt;leadingZeros&gt;&lt;sigDigits&gt;  (e.g. 0.001, 0.5)
    /// </summary>
    private static string EmitCanonical(in DdbNumberParts parts)
    {
        int significantDigits = parts.SignificantDigits;
        int msdExponent = parts.MsdExponent;
        int lsdExponent = msdExponent - (significantDigits - 1);
        string d = parts.Digits;

        var sb = new System.Text.StringBuilder();
        if (parts.Negative) sb.Append('-');

        if (msdExponent >= 0)
        {
            int intLen = msdExponent + 1;
            for (int k = 0; k < intLen; k++)
                sb.Append(k < significantDigits ? d[k] : '0');
            if (lsdExponent < 0)
            {
                sb.Append('.');
                int fracLen = -lsdExponent;
                for (int k = 0; k < fracLen; k++)
                {
                    // intLen + (fracLen-1) == significantDigits-1, so the
                    // index is always within the significant-digit string.
                    sb.Append(d[intLen + k]);
                }
            }
        }
        else
        {
            sb.Append('0');
            sb.Append('.');
            int leadingZeros = -msdExponent - 1;
            for (int k = 0; k < leadingZeros; k++) sb.Append('0');
            for (int k = 0; k < significantDigits; k++) sb.Append(d[k]);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Parses and validates a raw DDB Number string (strip leading zeros,
    /// strip trailing zeros from the fraction, expand exponent notation,
    /// collapse <c>-0</c> to <c>0</c>) against DDB's published bounds
    /// (≤38 significant digits, magnitude in <c>[1e-130, 9.99...e+125]</c>),
    /// returning its structural decomposition. Returns false with
    /// <paramref name="error"/> populated on any malformed or out-of-range
    /// input.
    /// </summary>
    internal static bool TryParseDdbNumber(string raw, out DdbNumberParts parts, out string error)
    {
        parts = default;
        error = string.Empty;

        if (string.IsNullOrEmpty(raw))
        {
            error = "Number AttributeValue must not be empty.";
            return false;
        }

        var span = raw.AsSpan();
        int i = 0;
        bool negative = false;

        if (span[i] == '+' || span[i] == '-')
        {
            negative = span[i] == '-';
            i++;
        }

        int intStart = i;
        while (i < span.Length && span[i] >= '0' && span[i] <= '9') i++;
        int intEnd = i;

        int fracStart = -1, fracEnd = -1;
        if (i < span.Length && span[i] == '.')
        {
            i++;
            fracStart = i;
            while (i < span.Length && span[i] >= '0' && span[i] <= '9') i++;
            fracEnd = i;
        }

        int expValue = 0;
        if (i < span.Length && (span[i] == 'e' || span[i] == 'E'))
        {
            i++;
            bool expNeg = false;
            if (i < span.Length && (span[i] == '+' || span[i] == '-'))
            {
                expNeg = span[i] == '-';
                i++;
            }
            int expDigits = 0;
            while (i < span.Length && span[i] >= '0' && span[i] <= '9')
            {
                if (expDigits < 5)               // bound below int overflow
                    expValue = expValue * 10 + (span[i] - '0');
                else
                    expValue = int.MaxValue / 2; // saturate; out-of-range below
                expDigits++;
                i++;
            }
            if (expDigits == 0)
            {
                error = "Number has malformed exponent.";
                return false;
            }
            if (expNeg) expValue = -expValue;
        }

        if (i != span.Length)
        {
            error = "Number contains unexpected characters.";
            return false;
        }

        int intDigits = intEnd - intStart;
        int fracDigits = fracEnd >= 0 ? fracEnd - fracStart : 0;
        if (intDigits == 0)
        {
            // Real DDB requires at least one digit before the decimal
            // point: ".5" is rejected as malformed.
            error = "Number must have at least one digit before the decimal point.";
            return false;
        }
        if (fracEnd >= 0 && fracDigits == 0)
        {
            // "1." with no fraction digits is also rejected.
            error = "Number has a decimal point with no following digits.";
            return false;
        }

        // Locate the first non-zero digit across (int, fraction).
        int firstNonZero = -1;
        for (int k = 0; k < intDigits; k++)
        {
            if (span[intStart + k] != '0') { firstNonZero = k; break; }
        }
        if (firstNonZero == -1)
        {
            for (int k = 0; k < fracDigits; k++)
            {
                if (span[fracStart + k] != '0')
                {
                    firstNonZero = intDigits + k;
                    break;
                }
            }
        }
        if (firstNonZero == -1)
        {
            parts = new DdbNumberParts(isZero: true, negative: false, msdExponent: 0, significantDigits: 1, digits: string.Empty);
            return true;
        }

        // Locate last non-zero (across concatenated int + fraction).
        int totalDigits = intDigits + fracDigits;
        int lastNonZero = totalDigits - 1;
        for (; lastNonZero >= 0; lastNonZero--)
        {
            char d = lastNonZero < intDigits
                ? span[intStart + lastNonZero]
                : span[fracStart + (lastNonZero - intDigits)];
            if (d != '0') break;
        }

        int significantDigits = lastNonZero - firstNonZero + 1;
        if (significantDigits > MaxDdbNumberSignificantDigits)
        {
            error = $"Number exceeds DynamoDB's {MaxDdbNumberSignificantDigits}-digit precision limit.";
            return false;
        }

        // Decimal exponent of the most-significant digit, with the
        // explicit exponent folded in.
        int msdExponent = (intDigits - 1 - firstNonZero) + expValue;
        int lsdExponent = msdExponent - (significantDigits - 1);

        if (msdExponent > MaxDdbNumberDecimalExponent)
        {
            error = $"Number magnitude exceeds DynamoDB's 1e+{MaxDdbNumberDecimalExponent} upper bound.";
            return false;
        }
        if (lsdExponent < MinDdbNumberDecimalExponent)
        {
            error = $"Number magnitude is below DynamoDB's 1e{MinDdbNumberDecimalExponent} lower bound.";
            return false;
        }

        // Materialize the significant digits MSD-first (no leading/trailing
        // zeros) so callers don't need the raw input span.
        char[] digitChars = new char[significantDigits];
        for (int k = 0; k < significantDigits; k++)
            digitChars[k] = GetSignificantDigit(span, intStart, fracStart, intDigits, firstNonZero, k);

        parts = new DdbNumberParts(
            isZero: false,
            negative: negative,
            msdExponent: msdExponent,
            significantDigits: significantDigits,
            digits: new string(digitChars));
        return true;
    }

    /// <summary>
    /// Returns the k-th significant digit (0 = MSD) from the original
    /// raw input span, given the int/fraction segment offsets resolved
    /// by <see cref="TryNormalizeDdbNumber"/>.
    /// </summary>
    private static char GetSignificantDigit(
        ReadOnlySpan<char> span, int intStart, int fracStart,
        int intDigits, int firstNonZero, int k)
    {
        int srcIdx = firstNonZero + k;
        return srcIdx < intDigits
            ? span[intStart + srcIdx]
            : span[fracStart + (srcIdx - intDigits)];
    }

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
                else if (IsReservedTopLevelName(prop.Name))
                {
                    // Routing fields, discriminator, other reserved
                    // _a2a-namespace props — never user data.
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
                writer.WriteString(TagS, value.GetString());
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
                    writer.WriteStringValue(only.Value.GetString());
                    writer.WriteEndObject();
                    return;

                case EnvelopeTagB:
                    writer.WriteStartObject();
                    writer.WriteString(TagB, only.Value.GetString());
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
            writer.WriteStringValue(member.GetString());
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
