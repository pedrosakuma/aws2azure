using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using Aws2Azure.Modules.DynamoDb.Expressions;

namespace Aws2Azure.Modules.DynamoDb.Internal;

/// <summary>
/// Serializes the C# condition/update AST into JSON that the atomicWrite sproc can interpret.
/// The JS sproc evaluates conditions and applies updates using these serialized ASTs.
/// </summary>
internal static class SprocAstSerializer
{
    /// <summary>
    /// Serializes a ConditionNode tree to JSON for the sproc condition evaluator.
    /// Returns null if no condition is present.
    /// </summary>
    public static string? SerializeCondition(ConditionNode? node)
    {
        if (node is null) return null;
        var sb = new StringBuilder(256);
        WriteCondition(sb, node);
        return sb.ToString();
    }

    /// <summary>
    /// Serializes an UpdateExpressionAst to JSON for the sproc update executor.
    /// Returns null if no updates are present.
    /// </summary>
    public static string? SerializeUpdate(UpdateExpressionAst? ast)
    {
        if (ast is null) return null;
        var sb = new StringBuilder(256);
        sb.Append('{');
        var first = true;

        if (ast.Set is { Actions.Count: > 0 })
        {
            first = false;
            sb.Append("\"set\":[");
            for (int i = 0; i < ast.Set.Actions.Count; i++)
            {
                if (i > 0) sb.Append(',');
                WriteSetAction(sb, ast.Set.Actions[i]);
            }
            sb.Append(']');
        }

        if (ast.Remove is { Paths.Count: > 0 })
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append("\"remove\":[");
            for (int i = 0; i < ast.Remove.Paths.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append('"').Append(EscapeJson(PathToString(ast.Remove.Paths[i]))).Append('"');
            }
            sb.Append(']');
        }

        if (ast.Add is { Actions.Count: > 0 })
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append("\"add\":[");
            for (int i = 0; i < ast.Add.Actions.Count; i++)
            {
                if (i > 0) sb.Append(',');
                WriteAddAction(sb, ast.Add.Actions[i]);
            }
            sb.Append(']');
        }

        if (ast.Delete is { Actions.Count: > 0 })
        {
            if (!first) sb.Append(',');
            sb.Append("\"delete\":[");
            for (int i = 0; i < ast.Delete.Actions.Count; i++)
            {
                if (i > 0) sb.Append(',');
                WriteDeleteAction(sb, ast.Delete.Actions[i]);
            }
            sb.Append(']');
        }

        sb.Append('}');
        return sb.ToString();
    }

    private static void WriteCondition(StringBuilder sb, ConditionNode node)
    {
        switch (node)
        {
            case AndCondition and:
                sb.Append("{\"type\":\"AND\",\"left\":");
                WriteCondition(sb, and.Left);
                sb.Append(",\"right\":");
                WriteCondition(sb, and.Right);
                sb.Append('}');
                break;

            case OrCondition or:
                sb.Append("{\"type\":\"OR\",\"left\":");
                WriteCondition(sb, or.Left);
                sb.Append(",\"right\":");
                WriteCondition(sb, or.Right);
                sb.Append('}');
                break;

            case NotCondition not:
                sb.Append("{\"type\":\"NOT\",\"operand\":");
                WriteCondition(sb, not.Inner);
                sb.Append('}');
                break;

            case AttributeExistsCondition ae:
                sb.Append("{\"type\":\"ATTR_EXISTS\",\"attr\":\"")
                  .Append(EscapeJson(PathToString(ae.Path)))
                  .Append("\"}");
                break;

            case AttributeNotExistsCondition ane:
                sb.Append("{\"type\":\"ATTR_NOT_EXISTS\",\"attr\":\"")
                  .Append(EscapeJson(PathToString(ane.Path)))
                  .Append("\"}");
                break;

            case AttributeTypeCondition at:
                sb.Append("{\"type\":\"ATTR_TYPE\",\"attr\":\"")
                  .Append(EscapeJson(PathToString(at.Path)))
                  .Append("\",\"attrType\":");
                WriteValue(sb, at.TypeTag.Value);
                sb.Append('}');
                break;

            case BeginsWithCondition bw:
                sb.Append("{\"type\":\"BEGINS_WITH\",\"attr\":");
                WriteOperand(sb, bw.Path);
                sb.Append(",\"prefix\":");
                WriteOperand(sb, bw.Prefix);
                sb.Append('}');
                break;

            case ContainsCondition c:
                sb.Append("{\"type\":\"CONTAINS\",\"attr\":");
                WriteOperand(sb, c.Container);
                sb.Append(",\"value\":");
                WriteOperand(sb, c.Item);
                sb.Append('}');
                break;

            case CompareCondition cc:
                sb.Append("{\"type\":\"COMPARE\",\"attr\":");
                WriteOperand(sb, cc.Left);
                sb.Append(",\"op\":\"").Append(OpToString(cc.Op)).Append("\",\"value\":");
                WriteOperand(sb, cc.Right);
                sb.Append('}');
                break;

            case BetweenCondition bt:
                sb.Append("{\"type\":\"BETWEEN\",\"value\":");
                WriteOperand(sb, bt.Value);
                sb.Append(",\"low\":");
                WriteOperand(sb, bt.Lower);
                sb.Append(",\"high\":");
                WriteOperand(sb, bt.Upper);
                sb.Append('}');
                break;

            case InCondition inn:
                sb.Append("{\"type\":\"IN\",\"attr\":");
                WriteOperand(sb, inn.Value);
                sb.Append(",\"values\":[");
                for (int i = 0; i < inn.Set.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    WriteOperand(sb, inn.Set[i]);
                }
                sb.Append("]}");
                break;

            default:
                sb.Append("{\"type\":\"TRUE\"}"); // fallback - pass through
                break;
        }
    }

    private static void WriteOperand(StringBuilder sb, ConditionOperand operand)
    {
        switch (operand)
        {
            case ConditionPathOperand cp:
                sb.Append("{\"path\":\"").Append(EscapeJson(PathToString(cp.Path))).Append("\"}");
                break;
            case ConditionValueOperand cv:
                WriteValue(sb, cv.Value.Value);
                break;
            case SizeOperand sz:
                sb.Append("{\"size\":\"").Append(EscapeJson(PathToString(sz.Path))).Append("\"}");
                break;
            default:
                sb.Append("null");
                break;
        }
    }

    private static void WriteSetAction(StringBuilder sb, SetAction action)
    {
        sb.Append("{\"path\":\"").Append(EscapeJson(PathToString(action.Path))).Append("\",\"value\":");
        WriteValueOperand(sb, action.Value);
        sb.Append('}');
    }

    private static void WriteAddAction(StringBuilder sb, AddAction action)
    {
        sb.Append("{\"path\":\"").Append(EscapeJson(PathToString(action.Path))).Append("\",\"value\":");
        WriteValue(sb, action.Value.Value);
        sb.Append('}');
    }

    private static void WriteDeleteAction(StringBuilder sb, DeleteAction action)
    {
        sb.Append("{\"path\":\"").Append(EscapeJson(PathToString(action.Path))).Append("\",\"value\":");
        WriteValue(sb, action.Value.Value);
        sb.Append('}');
    }

    private static void WriteValueOperand(StringBuilder sb, ValueOperand operand)
    {
        switch (operand)
        {
            case ValueRefOperand vr:
                WriteValue(sb, vr.Value);
                break;
            case PathOperand po:
                sb.Append("{\"path\":\"").Append(EscapeJson(PathToString(po.Path))).Append("\"}");
                break;
            case ArithmeticOperand ao:
                sb.Append("{\"op\":\"").Append(ao.Op == ArithmeticOp.Add ? "+" : "-").Append("\",\"left\":");
                WriteValueOperand(sb, ao.Left);
                sb.Append(",\"right\":");
                WriteValueOperand(sb, ao.Right);
                sb.Append('}');
                break;
            case IfNotExistsOperand ine:
                sb.Append("{\"ifNotExists\":{\"path\":\"").Append(EscapeJson(PathToString(ine.Path))).Append("\",\"fallback\":");
                WriteValueOperand(sb, ine.Fallback);
                sb.Append("}}");
                break;
            case ListAppendOperand la:
                sb.Append("{\"listAppend\":{\"left\":");
                WriteValueOperand(sb, la.Left);
                sb.Append(",\"right\":");
                WriteValueOperand(sb, la.Right);
                sb.Append("}}");
                break;
            default:
                sb.Append("null");
                break;
        }
    }

    private static void WriteValue(StringBuilder sb, JsonElement value)
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
                        sb.Append('"').Append(EscapeJson(prop.Value.GetString() ?? "")).Append('"');
                        return;
                    case "N":
                        // Numbers are stored as strings in DDB; output as JSON number if possible
                        var numStr = prop.Value.GetString() ?? "0";
                        sb.Append(numStr);
                        return;
                    case "BOOL":
                        sb.Append(prop.Value.GetBoolean() ? "true" : "false");
                        return;
                    case "NULL":
                        sb.Append("null");
                        return;
                    case "B":
                        // Binary as envelope
                        sb.Append("{\"_a2a:B\":\"").Append(EscapeJson(prop.Value.GetString() ?? "")).Append("\"}");
                        return;
                    case "M":
                        // Map: recurse
                        WriteMapValue(sb, prop.Value);
                        return;
                    case "L":
                        // List: recurse
                        WriteListValue(sb, prop.Value);
                        return;
                    case "SS":
                        // String set as envelope
                        sb.Append("{\"_a2a:SS\":");
                        WriteStringArray(sb, prop.Value);
                        sb.Append('}');
                        return;
                    case "NS":
                        // Number set as envelope
                        sb.Append("{\"_a2a:NS\":");
                        WriteStringArray(sb, prop.Value);
                        sb.Append('}');
                        return;
                    case "BS":
                        // Binary set as envelope
                        sb.Append("{\"_a2a:BS\":");
                        WriteStringArray(sb, prop.Value);
                        sb.Append('}');
                        return;
                }
            }
        }
        // Fallback: write raw JSON
        sb.Append(value.GetRawText());
    }

    private static void WriteMapValue(StringBuilder sb, JsonElement map)
    {
        sb.Append('{');
        var first = true;
        foreach (var prop in map.EnumerateObject())
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append('"').Append(EscapeJson(prop.Name)).Append("\":");
            WriteValue(sb, prop.Value);
        }
        sb.Append('}');
    }

    private static void WriteListValue(StringBuilder sb, JsonElement list)
    {
        sb.Append('[');
        var first = true;
        foreach (var item in list.EnumerateArray())
        {
            if (!first) sb.Append(',');
            first = false;
            WriteValue(sb, item);
        }
        sb.Append(']');
    }

    private static void WriteStringArray(StringBuilder sb, JsonElement arr)
    {
        sb.Append('[');
        var first = true;
        foreach (var item in arr.EnumerateArray())
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append('"').Append(EscapeJson(item.GetString() ?? "")).Append('"');
        }
        sb.Append(']');
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

    private static string EscapeJson(string s) =>
        s.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
