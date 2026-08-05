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
        //
        // The edge kind rides down the stack with the type and member keys, for the same reason
        // they do: it is a property of where a node sits, and the walk already knows that. It used
        // to be recomputed per node with two FirstAncestorOrSelf calls, each of which climbs to the
        // root - O(depth) work on every node of every file, and paid before the switch below had
        // decided whether the node was interesting at all.
        var stack = new Stack<(SyntaxNode Node, string? TypeKey, string? MemberKey, EdgeKind Kind)>();
        stack.Push((tree.GetRoot(cancellationToken), null, null, EdgeKind.Reference));

        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (node, typeKey, memberKey, edgeKind) = stack.Pop();

            // Attribute wins over Derived, and both are sticky once entered - exactly what
            // "this node or any ancestor is an AttributeSyntax / BaseListSyntax" meant before.
            if (edgeKind != EdgeKind.Attribute)
            {
                edgeKind = node is AttributeSyntax ? EdgeKind.Attribute
                    : node is BaseListSyntax ? EdgeKind.Derived
                    : edgeKind;
            }

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

            RecordReferences(model, node, typeKey, memberKey, edgeKind, graph, cancellationToken);

            foreach (var child in node.ChildNodes())
            {
                stack.Push((child, typeKey, memberKey, edgeKind));
            }
        }
    }

    private void RecordReferences(
        SemanticModel model,
        SyntaxNode node,
        string? typeKey,
        string? memberKey,
        EdgeKind edgeKind,
        ImpactGraph graph,
        CancellationToken cancellationToken)
    {
        var source = memberKey ?? typeKey;
        if (source is null)
        {
            return;
        }

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

                // `var (a, b) = point` calls Deconstruct without naming it either.
                if (node is AssignmentExpressionSyntax assignment)
                {
                    AddReference(graph, model.GetDeconstructionInfo(assignment).Method, source, EdgeKind.Reference);
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

            case InterpolationSyntax interpolation:
                AddFormattingReference(graph, model, interpolation.Expression, source, cancellationToken);
                break;

            case UsingStatementSyntax usingStatement:
                AddDisposalReference(graph, model, usingStatement.Declaration?.Type, source, cancellationToken);
                AddDisposalReference(graph, model, usingStatement.Expression, source, cancellationToken);
                break;

            case LocalDeclarationStatementSyntax local when local.UsingKeyword.IsKind(SyntaxKind.UsingKeyword):
                AddDisposalReference(graph, model, local.Declaration.Type, source, cancellationToken);
                break;

            case InitializerExpressionSyntax { RawKind: (int)SyntaxKind.CollectionInitializerExpression } initializer:
            {
                // `new List<T> { a, b }` calls Add without naming it.
                foreach (var element in initializer.Expressions)
                {
                    AddReference(graph, model.GetCollectionInitializerSymbolInfo(element, cancellationToken).Symbol, source, EdgeKind.Reference);
                }

                break;
            }
        }
    }

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
    /// Connects a handler to the request type that selects it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The interface edges above make ordinary dependency injection work: a caller of
    /// <c>IFoo.Bar()</c> is connected to <c>Foo.Bar()</c>. Mediator dispatch defeats them, because
    /// the caller does not name the handler's interface at all - it calls
    /// <c>IMediator.Send(new ListContributorsQuery(...))</c>, and a container resolves
    /// <c>IQueryHandler&lt;ListContributorsQuery, ...&gt;</c> by scanning the assembly. Confirmed on
    /// ardalis/CleanArchitecture: changing the handler selected nothing at all, including the
    /// functional test that exercises the endpoint that sends the query.
    /// </para>
    /// <para>
    /// The request type is the missing link, and it is entirely static. A handler implements
    /// <c>ISomething&lt;TRequest, ...&gt;</c> and the caller constructs a <c>TRequest</c>, so an
    /// edge from the handler's members to the request type lets the walk continue through
    /// everything that builds one. That is how request dispatch works generally rather than a
    /// property of any one library.
    /// </para>
    /// <para>
    /// The type argument has to appear as a *parameter* of an interface member for this to mean
    /// dispatch. <c>IRequestHandler&lt;TRequest, TResponse&gt;.Handle(TRequest, ...)</c> qualifies;
    /// <c>IEnumerable&lt;T&gt;</c> does not, which is the difference between "selected by a T" and
    /// "has some T in it". Whether the edge is then followed is decided at selection time, where
    /// the whole solution is visible.
    /// </para>
    /// </remarks>
    private void AddDispatchEdges(ImpactGraph graph, INamedTypeSymbol type, string typeKey)
    {
        if (type.IsAbstract || type.TypeKind == TypeKind.Interface)
        {
            return;
        }

        var requestTypes = new HashSet<string>(StringComparer.Ordinal);

        foreach (var iface in type.AllInterfaces)
        {
            if (iface.TypeArguments.Length == 0)
            {
                continue;
            }

            foreach (var argument in ParameterTypeArguments(iface))
            {
                if (IsTracked(argument) &&
                    !SymbolEqualityComparer.Default.Equals(argument, type) &&
                    SymbolKeys.For(argument) is { } requestKey)
                {
                    requestTypes.Add(requestKey);
                }
            }
        }

        if (requestTypes.Count == 0)
        {
            return;
        }

        foreach (var requestKey in requestTypes)
        {
            graph.AddEdge(typeKey, requestKey, EdgeKind.DispatchByRequest);

            foreach (var member in type.GetMembers())
            {
                if (SymbolKeys.For(member) is { } memberKey)
                {
                    graph.AddEdge(memberKey, requestKey, EdgeKind.DispatchByRequest);
                }
            }
        }
    }

    /// <summary>Type arguments of <paramref name="iface"/> that appear as a parameter of one of
    /// its members - the ones a caller supplies rather than receives.</summary>
    private static IEnumerable<ITypeSymbol> ParameterTypeArguments(INamedTypeSymbol iface)
    {
        foreach (var member in iface.GetMembers())
        {
            if (member is not IMethodSymbol method)
            {
                continue;
            }

            foreach (var parameter in method.Parameters)
            {
                if (iface.TypeArguments.Contains(parameter.Type, SymbolEqualityComparer.Default))
                {
                    yield return parameter.Type;
                }
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

            AddDispatchEdges(graph, type, typeKey);

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

        AddStaticInitializationReference(graph, normalized, source, kind);
    }

    /// <summary>
    /// Touching a type's statics runs its static constructor, so whoever touches them depends on
    /// everything that constructor does.
    /// </summary>
    /// <remarks>
    /// The NodaTime gate found this one. <c>XmlSchemaTest</c> reads
    /// <c>XmlSchemaDefinition.NodaTimeXmlSchema</c>, a static property whose type initialiser
    /// builds the schema from the TZDB zone list - so corrupting the TZDB reader failed the test,
    /// through a call the source never makes. The auto-implemented getter references nothing, and
    /// the static constructor referenced nothing that led back to the reader's callers, so the two
    /// halves of the runtime path had no edge between them.
    /// </remarks>
    private static void AddStaticInitializationReference(ImpactGraph graph, ISymbol referenced, string source, EdgeKind kind)
    {
        // Naming a type is not touching it: `typeof(Foo)` and `Foo x` do not run the initialiser.
        // Reading a static member or constructing an instance do.
        var triggersInitialization = referenced switch
        {
            IFieldSymbol { IsStatic: true } => true,
            IPropertySymbol { IsStatic: true } => true,
            IEventSymbol { IsStatic: true } => true,
            IMethodSymbol { IsStatic: true, MethodKind: not MethodKind.StaticConstructor } => true,
            IMethodSymbol { MethodKind: MethodKind.Constructor } => true,
            _ => false,
        };

        if (!triggersInitialization)
        {
            return;
        }

        foreach (var initializer in referenced.ContainingType?.StaticConstructors ?? [])
        {
            if (SymbolKeys.For(initializer) is { } initializerKey)
            {
                graph.AddEdge(initializerKey, source, kind);
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
