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
/// enveloped numbers (all stored as <c>_a2a:</c> objects the JS does not
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
    private enum TransactionScalarKind
    {
        String,
        Number,
        Boolean,
        Null,
    }

    public static bool IsEligible(ConditionNode? condition, UpdateExpressionAst? update)
        => IsConditionEligible(condition) && IsUpdateEligible(update);

    /// <summary>
    /// Validates the exact condition subset interpreted by
    /// <c>atomicTransactWrite_v5</c>. Transactional writes have no in-process
    /// fallback, so every unsupported shape fails before the stored procedure is
    /// invoked instead of risking divergent server-side evaluation.
    /// </summary>
    public static bool TryValidateTransactionCondition(
        ConditionNode? condition,
        out string? error)
    {
        error = null;
        return ValidateTransactionCondition(condition, ref error);
    }

    private static bool ValidateTransactionCondition(
        ConditionNode? node,
        ref string? error)
    {
        switch (node)
        {
            case null:
                return true;
            case AndCondition and:
                return ValidateTransactionCondition(and.Left, ref error)
                    && ValidateTransactionCondition(and.Right, ref error);
            case OrCondition or:
                return ValidateTransactionCondition(or.Left, ref error)
                    && ValidateTransactionCondition(or.Right, ref error);
            case NotCondition not:
                return ValidateTransactionCondition(not.Inner, ref error);
            case CompareCondition compare:
                if (!TryGetTransactionComparisonOperands(
                        compare.Left,
                        compare.Right,
                        out var compareKind,
                        ref error))
                {
                    return false;
                }
                if (compare.Op is not (CompareOp.Equal or CompareOp.NotEqual)
                    && compareKind != TransactionScalarKind.String)
                {
                    error = "Transactional ordered comparisons support strings only; numbers are limited to equality, not-equal, and IN.";
                    return false;
                }
                return true;
            case BetweenCondition between:
                if (!TryGetTransactionPath(between.Value, out _, ref error)
                    || !TryGetTransactionScalar(between.Lower, out var lowerKind, ref error)
                    || !TryGetTransactionScalar(between.Upper, out var upperKind, ref error))
                {
                    return false;
                }
                if (lowerKind != upperKind
                    || lowerKind != TransactionScalarKind.String)
                {
                    error = "Transactional BETWEEN supports string bounds only.";
                    return false;
                }
                return true;
            case InCondition @in:
                if (!TryGetTransactionPath(@in.Value, out _, ref error)
                    || @in.Set.Count == 0)
                {
                    error ??= "Transactional IN requires a top-level attribute path and at least one scalar value.";
                    return false;
                }
                TransactionScalarKind? inKind = null;
                foreach (var operand in @in.Set)
                {
                    if (!TryGetTransactionScalar(operand, out var candidateKind, ref error))
                    {
                        return false;
                    }
                    if (inKind is null)
                    {
                        inKind = candidateKind;
                    }
                    else if (inKind.Value != candidateKind)
                    {
                        error = "Transactional IN values must all have the same scalar type.";
                        return false;
                    }
                }
                return true;
            case AttributeExistsCondition exists:
                return TryValidateTransactionPath(exists.Path, ref error);
            case AttributeNotExistsCondition notExists:
                return TryValidateTransactionPath(notExists.Path, ref error);
            case BeginsWithCondition begins:
                if (!TryGetTransactionPath(begins.Path, out _, ref error)
                    || !TryGetTransactionScalar(begins.Prefix, out var prefixKind, ref error))
                {
                    return false;
                }
                if (prefixKind != TransactionScalarKind.String)
                {
                    error = "Transactional begins_with requires a string prefix.";
                    return false;
                }
                return true;
            case AttributeTypeCondition type:
                if (!TryValidateTransactionPath(type.Path, ref error)
                    || !TryReadTypeTag(type.TypeTag.Value, out var typeTag))
                {
                    error ??= "Transactional attribute_type requires a literal S, BOOL, or NULL type tag.";
                    return false;
                }
                if (typeTag is not ("S" or "BOOL" or "NULL"))
                {
                    error = "Transactional attribute_type supports only S, BOOL, and NULL.";
                    return false;
                }
                return true;
            case ContainsCondition:
                error = "contains() is not supported in transactional conditions.";
                return false;
            default:
                error = "The ConditionExpression contains a node that is not supported in transactions.";
                return false;
        }
    }

    /// <summary>
    /// Validates the exact <c>Update</c> transact-item subset interpreted by
    /// <c>atomicTransactWrite_v6</c> (#798). Reuses the same faithfulness
    /// rules as the single-item <c>atomicWrite_v2</c> sproc-eligible update
    /// path (<see cref="IsUpdateEligible"/>): SET/REMOVE only (no ADD/DELETE
    /// — those carry set/number-envelope semantics the JS does not
    /// replicate), top-level non-reserved attribute paths only, and native
    /// JSON-representable literal values only. Transactional writes have no
    /// in-process fallback, so an ineligible shape must fail before the
    /// stored procedure is invoked rather than risk divergent execution.
    /// </summary>
    public static bool TryValidateTransactionUpdate(
        UpdateExpressionAst? update,
        out string? error)
    {
        error = null;
        if (update is null)
        {
            error = "Update requires an UpdateExpression.";
            return false;
        }
        if (update.Add is not null || update.Delete is not null)
        {
            error =
                "Transactional Update supports SET and REMOVE only; ADD and DELETE carry set/number-envelope semantics that are not supported in this profile.";
            return false;
        }
        if (update.Set is { Actions.Count: > 0 } set)
        {
            foreach (var action in set.Actions)
            {
                if (!TryValidateTransactionPath(action.Path, ref error, "Update"))
                {
                    return false;
                }
                if (!TryValidateTransactionUpdateOperand(action.Value, ref error))
                {
                    return false;
                }
            }
        }
        if (update.Remove is { Paths.Count: > 0 } remove)
        {
            foreach (var path in remove.Paths)
            {
                if (!TryValidateTransactionPath(path, ref error, "Update"))
                {
                    return false;
                }
            }
        }
        if ((update.Set is null || update.Set.Actions.Count == 0)
            && (update.Remove is null || update.Remove.Paths.Count == 0))
        {
            error = "Transactional Update requires at least one SET or REMOVE action.";
            return false;
        }
        return true;
    }

    private static bool TryValidateTransactionUpdateOperand(
        ValueOperand operand,
        ref string? error)
    {
        switch (operand)
        {
            case ValueRefOperand vr:
                if (!IsNativeValue(vr.Value))
                {
                    error =
                        "Transactional Update literal values must be S, BOOL, NULL, or a plain number/map/list of those; sets, binary, and enveloped numbers are rejected.";
                    return false;
                }
                return true;
            case PathOperand po:
                return TryValidateTransactionPath(po.Path, ref error, "Update");
            case ArithmeticOperand ao:
                return TryValidateTransactionUpdateOperand(ao.Left, ref error)
                    && TryValidateTransactionUpdateOperand(ao.Right, ref error);
            case IfNotExistsOperand ine:
                return TryValidateTransactionPath(ine.Path, ref error, "Update")
                    && TryValidateTransactionUpdateOperand(ine.Fallback, ref error);
            case ListAppendOperand la:
                return TryValidateTransactionUpdateOperand(la.Left, ref error)
                    && TryValidateTransactionUpdateOperand(la.Right, ref error);
            default:
                error = "Unsupported transactional Update value operand.";
                return false;
        }
    }

    private static bool TryGetTransactionPath(
        ConditionOperand operand,
        out DocumentPath? path,
        ref string? error)
    {
        if (operand is ConditionPathOperand pathOperand
            && TryValidateTransactionPath(pathOperand.Path, ref error))
        {
            path = pathOperand.Path;
            return true;
        }

        path = null;
        error ??= "Transactional comparisons require a top-level attribute path on the left and a scalar value on the right.";
        return false;
    }

    private static bool TryGetTransactionComparisonOperands(
        ConditionOperand left,
        ConditionOperand right,
        out TransactionScalarKind kind,
        ref string? error)
    {
        if (left is ConditionPathOperand leftPath)
        {
            if (!TryValidateTransactionPath(leftPath.Path, ref error))
            {
                kind = default;
                return false;
            }
            return TryGetTransactionScalar(right, out kind, ref error);
        }
        if (right is ConditionPathOperand rightPath)
        {
            if (!TryValidateTransactionPath(rightPath.Path, ref error))
            {
                kind = default;
                return false;
            }
            return TryGetTransactionScalar(left, out kind, ref error);
        }

        kind = default;
        error =
            "Transactional comparisons require exactly one top-level attribute path and one scalar value.";
        return false;
    }

    private static bool TryValidateTransactionPath(
        DocumentPath path,
        ref string? error,
        string context = "conditions")
    {
        if (path.Segments.Count != 1
            || path.Segments[0] is not AttributePathSegment attribute)
        {
            error = $"Transactional {context} support top-level attribute paths only; nested and list-index paths are rejected.";
            return false;
        }
        if (attribute.Name.IndexOf('.') >= 0)
        {
            error = $"Transactional {context} do not support attribute names containing '.'.";
            return false;
        }
        if (InferredAttributeStorage.IsReservedTopLevelName(attribute.Name))
        {
            error =
                $"Transactional {context} cannot reference reserved attribute '{attribute.Name}'.";
            return false;
        }
        if (InferredAttributeStorage.IsCosmosSystemField(attribute.Name))
        {
            error =
                $"Transactional {context} cannot reference Cosmos system attribute '{attribute.Name}'.";
            return false;
        }
        return true;
    }

    private static bool TryGetTransactionScalar(
        ConditionOperand operand,
        out TransactionScalarKind kind,
        ref string? error)
    {
        if (operand is ConditionValueOperand value
            && TryReadTransactionScalar(value.Value.Value, out kind))
        {
            return true;
        }

        kind = default;
        error =
            "Transactional condition values must be scalar S, BOOL, NULL, or N values persisted as bare JSON numbers; maps, lists, sets, binary, and enveloped numbers are rejected.";
        return false;
    }

    private static bool TryReadTransactionScalar(
        JsonElement attributeValue,
        out TransactionScalarKind kind)
    {
        kind = default;
        if (attributeValue.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        using var enumerator = attributeValue.EnumerateObject();
        if (!enumerator.MoveNext())
        {
            return false;
        }
        var property = enumerator.Current;
        if (enumerator.MoveNext())
        {
            return false;
        }

        switch (property.Name)
        {
            case "S" when property.Value.ValueKind == JsonValueKind.String:
                kind = TransactionScalarKind.String;
                return true;
            case "N" when property.Value.ValueKind == JsonValueKind.String
                && IsBareStorageNumber(property.Value.GetString()):
                kind = TransactionScalarKind.Number;
                return true;
            case "BOOL" when property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False:
                kind = TransactionScalarKind.Boolean;
                return true;
            case "NULL" when property.Value.ValueKind == JsonValueKind.True:
                kind = TransactionScalarKind.Null;
                return true;
            default:
                return false;
        }
    }

    private static bool TryReadTypeTag(JsonElement attributeValue, out string? typeTag)
    {
        typeTag = null;
        if (attributeValue.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        using var enumerator = attributeValue.EnumerateObject();
        if (!enumerator.MoveNext())
        {
            return false;
        }
        var property = enumerator.Current;
        if (enumerator.MoveNext()
            || property.Name != "S"
            || property.Value.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        typeTag = property.Value.GetString();
        return typeTag is not null;
    }

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
    /// sproc can read back losslessly: S, BOOL, NULL, bare-storage N, and
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
                    return IsBareStorageNumber(prop.Value.GetString());
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
    /// True only when the persisted codec writes the DynamoDB number as a bare
    /// JSON number. This intentionally delegates to the codec so eligibility can
    /// never admit a value that storage represents as an <c>_a2a:N</c> envelope.
    /// </summary>
    private static bool IsBareStorageNumber(string? number)
        => InferredAttributeStorage.TryGetCanonicalBareJsonNumber(number, out _);
}
