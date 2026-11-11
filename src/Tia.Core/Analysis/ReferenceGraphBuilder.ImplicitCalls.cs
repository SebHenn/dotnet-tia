using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Tia.Core.Model;

namespace Tia.Core.Analysis;

// Calls the source never names: Dispose from a using, ToString from formatting and concatenation.
public sealed partial class ReferenceGraphBuilder
{
    /// <summary>
    /// The <c>Dispose</c> a <c>using</c> will call. Nothing in <c>using var x = new Foo();</c>
    /// names it, and a broken <c>Dispose</c> is exactly the kind of thing a test notices.
    /// </summary>
    private void AddDisposalReference(
        ImpactGraph graph,
        SemanticModel model,
        ExpressionSyntax? typeOrExpression,
        string source,
        CancellationToken cancellationToken)
    {
        if (typeOrExpression is null || model.GetTypeInfo(typeOrExpression, cancellationToken).Type is not { } type)
        {
            return;
        }

        foreach (var member in type.GetMembers())
        {
            if (member is IMethodSymbol { Parameters.Length: 0, Name: "Dispose" or "DisposeAsync" })
            {
                AddReference(graph, member, source, EdgeKind.Reference);
            }
        }
    }

    /// <summary>
    /// Connects a formatted value to the <c>ToString</c> it will actually call.
    /// </summary>
    /// <remarks>
    /// Nothing in <c>$"{instant}"</c> names <c>Instant.ToString</c>: the interpolation binds to the
    /// handler's <c>AppendFormatted</c>, and the call to the value's own formatting happens inside
    /// it. So a method whose entire body is an interpolated string had no edge to the types it
    /// formats. The NodaTime gate found it - <c>ZoneInterval.ToString</c> formats an
    /// <c>Instant</c>, breaking the Instant pattern parser broke it, and nothing connected the two.
    /// The type is known statically here even though the call is not, which is what makes this
    /// exact rather than a widening.
    /// </remarks>
    private void AddFormattingReference(
        ImpactGraph graph,
        SemanticModel model,
        ExpressionSyntax expression,
        string source,
        CancellationToken cancellationToken)
    {
        if (model.GetTypeInfo(expression, cancellationToken).Type is not { } type)
        {
            return;
        }

        foreach (var member in type.GetMembers("ToString"))
        {
            if (member is IMethodSymbol { Parameters.Length: 0 } or IMethodSymbol { Parameters.Length: 2 })
            {
                AddReference(graph, member, source, EdgeKind.Reference);
            }
        }
    }

    /// <summary>
    /// Records a formatted value as handed to code outside the solution, which is what formatting
    /// is: the interpolation handler or <c>string.Concat</c> calls the value's <c>ToString</c>, or
    /// <c>TryFormat</c> in preference to it (#120).
    /// </summary>
    /// <remarks>
    /// The edge in <see cref="AddFormattingReference"/> reaches a <c>ToString</c> declared on the
    /// static type, and nothing else: not one it inherits from a base class, not an override in the
    /// class behind an interface-typed value, not <c>TryFormat</c>. The type's dispatch hub already
    /// holds all of those and is linked along the hierarchy, so a site of it reaches them. A string
    /// operand is not formatted and adds nothing.
    /// </remarks>
    private void AddFormattedValueSite(
        ImpactGraph graph,
        SemanticModel model,
        ExpressionSyntax expression,
        string memberKey,
        CancellationToken cancellationToken)
    {
        if (model.GetTypeInfo(expression, cancellationToken).Type is { SpecialType: not SpecialType.System_String } type)
        {
            AddDispatchSite(graph, type, memberKey);
        }
    }
}
