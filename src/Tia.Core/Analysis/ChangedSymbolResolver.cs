using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Tia.Core.Diff;
using Tia.Core.Model;

namespace Tia.Core.Analysis;

/// <summary>
/// Maps changed line ranges in a document onto the symbols they declare.
/// </summary>
/// <remarks>
/// The rules that are easy to get wrong live here, and every one of them is a correctness rule
/// rather than a refinement: constants are inlined by callers, partial types span files, generics
/// have to be reduced to their original definition, attribute edits belong to the annotated symbol
/// and not to the attribute class, and a change to a type header is a change to every member.
/// </remarks>
public sealed class ChangedSymbolResolver
{
    /// <summary>Maps the changed lines of one document onto symbols.</summary>
    public SymbolChangeSet Resolve(
        SemanticModel model,
        IReadOnlyList<LineRange> changedLines,
        string projectName,
        CancellationToken cancellationToken = default)
    {
        var result = new SymbolChangeSet();
        if (changedLines.Count == 0)
        {
            return result;
        }

        var tree = model.SyntaxTree;
        var root = tree.GetRoot(cancellationToken);
        var text = tree.GetText(cancellationToken);
        var lineCount = text.Lines.Count;

        var members = new List<DeclarationSpan>();
        var types = new List<DeclarationSpan>();
        CollectDeclarations(root, tree, members, types, cancellationToken);

        foreach (var range in changedLines)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (range.Start > lineCount)
            {
                // Trailing deletions can point past the end of the new file.
                continue;
            }

            var matchedMember = false;
            foreach (var member in members)
            {
                if (!member.Intersects(range))
                {
                    continue;
                }

                matchedMember = true;
                MarkMember(model, member.Node, projectName, result, cancellationToken);
            }

            if (matchedMember)
            {
                continue;
            }

            // Not inside a member. Either the type header changed (base list, type parameters,
            // attributes) or a member was removed at this position - both are type-wide.
            var matchedType = false;
            foreach (var type in types)
            {
                if (!type.Intersects(range))
                {
                    continue;
                }

                matchedType = true;
                MarkSymbol(model.GetDeclaredSymbol(type.Node, cancellationToken), result);
            }

            if (matchedType)
            {
                continue;
            }

            // Outside every declaration: usings, namespace declarations, assembly attributes.
            if (TouchesGlobalUsing(root, tree, range, cancellationToken))
            {
                result.AddProjectWide(projectName, ProjectWideCause.GlobalUsing,
                    $"{Path.GetFileName(tree.FilePath)} line {range}: global using or file-level directive changed");
                continue;
            }

            // A plain using directive or a namespace rename rebinds every type in the file.
            var markedAny = false;
            foreach (var type in types)
            {
                MarkSymbol(model.GetDeclaredSymbol(type.Node, cancellationToken), result);
                markedAny = true;
            }

            if (!markedAny)
            {
                result.UnmappedChanges.Add($"{tree.FilePath}:{range}");
            }
        }

        return result;
    }

    private void MarkMember(SemanticModel model, SyntaxNode node, string projectName, SymbolChangeSet result, CancellationToken cancellationToken)
    {
        var symbol = model.GetDeclaredSymbol(node, cancellationToken);
        if (symbol is null)
        {
            return;
        }

        // Constants and enum members are baked into their callers at compile time, so no caller
        // carries a reference the graph could follow. The only sound answer is to treat the whole
        // declaring type as changed and to widen to the referencing projects.
        var isConstant = symbol is IFieldSymbol { IsConst: true } or IFieldSymbol { ContainingType.TypeKind: TypeKind.Enum };
        if (isConstant)
        {
            var containing = symbol.ContainingType;
            MarkSymbol(containing, result);
            result.AddProjectWide(projectName, ProjectWideCause.ConstantInlining,
                $"{SymbolKeys.DisplayName(symbol)} is compile-time inlined into its callers");
            return;
        }

        MarkSymbol(symbol, result);
    }

    private static void MarkSymbol(ISymbol? symbol, SymbolChangeSet result)
    {
        var key = SymbolKeys.For(symbol);
        if (key is not null)
        {
            result.Add(key);
        }
    }

    private static bool TouchesGlobalUsing(SyntaxNode root, SyntaxTree tree, LineRange range, CancellationToken cancellationToken)
    {
        foreach (var directive in root.DescendantNodes(descendIntoChildren: n => n is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax)
                     .OfType<UsingDirectiveSyntax>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (directive.GlobalKeyword.IsKind(SyntaxKind.GlobalKeyword) && Span(tree, directive).Intersects(range))
            {
                return true;
            }
        }

        return false;
    }

    private static void CollectDeclarations(
        SyntaxNode root,
        SyntaxTree tree,
        List<DeclarationSpan> members,
        List<DeclarationSpan> types,
        CancellationToken cancellationToken)
    {
        foreach (var node in root.DescendantNodesAndSelf())
        {
            cancellationToken.ThrowIfCancellationRequested();

            switch (node)
            {
                case BaseTypeDeclarationSyntax or DelegateDeclarationSyntax:
                    types.Add(new DeclarationSpan(node, Span(tree, node)));
                    break;

                case BaseMethodDeclarationSyntax or BasePropertyDeclarationSyntax or EnumMemberDeclarationSyntax:
                    members.Add(new DeclarationSpan(node, Span(tree, node)));
                    break;

                case VariableDeclaratorSyntax variable when variable.Parent?.Parent is BaseFieldDeclarationSyntax field:
                    // Span the whole field declaration so a change to the modifiers or the type -
                    // `const int X = 1;` becoming `int X = 1;` - counts against every declarator.
                    members.Add(new DeclarationSpan(variable, Span(tree, field)));
                    break;
            }
        }
    }

    private static LineRange Span(SyntaxTree tree, SyntaxNode node)
    {
        // Span, not FullSpan: leading trivia would stretch the first member of a file up over the
        // using directives and swallow changes that belong to the file rather than to the member.
        // Attribute lists are part of the declaration node already, so attribute edits land on the
        // annotated symbol - which is what they mean.
        var span = tree.GetLineSpan(node.Span);
        return new LineRange(span.StartLinePosition.Line + 1, span.EndLinePosition.Line + 1);
    }

    private readonly record struct DeclarationSpan(SyntaxNode Node, LineRange Lines)
    {
        public bool Intersects(LineRange range) => Lines.Intersects(range.Start, range.End);
    }
}
