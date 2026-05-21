using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text.Json;
using Aws2Azure.Modules.DynamoDb.Expressions;
using Aws2Azure.Modules.DynamoDb.Internal;

namespace Aws2Azure.Modules.DynamoDb.Operations;

/// <summary>
/// Executes a parsed <see cref="UpdateExpressionAst"/> against an
/// in-memory item map (a <c>Dictionary&lt;string, JsonElement&gt;</c>
/// holding raw DynamoDB-typed attribute values such as
/// <c>{"S":"foo"}</c> or <c>{"N":"42"}</c>).
///
/// <para>Order of operations follows the DynamoDB documentation:
/// SET → REMOVE → ADD → DELETE. Within each clause, actions run in the
/// order they appear in the expression. Path-overlap detection runs
/// over the whole expression up-front so callers get the same
/// <c>ValidationException</c> AWS emits.</para>
///
/// <para>Numeric arithmetic uses .NET <see cref="BigInteger"/> when
/// both operands are integers and <see cref="System.Decimal"/> otherwise,
/// matching DynamoDB's 38-significant-digit precision contract. Set
/// operations preserve insertion order to avoid surprising diffs even
/// though DynamoDB treats sets as unordered.</para>
/// </summary>
internal static class UpdateExecutor
{
    /// <summary>
    /// Captured values for ReturnValues bookkeeping. <see cref="OldItem"/>
    /// is the item map before any mutation (clone, may be null if the
    /// item did not exist); <see cref="NewItem"/> is the item after all
    /// ops have run. <see cref="UpdatedAttributes"/> is the set of
    /// top-level attribute names touched by the expression (drives
    /// UPDATED_OLD / UPDATED_NEW projections).
    /// </summary>
    internal sealed class ExecutionResult
    {
        public Dictionary<string, JsonElement>? OldItem { get; init; }
        public Dictionary<string, JsonElement> NewItem { get; init; } = new();
        public HashSet<string> UpdatedAttributes { get; init; } = new(StringComparer.Ordinal);
        public bool ItemExistedBefore { get; init; }
    }

    public static ExecutionResult Apply(
        UpdateExpressionAst ast,
        Dictionary<string, JsonElement>? existingItem)
    {
        DetectPathOverlap(ast);

        var existed = existingItem is not null;
        var oldClone = existingItem is null ? null : CloneItemMap(existingItem);
        var working = existingItem is null
            ? new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            : CloneItemMap(existingItem);
        var touched = new HashSet<string>(StringComparer.Ordinal);

        if (ast.Set is { } set)
        {
            foreach (var a in set.Actions)
            {
                var newValue = EvaluateOperand(a.Value, working);
                ApplySet(working, a.Path, newValue);
                touched.Add(a.Path.Root);
            }
        }
        if (ast.Remove is { } rem)
        {
            foreach (var p in rem.Paths)
            {
                ApplyRemove(working, p);
                touched.Add(p.Root);
            }
        }
        if (ast.Add is { } add)
        {
            foreach (var a in add.Actions)
            {
                ApplyAdd(working, a.Path, a.Value);
                touched.Add(a.Path.Root);
            }
        }
        if (ast.Delete is { } del)
        {
            foreach (var a in del.Actions)
            {
                ApplyDelete(working, a.Path, a.Value);
                touched.Add(a.Path.Root);
            }
        }

        return new ExecutionResult
        {
            OldItem = oldClone,
            NewItem = working,
            UpdatedAttributes = touched,
            ItemExistedBefore = existed,
        };
    }

    // ----- operand evaluation ----------------------------------------

    private static JsonElement EvaluateOperand(
        ValueOperand operand,
        Dictionary<string, JsonElement> currentItem)
    {
        switch (operand)
        {
            case ValueRefOperand v:
                return v.Value;
            case PathOperand p:
                if (!TryReadPath(currentItem, p.Path, out var existing))
                    throw new UpdateValidationException(
                        $"The provided expression refers to an attribute that does not exist in the item: {p.Path.Display}");
                return existing;
            case IfNotExistsOperand ine:
                if (TryReadPath(currentItem, ine.Path, out var current))
                    return current;
                return EvaluateOperand(ine.Fallback, currentItem);
            case ListAppendOperand la:
                var left = EvaluateOperand(la.Left, currentItem);
                var right = EvaluateOperand(la.Right, currentItem);
                return ListAppend(left, right);
            case ArithmeticOperand ar:
                var l = EvaluateOperand(ar.Left, currentItem);
                var r = EvaluateOperand(ar.Right, currentItem);
                return Arithmetic(ar.Op, l, r);
            default:
                throw new UpdateValidationException("Unsupported operand in SET assignment.");
        }
    }

    // ----- path mutation ---------------------------------------------

    private static void ApplySet(
        Dictionary<string, JsonElement> root, DocumentPath path, JsonElement value)
    {
        if (!ParsedAttributeValue.TryParse(value, out _))
            throw new UpdateValidationException(
                $"The result of the SET assignment for '{path.Display}' is not a valid DynamoDB attribute value.");

        if (path.IsTopLevel)
        {
            root[path.Root] = value;
            return;
        }

        // Walk to the parent container, then write the leaf.
        var parent = WalkToParent(root, path, createIntermediate: false);
        WriteLeaf(parent, path.Segments[^1], value, path);
    }

    private static void ApplyRemove(
        Dictionary<string, JsonElement> root, DocumentPath path)
    {
        if (path.IsTopLevel)
        {
            root.Remove(path.Root);
            return;
        }
        if (!TryWalkToParent(root, path, out var parent))
        {
            // REMOVE on a path whose parent doesn't exist is a no-op,
            // matching DynamoDB.
            return;
        }
        RemoveLeaf(parent, path.Segments[^1]);
    }

    private static void ApplyAdd(
        Dictionary<string, JsonElement> root, DocumentPath path, ValueRefOperand value)
    {
        // ADD is documented as top-level only.
        if (!path.IsTopLevel)
            throw new UpdateValidationException(
                $"ADD action does not support nested document paths: {path.Display}");

        if (!ParsedAttributeValue.TryParse(value.Value, out var addend))
            throw new UpdateValidationException(
                $"ADD operand for '{path.Display}' is not a valid attribute value.");

        if (!root.TryGetValue(path.Root, out var current))
        {
            // Attribute absent → ADD initialises it with the operand.
            root[path.Root] = value.Value;
            return;
        }
        if (!ParsedAttributeValue.TryParse(current, out var currentParsed))
            throw new UpdateValidationException(
                $"ADD target '{path.Root}' is not a typed attribute value.");

        switch (addend.TypeTag)
        {
            case AttributeValueTypes.Number:
                if (currentParsed.TypeTag != AttributeValueTypes.Number)
                    throw new UpdateValidationException(
                        $"ADD operand type N is incompatible with the current type ({currentParsed.TypeTag}) of '{path.Root}'.");
                root[path.Root] = BuildNumber(AddNumbers(currentParsed.Value.GetString()!, addend.Value.GetString()!));
                return;
            case AttributeValueTypes.StringSet:
            case AttributeValueTypes.NumberSet:
            case AttributeValueTypes.BinarySet:
                if (currentParsed.TypeTag != addend.TypeTag)
                    throw new UpdateValidationException(
                        $"ADD operand type {addend.TypeTag} is incompatible with the current type ({currentParsed.TypeTag}) of '{path.Root}'.");
                root[path.Root] = SetUnion(addend.TypeTag, currentParsed.Value, addend.Value);
                return;
            default:
                throw new UpdateValidationException(
                    $"ADD requires an N or set-typed operand; got {addend.TypeTag}.");
        }
    }

    private static void ApplyDelete(
        Dictionary<string, JsonElement> root, DocumentPath path, ValueRefOperand value)
    {
        if (!path.IsTopLevel)
            throw new UpdateValidationException(
                $"DELETE action does not support nested document paths: {path.Display}");

        if (!ParsedAttributeValue.TryParse(value.Value, out var operand))
            throw new UpdateValidationException(
                $"DELETE operand for '{path.Display}' is not a valid attribute value.");

        if (operand.TypeTag is not (AttributeValueTypes.StringSet
            or AttributeValueTypes.NumberSet or AttributeValueTypes.BinarySet))
            throw new UpdateValidationException(
                $"DELETE requires a set-typed operand; got {operand.TypeTag}.");

        if (!root.TryGetValue(path.Root, out var current)) return; // no-op when target missing
        if (!ParsedAttributeValue.TryParse(current, out var currentParsed)
            || currentParsed.TypeTag != operand.TypeTag)
            throw new UpdateValidationException(
                $"DELETE operand type {operand.TypeTag} is incompatible with the current type of '{path.Root}'.");

        var remaining = SetDifference(operand.TypeTag, currentParsed.Value, operand.Value);
        // An empty set is illegal in DynamoDB — remove the attribute.
        if (remaining is null)
            root.Remove(path.Root);
        else
            root[path.Root] = remaining.Value;
    }

    // ----- path walking ----------------------------------------------

    private static Dictionary<string, JsonElement> WalkToParent(
        Dictionary<string, JsonElement> root, DocumentPath path, bool createIntermediate)
    {
        // Returns a container (either a top-level Dictionary or the
        // attribute map inside a nested {"M": ...} envelope) holding the
        // leaf segment of the path. Throws if any intermediate is
        // missing or of the wrong kind, matching DynamoDB behaviour.
        object container = root;
        for (int i = 0; i < path.Segments.Count - 1; i++)
        {
            var seg = path.Segments[i];
            container = DescendInto(container, seg, path);
        }
        if (container is Dictionary<string, JsonElement> dict) return dict;
        if (container is List<JsonElement>)
            throw new UpdateValidationException(
                $"The document path tries to assign a named attribute to a list at '{path.Display}'.");
        throw new UpdateValidationException(
            $"Internal: unexpected container type for path '{path.Display}'.");
    }

    private static bool TryWalkToParent(
        Dictionary<string, JsonElement> root, DocumentPath path,
        out Dictionary<string, JsonElement> parent)
    {
        parent = root;
        object container = root;
        try
        {
            for (int i = 0; i < path.Segments.Count - 1; i++)
            {
                var seg = path.Segments[i];
                container = DescendInto(container, seg, path);
            }
            if (container is Dictionary<string, JsonElement> dict)
            {
                parent = dict;
                return true;
            }
            return false;
        }
        catch (UpdateValidationException)
        {
            return false;
        }
    }

    private static object DescendInto(object container, PathSegment seg, DocumentPath fullPath)
    {
        switch (seg)
        {
            case AttributePathSegment a:
            {
                if (container is not Dictionary<string, JsonElement> dict)
                    throw new UpdateValidationException(
                        $"The document path tries to access attribute '{a.Name}' on a list at '{fullPath.Display}'.");
                if (!dict.TryGetValue(a.Name, out var child))
                    throw new UpdateValidationException(
                        $"The document path provided in the update expression is invalid for update: {fullPath.Display}");
                return UnwrapTypedContainer(child, a.Name, fullPath);
            }
            case IndexPathSegment idx:
            {
                if (container is not List<JsonElement> list)
                    throw new UpdateValidationException(
                        $"The document path tries to index into a non-list at '{fullPath.Display}'.");
                if (idx.Index >= list.Count)
                    throw new UpdateValidationException(
                        $"The document path provided in the update expression is invalid for update: {fullPath.Display}");
                return UnwrapTypedContainer(list[idx.Index], $"[{idx.Index}]", fullPath);
            }
            default:
                throw new UpdateValidationException(
                    $"Internal: unknown path segment kind in '{fullPath.Display}'.");
        }
    }

    /// <summary>
    /// Unwraps a single-property typed attribute value
    /// (<c>{"M":{...}}</c> or <c>{"L":[...]}</c>) into its inner
    /// container so the next path segment can descend. Other type tags
    /// (S/N/B/SS/etc.) are not navigable.
    /// </summary>
    private static object UnwrapTypedContainer(JsonElement value, string segLabel, DocumentPath fullPath)
    {
        if (!ParsedAttributeValue.TryParse(value, out var parsed))
            throw new UpdateValidationException(
                $"The document path provided in the update expression is invalid for update: {fullPath.Display}");
        switch (parsed.TypeTag)
        {
            case AttributeValueTypes.Map:
            {
                var d = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                foreach (var p in parsed.Value.EnumerateObject()) d[p.Name] = p.Value;
                return d;
            }
            case AttributeValueTypes.List:
            {
                var list = new List<JsonElement>();
                foreach (var e in parsed.Value.EnumerateArray()) list.Add(e);
                return list;
            }
            default:
                throw new UpdateValidationException(
                    $"The document path provided in the update expression is invalid for update: {fullPath.Display}");
        }
    }

    private static void WriteLeaf(
        Dictionary<string, JsonElement> parent, PathSegment leaf, JsonElement value, DocumentPath fullPath)
    {
        if (leaf is AttributePathSegment a)
        {
            parent[a.Name] = value;
            return;
        }
        throw new UpdateValidationException(
            $"Cannot write list index leaf via parent dictionary at '{fullPath.Display}'.");
    }

    private static void RemoveLeaf(Dictionary<string, JsonElement> parent, PathSegment leaf)
    {
        if (leaf is AttributePathSegment a) parent.Remove(a.Name);
    }

    /// <summary>
    /// Deep-clones an item map by re-parsing each <see cref="JsonElement"/>
    /// so the clone is independent of any source <see cref="JsonDocument"/>.
    /// </summary>
    private static Dictionary<string, JsonElement> CloneItemMap(Dictionary<string, JsonElement> source)
    {
        var copy = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var kv in source) copy[kv.Key] = kv.Value.Clone();
        return copy;
    }

    // ----- overlap detection -----------------------------------------

    private static void DetectPathOverlap(UpdateExpressionAst ast)
    {
        // Collect canonical string form of every mutated path. DynamoDB
        // rejects any pair where one is a prefix of the other.
        var paths = new List<string>();
        void Add(DocumentPath p) => paths.Add(p.Display);

        if (ast.Set is { } set) foreach (var a in set.Actions) Add(a.Path);
        if (ast.Remove is { } rem) foreach (var p in rem.Paths) Add(p);
        if (ast.Add is { } add) foreach (var a in add.Actions) Add(a.Path);
        if (ast.Delete is { } del) foreach (var a in del.Actions) Add(a.Path);

        for (int i = 0; i < paths.Count; i++)
        {
            for (int j = i + 1; j < paths.Count; j++)
            {
                if (PathsOverlap(paths[i], paths[j]))
                    throw new UpdateValidationException(
                        $"Two document paths overlap with each other; must remove or rewrite one of these paths: [{paths[i]}, {paths[j]}]");
            }
        }
    }

    private static bool PathsOverlap(string a, string b)
    {
        if (a == b) return true;
        // Treat 'a' as overlapping 'b' if one is a prefix of the other
        // at a segment boundary ('.' or '[').
        var (shorter, longer) = a.Length < b.Length ? (a, b) : (b, a);
        if (!longer.StartsWith(shorter, StringComparison.Ordinal)) return false;
        var next = longer[shorter.Length];
        return next == '.' || next == '[';
    }

    // ----- arithmetic ------------------------------------------------

    private static JsonElement Arithmetic(ArithmeticOp op, JsonElement left, JsonElement right)
    {
        if (!ParsedAttributeValue.TryParse(left, out var lp) || lp.TypeTag != AttributeValueTypes.Number)
            throw new UpdateValidationException("Arithmetic operands must be N (numeric) attribute values.");
        if (!ParsedAttributeValue.TryParse(right, out var rp) || rp.TypeTag != AttributeValueTypes.Number)
            throw new UpdateValidationException("Arithmetic operands must be N (numeric) attribute values.");
        var l = lp.Value.GetString()!;
        var r = rp.Value.GetString()!;
        var text = op == ArithmeticOp.Add ? AddNumbers(l, r) : SubNumbers(l, r);
        return BuildNumber(text);
    }

    private static string AddNumbers(string a, string b)
    {
        // Use BigInteger when both are pure integers; otherwise fall
        // back to System.Decimal. DynamoDB's contract is 38 significant
        // digits, ±10^126 magnitude — Decimal covers the common case;
        // for the long tail we'd need a custom big-decimal but in this
        // slice we surface OverflowException as a ValidationException.
        if (TryInt(a, out var ai) && TryInt(b, out var bi))
            return (ai + bi).ToString(CultureInfo.InvariantCulture);
        try
        {
            var ad = decimal.Parse(a, NumberStyles.Float, CultureInfo.InvariantCulture);
            var bd = decimal.Parse(b, NumberStyles.Float, CultureInfo.InvariantCulture);
            return (ad + bd).ToString("0.###############################", CultureInfo.InvariantCulture);
        }
        catch (OverflowException)
        {
            throw new UpdateValidationException(
                "Arithmetic overflow: result exceeds the proxy's 28-digit precision in this slice.");
        }
    }

    private static string SubNumbers(string a, string b)
    {
        if (TryInt(a, out var ai) && TryInt(b, out var bi))
            return (ai - bi).ToString(CultureInfo.InvariantCulture);
        try
        {
            var ad = decimal.Parse(a, NumberStyles.Float, CultureInfo.InvariantCulture);
            var bd = decimal.Parse(b, NumberStyles.Float, CultureInfo.InvariantCulture);
            return (ad - bd).ToString("0.###############################", CultureInfo.InvariantCulture);
        }
        catch (OverflowException)
        {
            throw new UpdateValidationException(
                "Arithmetic overflow: result exceeds the proxy's 28-digit precision in this slice.");
        }
    }

    private static bool TryInt(string s, out BigInteger value)
        => BigInteger.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

    private static JsonElement BuildNumber(string text)
    {
        using var ms = new MemoryStream();
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

    // ----- list / set helpers ----------------------------------------

    private static JsonElement ListAppend(JsonElement left, JsonElement right)
    {
        if (!ParsedAttributeValue.TryParse(left, out var lp) || lp.TypeTag != AttributeValueTypes.List)
            throw new UpdateValidationException("list_append: first argument must be a list (L) attribute.");
        if (!ParsedAttributeValue.TryParse(right, out var rp) || rp.TypeTag != AttributeValueTypes.List)
            throw new UpdateValidationException("list_append: second argument must be a list (L) attribute.");
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WritePropertyName("L");
            w.WriteStartArray();
            foreach (var e in lp.Value.EnumerateArray()) e.WriteTo(w);
            foreach (var e in rp.Value.EnumerateArray()) e.WriteTo(w);
            w.WriteEndArray();
            w.WriteEndObject();
        }
        ms.Position = 0;
        using var doc = JsonDocument.Parse(ms);
        return doc.RootElement.Clone();
    }

    private static JsonElement SetUnion(string typeTag, JsonElement current, JsonElement addend)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WritePropertyName(typeTag);
            w.WriteStartArray();
            foreach (var e in current.EnumerateArray()) WriteIfUnseen(w, e, seen);
            foreach (var e in addend.EnumerateArray()) WriteIfUnseen(w, e, seen);
            w.WriteEndArray();
            w.WriteEndObject();
        }
        ms.Position = 0;
        using var doc = JsonDocument.Parse(ms);
        return doc.RootElement.Clone();
    }

    private static JsonElement? SetDifference(string typeTag, JsonElement current, JsonElement subtractor)
    {
        var toRemove = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in subtractor.EnumerateArray())
            if (e.ValueKind == JsonValueKind.String)
                toRemove.Add(e.GetString()!);

        var remaining = new List<JsonElement>();
        foreach (var e in current.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.String) { remaining.Add(e); continue; }
            if (!toRemove.Contains(e.GetString()!)) remaining.Add(e);
        }
        if (remaining.Count == 0) return null;

        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WritePropertyName(typeTag);
            w.WriteStartArray();
            foreach (var e in remaining) e.WriteTo(w);
            w.WriteEndArray();
            w.WriteEndObject();
        }
        ms.Position = 0;
        using var doc = JsonDocument.Parse(ms);
        return doc.RootElement.Clone();
    }

    private static void WriteIfUnseen(Utf8JsonWriter w, JsonElement e, HashSet<string> seen)
    {
        if (e.ValueKind != JsonValueKind.String) { e.WriteTo(w); return; }
        var s = e.GetString()!;
        if (seen.Add(s)) w.WriteStringValue(s);
    }

    // ----- read path -------------------------------------------------

    private static bool TryReadPath(Dictionary<string, JsonElement> root, DocumentPath path, out JsonElement value)
    {
        value = default;
        if (!root.TryGetValue(path.Root, out var current)) return false;
        if (path.Segments.Count == 1) { value = current; return true; }

        for (int i = 1; i < path.Segments.Count; i++)
        {
            if (!ParsedAttributeValue.TryParse(current, out var parsed)) return false;
            switch (path.Segments[i])
            {
                case AttributePathSegment a:
                    if (parsed.TypeTag != AttributeValueTypes.Map) return false;
                    if (!parsed.Value.TryGetProperty(a.Name, out var nextAttr)) return false;
                    current = nextAttr;
                    break;
                case IndexPathSegment idx:
                    if (parsed.TypeTag != AttributeValueTypes.List) return false;
                    var arr = parsed.Value;
                    if (idx.Index >= arr.GetArrayLength()) return false;
                    int k = 0;
                    JsonElement found = default;
                    foreach (var e in arr.EnumerateArray()) { if (k++ == idx.Index) { found = e; break; } }
                    current = found;
                    break;
            }
        }
        value = current;
        return true;
    }
}

/// <summary>
/// Validation failure surfaced by the parser or executor. The handler
/// converts this to a DynamoDB <c>ValidationException</c> 400.
/// </summary>
internal sealed class UpdateValidationException : Exception
{
    public UpdateValidationException(string message) : base(message) { }
}
