using System.Collections.Generic;
using System.Text.Json;

namespace Aws2Azure.Modules.DynamoDb.Expressions;

/// <summary>
/// AST for the DynamoDB UpdateExpression grammar. Clauses are
/// optional but at least one must be present. Within a clause, actions
/// are evaluated in order; the executor enforces the "no two paths
/// overlap" rule across clauses.
/// </summary>
internal sealed record UpdateExpressionAst(
    SetClause? Set,
    RemoveClause? Remove,
    AddClause? Add,
    DeleteClause? Delete);

internal sealed record SetClause(IReadOnlyList<SetAction> Actions);
internal sealed record SetAction(DocumentPath Path, ValueOperand Value);

internal sealed record RemoveClause(IReadOnlyList<DocumentPath> Paths);

internal sealed record AddClause(IReadOnlyList<AddAction> Actions);
internal sealed record AddAction(DocumentPath Path, ValueRefOperand Value);

internal sealed record DeleteClause(IReadOnlyList<DeleteAction> Actions);
internal sealed record DeleteAction(DocumentPath Path, ValueRefOperand Value);

/// <summary>
/// A document path is a non-empty sequence of segments:
/// <c>name</c>, <c>name.sub</c>, <c>name[0]</c>, <c>name[0].sub</c>,
/// <c>#alias.sub</c>, etc. After parsing all <c>#name</c> aliases are
/// expanded so segments only carry resolved attribute names. The first
/// segment is always an <see cref="AttributePathSegment"/>; subsequent
/// segments may be either attribute or index.
/// </summary>
internal sealed record DocumentPath(IReadOnlyList<PathSegment> Segments)
{
    /// <summary>True if the path is a single top-level attribute (no
    /// dots or indices).</summary>
    public bool IsTopLevel => Segments.Count == 1 && Segments[0] is AttributePathSegment;

    /// <summary>The top-level (root) attribute name. Always present.</summary>
    public string Root => ((AttributePathSegment)Segments[0]).Name;

    /// <summary>Human-readable form used in error messages.</summary>
    public string Display
    {
        get
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < Segments.Count; i++)
            {
                switch (Segments[i])
                {
                    case AttributePathSegment a:
                        if (i > 0) sb.Append('.');
                        sb.Append(a.Name);
                        break;
                    case IndexPathSegment idx:
                        sb.Append('[').Append(idx.Index).Append(']');
                        break;
                }
            }
            return sb.ToString();
        }
    }
}

internal abstract record PathSegment;
internal sealed record AttributePathSegment(string Name) : PathSegment;
internal sealed record IndexPathSegment(int Index) : PathSegment;

/// <summary>
/// Right-hand-side operand of a SET assignment. Either a literal value
/// reference (<c>:v</c>), a path lookup, a function call
/// (<c>if_not_exists</c> / <c>list_append</c>) or an arithmetic
/// expression (<c>+</c>/<c>-</c>).
/// </summary>
internal abstract record ValueOperand;

internal sealed record ValueRefOperand(string Placeholder, JsonElement Value) : ValueOperand;
internal sealed record PathOperand(DocumentPath Path) : ValueOperand;
internal sealed record ArithmeticOperand(ArithmeticOp Op, ValueOperand Left, ValueOperand Right) : ValueOperand;
internal sealed record IfNotExistsOperand(DocumentPath Path, ValueOperand Fallback) : ValueOperand;
internal sealed record ListAppendOperand(ValueOperand Left, ValueOperand Right) : ValueOperand;

internal enum ArithmeticOp { Add, Subtract }
