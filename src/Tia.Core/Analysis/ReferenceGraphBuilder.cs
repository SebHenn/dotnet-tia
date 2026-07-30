using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Tia.Core.Model;

namespace Tia.Core.Analysis;

/// <summary>
/// Builds the reverse reference graph for a single compilation.
/// </summary>
/// <remarks>
/// Two passes. The syntactic pass walks every document and emits <c>referenced -&gt; containing
/// member</c> edges for invocations, member access, object creation, identifiers, operators, base
/// type lists and attributes. The semantic pass adds the edges that make selection correct rather
/// than merely plausible: interface member to implementation, virtual member to override, and base
/// type to derived type - each in both directions where behaviour can flow both ways.
/// </remarks>
public sealed class ReferenceGraphBuilder
{
    private readonly IReadOnlySet<string> _trackedAssemblies;

    /// <param name="trackedAssemblies">
    /// Assembly names belonging to the solution. References to anything else (the BCL, NuGet
    /// packages) are dropped: they can never be the thing that changed, and keeping them would
    /// multiply the graph size for no selection benefit.
    /// </param>
    public ReferenceGraphBuilder(IReadOnlySet<string> trackedAssemblies)
    {
        _trackedAssemblies = trackedAssemblies;
    }

    public ImpactGraph Build(Compilation compilation, string projectName, CancellationToken cancellationToken = default)
    {
        var graph = new ImpactGraph();

        AddDeclaredSymbols(compilation, projectName, graph, cancellationToken);

        foreach (var tree in compilation.SyntaxTrees)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WalkTree(compilation, tree, projectName, graph, cancellationToken);
        }

        AddSemanticEdges(compilation, projectName, graph, cancellationToken);

        return graph;
    }

    private void AddDeclaredSymbols(Compilation compilation, string projectName, ImpactGraph graph, CancellationToken cancellationToken)
    {
        foreach (var type in EnumerateSourceTypes(compilation, cancellationToken))
        {
            AddNodeFor(graph, type, projectName);

            foreach (var member in type.GetMembers())
            {
                if (member.IsImplicitlyDeclared && member is not IMethodSymbol { MethodKind: MethodKind.Constructor })
                {
                    continue;
                }

                AddNodeFor(graph, member, projectName);
            }
        }
    }

    private void WalkTree(Compilation compilation, SyntaxTree tree, string projectName, ImpactGraph graph, CancellationToken cancellationToken)
    {
        var model = compilation.GetSemanticModel(tree);
        var filePath = tree.FilePath;

        // Explicit stack rather than recursion: generated files can nest expressions deeply
        // enough to overflow a pooled thread's stack.
        var stack = new Stack<(SyntaxNode Node, string? TypeKey, string? MemberKey)>();
        stack.Push((tree.GetRoot(cancellationToken), null, null));

        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (node, typeKey, memberKey) = stack.Pop();

            switch (node)
            {
                case BaseTypeDeclarationSyntax or DelegateDeclarationSyntax:
                {
                    var declared = model.GetDeclaredSymbol(node, cancellationToken);
                    var key = AddNodeFor(graph, declared, projectName, filePath);
                    if (key is not null)
                    {
                        typeKey = key;
                        memberKey = null;
                    }

                    break;
                }

                case BaseMethodDeclarationSyntax or BasePropertyDeclarationSyntax or EnumMemberDeclarationSyntax:
                {
                    var declared = model.GetDeclaredSymbol(node, cancellationToken);
                    var key = AddNodeFor(graph, declared, projectName, filePath);
                    if (key is not null)
                    {
                        memberKey = key;
                    }

                    break;
                }

                case VariableDeclaratorSyntax variable
                    when variable.Parent?.Parent is BaseFieldDeclarationSyntax:
                {
                    var declared = model.GetDeclaredSymbol(variable, cancellationToken);
                    var key = AddNodeFor(graph, declared, projectName, filePath);
                    if (key is not null)
                    {
                        memberKey = key;
                    }

                    break;
                }
            }

            RecordReferences(model, node, typeKey, memberKey, graph, cancellationToken);

            foreach (var child in node.ChildNodes())
            {
                stack.Push((child, typeKey, memberKey));
            }
        }
    }

    private void RecordReferences(
        SemanticModel model,
        SyntaxNode node,
        string? typeKey,
        string? memberKey,
        ImpactGraph graph,
        CancellationToken cancellationToken)
    {
        var source = memberKey ?? typeKey;
        if (source is null)
        {
            return;
        }

        var edgeKind = node.FirstAncestorOrSelf<AttributeSyntax>() is not null ? EdgeKind.Attribute
            : node.FirstAncestorOrSelf<BaseListSyntax>() is not null ? EdgeKind.Derived
            : EdgeKind.Reference;

        switch (node)
        {
            case SimpleNameSyntax:
            case ObjectCreationExpressionSyntax:
            case ImplicitObjectCreationExpressionSyntax:
            case ConstructorInitializerSyntax:
            case PrimaryConstructorBaseTypeSyntax:
            case ElementAccessExpressionSyntax:
            case InvocationExpressionSyntax:
            case BinaryExpressionSyntax:
            case PrefixUnaryExpressionSyntax:
            case PostfixUnaryExpressionSyntax:
            case AssignmentExpressionSyntax:
            case CastExpressionSyntax:
            case AttributeSyntax:
            {
                var info = model.GetSymbolInfo(node, cancellationToken);
                AddReference(graph, info.Symbol, source, edgeKind);
                foreach (var candidate in info.CandidateSymbols)
                {
                    AddReference(graph, candidate, source, edgeKind);
                }

                break;
            }

            case ForEachStatementSyntax foreachStatement:
            {
                var info = model.GetForEachStatementInfo(foreachStatement);
                AddReference(graph, info.GetEnumeratorMethod, source, EdgeKind.Reference);
                AddReference(graph, info.MoveNextMethod, source, EdgeKind.Reference);
                AddReference(graph, info.CurrentProperty, source, EdgeKind.Reference);
                AddReference(graph, info.DisposeMethod, source, EdgeKind.Reference);
                break;
            }

            case AwaitExpressionSyntax awaitExpression:
            {
                var info = model.GetAwaitExpressionInfo(awaitExpression);
                AddReference(graph, info.GetAwaiterMethod, source, EdgeKind.Reference);
                AddReference(graph, info.GetResultMethod, source, EdgeKind.Reference);
                AddReference(graph, info.IsCompletedProperty, source, EdgeKind.Reference);
                break;
            }
        }
    }

    private void AddSemanticEdges(Compilation compilation, string projectName, ImpactGraph graph, CancellationToken cancellationToken)
    {
        foreach (var type in EnumerateSourceTypes(compilation, cancellationToken))
        {
            var typeKey = SymbolKeys.For(type);
            if (typeKey is null)
            {
                continue;
            }

            // Base type and interface -> derived type. A change to a base type can alter every
            // type built on it, and reaching a type node fans out to its members.
            for (var baseType = type.BaseType; baseType is not null; baseType = baseType.BaseType)
            {
                LinkTypes(graph, baseType, typeKey);
            }

            foreach (var iface in type.AllInterfaces)
            {
                LinkTypes(graph, iface, typeKey);
            }

            // Interface member <-> implementation, both directions. This is also what makes
            // dependency injection work without parsing any registration code: a test that calls
            // IFoo.Bar() is already connected to Foo.Bar() through these edges.
            foreach (var iface in type.AllInterfaces)
            {
                if (!IsTracked(iface))
                {
                    continue;
                }

                foreach (var interfaceMember in iface.GetMembers())
                {
                    if (interfaceMember is IMethodSymbol { AssociatedSymbol: not null })
                    {
                        continue;
                    }

                    var implementation = type.FindImplementationForInterfaceMember(interfaceMember);
                    if (implementation is null)
                    {
                        continue;
                    }

                    LinkBothWays(graph, interfaceMember, implementation,
                        EdgeKind.InterfaceToImplementation, EdgeKind.ImplementationToInterface, projectName);
                }
            }

            // Virtual or abstract member <-> override, both directions.
            foreach (var member in type.GetMembers())
            {
                if (!member.IsOverride)
                {
                    continue;
                }

                var overridden = member switch
                {
                    IMethodSymbol method => (ISymbol?)method.OverriddenMethod,
                    IPropertySymbol property => property.OverriddenProperty,
                    IEventSymbol @event => @event.OverriddenEvent,
                    _ => null,
                };

                if (overridden is not null)
                {
                    LinkBothWays(graph, overridden, member,
                        EdgeKind.VirtualToOverride, EdgeKind.OverrideToVirtual, projectName);
                }
            }
        }
    }

    private void LinkTypes(ImpactGraph graph, INamedTypeSymbol baseOrInterface, string derivedKey)
    {
        if (!IsTracked(baseOrInterface))
        {
            return;
        }

        var baseKey = SymbolKeys.For(baseOrInterface);
        if (baseKey is not null)
        {
            graph.AddEdge(baseKey, derivedKey, EdgeKind.Derived);
        }
    }

    /// <summary>
    /// Links a declaration to the thing that specialises it, in both directions but with distinct
    /// edge kinds. The traversal needs to tell them apart: going up from an implementation and
    /// straight back down to its siblings would assert that changing one implementation changes
    /// the others.
    /// </summary>
    private void LinkBothWays(ImpactGraph graph, ISymbol general, ISymbol specific, EdgeKind downward, EdgeKind upward, string projectName)
    {
        var generalKey = AddNodeFor(graph, general, projectName);
        var specificKey = AddNodeFor(graph, specific, projectName);
        if (generalKey is null || specificKey is null)
        {
            return;
        }

        graph.AddEdge(generalKey, specificKey, downward);
        graph.AddEdge(specificKey, generalKey, upward);
    }

    private void AddReference(ImpactGraph graph, ISymbol? referenced, string source, EdgeKind kind)
    {
        var normalized = SymbolKeys.Normalize(referenced);
        if (normalized is null || !IsTracked(normalized))
        {
            return;
        }

        var key = SymbolKeys.For(normalized);
        if (key is null)
        {
            return;
        }

        graph.AddEdge(key, source, kind);

        // A constructor reference is also a reference to the type: `new Foo()` has to be
        // re-examined when anything about Foo's shape changes.
        if (normalized is IMethodSymbol { MethodKind: MethodKind.Constructor } ctor)
        {
            var typeKey = SymbolKeys.For(ctor.ContainingType);
            if (typeKey is not null)
            {
                graph.AddEdge(typeKey, source, kind);
            }
        }
    }

    private string? AddNodeFor(ImpactGraph graph, ISymbol? symbol, string projectName, string? filePath = null)
    {
        var normalized = SymbolKeys.Normalize(symbol);
        if (normalized is null || !IsTracked(normalized))
        {
            return null;
        }

        var key = SymbolKeys.For(normalized);
        if (key is null)
        {
            return null;
        }

        graph.AddNode(new SymbolNode
        {
            Key = key,
            DisplayName = SymbolKeys.DisplayName(normalized),
            Kind = SymbolKeys.KindOf(normalized),
            ProjectName = projectName,
            ContainingTypeKey = SymbolKeys.ForContainingType(normalized),
            FilePath = filePath ?? DeclarationFile(normalized),
        });

        return key;
    }

    private static string? DeclarationFile(ISymbol symbol)
    {
        foreach (var location in symbol.Locations)
        {
            if (location.IsInSource && location.SourceTree is { FilePath.Length: > 0 } tree)
            {
                return tree.FilePath;
            }
        }

        return null;
    }

    private bool IsTracked(ISymbol symbol)
    {
        var assembly = symbol.ContainingAssembly?.Identity.Name;
        return assembly is not null && _trackedAssemblies.Contains(assembly);
    }

    /// <summary>Every named type declared in the compilation's own assembly, nested types included.</summary>
    public static IEnumerable<INamedTypeSymbol> EnumerateSourceTypes(Compilation compilation, CancellationToken cancellationToken = default)
    {
        var stack = new Stack<INamespaceOrTypeSymbol>();
        stack.Push(compilation.Assembly.GlobalNamespace);

        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = stack.Pop();

            foreach (var member in current.GetMembers())
            {
                switch (member)
                {
                    case INamespaceSymbol ns:
                        stack.Push(ns);
                        break;

                    case INamedTypeSymbol type:
                        stack.Push(type);
                        yield return type;
                        break;
                }
            }
        }
    }
}
