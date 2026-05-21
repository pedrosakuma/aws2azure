using System;
using System.Collections.Generic;
using System.Text.Json;
using Aws2Azure.Modules.DynamoDb.Expressions;
using Aws2Azure.Modules.DynamoDb.Internal;
namespace Aws2Azure.Modules.DynamoDb.Operations;

/// <summary>
/// Walks a parsed <see cref="ConditionNode"/> tree and validates that
/// it conforms to the DynamoDB <c>KeyConditionExpression</c> shape:
/// <c>HASH = :v</c> optionally combined with a single sort-key
/// predicate via <c>AND</c>. Returns the canonical (hashValue,
/// optional sortKeyPredicate) pair the Query handler then projects
/// into a Cosmos partition lookup + <c>c.id</c> range filter.
///
/// <para>The DynamoDB language allows exactly:</para>
/// <list type="bullet">
///   <item><c>HASH = :v</c></item>
///   <item><c>HASH = :v AND SK = :sk</c></item>
///   <item><c>HASH = :v AND SK &lt; :sk</c> (and &lt;= / &gt; / &gt;=)</item>
///   <item><c>HASH = :v AND SK BETWEEN :lo AND :hi</c></item>
///   <item><c>HASH = :v AND begins_with(SK, :prefix)</c></item>
/// </list>
/// Anything else (OR, NOT, attribute_exists, contains, IN, ...) is
/// rejected as a <see cref="ValidationException"/>-bound error.
/// </summary>
internal static class KeyConditionAnalyser
{
    internal sealed record AnalysedKeyCondition(
        string HashValue,
        SkPredicate? Sk);

    internal abstract record SkPredicate;
    internal sealed record SkCompare(string Op, string Value) : SkPredicate;
    internal sealed record SkBetween(string Lo, string Hi) : SkPredicate;
    internal sealed record SkBeginsWith(string Prefix) : SkPredicate;

    public static AnalysedKeyCondition Analyse(ConditionNode root, TableMetadata meta)
    {
        var hash = FindKey(meta, "HASH")
            ?? throw new KeyConditionException("Table has no HASH key declared in metadata.");
        var range = FindKey(meta, "RANGE");

        // Top-level node is either a single comparison (hash-only KCE) or
        // an AndCondition combining the hash predicate with one sort-key
        // predicate. Reject any other shape.
        ConditionNode hashNode;
        ConditionNode? skNode;
        if (root is AndCondition and)
        {
            hashNode = and.Left;
            skNode = and.Right;
            // If the hash predicate happens to be on the right and the sk
            // predicate on the left, swap so downstream code sees a stable
            // order.
            if (!ReferencesAttribute(hashNode, hash.Name) && ReferencesAttribute(and.Right, hash.Name))
            {
                hashNode = and.Right;
                skNode = and.Left;
            }
        }
        else
        {
            hashNode = root;
            skNode = null;
        }

        string hashValue = ExtractHashEquality(hashNode, hash.Name, meta);

        SkPredicate? sk = null;
        if (skNode is not null)
        {
            if (range is null)
                throw new KeyConditionException(
                    "KeyConditionExpression references a sort-key predicate but the table has no RANGE key.");
            sk = ExtractSkPredicate(skNode, range.Name, meta);
        }

        return new AnalysedKeyCondition(hashValue, sk);
    }

    private static TableKeySchemaElement? FindKey(TableMetadata meta, string role)
    {
        foreach (var k in meta.KeySchema)
        {
            if (string.Equals(k.KeyType, role, StringComparison.OrdinalIgnoreCase))
                return k;
        }
        return null;
    }

    private static string ExtractHashEquality(ConditionNode node, string hashName, TableMetadata meta)
    {
        if (node is not CompareCondition cmp || cmp.Op != CompareOp.Equal)
            throw new KeyConditionException(
                $"KeyConditionExpression must compare HASH attribute '{hashName}' with =.");
        if (!IsAttributeReference(cmp.Left, hashName) || cmp.Right is not ConditionValueOperand vr)
            throw new KeyConditionException(
                $"KeyConditionExpression must be of the form '{hashName} = :value'.");
        return ScalarFromValueRef(vr.Value, hashName, meta);
    }

    private static SkPredicate ExtractSkPredicate(ConditionNode node, string skName, TableMetadata meta)
    {
        switch (node)
        {
            case CompareCondition cmp:
                if (!IsAttributeReference(cmp.Left, skName) || cmp.Right is not ConditionValueOperand v)
                    throw new KeyConditionException(
                        $"Sort-key predicate must reference '{skName}' on the left and a value placeholder on the right.");
                var opStr = cmp.Op switch
                {
                    CompareOp.Equal => "=",
                    CompareOp.Less => "<",
                    CompareOp.LessEqual => "<=",
                    CompareOp.Greater => ">",
                    CompareOp.GreaterEqual => ">=",
                    _ => throw new KeyConditionException(
                        $"Sort-key predicate operator {cmp.Op} is not supported in KeyConditionExpression."),
                };
                return new SkCompare(opStr, ScalarFromValueRef(v.Value, skName, meta));
            case BetweenCondition bt:
                if (!IsAttributeReference(bt.Value, skName)
                    || bt.Lower is not ConditionValueOperand lo
                    || bt.Upper is not ConditionValueOperand hi)
                    throw new KeyConditionException(
                        $"BETWEEN sort-key predicate must reference '{skName}' and two value placeholders.");
                return new SkBetween(ScalarFromValueRef(lo.Value, skName, meta), ScalarFromValueRef(hi.Value, skName, meta));
            case BeginsWithCondition bw:
                if (!IsAttributeReference(bw.Path, skName)
                    || bw.Prefix is not ConditionValueOperand pr)
                    throw new KeyConditionException(
                        $"begins_with sort-key predicate must reference '{skName}' and a value placeholder.");
                return new SkBeginsWith(ScalarFromValueRef(pr.Value, skName, meta));
            default:
                throw new KeyConditionException(
                    "Sort-key predicate must be one of: =, <, <=, >, >=, BETWEEN, begins_with.");
        }
    }

    private static bool IsAttributeReference(ConditionOperand operand, string expected)
    {
        if (operand is not ConditionPathOperand path) return false;
        if (path.Path.Segments.Count != 1) return false;
        if (path.Path.Segments[0] is not AttributePathSegment seg) return false;
        return string.Equals(seg.Name, expected, StringComparison.Ordinal);
    }

    private static bool ReferencesAttribute(ConditionNode node, string name)
    {
        return node switch
        {
            CompareCondition cmp => IsAttributeReference(cmp.Left, name) || IsAttributeReference(cmp.Right, name),
            BetweenCondition bt => IsAttributeReference(bt.Value, name),
            BeginsWithCondition bw => IsAttributeReference(bw.Path, name),
            _ => false,
        };
    }

    /// <summary>
    /// Extracts a scalar string from a typed attribute value
    /// (<c>{"S":"x"}</c>, <c>{"N":"123"}</c>, or <c>{"B":"base64"}</c>),
    /// after validating that the operand's type tag matches the table's
    /// declared <c>AttributeDefinitions</c>. Mirrors
    /// <see cref="ItemKeyFormatter"/>'s scalar extraction so a query
    /// operand routes to exactly the same partition / id that a
    /// matching PutItem produced.
    /// </summary>
    private static string ScalarFromValueRef(ValueRefOperand vr, string attrName, TableMetadata meta)
    {
        if (!ItemKeyFormatter.ValidateKeyAttributeType(vr.Value, meta, attrName, out var typeError))
            throw new KeyConditionException(typeError);
        if (!ParsedAttributeValue.TryParse(vr.Value, out var parsed))
            throw new KeyConditionException(
                $"Value bound to '{attrName}' is not a typed attribute value.");
        if (parsed.Value.ValueKind != JsonValueKind.String)
            throw new KeyConditionException(
                $"Key attribute '{attrName}' value must be a JSON string per DynamoDB wire format.");
        var raw = parsed.Value.GetString() ?? string.Empty;
        if (raw.Length == 0)
            throw new KeyConditionException(
                $"Key attribute '{attrName}' value must not be empty.");
        return raw;
    }
}

/// <summary>Thrown when a KeyConditionExpression violates the DynamoDB
/// grammar or the table's key schema. Surfaces as <c>ValidationException</c>
/// to the caller.</summary>
internal sealed class KeyConditionException : Exception
{
    public KeyConditionException(string message) : base(message) { }
}
