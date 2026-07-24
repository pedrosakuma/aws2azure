using System;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Aws2Azure.Core.Buffers;
using Aws2Azure.Modules.DynamoDb.Expressions;
using Aws2Azure.Modules.DynamoDb.Persistence;

namespace Aws2Azure.Modules.DynamoDb.Internal;

/// <summary>
/// Serializes the C# condition/update AST into JSON that the atomicWrite sproc can interpret.
/// The JS sproc evaluates conditions and applies updates using these serialized ASTs.
/// </summary>
internal static class SprocAstSerializer
{
    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Serializes a ConditionNode tree to JSON for the sproc condition evaluator.
    /// Returns null if no condition is present.
    /// </summary>
    public static string? SerializeCondition(ConditionNode? node)
    {
        if (node is null) return null;
        using var buffer = new PooledByteBufferWriter(256);
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            WriteCondition(writer, node);
            writer.Flush();
        }
        return Encoding.UTF8.GetString(buffer.WrittenMemory.Span);
    }

    /// <summary>
    /// Serializes an UpdateExpressionAst to JSON for the sproc update executor.
    /// Returns null if no updates are present.
    /// </summary>
    public static string? SerializeUpdate(UpdateExpressionAst? ast)
    {
        if (ast is null) return null;
        using var buffer = new PooledByteBufferWriter(256);
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();

            if (ast.Set is { Actions.Count: > 0 })
            {
                writer.WriteStartArray("set");
                for (int i = 0; i < ast.Set.Actions.Count; i++)
                {
                    WriteSetAction(writer, ast.Set.Actions[i]);
                }
                writer.WriteEndArray();
            }

            if (ast.Remove is { Paths.Count: > 0 })
            {
                writer.WriteStartArray("remove");
                for (int i = 0; i < ast.Remove.Paths.Count; i++)
                {
                    writer.WriteStringValue(PathToString(ast.Remove.Paths[i]));
                }
                writer.WriteEndArray();
            }

            if (ast.Add is { Actions.Count: > 0 })
            {
                writer.WriteStartArray("add");
                for (int i = 0; i < ast.Add.Actions.Count; i++)
                {
                    WriteAddAction(writer, ast.Add.Actions[i]);
                }
                writer.WriteEndArray();
            }

            if (ast.Delete is { Actions.Count: > 0 })
            {
                writer.WriteStartArray("delete");
                for (int i = 0; i < ast.Delete.Actions.Count; i++)
                {
                    WriteDeleteAction(writer, ast.Delete.Actions[i]);
                }
                writer.WriteEndArray();
            }

            writer.WriteEndObject();
            writer.Flush();
        }
        return Encoding.UTF8.GetString(buffer.WrittenMemory.Span);
    }

    private static void WriteCondition(Utf8JsonWriter writer, ConditionNode node)
    {
        switch (node)
        {
            case AndCondition and:
                writer.WriteStartObject();
                writer.WriteString("type", "AND");
                writer.WritePropertyName("left");
                WriteCondition(writer, and.Left);
                writer.WritePropertyName("right");
                WriteCondition(writer, and.Right);
                writer.WriteEndObject();
                break;

            case OrCondition or:
                writer.WriteStartObject();
                writer.WriteString("type", "OR");
                writer.WritePropertyName("left");
                WriteCondition(writer, or.Left);
                writer.WritePropertyName("right");
                WriteCondition(writer, or.Right);
                writer.WriteEndObject();
                break;

            case NotCondition not:
                writer.WriteStartObject();
                writer.WriteString("type", "NOT");
                writer.WritePropertyName("operand");
                WriteCondition(writer, not.Inner);
                writer.WriteEndObject();
                break;

            case AttributeExistsCondition ae:
                writer.WriteStartObject();
                writer.WriteString("type", "ATTR_EXISTS");
                writer.WriteString("attr", PathToString(ae.Path));
                writer.WriteEndObject();
                break;

            case AttributeNotExistsCondition ane:
                writer.WriteStartObject();
                writer.WriteString("type", "ATTR_NOT_EXISTS");
                writer.WriteString("attr", PathToString(ane.Path));
                writer.WriteEndObject();
                break;

            case AttributeTypeCondition at:
                writer.WriteStartObject();
                writer.WriteString("type", "ATTR_TYPE");
                writer.WriteString("attr", PathToString(at.Path));
                writer.WritePropertyName("attrType");
                WriteValue(writer, at.TypeTag.Value);
                writer.WriteEndObject();
                break;

            case BeginsWithCondition bw:
                writer.WriteStartObject();
                writer.WriteString("type", "BEGINS_WITH");
                writer.WritePropertyName("attr");
                WriteOperand(writer, bw.Path);
                writer.WritePropertyName("prefix");
                WriteOperand(writer, bw.Prefix);
                writer.WriteEndObject();
                break;

            case ContainsCondition c:
                writer.WriteStartObject();
                writer.WriteString("type", "CONTAINS");
                writer.WritePropertyName("attr");
                WriteOperand(writer, c.Container);
                writer.WritePropertyName("value");
                WriteOperand(writer, c.Item);
                writer.WriteEndObject();
                break;

            case CompareCondition cc:
                writer.WriteStartObject();
                writer.WriteString("type", "COMPARE");
                writer.WritePropertyName("attr");
                WriteOperand(writer, cc.Left);
                writer.WriteString("op", OpToString(cc.Op));
                writer.WritePropertyName("value");
                WriteOperand(writer, cc.Right);
                writer.WriteEndObject();
                break;

            case BetweenCondition bt:
                writer.WriteStartObject();
                writer.WriteString("type", "BETWEEN");
                writer.WritePropertyName("value");
                WriteOperand(writer, bt.Value);
                writer.WritePropertyName("low");
                WriteOperand(writer, bt.Lower);
                writer.WritePropertyName("high");
                WriteOperand(writer, bt.Upper);
                writer.WriteEndObject();
                break;

            case InCondition inn:
                writer.WriteStartObject();
                writer.WriteString("type", "IN");
                writer.WritePropertyName("attr");
                WriteOperand(writer, inn.Value);
                writer.WriteStartArray("values");
                for (int i = 0; i < inn.Set.Count; i++)
                {
                    WriteOperand(writer, inn.Set[i]);
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
                break;

            default:
                throw new NotSupportedException(
                    $"Unsupported condition AST node '{node.GetType().Name}'.");
        }
    }

    private static void WriteOperand(Utf8JsonWriter writer, ConditionOperand operand)
    {
        switch (operand)
        {
            case ConditionPathOperand cp:
                writer.WriteStartObject();
                writer.WriteString("path", PathToString(cp.Path));
                writer.WriteEndObject();
                break;
            case ConditionValueOperand cv:
                WriteValue(writer, cv.Value.Value);
                break;
            case SizeOperand sz:
                writer.WriteStartObject();
                writer.WriteString("size", PathToString(sz.Path));
                writer.WriteEndObject();
                break;
            default:
                throw new NotSupportedException(
                    $"Unsupported condition operand '{operand.GetType().Name}'.");
        }
    }

    private static void WriteSetAction(Utf8JsonWriter writer, SetAction action)
    {
        writer.WriteStartObject();
        writer.WriteString("path", PathToString(action.Path));
        writer.WritePropertyName("value");
        WriteValueOperand(writer, action.Value);
        writer.WriteEndObject();
    }

    private static void WriteAddAction(Utf8JsonWriter writer, AddAction action)
    {
        writer.WriteStartObject();
        writer.WriteString("path", PathToString(action.Path));
        writer.WritePropertyName("value");
        WriteValue(writer, action.Value.Value);
        writer.WriteEndObject();
    }

    private static void WriteDeleteAction(Utf8JsonWriter writer, DeleteAction action)
    {
        writer.WriteStartObject();
        writer.WriteString("path", PathToString(action.Path));
        writer.WritePropertyName("value");
        WriteValue(writer, action.Value.Value);
        writer.WriteEndObject();
    }

    private static void WriteValueOperand(Utf8JsonWriter writer, ValueOperand operand)
    {
        // SET-value operands are tagged with a "$k" discriminator so the sproc's
        // resolveSetValue can interpret them unambiguously. Literal values are
        // wrapped (even maps/lists), so a user attribute that happens to look
        // like an operand can never be misread (#202).
        switch (operand)
        {
            case ValueRefOperand vr:
                writer.WriteStartObject();
                writer.WriteString("$k", "lit");
                writer.WritePropertyName("v");
                WriteValue(writer, vr.Value);
                writer.WriteEndObject();
                break;
            case PathOperand po:
                writer.WriteStartObject();
                writer.WriteString("$k", "path");
                writer.WriteString("p", PathToString(po.Path));
                writer.WriteEndObject();
                break;
            case ArithmeticOperand ao:
                writer.WriteStartObject();
                writer.WriteString("$k", "op");
                writer.WriteString("o", ao.Op == ArithmeticOp.Add ? "+" : "-");
                writer.WritePropertyName("l");
                WriteValueOperand(writer, ao.Left);
                writer.WritePropertyName("r");
                WriteValueOperand(writer, ao.Right);
                writer.WriteEndObject();
                break;
            case IfNotExistsOperand ine:
                writer.WriteStartObject();
                writer.WriteString("$k", "ifne");
                writer.WriteString("p", PathToString(ine.Path));
                writer.WritePropertyName("f");
                WriteValueOperand(writer, ine.Fallback);
                writer.WriteEndObject();
                break;
            case ListAppendOperand la:
                writer.WriteStartObject();
                writer.WriteString("$k", "lap");
                writer.WritePropertyName("l");
                WriteValueOperand(writer, la.Left);
                writer.WritePropertyName("r");
                WriteValueOperand(writer, la.Right);
                writer.WriteEndObject();
                break;
            default:
                throw new NotSupportedException(
                    $"Unsupported update operand '{operand.GetType().Name}'.");
        }
    }

    private static void WriteValue(Utf8JsonWriter writer, JsonElement value)
    {
        // Convert DynamoDB AttributeValue to inferred (native) format
        // DynamoDB format: {"S": "hello"}, {"N": "123"}, {"BOOL": true}, etc.
        // Inferred format: "hello", 123, true, etc.
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in value.EnumerateObject())
            {
                switch (prop.Name)
                {
                    case "S":
                        writer.WriteStringValue(prop.Value.GetString() ?? "");
                        return;
                    case "N":
                        if (!InferredAttributeStorage.TryGetCanonicalBareJsonNumber(
                                prop.Value.GetString(),
                                out var canonicalNumber))
                        {
                            throw new NotSupportedException(
                                "Stored-procedure operands cannot contain enveloped DynamoDB numbers.");
                        }
                        writer.WriteRawValue(canonicalNumber, skipInputValidation: true);
                        return;
                    case "BOOL":
                        writer.WriteBooleanValue(prop.Value.GetBoolean());
                        return;
                    case "NULL":
                        writer.WriteNullValue();
                        return;
                    case "B":
                        writer.WriteStartObject();
                        writer.WriteString("_a2a:B", prop.Value.GetString() ?? "");
                        writer.WriteEndObject();
                        return;
                    case "M":
                        WriteMapValue(writer, prop.Value);
                        return;
                    case "L":
                        WriteListValue(writer, prop.Value);
                        return;
                    case "SS":
                        writer.WriteStartObject();
                        writer.WritePropertyName("_a2a:SS");
                        WriteStringArray(writer, prop.Value);
                        writer.WriteEndObject();
                        return;
                    case "NS":
                        writer.WriteStartObject();
                        writer.WritePropertyName("_a2a:NS");
                        WriteStringArray(writer, prop.Value);
                        writer.WriteEndObject();
                        return;
                    case "BS":
                        writer.WriteStartObject();
                        writer.WritePropertyName("_a2a:BS");
                        WriteStringArray(writer, prop.Value);
                        writer.WriteEndObject();
                        return;
                }
            }
        }
        value.WriteTo(writer);
    }

    private static void WriteMapValue(Utf8JsonWriter writer, JsonElement map)
    {
        writer.WriteStartObject();
        foreach (var prop in map.EnumerateObject())
        {
            writer.WritePropertyName(prop.Name);
            WriteValue(writer, prop.Value);
        }
        writer.WriteEndObject();
    }

    private static void WriteListValue(Utf8JsonWriter writer, JsonElement list)
    {
        writer.WriteStartArray();
        foreach (var item in list.EnumerateArray())
        {
            WriteValue(writer, item);
        }
        writer.WriteEndArray();
    }

    private static void WriteStringArray(Utf8JsonWriter writer, JsonElement arr)
    {
        writer.WriteStartArray();
        foreach (var item in arr.EnumerateArray())
        {
            writer.WriteStringValue(item.GetString() ?? "");
        }
        writer.WriteEndArray();
    }

    private static string PathToString(DocumentPath path)
    {
        var sb = new StringBuilder();
        foreach (var seg in path.Segments)
        {
            switch (seg)
            {
                case AttributePathSegment a:
                    if (sb.Length > 0) sb.Append('.');
                    sb.Append(a.Name);
                    break;
                case IndexPathSegment i:
                    sb.Append('[').Append(i.Index).Append(']');
                    break;
            }
        }
        return sb.ToString();
    }

    private static string OpToString(CompareOp op) => op switch
    {
        CompareOp.Equal => "=",
        CompareOp.NotEqual => "<>",
        CompareOp.Less => "<",
        CompareOp.LessEqual => "<=",
        CompareOp.Greater => ">",
        CompareOp.GreaterEqual => ">=",
        _ => "="
    };

}
