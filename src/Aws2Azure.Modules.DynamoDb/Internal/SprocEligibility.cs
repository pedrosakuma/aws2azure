using System.Globalization;
using System.Text.Json;
using Aws2Azure.Modules.DynamoDb.Expressions;
using Aws2Azure.Modules.DynamoDb.Persistence;

namespace Aws2Azure.Modules.DynamoDb.Internal;

/// <summary>
/// Decides whether a conditional write can be executed faithfully by the
/// single-item <c>atomicWrite_v2</c> Cosmos stored procedure.
///
/// The server-side JS interprets a deliberately small slice of the DynamoDB
/// expression surface. Features outside that slice — sets / binary / very
/// high-precision numbers (all stored as <c>_a2a:</c> envelopes the JS does not
/// understand), list-index paths, <c>ADD</c>/<c>DELETE</c> clauses, and the
/// <c>size()</c> / <c>contains()</c> condition forms whose result depends on the
/// stored attribute's encoded type — would produce results that silently diverge
/// from the in-process <see cref="Operations.UpdateExecutor"/> fallback.
///
/// This gate is intentionally conservative: when in doubt it returns
/// <c>false</c>, routing the request to the proven GET → modify → PUT path
/// (stored-procedure mode <c>Preferred</c>) or failing loud (<c>Required</c>),
/// never running the sproc on an input it cannot faithfully execute (#202).
/// </summary>
internal static class SprocEligibility
{
    public static bool IsEligible(ConditionNode? condition, UpdateExpressionAst? update)
        => IsConditionEligible(condition) && IsUpdateEligible(update);

    /// <summary>
    /// Finds the first condition path whose ROOT attribute is a reserved Cosmos
    /// doc property (<c>id</c>, <c>ttl</c>, or any <c>_a2a</c> name) — i.e. a
    /// name the storage layer shadow-encodes or injects, so the raw Cosmos
    /// document the server-side sproc sees does not hold the user's value under
    /// that key. The single-write path routes such conditions to the in-process
    /// fallback (<see cref="IsPathEligible"/>); callers with no fallback (e.g.
    /// TransactWriteItems, whose sproc is the only execution path) must reject.
    /// Returns the offending root name, or null if every path root is safe.
    /// </summary>
    public static string? FindReservedConditionRoot(ConditionNode? node)
    {
        switch (node)
        {
            case null:
                return null;
            case AndCondition a:
                return FindReservedConditionRoot(a.Left) ?? FindReservedConditionRoot(a.Right);
            case OrCondition o:
                return FindReservedConditionRoot(o.Left) ?? FindReservedConditionRoot(o.Right);
            case NotCondition n:
                return FindReservedConditionRoot(n.Inner);
            case CompareCondition c:
                return ReservedOperandRoot(c.Left) ?? ReservedOperandRoot(c.Right);
            case BetweenCondition b:
                return ReservedOperandRoot(b.Value) ?? ReservedOperandRoot(b.Lower) ?? ReservedOperandRoot(b.Upper);
            case InCondition inc:
                if (ReservedOperandRoot(inc.Value) is { } vr) return vr;
                foreach (var op in inc.Set)
                {
                    if (ReservedOperandRoot(op) is { } sr) return sr;
                }
                return null;
            case AttributeExistsCondition ae:
                return ReservedPathRoot(ae.Path);
            case AttributeNotExistsCondition ane:
                return ReservedPathRoot(ane.Path);
            case AttributeTypeCondition at:
                return ReservedPathRoot(at.Path);
            case BeginsWithCondition bw:
                return ReservedOperandRoot(bw.Path) ?? ReservedOperandRoot(bw.Prefix);
            case ContainsCondition cc:
                return ReservedOperandRoot(cc.Container) ?? ReservedOperandRoot(cc.Item);
            default:
                return null;
        }
    }

    private static string? ReservedOperandRoot(ConditionOperand operand) => operand switch
    {
        ConditionPathOperand cp => ReservedPathRoot(cp.Path),
        SizeOperand so => ReservedPathRoot(so.Path),
        _ => null,
    };

    private static string? ReservedPathRoot(DocumentPath path)
        => path.Segments.Count > 0 && path.Segments[0] is AttributePathSegment attr
            && InferredAttributeStorage.IsReservedTopLevelName(attr.Name)
            ? attr.Name
            : null;

    private static bool IsUpdateEligible(UpdateExpressionAst? update)
    {
        if (update is null)
        {
            return true;
        }

        // ADD / DELETE carry set- and number-envelope semantics the JS does not
        // replicate. Atomic counters are still served by `SET c = c + :n`.
        if (update.Add is not null || update.Delete is not null)
        {
            return false;
        }

        if (update.Set is { } set)
        {
            foreach (var action in set.Actions)
            {
                if (!IsPathEligible(action.Path) || !IsOperandEligible(action.Value))
                {
                    return false;
                }
            }
        }

        if (update.Remove is { } remove)
        {
            foreach (var path in remove.Paths)
            {
                if (!IsPathEligible(path))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool IsOperandEligible(ValueOperand operand) => operand switch
    {
        ValueRefOperand vr => IsNativeValue(vr.Value),
        PathOperand po => IsPathEligible(po.Path),
        ArithmeticOperand ao => IsOperandEligible(ao.Left) && IsOperandEligible(ao.Right),
        IfNotExistsOperand ine => IsPathEligible(ine.Path) && IsOperandEligible(ine.Fallback),
        ListAppendOperand la => IsOperandEligible(la.Left) && IsOperandEligible(la.Right),
        _ => false,
    };

    private static bool IsConditionEligible(ConditionNode? node)
    {
        switch (node)
        {
            case null:
                return true;
            case AndCondition and:
                return IsConditionEligible(and.Left) && IsConditionEligible(and.Right);
            case OrCondition or:
                return IsConditionEligible(or.Left) && IsConditionEligible(or.Right);
            case NotCondition not:
                return IsConditionEligible(not.Inner);
            case CompareCondition cmp:
                return IsConditionOperandEligible(cmp.Left) && IsConditionOperandEligible(cmp.Right);
            case BetweenCondition bt:
                return IsConditionOperandEligible(bt.Value)
                    && IsConditionOperandEligible(bt.Lower)
                    && IsConditionOperandEligible(bt.Upper);
            case InCondition inn:
                if (!IsConditionOperandEligible(inn.Value))
                {
                    return false;
                }
                foreach (var v in inn.Set)
                {
                    if (!IsConditionOperandEligible(v))
                    {
                        return false;
                    }
                }
                return true;
            case AttributeExistsCondition ae:
                return IsPathEligible(ae.Path);
            case AttributeNotExistsCondition ane:
                return IsPathEligible(ane.Path);
            case BeginsWithCondition bw:
                return IsConditionOperandEligible(bw.Path) && IsConditionOperandEligible(bw.Prefix);
            case AttributeTypeCondition at:
                // checkAttrType only matches native JSON shapes; B / SS / NS / BS
                // are stored as `_a2a:` envelopes and evaluate incorrectly.
                return IsPathEligible(at.Path) && IsNativeTypeTag(at.TypeTag.Value);
            // size() depends on the stored encoded type (a set envelope is an
            // object, not an array); contains() likewise. Route to fallback.
            case ContainsCondition:
            default:
                return false;
        }
    }

    private static bool IsConditionOperandEligible(ConditionOperand operand) => operand switch
    {
        ConditionPathOperand cp => IsPathEligible(cp.Path),
        ConditionValueOperand cv => IsNativeValue(cv.Value.Value),
        SizeOperand => false,
        _ => false,
    };

    private static bool IsPathEligible(DocumentPath path)
    {
        var segments = path.Segments;
        for (var i = 0; i < segments.Count; i++)
        {
            var seg = segments[i];

            // The JS path helpers split on '.' only; list indexes are not parsed.
            if (seg is IndexPathSegment)
            {
                return false;
            }

            if (seg is AttributePathSegment attr)
            {
                // The serializer flattens the path into a single dot-joined
                // string and the sproc splits it back on '.'. An attribute
                // name that itself contains a dot (legal via
                // ExpressionAttributeNames) would be mis-parsed as a nested
                // path, so it cannot be executed faithfully server-side.
                if (attr.Name.IndexOf('.') >= 0)
                {
                    return false;
                }

                // The root attribute may collide with a reserved Cosmos doc
                // property. The most important case is a user attribute named
                // exactly "id": storage shadow-encodes it as "_a2a$id"
                // (InferredAttributeStorage), but the sproc operates on the raw
                // Cosmos document where "id" is the routing/sort-key field — so
                // a condition or update on it would read/write the wrong value.
                // Any name in the reserved "_a2a" namespace (or "id") is
                // likewise translated on the C# write path the sproc bypasses.
                if (i == 0 && InferredAttributeStorage.IsReservedTopLevelName(attr.Name))
                {
                    return false;
                }
            }
        }
        return true;
    }

    private static bool IsNativeTypeTag(JsonElement typeTag)
    {
        if (typeTag.ValueKind != JsonValueKind.Object)
        {
            return false;
        }
        foreach (var prop in typeTag.EnumerateObject())
        {
            if (prop.Name != "S")
            {
                return false;
            }
            return prop.Value.GetString() switch
            {
                // S / BOOL / NULL / L map to plain JSON shapes the sproc's
                // checkAttrType can test unambiguously. "N" and "M" are
                // deliberately excluded: high-precision numbers are stored as
                // {"_a2a:N":...} envelope OBJECTS (so checkAttrType would report
                // them as "M", not "N") and binary/set values are likewise
                // envelope objects that would be mis-reported as "M". Allowing
                // either tag would let attribute_type() silently mis-evaluate
                // against encoded stored values, so route those to the fallback.
                "S" or "BOOL" or "NULL" or "L" => true,
                _ => false,
            };
        }
        return false;
    }

    /// <summary>
    /// True if the DynamoDB AttributeValue is stored as a plain JSON shape the
    /// sproc can read back losslessly: S, BOOL, NULL, native (JS-safe) N, and
    /// maps / lists composed recursively of those. B / SS / NS / BS — and any N
    /// that does not round-trip through an IEEE-754 double — are rejected.
    /// </summary>
    private static bool IsNativeValue(JsonElement attributeValue)
    {
        if (attributeValue.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (var prop in attributeValue.EnumerateObject())
        {
            switch (prop.Name)
            {
                case "S":
                case "BOOL":
                case "NULL":
                    return true;
                case "N":
                    return IsJsSafeNumber(prop.Value.GetString());
                case "M":
                    foreach (var member in prop.Value.EnumerateObject())
                    {
                        if (!IsNativeValue(member.Value))
                        {
                            return false;
                        }
                    }
                    return true;
                case "L":
                    foreach (var item in prop.Value.EnumerateArray())
                    {
                        if (!IsNativeValue(item))
                        {
                            return false;
                        }
                    }
                    return true;
                default:
                    // B, SS, NS, BS, or anything unexpected.
                    return false;
            }
        }

        return false;
    }

    /// <summary>
    /// True if the DynamoDB number string round-trips exactly through a double,
    /// so the sproc (which parses it as a JS number) stores and compares it
    /// without precision loss. Numbers beyond System.Decimal range, or that lose
    /// digits through a double, are rejected so they take the Decimal-based
    /// fallback path instead.
    /// </summary>
    private static bool IsJsSafeNumber(string? number)
    {
        if (string.IsNullOrEmpty(number))
        {
            return false;
        }

        if (!decimal.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var dec))
        {
            return false;
        }

        var d = (double)dec;
        if (!double.IsFinite(d))
        {
            return false;
        }

        try
        {
            return (decimal)d == dec;
        }
        catch (System.OverflowException)
        {
            return false;
        }
    }
}
