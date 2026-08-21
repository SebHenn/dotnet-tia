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

        foreach (var typeKey in Resolve(owningType, result, projectName, displayPath))
        {
            var matches = typeIndex.FindMembers(typeKey, name);
            if (matches.Count == 0)
            {
                // The member was deleted or renamed. Its former dependents are reachable through
                // the declaring type, whose node fans out to every member.
                result.Add(typeKey);
                continue;
            }

            // Overloads are indistinguishable without binding, so all of them are marked.
            foreach (var match in matches)
            {
                result.Add(match);
            }
        }
    }

    private void ResolveType(SyntaxNode node, SymbolChangeSet result, string projectName, string displayPath)
    {
        foreach (var typeKey in Resolve(node, result, projectName, displayPath))
        {
            result.Add(typeKey);
        }
    }

    private IReadOnlyList<string> Resolve(SyntaxNode typeDeclaration, SymbolChangeSet result, string projectName, string displayPath)
    {
        var typePath = BuildTypePath(typeDeclaration);
        var resolved = typeIndex.FindTypes(typePath);

        if (resolved.Count == 0)
        {
            // The type is gone from the current revision, so nothing can be matched against it.
            // Whatever used to depend on it is unknowable at symbol granularity.
            result.AddProjectWide(projectName, ProjectWideCause.DeletedType,
                $"{typePath} existed at the base revision but not at HEAD ({displayPath})");
        }

        return resolved;
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
/// Name-path lookup over one project's types, used to bind old-revision declarations onto current
/// symbols. Generic arity is ignored: without binding, the old tree cannot be matched on arity
/// reliably, and over-matching here is safe.
/// </summary>
/// <remarks>
/// Built from the graph rather than from a compilation. The graph holds a node for every type and
/// every member the project declares, and both a type's name path and a member's simple name can
/// be read straight back out of the key - so binding the old side no longer needs the project
/// parsed. It used to, and on a warm run producing that compilation was most of what change
/// resolution cost.
/// </remarks>
public sealed class SourceTypeIndex
{
    private readonly Dictionary<string, List<string>> _typesByPath = new(StringComparer.Ordinal);

    private readonly Dictionary<string, Dictionary<string, List<string>>> _membersByType = new(StringComparer.Ordinal);

    /// <summary>Indexes the types one project declared.</summary>
    public static SourceTypeIndex FromGraph(ImpactGraph graph, string projectName, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(graph);

        var index = new SourceTypeIndex();

        foreach (var node in graph.Nodes.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (node.Kind != SymbolNodeKind.Type || !string.Equals(node.ProjectName, projectName, StringComparison.Ordinal))
            {
                continue;
            }

            var path = SymbolKeys.TypePathOf(node.Key);
            if (path is null)
            {
                continue;
            }

            if (!index._typesByPath.TryGetValue(path, out var list))
            {
                list = [];
                index._typesByPath[path] = list;
            }

            list.Add(node.Key);

            var byName = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var memberKey in graph.MembersOfType(node.Key))
            {
                if (SymbolKeys.SimpleNameOf(memberKey) is not { } name)
                {
                    continue;
                }

                if (!byName.TryGetValue(name, out var members))
                {
                    members = [];
                    byName[name] = members;
                }

                members.Add(memberKey);
            }

            index._membersByType[node.Key] = byName;
        }

        // Ordinal so that two types sharing a name path - the same name at two arities - are
        // reported in the same order on every run rather than in whatever order the graph
        // enumerated them.
        foreach (var list in index._typesByPath.Values)
        {
            list.Sort(StringComparer.Ordinal);
        }

        return index;
    }

    /// <summary>Every type declared at this dotted name path, arity ignored.</summary>
    public IReadOnlyList<string> FindTypes(string path) =>
        _typesByPath.TryGetValue(path, out var list) ? list : [];

    /// <summary>That type's own members with this simple name. Inherited members are not its own.</summary>
    public IReadOnlyList<string> FindMembers(string typeKey, string name) =>
        _membersByType.TryGetValue(typeKey, out var byName) && byName.TryGetValue(name, out var members)
            ? members
            : [];
}
