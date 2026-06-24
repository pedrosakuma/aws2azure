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
        SkPredicate? Sk,
        ConditionNode? IndexSortKeyNode = null);

    internal abstract record SkPredicate;
    internal sealed record SkCompare(string Op, string Value) : SkPredicate;
    internal sealed record SkBetween(string Lo, string Hi) : SkPredicate;
    internal sealed record SkBeginsWith(string Prefix) : SkPredicate;

    /// <summary>
    /// Identifies the alternate sort attribute of a Local Secondary Index:
    /// the attribute <paramref name="Name"/> and its declared scalar type
    /// (<c>S</c>/<c>N</c>/<c>B</c> per <see cref="AttributeValueTypes"/>).
    /// </summary>
    internal sealed record IndexSortKeySpec(string Name, string Type);

    /// <summary>
    /// Identifies the HASH attribute of a Global Secondary Index: the
    /// attribute <paramref name="Name"/> and its declared scalar type. Unlike
    /// an LSI (whose HASH is the base table partition key), a GSI HASH is an
    /// arbitrary attribute stored raw, so a GSI query is cross-partition with a
    /// <c>c.&lt;gsiHash&gt; = @v</c> predicate rather than a partition-key
    /// header.
    /// </summary>
    internal sealed record IndexHashKeySpec(string Name, string Type);

    /// <summary>
    /// The validated raw KeyConditionExpression nodes for a Global Secondary
    /// Index query: the mandatory HASH equality and an optional sort-key
    /// predicate, both referencing the GSI's own (raw-stored) attributes. The
    /// SQL builder translates them against <c>c.&lt;gsiHash&gt;</c> /
    /// <c>c.&lt;gsiSort&gt;</c> via the filter pushdown.
    /// </summary>
    internal sealed record AnalysedGsiKeyCondition(ConditionNode HashNode, ConditionNode? SkNode);

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

    /// <summary>
    /// Analyses a <c>KeyConditionExpression</c> for a Local Secondary Index
    /// query. The HASH equality is on the BASE table HASH attribute and is
    /// encoded with the same codec as a base-table query so partition
    /// routing is identical. The optional sort-key predicate, however, must
    /// reference the LSI's alternate sort attribute (<paramref name="indexSk"/>)
    /// and is returned as a raw <see cref="ConditionNode"/> — NOT encoded into
    /// <c>c.id</c> — so the SQL builder can translate it against the raw
    /// stored attribute (<c>c.&lt;lsiSort&gt;</c>) via the filter pushdown.
    /// </summary>
    public static AnalysedKeyCondition AnalyseForIndex(
        ConditionNode root, TableMetadata meta, IndexSortKeySpec indexSk)
    {
        var hash = FindKey(meta, "HASH")
            ?? throw new KeyConditionException("Table has no HASH key declared in metadata.");

        ConditionNode hashNode;
        ConditionNode? skNode;
        if (root is AndCondition and)
        {
            hashNode = and.Left;
            skNode = and.Right;
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

        ConditionNode? indexSkNode = null;
        if (skNode is not null)
        {
            indexSkNode = ValidateIndexSortKeyNode(skNode, indexSk);
        }

        return new AnalysedKeyCondition(hashValue, Sk: null, IndexSortKeyNode: indexSkNode);
    }

    /// <summary>
    /// Analyses a <c>KeyConditionExpression</c> for a Global Secondary Index
    /// query. Both the mandatory HASH equality and the optional sort-key
    /// predicate reference the GSI's own attributes, which are stored raw (not
    /// the key codec) — so both are returned as validated raw
    /// <see cref="ConditionNode"/>s for the SQL builder to translate against
    /// <c>c.&lt;gsiHash&gt;</c> / <c>c.&lt;gsiSort&gt;</c>. There is no
    /// partition-key routing: a GSI query is cross-partition.
    /// </summary>
    public static AnalysedGsiKeyCondition AnalyseForGsi(
        ConditionNode root, IndexHashKeySpec gsiHash, IndexSortKeySpec? gsiSk)
    {
        ConditionNode hashNode;
        ConditionNode? skNode;
        if (root is AndCondition and)
        {
            hashNode = and.Left;
            skNode = and.Right;
            if (!ReferencesAttribute(hashNode, gsiHash.Name) && ReferencesAttribute(and.Right, gsiHash.Name))
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

        var validatedHash = ValidateGsiHashNode(hashNode, gsiHash);

        ConditionNode? validatedSk = null;
        if (skNode is not null)
        {
            if (gsiSk is null)
                throw new KeyConditionException(
                    "KeyConditionExpression references a sort-key predicate but the index has no RANGE key.");
            validatedSk = ValidateIndexSortKeyNode(skNode, gsiSk);
        }

        return new AnalysedGsiKeyCondition(validatedHash, validatedSk);
    }

    /// <summary>
    /// Validates that a GSI HASH predicate is a plain equality
    /// (<c>gsiHash = :v</c>) referencing the index hash attribute, with the
    /// value typed to the index hash's declared scalar type. Returns the
    /// validated raw node unchanged.
    /// </summary>
    private static ConditionNode ValidateGsiHashNode(ConditionNode node, IndexHashKeySpec gsiHash)
    {
        if (node is not CompareCondition cmp || cmp.Op != CompareOp.Equal)
            throw new KeyConditionException(
                $"KeyConditionExpression must compare the index HASH attribute '{gsiHash.Name}' with =.");
        if (!IsAttributeReference(cmp.Left, gsiHash.Name) || cmp.Right is not ConditionValueOperand vop)
            throw new KeyConditionException(
                $"KeyConditionExpression must be of the form '{gsiHash.Name} = :value'.");
        if (!ParsedAttributeValue.TryParse(vop.Value.Value, out var parsed))
            throw new KeyConditionException(
                $"Value bound to '{gsiHash.Name}' is not a typed attribute value.");
        if (!string.Equals(parsed.TypeTag, gsiHash.Type, StringComparison.Ordinal))
            throw new KeyConditionException(
                $"Index hash key '{gsiHash.Name}' has type {parsed.TypeTag} but the index declares {gsiHash.Type}.");
        return cmp;
    }

    /// <summary>
    /// Validates that an LSI sort-key predicate conforms to the DynamoDB
    /// KCE grammar (<c>=, &lt;, &lt;=, &gt;, &gt;=, BETWEEN, begins_with</c>)
    /// and references the index sort attribute. Returns the validated raw
    /// node unchanged (the SQL builder translates it against the raw stored
    /// attribute). <c>begins_with</c> on a Number sort key is rejected,
    /// matching the base-table path and real DynamoDB.
    /// </summary>
    private static ConditionNode ValidateIndexSortKeyNode(ConditionNode node, IndexSortKeySpec indexSk)
    {
        switch (node)
        {
            case CompareCondition cmp:
                if (!IsAttributeReference(cmp.Left, indexSk.Name) || cmp.Right is not ConditionValueOperand)
                    throw new KeyConditionException(
                        $"Sort-key predicate must reference '{indexSk.Name}' on the left and a value placeholder on the right.");
                switch (cmp.Op)
                {
                    case CompareOp.Equal:
                    case CompareOp.Less:
                    case CompareOp.LessEqual:
                    case CompareOp.Greater:
                    case CompareOp.GreaterEqual:
                        ValidateOperandType(cmp.Right, indexSk);
                        return cmp;
                    default:
                        throw new KeyConditionException(
                            $"Sort-key predicate operator {cmp.Op} is not supported in KeyConditionExpression.");
                }
            case BetweenCondition bt:
                if (!IsAttributeReference(bt.Value, indexSk.Name)
                    || bt.Lower is not ConditionValueOperand
                    || bt.Upper is not ConditionValueOperand)
                    throw new KeyConditionException(
                        $"BETWEEN sort-key predicate must reference '{indexSk.Name}' and two value placeholders.");
                ValidateOperandType(bt.Lower, indexSk);
                ValidateOperandType(bt.Upper, indexSk);
                return bt;
            case BeginsWithCondition bw:
                if (!IsAttributeReference(bw.Path, indexSk.Name)
                    || bw.Prefix is not ConditionValueOperand)
                    throw new KeyConditionException(
                        $"begins_with sort-key predicate must reference '{indexSk.Name}' and a value placeholder.");
                if (string.Equals(indexSk.Type, AttributeValueTypes.Number, StringComparison.Ordinal))
                    throw new KeyConditionException(
                        $"begins_with is not supported on Number sort key '{indexSk.Name}'.");
                ValidateOperandType(bw.Prefix, indexSk);
                return bw;
            default:
                throw new KeyConditionException(
                    "Sort-key predicate must be one of: =, <, <=, >, >=, BETWEEN, begins_with.");
        }
    }

    /// <summary>
    /// Validates that a value operand bound to an LSI sort-key predicate
    /// carries the index sort key's declared scalar type, mirroring the
    /// base-table path's key-type check. Surfaces a type mismatch as a
    /// ValidationException, matching real DynamoDB.
    /// </summary>
    private static void ValidateOperandType(ConditionOperand operand, IndexSortKeySpec indexSk)
    {
        if (operand is not ConditionValueOperand vop) return;
        if (!ParsedAttributeValue.TryParse(vop.Value.Value, out var parsed))
            throw new KeyConditionException(
                $"Value bound to '{indexSk.Name}' is not a typed attribute value.");
        if (!string.Equals(parsed.TypeTag, indexSk.Type, StringComparison.Ordinal))
            throw new KeyConditionException(
                $"Sort key '{indexSk.Name}' has type {parsed.TypeTag} but the index declares {indexSk.Type}.");
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
                // DynamoDB only permits begins_with on String / Binary sort
                // keys; the encoded form of a Number key is not a meaningful
                // textual prefix, so reject it as real DDB does.
                if (ItemKeyFormatter.TryGetDeclaredKeyType(meta, skName, out var bwType)
                    && string.Equals(bwType, AttributeValueTypes.Number, StringComparison.Ordinal))
                    throw new KeyConditionException(
                        $"begins_with is not supported on Number sort key '{skName}'.");
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
        if (!ItemKeyFormatter.TryGetDeclaredKeyType(meta, attrName, out var declaredType))
            throw new KeyConditionException(
                $"Key attribute '{attrName}' is not declared in the table's AttributeDefinitions.");

        // Encode with the SAME codec used by the write/point-read path so a
        // query operand routes to exactly the partition / id a matching
        // PutItem produced. begins_with prefixes encode to a hex prefix,
        // keeping STARTSWITH(c.id, <prefix>) exact for S and B keys.
        if (!KeyScalarCodec.TryEncode(declaredType, parsed, attrName, out var encoded, out var encodeError))
            throw new KeyConditionException(encodeError);
        return encoded;
    }
}

/// <summary>Thrown when a KeyConditionExpression violates the DynamoDB
/// grammar or the table's key schema. Surfaces as <c>ValidationException</c>
/// to the caller.</summary>
internal sealed class KeyConditionException : Exception
{
    public KeyConditionException(string message) : base(message) { }
}
