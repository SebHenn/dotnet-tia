using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Tia.Core.Diff;
using Tia.Core.Model;

namespace Tia.Core.Analysis;

/// <summary>
/// Resolves the *base* revision of a changed file against the current compilation.
/// </summary>
/// <remarks>
/// A selection built only from the new tree silently misses deletions, and deletions are not a
/// corner case: removing an override makes callers run the base implementation, and removing an
/// interface implementation redirects dispatch - in both cases every caller still compiles and
/// nothing in the new tree marks the change.
///
/// The old tree cannot be bound to a semantic model (the compilation holds the new code), so
/// declarations are matched by name path instead. A member that no longer exists widens to its
/// declaring type; a type that no longer exists widens to the whole project.
/// </remarks>
public sealed class OldSideResolver(SourceTypeIndex typeIndex)
{
    public SymbolChangeSet Resolve(
        string oldFileContent,
        IReadOnlyList<LineRange> changedLines,
        string projectName,
        string displayPath,
        CancellationToken cancellationToken = default)
    {
        var result = new SymbolChangeSet();
        if (changedLines.Count == 0)
        {
            return result;
        }

        var tree = CSharpSyntaxTree.ParseText(oldFileContent, path: displayPath, cancellationToken: cancellationToken);
        var root = tree.GetRoot(cancellationToken);

        var members = new List<(SyntaxNode Node, string Name, LineRange Lines)>();
        var types = new List<(SyntaxNode Node, LineRange Lines)>();

        foreach (var node in root.DescendantNodesAndSelf())
        {
            cancellationToken.ThrowIfCancellationRequested();

            switch (node)
            {
                case BaseTypeDeclarationSyntax or DelegateDeclarationSyntax:
                    types.Add((node, LineSpanOf(tree, node)));
                    break;

                case MethodDeclarationSyntax method:
                    members.Add((node, method.Identifier.ValueText, LineSpanOf(tree, node)));
                    break;

                case PropertyDeclarationSyntax property:
                    members.Add((node, property.Identifier.ValueText, LineSpanOf(tree, node)));
                    break;

                case EventDeclarationSyntax @event:
                    members.Add((node, @event.Identifier.ValueText, LineSpanOf(tree, node)));
                    break;

                case EnumMemberDeclarationSyntax enumMember:
                    members.Add((node, enumMember.Identifier.ValueText, LineSpanOf(tree, node)));
                    break;

                case ConstructorDeclarationSyntax ctor:
                    members.Add((node, ctor.Identifier.ValueText, LineSpanOf(tree, node)));
                    break;

                case VariableDeclaratorSyntax { Parent.Parent: BaseFieldDeclarationSyntax field } variable:
                    members.Add((node, variable.Identifier.ValueText, LineSpanOf(tree, field)));
                    break;
            }
        }

        // Members first, then types, exactly as on the new side: a change inside a method belongs
        // to that method, not to every member of the enclosing type.
        foreach (var range in changedLines)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var matchedMember = false;

            foreach (var (node, name, lines) in members)
            {
                if (!lines.Intersects(range))
                {
                    continue;
                }

                matchedMember = true;
                ResolveMember(node, name, result, projectName, displayPath);
            }

            if (matchedMember)
            {
                continue;
            }

            foreach (var (node, lines) in types)
            {
                if (lines.Intersects(range))
                {
                    ResolveType(node, result, projectName, displayPath);
                }
            }
        }

        return result;
    }

    private void ResolveMember(SyntaxNode node, string name, SymbolChangeSet result, string projectName, string displayPath)
    {
        var owningType = node.FirstAncestorOrSelf<BaseTypeDeclarationSyntax>();
        if (owningType is null)
        {
            return;
        }

        var resolvedType = Resolve(owningType, result, projectName, displayPath);
        if (resolvedType is null)
        {
            return;
        }

        var matches = resolvedType.GetMembers(name);
        if (matches.Length == 0)
        {
            // The member was deleted or renamed. Its former dependents are reachable through the
            // declaring type, whose node fans out to every member.
            AddKey(result, resolvedType);
            return;
        }

        // Overloads are indistinguishable without binding, so all of them are marked.
        foreach (var match in matches)
        {
            AddKey(result, match);
        }
    }

    private void ResolveType(SyntaxNode node, SymbolChangeSet result, string projectName, string displayPath)
    {
        var resolved = Resolve(node, result, projectName, displayPath);
        if (resolved is not null)
        {
            AddKey(result, resolved);
        }
    }

    private INamedTypeSymbol? Resolve(SyntaxNode typeDeclaration, SymbolChangeSet result, string projectName, string displayPath)
    {
        var typePath = BuildTypePath(typeDeclaration);
        var resolved = typeIndex.Find(typePath);

        if (resolved is null)
        {
            // The type is gone from the current compilation, so nothing can be matched against
            // it. Whatever used to depend on it is unknowable at symbol granularity.
            result.AddProjectWide(projectName, ProjectWideCause.DeletedType,
                $"{typePath} existed at the base revision but not at HEAD ({displayPath})");
        }

        return resolved;
    }

    private static void AddKey(SymbolChangeSet result, ISymbol symbol)
    {
        var key = SymbolKeys.For(symbol);
        if (key is not null)
        {
            result.Add(key);
        }
    }

    private static LineRange LineSpanOf(SyntaxTree tree, SyntaxNode node)
    {
        var span = tree.GetLineSpan(node.Span);
        return new LineRange(span.StartLinePosition.Line + 1, span.EndLinePosition.Line + 1);
    }

    internal static string BuildTypePath(SyntaxNode typeDeclaration)
    {
        var names = new List<string>();

        for (var node = typeDeclaration; node is not null; node = node.Parent)
        {
            switch (node)
            {
                case BaseTypeDeclarationSyntax type:
                    names.Insert(0, type.Identifier.ValueText);
                    break;

                case DelegateDeclarationSyntax del:
                    names.Insert(0, del.Identifier.ValueText);
                    break;

                case BaseNamespaceDeclarationSyntax ns:
                    names.Insert(0, ns.Name.ToString());
                    break;
            }
        }

        return string.Join('.', names);
    }
}

/// <summary>
/// Name-path lookup over the source types of a compilation, used to bind old-revision
/// declarations onto current symbols. Generic arity is ignored: without binding, the old tree
/// cannot be matched on arity reliably, and over-matching here is safe.
/// </summary>
public sealed class SourceTypeIndex
{
    private readonly Dictionary<string, List<INamedTypeSymbol>> _byPath = new(StringComparer.Ordinal);

    public static SourceTypeIndex Build(Compilation compilation, CancellationToken cancellationToken = default)
    {
        var index = new SourceTypeIndex();

        foreach (var type in ReferenceGraphBuilder.EnumerateSourceTypes(compilation, cancellationToken))
        {
            var path = PathOf(type);
            if (!index._byPath.TryGetValue(path, out var list))
            {
                list = [];
                index._byPath[path] = list;
            }

            list.Add(type);
        }

        return index;
    }

    public INamedTypeSymbol? Find(string path) =>
        _byPath.TryGetValue(path, out var list) && list.Count > 0 ? list[0] : null;

    private static string PathOf(INamedTypeSymbol type)
    {
        var names = new List<string>();
        for (INamedTypeSymbol? current = type; current is not null; current = current.ContainingType)
        {
            names.Insert(0, current.Name);
        }

        var ns = type.ContainingNamespace;
        var namespaceParts = new List<string>();
        while (ns is { IsGlobalNamespace: false })
        {
            namespaceParts.Insert(0, ns.Name);
            ns = ns.ContainingNamespace;
        }

        return string.Join('.', namespaceParts.Concat(names));
    }
}
