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
    private static void EncodeNumber<TWriter>(TWriter writer, JsonElement numberAttrValue)
        where TWriter : ITokenWriter
    {
        if (numberAttrValue.ValueKind != JsonValueKind.String)
            throw new ArgumentException(
                "Number AttributeValue payload must be a JSON string per DDB wire format.",
                nameof(numberAttrValue));

        EncodeNumberFromRaw(writer, numberAttrValue.GetString() ?? string.Empty);
    }

    /// <summary>
    /// Shared number-encoding core over the raw DDB number text — drives both
    /// the <see cref="JsonElement"/> walk (<see cref="EncodeNumber"/>) and the
    /// single-pass wire walk (#342). Numbers are a short ASCII minority, so the
    /// reader path materializes the raw text once (no per-attribute string tax
    /// in the common string-heavy case); the canonical <paramref name="raw"/>
    /// normalisation is identical for both.
    /// </summary>
    private static void EncodeNumberFromRaw<TWriter>(TWriter writer, string raw)
        where TWriter : ITokenWriter
    {
        if (!TryNormalizeDdbNumber(raw, out var normalised, out _, out var error))
            throw new ArgumentException(error, nameof(raw));

        if (CanRoundTripAsBareJsonNumber(normalised))
        {
            // Bare: normalised form survives Cosmos's JSON-number
            // double conversion byte-identical.
            writer.WriteNumberRaw(normalised);
            return;
        }

        // Envelope: high precision or magnitude — Cosmos would silently
        // truncate / renormalise via double on bare storage. String
        // preserves digits.
        writer.WriteStartObject();
        writer.WriteString(EnvelopeNName, normalised);
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
    /// <c>[MinDdbNumberDecimalExponent, MaxDdbNumberDecimalExponent]</c>
    /// == <c>[-130, 125]</c>.
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

        // DynamoDB's documented numeric range is a *magnitude* constraint on
        // the value (most-significant-digit exponent), combined with the
        // 38-significant-digit precision cap enforced above — NOT a constraint
        // on the least-significant-digit position. A value like 1.1e-130 has
        // magnitude >= 1e-130 and is accepted by real DynamoDB even though its
        // LSD sits at 1e-131; so the floor must test msdExponent, not lsdExponent.
        if (msdExponent > MaxDdbNumberDecimalExponent)
        {
            error = $"Number magnitude exceeds DynamoDB's 1e+{MaxDdbNumberDecimalExponent} upper bound.";
            return false;
        }
        if (msdExponent < MinDdbNumberDecimalExponent)
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

}
