using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using Aws2Azure.Modules.DynamoDb.Operations;

namespace Aws2Azure.Modules.DynamoDb.Expressions;

/// <summary>
/// Evaluates a <see cref="ConditionNode"/> against a DynamoDB item map
/// (<c>Dictionary&lt;string, JsonElement&gt;</c> where each value is a
/// single-tag attribute value like <c>{"S":"x"}</c>).
///
/// <para>The item may be <c>null</c> to denote "this item does not
/// exist" — in that case every path lookup is treated as a missing
/// attribute, so <c>attribute_not_exists</c> on any path is true and
/// every comparison is false.</para>
///
/// <para>Throws <see cref="ConditionEvaluationException"/> on type-
/// incompatible comparisons (mirrors DDB's "Invalid operand type" /
/// "Incorrect operand type for operator" ValidationException).</para>
/// </summary>
internal static class ConditionEvaluator
{
    public static bool Evaluate(ConditionNode node, IReadOnlyDictionary<string, JsonElement>? item)
        => node switch
        {
            AndCondition and => Evaluate(and.Left, item) && Evaluate(and.Right, item),
            OrCondition or => Evaluate(or.Left, item) || Evaluate(or.Right, item),
            NotCondition not => !Evaluate(not.Inner, item),
            AttributeExistsCondition ae => TryResolvePath(ae.Path, item, out _),
            AttributeNotExistsCondition ane => !TryResolvePath(ane.Path, item, out _),
            AttributeTypeCondition at => EvaluateAttributeType(at, item),
            BeginsWithCondition bw => EvaluateBeginsWith(bw, item),
            ContainsCondition c => EvaluateContains(c, item),
            CompareCondition cc => EvaluateCompare(cc, item),
            BetweenCondition bt => EvaluateBetween(bt, item),
            InCondition inn => EvaluateIn(inn, item),
            _ => throw new ConditionEvaluationException($"Unsupported condition node: {node.GetType().Name}"),
        };

    // ---------------- functions -------------------------------------

    private static bool EvaluateAttributeType(
        AttributeTypeCondition at, IReadOnlyDictionary<string, JsonElement>? item)
    {
        if (!TryResolvePath(at.Path, item, out var element)) return false;
        if (!ParsedAttributeValue.TryParse(at.TypeTag.Value, out var tagParsed)
            || tagParsed.TypeTag != AttributeValueTypes.String)
            throw new ConditionEvaluationException(
                "attribute_type expects a string (S) type tag as its second argument.");
        var requested = tagParsed.Value.GetString()!;
        if (!ParsedAttributeValue.TryParse(element, out var actual)) return false;
        return string.Equals(actual.TypeTag, requested, StringComparison.Ordinal);
    }

    private static bool EvaluateBeginsWith(
        BeginsWithCondition bw, IReadOnlyDictionary<string, JsonElement>? item)
    {
        if (!TryEvaluateOperand(bw.Path, item, out var lhs)) return false;
        if (!TryEvaluateOperand(bw.Prefix, item, out var rhs)) return false;
        if (!ParsedAttributeValue.TryParse(lhs, out var lp) || !ParsedAttributeValue.TryParse(rhs, out var rp))
            throw new ConditionEvaluationException("begins_with requires typed attribute values.");
        if (lp.TypeTag != rp.TypeTag)
            throw new ConditionEvaluationException("begins_with operands must share the same type tag.");
        return lp.TypeTag switch
        {
            AttributeValueTypes.String =>
                lp.Value.GetString()!.StartsWith(rp.Value.GetString()!, StringComparison.Ordinal),
            AttributeValueTypes.Binary =>
                StartsWithBinary(lp.Value.GetString()!, rp.Value.GetString()!),
            _ => throw new ConditionEvaluationException("begins_with only supports S or B operands."),
        };
    }

    private static bool EvaluateContains(
        ContainsCondition c, IReadOnlyDictionary<string, JsonElement>? item)
    {
        if (!TryEvaluateOperand(c.Container, item, out var container)) return false;
        if (!TryEvaluateOperand(c.Item, item, out var needle)) return false;
        if (!ParsedAttributeValue.TryParse(container, out var cp)) return false;
        if (!ParsedAttributeValue.TryParse(needle, out var np)) return false;

        switch (cp.TypeTag)
        {
            case AttributeValueTypes.String:
                if (np.TypeTag != AttributeValueTypes.String)
                    throw new ConditionEvaluationException("contains on S requires an S argument.");
                return cp.Value.GetString()!.Contains(np.Value.GetString()!, StringComparison.Ordinal);
            case AttributeValueTypes.StringSet:
                if (np.TypeTag != AttributeValueTypes.String)
                    throw new ConditionEvaluationException("contains on SS requires an S argument.");
                return SetMembership(cp.Value, np.Value.GetString()!);
            case AttributeValueTypes.NumberSet:
                if (np.TypeTag != AttributeValueTypes.Number)
                    throw new ConditionEvaluationException("contains on NS requires an N argument.");
                return NumericSetMembership(cp.Value, np.Value.GetString()!);
            case AttributeValueTypes.BinarySet:
                if (np.TypeTag != AttributeValueTypes.Binary)
                    throw new ConditionEvaluationException("contains on BS requires a B argument.");
                return SetMembership(cp.Value, np.Value.GetString()!);
            case AttributeValueTypes.List:
                foreach (var elem in cp.Value.EnumerateArray())
                {
                    if (JsonEquals(elem, needle)) return true;
                }
                return false;
            default:
                throw new ConditionEvaluationException(
                    $"contains requires a String, Set, or List container; got {cp.TypeTag}.");
        }
    }

    private static bool SetMembership(JsonElement setElement, string needle)
    {
        foreach (var e in setElement.EnumerateArray())
        {
            if (string.Equals(e.GetString(), needle, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    private static bool NumericSetMembership(JsonElement setElement, string needle)
    {
        foreach (var e in setElement.EnumerateArray())
        {
            if (CompareNumeric(e.GetString()!, needle) == 0) return true;
        }
        return false;
    }

    // ---------------- comparisons -----------------------------------

    private static bool EvaluateCompare(
        CompareCondition cc, IReadOnlyDictionary<string, JsonElement>? item)
    {
        var hasL = TryEvaluateOperand(cc.Left, item, out var l);
        var hasR = TryEvaluateOperand(cc.Right, item, out var r);
        // DDB: comparisons against a missing attribute are always false
        // (including <>) — there is no "null equals null" sleight of hand.
        if (!hasL || !hasR) return false;
        return Compare(cc.Op, l, r);
    }

    private static bool EvaluateBetween(
        BetweenCondition bt, IReadOnlyDictionary<string, JsonElement>? item)
    {
        if (!TryEvaluateOperand(bt.Value, item, out var v)) return false;
        if (!TryEvaluateOperand(bt.Lower, item, out var lo)) return false;
        if (!TryEvaluateOperand(bt.Upper, item, out var hi)) return false;
        return Compare(CompareOp.GreaterEqual, v, lo) && Compare(CompareOp.LessEqual, v, hi);
    }

    private static bool EvaluateIn(
        InCondition inn, IReadOnlyDictionary<string, JsonElement>? item)
    {
        if (!TryEvaluateOperand(inn.Value, item, out var v)) return false;
        foreach (var op in inn.Set)
        {
            if (!TryEvaluateOperand(op, item, out var candidate)) continue;
            if (Compare(CompareOp.Equal, v, candidate)) return true;
        }
        return false;
    }

    private static bool Compare(CompareOp op, JsonElement left, JsonElement right)
    {
        if (!ParsedAttributeValue.TryParse(left, out var lp) || !ParsedAttributeValue.TryParse(right, out var rp))
            throw new ConditionEvaluationException("Comparison operands must be typed attribute values.");

        if (op is CompareOp.Equal or CompareOp.NotEqual)
        {
            bool eq;
            if (lp.TypeTag != rp.TypeTag)
            {
                // Different type tags compare false (not error).
                eq = false;
            }
            else if (lp.TypeTag == AttributeValueTypes.Number)
            {
                // Numbers compare by numeric value, not by raw textual form
                // (e.g. "1" == "1.0" == "1.00" must hold).
                eq = CompareNumeric(lp.Value.GetString()!, rp.Value.GetString()!) == 0;
            }
            else if (lp.TypeTag == AttributeValueTypes.Binary)
            {
                eq = CompareBinary(lp.Value.GetString()!, rp.Value.GetString()!) == 0;
            }
            else
            {
                // Equality is allowed across any other matching type tag
                // including S, BOOL, NULL, M, L, and sets.
                eq = JsonEquals(lp.Value, rp.Value);
            }
            return op == CompareOp.Equal ? eq : !eq;
        }

        // Ordered comparisons require same scalar type and only S / N / B.
        if (lp.TypeTag != rp.TypeTag)
            throw new ConditionEvaluationException(
                $"Cannot compare {lp.TypeTag} with {rp.TypeTag} using an ordered operator.");
        int cmp = lp.TypeTag switch
        {
            AttributeValueTypes.Number => CompareNumeric(lp.Value.GetString()!, rp.Value.GetString()!),
            AttributeValueTypes.String => string.CompareOrdinal(lp.Value.GetString(), rp.Value.GetString()),
            AttributeValueTypes.Binary => CompareBinary(lp.Value.GetString()!, rp.Value.GetString()!),
            _ => throw new ConditionEvaluationException(
                $"Ordered comparison is not defined for type {lp.TypeTag}."),
        };
        return op switch
        {
            CompareOp.Less => cmp < 0,
            CompareOp.LessEqual => cmp <= 0,
            CompareOp.Greater => cmp > 0,
            CompareOp.GreaterEqual => cmp >= 0,
            _ => throw new ConditionEvaluationException("Unreachable comparison op."),
        };
    }

    // ---------------- operand resolution ----------------------------

    /// <summary>
    /// Resolves an operand to a typed JSON element. Returns false when
    /// the operand is a path that does not exist on the item; throws if
    /// <c>size()</c> is applied to an incompatible attribute.
    /// </summary>
    private static bool TryEvaluateOperand(
        ConditionOperand op, IReadOnlyDictionary<string, JsonElement>? item, out JsonElement value)
    {
        switch (op)
        {
            case ConditionValueOperand v:
                value = v.Value.Value;
                return true;
            case ConditionPathOperand p:
                return TryResolvePath(p.Path, item, out value);
            case SizeOperand s:
            {
                if (!TryResolvePath(s.Path, item, out var resolved))
                {
                    value = default;
                    return false;
                }
                if (!ParsedAttributeValue.TryParse(resolved, out var rp))
                    throw new ConditionEvaluationException("size() argument is not a typed attribute value.");
                long size = rp.TypeTag switch
                {
                    AttributeValueTypes.String => rp.Value.GetString()!.Length,
                    AttributeValueTypes.Binary => Base64Length(rp.Value.GetString()!),
                    AttributeValueTypes.List => rp.Value.GetArrayLength(),
                    AttributeValueTypes.Map => MapPropertyCount(rp.Value),
                    AttributeValueTypes.StringSet
                        or AttributeValueTypes.NumberSet
                        or AttributeValueTypes.BinarySet => rp.Value.GetArrayLength(),
                    _ => throw new ConditionEvaluationException(
                        $"size() is not defined for type {rp.TypeTag}."),
                };
                value = BuildNumber(size.ToString(CultureInfo.InvariantCulture));
                return true;
            }
            default:
                throw new ConditionEvaluationException("Unknown operand kind.");
        }
    }

    private static bool TryResolvePath(
        DocumentPath path, IReadOnlyDictionary<string, JsonElement>? item, out JsonElement value)
    {
        value = default;
        if (item is null) return false;
        if (!item.TryGetValue(path.Root, out var current)) return false;
        for (int i = 1; i < path.Segments.Count; i++)
        {
            // Unwrap the typed container (M or L) and descend.
            if (!ParsedAttributeValue.TryParse(current, out var parsed)) return false;
            switch (path.Segments[i])
            {
                case AttributePathSegment a:
                    if (parsed.TypeTag != AttributeValueTypes.Map) return false;
                    if (!parsed.Value.TryGetProperty(a.Name, out var child)) return false;
                    current = child;
                    break;
                case IndexPathSegment idx:
                    if (parsed.TypeTag != AttributeValueTypes.List) return false;
                    if (idx.Index >= parsed.Value.GetArrayLength()) return false;
                    current = parsed.Value[idx.Index];
                    break;
                default:
                    return false;
            }
        }
        value = current;
        return true;
    }

    // ---------------- helpers ---------------------------------------

    private static int CompareNumeric(string a, string b)
    {
        // Lossless decimal compare on canonicalised string representations.
        // DynamoDB stores numbers as decimal strings with up to 38 significant
        // digits; System.Decimal silently rounds at 28-29 digits and would
        // make distinct large/precise operands compare equal. We therefore
        // compare digit-by-digit after normalising the two operands.
        if (!TryNormaliseDecimal(a, out var aNorm))
            throw new ConditionEvaluationException(
                $"Numeric comparison operand '{a}' is not a valid number.");
        if (!TryNormaliseDecimal(b, out var bNorm))
            throw new ConditionEvaluationException(
                $"Numeric comparison operand '{b}' is not a valid number.");

        if (aNorm.IsZero && bNorm.IsZero) return 0;
        if (aNorm.IsNegative != bNorm.IsNegative)
            return aNorm.IsNegative ? -1 : 1;

        int magCmp = CompareMagnitude(aNorm, bNorm);
        return aNorm.IsNegative ? -magCmp : magCmp;
    }

    private readonly struct NormalisedDecimal
    {
        public NormalisedDecimal(bool isNegative, string intDigits, string fracDigits, bool isZero)
        {
            IsNegative = isNegative;
            IntDigits = intDigits;
            FracDigits = fracDigits;
            IsZero = isZero;
        }
        public bool IsNegative { get; }
        public string IntDigits { get; }   // leading zeros stripped
        public string FracDigits { get; }  // trailing zeros stripped
        public bool IsZero { get; }
    }

    private static bool TryNormaliseDecimal(string s, out NormalisedDecimal result)
    {
        result = default;
        if (string.IsNullOrEmpty(s)) return false;

        int i = 0;
        bool neg = false;
        if (s[i] == '+' || s[i] == '-')
        {
            neg = s[i] == '-';
            i++;
        }
        if (i >= s.Length) return false;

        int intStart = i;
        while (i < s.Length && s[i] >= '0' && s[i] <= '9') i++;
        int intEnd = i;

        int fracStart = i, fracEnd = i;
        if (i < s.Length && s[i] == '.')
        {
            i++;
            fracStart = i;
            while (i < s.Length && s[i] >= '0' && s[i] <= '9') i++;
            fracEnd = i;
        }

        int expSign = 1, expVal = 0;
        if (i < s.Length && (s[i] == 'e' || s[i] == 'E'))
        {
            i++;
            if (i >= s.Length) return false;
            if (s[i] == '+' || s[i] == '-')
            {
                if (s[i] == '-') expSign = -1;
                i++;
            }
            int expDigitStart = i;
            while (i < s.Length && s[i] >= '0' && s[i] <= '9') i++;
            if (i == expDigitStart) return false;
            // Bound the exponent magnitude to avoid pathological growth.
            if (i - expDigitStart > 6) return false;
            expVal = int.Parse(s.AsSpan(expDigitStart, i - expDigitStart), CultureInfo.InvariantCulture);
        }
        if (i != s.Length) return false;
        if (intStart == intEnd && fracStart == fracEnd) return false;

        var intDigits = s.Substring(intStart, intEnd - intStart);
        var fracDigits = s.Substring(fracStart, fracEnd - fracStart);

        // Apply exponent by shifting the decimal point.
        int shift = expSign * expVal;
        if (shift > 0)
        {
            // Pull digits from fraction into integer.
            int take = Math.Min(shift, fracDigits.Length);
            intDigits += fracDigits.Substring(0, take);
            fracDigits = fracDigits.Substring(take);
            if (shift > take) intDigits += new string('0', shift - take);
        }
        else if (shift < 0)
        {
            int neg2 = -shift;
            int take = Math.Min(neg2, intDigits.Length);
            fracDigits = intDigits.Substring(intDigits.Length - take) + fracDigits;
            intDigits = intDigits.Substring(0, intDigits.Length - take);
            if (neg2 > take) fracDigits = new string('0', neg2 - take) + fracDigits;
        }

        // Strip leading zeros from integer portion.
        int li = 0;
        while (li < intDigits.Length - 1 && intDigits[li] == '0') li++;
        if (intDigits.Length > 0) intDigits = intDigits.Substring(li);
        // Strip trailing zeros from fractional portion.
        int fe = fracDigits.Length;
        while (fe > 0 && fracDigits[fe - 1] == '0') fe--;
        fracDigits = fracDigits.Substring(0, fe);

        bool isZero = (intDigits.Length == 0 || intDigits == "0") && fracDigits.Length == 0;
        if (isZero) neg = false;

        result = new NormalisedDecimal(neg, intDigits, fracDigits, isZero);
        return true;
    }

    private static int CompareMagnitude(NormalisedDecimal a, NormalisedDecimal b)
    {
        // Compare integer-part lengths (after stripping leading zeros) first.
        var aInt = a.IntDigits.Length == 0 ? "0" : a.IntDigits;
        var bInt = b.IntDigits.Length == 0 ? "0" : b.IntDigits;
        // Treat a bare "0" as zero-length for comparison purposes.
        int aIntLen = aInt == "0" ? 0 : aInt.Length;
        int bIntLen = bInt == "0" ? 0 : bInt.Length;
        if (aIntLen != bIntLen) return aIntLen < bIntLen ? -1 : 1;
        if (aIntLen > 0)
        {
            int cmp = string.CompareOrdinal(aInt, bInt);
            if (cmp != 0) return cmp < 0 ? -1 : 1;
        }
        // Integer parts equal — compare fractional digits left-to-right.
        int min = Math.Min(a.FracDigits.Length, b.FracDigits.Length);
        for (int i = 0; i < min; i++)
        {
            if (a.FracDigits[i] != b.FracDigits[i])
                return a.FracDigits[i] < b.FracDigits[i] ? -1 : 1;
        }
        if (a.FracDigits.Length == b.FracDigits.Length) return 0;
        return a.FracDigits.Length < b.FracDigits.Length ? -1 : 1;
    }

    private static int CompareBinary(string a, string b)
    {
        // DynamoDB compares B values as unsigned byte sequences.
        var ab = Convert.FromBase64String(a);
        var bb = Convert.FromBase64String(b);
        int min = Math.Min(ab.Length, bb.Length);
        for (int i = 0; i < min; i++)
        {
            int diff = ab[i] - bb[i];
            if (diff != 0) return diff < 0 ? -1 : 1;
        }
        return ab.Length.CompareTo(bb.Length);
    }

    private static bool StartsWithBinary(string container, string prefix)
    {
        var cb = Convert.FromBase64String(container);
        var pb = Convert.FromBase64String(prefix);
        if (pb.Length > cb.Length) return false;
        for (int i = 0; i < pb.Length; i++)
        {
            if (cb[i] != pb[i]) return false;
        }
        return true;
    }

    private static int Base64Length(string b64)
    {
        // Decoded length without allocating: (n * 3 / 4) minus padding.
        int n = b64.Length;
        int pad = 0;
        if (n >= 1 && b64[n - 1] == '=') pad++;
        if (n >= 2 && b64[n - 2] == '=') pad++;
        return n * 3 / 4 - pad;
    }

    private static int MapPropertyCount(JsonElement map)
    {
        int n = 0;
        foreach (var _ in map.EnumerateObject()) n++;
        return n;
    }

    private static bool JsonEquals(JsonElement a, JsonElement b)
    {
        if (a.ValueKind != b.ValueKind) return false;
        switch (a.ValueKind)
        {
            case JsonValueKind.String:
                return string.Equals(a.GetString(), b.GetString(), StringComparison.Ordinal);
            case JsonValueKind.Number:
                return string.Equals(a.GetRawText(), b.GetRawText(), StringComparison.Ordinal);
            case JsonValueKind.True:
            case JsonValueKind.False:
            case JsonValueKind.Null:
                return true;
            case JsonValueKind.Array:
            {
                if (a.GetArrayLength() != b.GetArrayLength()) return false;
                int i = 0;
                foreach (var ae in a.EnumerateArray())
                {
                    if (!JsonEquals(ae, b[i])) return false;
                    i++;
                }
                return true;
            }
            case JsonValueKind.Object:
            {
                int ac = 0, bc = 0;
                foreach (var _ in a.EnumerateObject()) ac++;
                foreach (var _ in b.EnumerateObject()) bc++;
                if (ac != bc) return false;
                foreach (var ap in a.EnumerateObject())
                {
                    if (!b.TryGetProperty(ap.Name, out var bv)) return false;
                    if (!JsonEquals(ap.Value, bv)) return false;
                }
                return true;
            }
            default:
                return false;
        }
    }

    private static JsonElement BuildNumber(string text)
    {
        using var ms = new System.IO.MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("N", text);
            w.WriteEndObject();
        }
        ms.Position = 0;
        using var doc = JsonDocument.Parse(ms);
        return doc.RootElement.Clone();
    }
}

/// <summary>Thrown by the condition evaluator on a type-incompatible
/// operand. Surfaced as DynamoDB <c>ValidationException</c>.</summary>
internal sealed class ConditionEvaluationException : Exception
{
    public ConditionEvaluationException(string message) : base(message) { }
}
