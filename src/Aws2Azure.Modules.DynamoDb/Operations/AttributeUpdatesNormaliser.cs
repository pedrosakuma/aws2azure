using System.Collections.Generic;
using System.Text.Json;
using Aws2Azure.Modules.DynamoDb.Expressions;

namespace Aws2Azure.Modules.DynamoDb.Operations;

/// <summary>
/// Converts the legacy <c>AttributeUpdates</c> request shape into the
/// same <see cref="UpdateExpressionAst"/> the modern UpdateExpression
/// path produces, so the executor stays uniform. AttributeUpdates only
/// supports top-level paths, no functions, no arithmetic; we therefore
/// produce flat AST nodes with a synthetic <see cref="ValueRefOperand"/>
/// for each value (the placeholder name is internal and never seen by
/// callers).
/// </summary>
internal static class AttributeUpdatesNormaliser
{
    public static UpdateExpressionAst Build(JsonElement attributeUpdates)
    {
        if (attributeUpdates.ValueKind != JsonValueKind.Object)
            throw new UpdateValidationException(
                "AttributeUpdates must be a JSON object mapping attribute names to update specs.");

        var setActions = new List<SetAction>();
        var removePaths = new List<DocumentPath>();
        var addActions = new List<AddAction>();
        var deleteActions = new List<DeleteAction>();

        int counter = 0;
        foreach (var prop in attributeUpdates.EnumerateObject())
        {
            var attrName = prop.Name;
            var spec = prop.Value;
            if (spec.ValueKind != JsonValueKind.Object)
                throw new UpdateValidationException(
                    $"AttributeUpdates['{attrName}'] must be a JSON object with an Action.");

            string action = "PUT";
            if (spec.TryGetProperty("Action", out var actionEl))
            {
                if (actionEl.ValueKind != JsonValueKind.String)
                    throw new UpdateValidationException(
                        $"AttributeUpdates['{attrName}'].Action must be a string.");
                action = actionEl.GetString() ?? "PUT";
            }

            var path = new DocumentPath(new PathSegment[] { new AttributePathSegment(attrName) });

            switch (action)
            {
                case "PUT":
                {
                    if (!spec.TryGetProperty("Value", out var value))
                        throw new UpdateValidationException(
                            $"AttributeUpdates['{attrName}'] PUT requires a Value.");
                    if (!ParsedAttributeValue.TryParse(value, out _))
                        throw new UpdateValidationException(
                            $"AttributeUpdates['{attrName}'].Value must be a single-property typed attribute value.");
                    setActions.Add(new SetAction(path,
                        new ValueRefOperand($":__au{counter++}", value)));
                    break;
                }
                case "DELETE":
                {
                    if (!spec.TryGetProperty("Value", out var value))
                    {
                        removePaths.Add(path);
                        break;
                    }
                    if (!ParsedAttributeValue.TryParse(value, out var parsed))
                        throw new UpdateValidationException(
                            $"AttributeUpdates['{attrName}'].Value must be a single-property typed attribute value.");
                    if (parsed.TypeTag is AttributeValueTypes.StringSet
                        or AttributeValueTypes.NumberSet
                        or AttributeValueTypes.BinarySet)
                    {
                        deleteActions.Add(new DeleteAction(path,
                            new ValueRefOperand($":__au{counter++}", value)));
                    }
                    else
                    {
                        // Legacy AttributeUpdates only permits DELETE
                        // with no Value (remove attribute) or with a
                        // set-typed Value (subtract members). A scalar
                        // Value is invalid; reject explicitly rather
                        // than silently wiping the attribute.
                        throw new UpdateValidationException(
                            $"AttributeUpdates['{attrName}'].Value for DELETE must be a set (SS/NS/BS); scalar values are not allowed.");
                    }
                    break;
                }
                case "ADD":
                {
                    if (!spec.TryGetProperty("Value", out var value))
                        throw new UpdateValidationException(
                            $"AttributeUpdates['{attrName}'] ADD requires a Value.");
                    if (!ParsedAttributeValue.TryParse(value, out _))
                        throw new UpdateValidationException(
                            $"AttributeUpdates['{attrName}'].Value must be a single-property typed attribute value.");
                    addActions.Add(new AddAction(path,
                        new ValueRefOperand($":__au{counter++}", value)));
                    break;
                }
                default:
                    throw new UpdateValidationException(
                        $"AttributeUpdates['{attrName}'].Action '{action}' is not a recognised action.");
            }
        }

        return new UpdateExpressionAst(
            setActions.Count == 0 ? null : new SetClause(setActions),
            removePaths.Count == 0 ? null : new RemoveClause(removePaths),
            addActions.Count == 0 ? null : new AddClause(addActions),
            deleteActions.Count == 0 ? null : new DeleteClause(deleteActions));
    }
}
