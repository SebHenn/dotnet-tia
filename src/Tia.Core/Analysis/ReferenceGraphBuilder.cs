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
/// <param name="Declarations">
/// Where every declaration sits, so a later run can map a changed line range onto its symbol
/// without producing a compilation. Collected here because this is the one pass that already
/// visits every declaration with a semantic model in hand.
/// </param>
public sealed record ProjectSymbols(ImpactGraph Graph, IReadOnlyList<DeclarationSite> Declarations);

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

    public ImpactGraph Build(Compilation compilation, string projectName, CancellationToken cancellationToken = default) =>
        BuildSymbols(compilation, projectName, cancellationToken).Graph;

    public ProjectSymbols BuildSymbols(Compilation compilation, string projectName, CancellationToken cancellationToken = default)
    {
        var graph = new ImpactGraph();
        var declarations = new List<DeclarationSite>();

        AddDeclaredSymbols(compilation, projectName, graph, cancellationToken);

        foreach (var tree in compilation.SyntaxTrees)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WalkTree(compilation, tree, projectName, graph, declarations, cancellationToken);
        }

        AddSemanticEdges(compilation, projectName, graph, cancellationToken);

        return new ProjectSymbols(graph, declarations);
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

    private void WalkTree(
        Compilation compilation,
        SyntaxTree tree,
        string projectName,
        ImpactGraph graph,
        List<DeclarationSite> declarations,
        CancellationToken cancellationToken)
    {
        var model = compilation.GetSemanticModel(tree);
        var filePath = tree.FilePath;

        // A generated tree has no file on disk, so no diff can ever name it and a site for it
        // could never be looked up. Its declarations are still nodes; only the position is dropped.
        var recordSites = filePath.Length > 0;

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

            // Its arguments name the converter and nothing else, so they are not walked: the
            // ordinary attribute edge from the converter to the type is what #65 replaces.
            if (node is AttributeSyntax { Parent.Parent: BaseTypeDeclarationSyntax declaration } converterAttribute &&
                AddValueConverterEdges(model, converterAttribute, declaration, projectName, graph, cancellationToken))
            {
                RecordReferences(model, node, typeKey, memberKey, edgeKind, graph, cancellationToken);
                continue;
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

                        if (recordSites)
                        {
                            declarations.Add(SiteFor(tree, node, key, projectName, filePath, isType: true, declared));
                        }
                    }

                    break;
                }

                // Top-level statements belong to the entry point the compiler synthesises, which no
                // declaration names. Their references are recorded against it, so a type registered
                // there - `AddSingleton<IGreeting, Greeting>()` - has a dependent (#40). No declaration
                // site: a changed statement still widens its project as unowned code (#35).
                case GlobalStatementSyntax when memberKey is null:
                {
                    if (model.GetEnclosingSymbol(node.SpanStart, cancellationToken) is IMethodSymbol entryPoint &&
                        AddNodeFor(graph, entryPoint, projectName, filePath) is { } key)
                    {
                        memberKey = key;
                        typeKey = SymbolKeys.ForContainingType(entryPoint);
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

                        if (recordSites)
                        {
                            declarations.Add(SiteFor(tree, node, key, projectName, filePath, isType: false, declared));
                        }
                    }

                    break;
                }

                case VariableDeclaratorSyntax variable
                    when variable.Parent?.Parent is BaseFieldDeclarationSyntax field:
                {
                    var declared = model.GetDeclaredSymbol(variable, cancellationToken);
                    var key = AddNodeFor(graph, declared, projectName, filePath);
                    if (key is not null)
                    {
                        memberKey = key;

                        if (recordSites)
                        {
                            // The whole field declaration, not the declarator: `const int X = 1;`
                            // becoming `int X = 1;` moves only the modifiers, and that counts
                            // against every declarator the statement introduces.
                            declarations.Add(SiteFor(tree, field, key, projectName, filePath, isType: false, declared));
                        }
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

        // A user-defined implicit conversion is a call nothing names (#42). It happens wherever
        // a value meets a type it converts to - an argument, a return, an initialiser, an
        // assignment, an operand, a lambda body, a collection element - and Roslyn records it on
        // the expression being converted, so every expression is asked rather than a list of
        // contexts that would always be one short. An explicit cast names its operator and is
        // resolved through GetSymbolInfo below.
        if (node is ExpressionSyntax converted &&
            model.GetConversion(converted, cancellationToken) is { IsUserDefined: true, MethodSymbol: { } conversion })
        {
            AddReference(graph, conversion, source, EdgeKind.Reference);
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

                // Only inside a member. At type level - a base list naming IEquatable<Self>, an
                // attribute - nothing is handed over, and a type as a site would fan out to all of
                // its members.
                if (memberKey is not null)
                {
                    if (node is InvocationExpressionSyntax or BaseObjectCreationExpressionSyntax &&
                        (info.Symbol ?? info.CandidateSymbols.FirstOrDefault()) is IMethodSymbol called)
                    {
                        AddSerializationSites(graph, model, node, called, memberKey, cancellationToken);
                    }

                    AddGenericDispatchSites(graph, info.Symbol, memberKey);
                    foreach (var candidate in info.CandidateSymbols)
                    {
                        AddGenericDispatchSites(graph, candidate, memberKey);
                    }

                    if (node is AssignmentExpressionSyntax handedOver)
                    {
                        AddConversionDispatchSite(graph, model, handedOver.Right, memberKey, cancellationToken);
                    }

                    if (node is CastExpressionSyntax cast)
                    {
                        AddConversionDispatchSite(graph, model, cast.Expression, memberKey, cancellationToken);
                    }

                    // `"Price in " + currency` binds to string.operator +, outside the solution,
                    // and the call to currency's ToString happens inside it.
                    if (node is BinaryExpressionSyntax { RawKind: (int)SyntaxKind.AddExpression } concatenation &&
                        info.Symbol is IMethodSymbol { ContainingType.SpecialType: SpecialType.System_String })
                    {
                        AddFormattingReference(graph, model, concatenation.Left, source, cancellationToken);
                        AddFormattingReference(graph, model, concatenation.Right, source, cancellationToken);
                        AddFormattedValueSite(graph, model, concatenation.Left, memberKey, cancellationToken);
                        AddFormattedValueSite(graph, model, concatenation.Right, memberKey, cancellationToken);
                    }
                }

                // An attribute is constructed by whatever reads it - a test framework, ASP.NET - and
                // that code then calls what it overrides: DataAttribute.GetData,
                // BeforeAfterTestAttribute.Before, an action filter's OnActionExecuting. That is an
                // instance handed to code outside the solution, so the usage is a site of the
                // attribute's hub (#119). On a type it reaches every member, all of which the
                // attribute applies to. An assembly-level one has no source here; the test
                // discoverer routes those to the tests.
                if (node is AttributeSyntax && info.Symbol is IMethodSymbol { ContainingType: { } attributeType })
                {
                    AddDispatchSite(graph, attributeType, source);
                }

                // `var (a, b) = point` calls Deconstruct without naming it either.
                if (node is AssignmentExpressionSyntax assignment)
                {
                    AddReference(graph, model.GetDeconstructionInfo(assignment).Method, source, EdgeKind.Reference);
                }

                break;
            }

            // Both shapes: `foreach (var x in xs)` and the deconstructing `foreach (var (a, b) in
            // pairs)`, which used to record nothing at all (#42).
            case CommonForEachStatementSyntax foreachStatement:
            {
                var info = model.GetForEachStatementInfo(foreachStatement);
                AddReference(graph, info.GetEnumeratorMethod, source, EdgeKind.Reference);
                AddReference(graph, info.MoveNextMethod, source, EdgeKind.Reference);
                AddReference(graph, info.CurrentProperty, source, EdgeKind.Reference);
                AddReference(graph, info.DisposeMethod, source, EdgeKind.Reference);

                // `foreach (Money m in amounts)` converts each element the way an assignment would.
                if (info.ElementConversion is { IsUserDefined: true, MethodSymbol: { } elementConversion })
                {
                    AddReference(graph, elementConversion, source, EdgeKind.Reference);
                }

                if (foreachStatement is ForEachVariableStatementSyntax deconstructing)
                {
                    AddReference(graph, model.GetDeconstructionInfo(deconstructing).Method, source, EdgeKind.Reference);
                }

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
                if (memberKey is not null)
                {
                    AddFormattedValueSite(graph, model, interpolation.Expression, memberKey, cancellationToken);
                }

                break;

            // The places a value is converted to the type something else expects: an argument,
            // what a member returns, an initialiser. Each is where an instance can cross into code
            // outside the solution as an external type.
            case ArgumentSyntax argument when memberKey is not null:
                AddConversionDispatchSite(graph, model, argument.Expression, memberKey, cancellationToken);
                break;

            case ReturnStatementSyntax returnStatement when memberKey is not null:
                AddConversionDispatchSite(graph, model, returnStatement.Expression, memberKey, cancellationToken);
                break;

            case ArrowExpressionClauseSyntax arrow when memberKey is not null:
                AddConversionDispatchSite(graph, model, arrow.Expression, memberKey, cancellationToken);
                break;

            case EqualsValueClauseSyntax initializerClause when memberKey is not null:
                AddConversionDispatchSite(graph, model, initializerClause.Value, memberKey, cancellationToken);
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

                var overridden = Overridden(member);

                if (overridden is not null)
                {
                    LinkBothWays(graph, overridden, member,
                        EdgeKind.VirtualToOverride, EdgeKind.OverrideToVirtual, projectName);
                }
            }

            AddExternalDispatchEdges(graph, type, typeKey, projectName);
            AddSerializationEdges(graph, type, typeKey, projectName);
            AddControllerActionSites(graph, type);
        }
    }

    /// <summary>
    /// Connects every member the runtime can dispatch to from outside the solution to the places
    /// that hand an instance of its type to code outside the solution.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The interface and override edges above stop at the solution's edge, because a member of the
    /// BCL has no node. So an override of <c>Comparer&lt;T&gt;.Compare</c>, <c>object.Equals</c> or
    /// <c>ToString</c>, or an implementation of <c>IComparable&lt;T&gt;</c>, was reached by nothing:
    /// whoever calls it does so through a member the graph does not have, and often from inside the
    /// BCL itself - <c>list.Sort()</c>, or a test framework comparing two values. The NodaTime gate
    /// found it: <c>PeriodTest.Comparer_DurationablePeriods</c> calls
    /// <c>IComparer&lt;Period?&gt;.Compare</c> on what <c>Period.CreateComparer</c> returns, that lands
    /// in <c>PeriodComparer.Compare</c>, which calls <c>Period.ToDuration</c> - and breaking
    /// <c>ToDuration</c> failed the test with no path to draw.
    /// </para>
    /// <para>
    /// The BCL can only dispatch into an instance the solution handed it, and handing one over
    /// happens in the source: a conversion to an external type (<c>CreateComparer</c> returning a
    /// <c>PeriodComparer</c> as an <c>IComparer&lt;Period?&gt;</c>, a value boxed into
    /// <c>Assert.AreEqual(object, object)</c>) or an external generic instantiated over the type
    /// (<c>Assert.Equal&lt;Duration&gt;</c>, <c>List&lt;Instant&gt;.Sort</c>). Those sites are recorded
    /// in <see cref="RecordReferences"/>. Each type gets one hub key between its dispatchable
    /// members and its sites, so the edge count is members plus sites rather than their product.
    /// </para>
    /// <para>
    /// The first version connected these members to every constructor of their type instead. It
    /// closed the miss, and on NodaTime it took mean replay selection from 60.7 % to 73.5 %: a
    /// struct such as <c>Duration</c> is constructed nearly everywhere, and its <c>ReadXml</c> - an
    /// <c>IXmlSerializable</c> implementation, reached by a pattern change - then reached nearly
    /// every test. Serializers are already seeded as reflection; the sites are what the BCL needs.
    /// </para>
    /// <para>
    /// Hubs are linked both ways along the solution's own type hierarchy, because a value handed
    /// over as its base type can be any subclass, and a subclass handed over still dispatches to
    /// what it inherits. Siblings therefore share their sites, which is wider than necessary and
    /// never narrower.
    /// </para>
    /// </remarks>
    private void AddExternalDispatchEdges(ImpactGraph graph, INamedTypeSymbol type, string typeKey, string projectName)
    {
        var hub = DispatchHub(typeKey);
        var dispatchable = new List<ISymbol>();

        foreach (var member in type.GetMembers())
        {
            if (member.IsOverride && OverridesOutsideTheSolution(member))
            {
                dispatchable.Add(member);
            }
        }

        foreach (var iface in type.AllInterfaces)
        {
            if (IsTracked(iface) || IsSerializationContract(iface))
            {
                continue;
            }

            foreach (var interfaceMember in iface.GetMembers())
            {
                // Inherited implementations belong to the base type's hub, which is linked to this
                // one below, so only the members declared here are added here.
                if (type.FindImplementationForInterfaceMember(interfaceMember) is { } implementation &&
                    SymbolEqualityComparer.Default.Equals(implementation.ContainingType, type))
                {
                    dispatchable.Add(implementation);
                }
            }
        }

        if (dispatchable.Count > 0)
        {
            AddHubNode(graph, hub, type, projectName);

            foreach (var member in dispatchable)
            {
                if (SymbolKeys.For(member) is { } memberKey)
                {
                    graph.AddEdge(memberKey, hub, EdgeKind.ExternalDispatch);
                }
            }
        }

        for (var baseType = type.BaseType; baseType is not null && IsTracked(baseType); baseType = baseType.BaseType)
        {
            LinkHubs(graph, hub, baseType);
        }

        foreach (var iface in type.AllInterfaces)
        {
            if (IsTracked(iface))
            {
                LinkHubs(graph, hub, iface);
            }
        }

        AddValueEqualityEdges(graph, type, hub, projectName);
    }

    /// <summary>
    /// Leads the hubs of a value type's members into its own, where comparing or printing it calls
    /// theirs without the source saying so (#121).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A record's synthesised <c>Equals</c> calls <c>EqualityComparer&lt;T&gt;.Default.Equals</c> on
    /// every field, its <c>GetHashCode</c> hashes them, and its <c>PrintMembers</c> formats every
    /// public property. A struct that does not declare <c>Equals</c> is compared field by field by
    /// <c>ValueType.Equals</c>. So whoever hands an <c>Order</c> to <c>Assert.Equal</c> calls
    /// <c>Money.Equals</c> when <c>Order</c> holds a <c>Money</c>, and the site of <c>Order</c>'s hub
    /// has to be reachable from <c>Money</c>'s.
    /// </para>
    /// <para>
    /// One way only: comparing a <c>Money</c> never compares an <c>Order</c>. A class that does not
    /// override <c>Equals</c> compares references and calls nothing of its fields', and one that does
    /// says so in its source, where the edges already are.
    /// </para>
    /// </remarks>
    private void AddValueEqualityEdges(ImpactGraph graph, INamedTypeSymbol type, string hub, string projectName)
    {
        var record = type.IsRecord;
        if (!record && !(type.IsValueType && type.TypeKind == TypeKind.Struct && !DeclaresEquals(type)))
        {
            return;
        }

        // An auto-property's value is its backing field, which the loop below already follows.
        var autoProperties = type.GetMembers().OfType<IFieldSymbol>()
            .Select(field => field.AssociatedSymbol).OfType<IPropertySymbol>()
            .ToHashSet<IPropertySymbol>(SymbolEqualityComparer.Default);

        var added = false;
        foreach (var member in type.GetMembers())
        {
            if (member.IsStatic)
            {
                continue;
            }

            var memberType = member switch
            {
                IFieldSymbol field => field.Type,
                IPropertySymbol { IsIndexer: false } property => property.Type,
                _ => null,
            };

            if (memberType is not null && HasTrackedPart(memberType))
            {
                AddDispatchSite(graph, memberType, hub);
                added = true;
            }

            // PrintMembers reads every public property, so a computed getter is part of ToString.
            if (record && member is IPropertySymbol { IsIndexer: false, DeclaredAccessibility: Accessibility.Public, GetMethod: not null } printed &&
                !printed.IsImplicitlyDeclared && !autoProperties.Contains(printed) && SymbolKeys.For(printed) is { } printedKey)
            {
                graph.AddEdge(printedKey, hub, EdgeKind.ExternalDispatch);
                added = true;
            }
        }

        if (added)
        {
            AddHubNode(graph, hub, type, projectName);
        }
    }

    /// <summary>Whether the struct declares an <c>Equals</c> of its own, which <c>ValueType.Equals</c> then does not replace.</summary>
    private static bool DeclaresEquals(INamedTypeSymbol type) =>
        type.GetMembers("Equals").Any(m => m is IMethodSymbol { Parameters.Length: 1, IsImplicitlyDeclared: false });

    /// <summary>Whether the type, its element type or a type argument is one the graph tracks.</summary>
    private bool HasTrackedPart(ITypeSymbol type, int depth = 0) => depth <= 4 && type switch
    {
        IArrayTypeSymbol array => HasTrackedPart(array.ElementType, depth + 1),
        INamedTypeSymbol named => IsTracked(named) || named.TypeArguments.Any(argument => HasTrackedPart(argument, depth + 1)),
        _ => false,
    };

    /// <summary>
    /// Interfaces only a serializer calls - <c>IXmlSerializable</c>, <c>ISerializable</c> and their
    /// kind. Left out of the hubs because serializers are already reflection seeds, so whatever
    /// round-trips a value through one is impacted by any change at all. Counting them here as well
    /// made <c>Instant.ReadXml</c> a dispatchable member of <c>Instant</c>, and every test that hands
    /// an <c>Instant</c> to an assertion reachable from any change to a pattern parser.
    /// </summary>
    private static bool IsSerializationContract(INamedTypeSymbol iface) =>
        iface.ContainingNamespace?.ToDisplayString() is "System.Xml.Serialization" or "System.Runtime.Serialization";

    private static void AddHubNode(ImpactGraph graph, string hub, INamedTypeSymbol type, string projectName)
    {
        var file = DeclarationFile(type);
        graph.AddNode(new SymbolNode
        {
            Key = hub,
            DisplayName = SymbolKeys.DisplayName(type) + " (handed to code outside the solution)",
            Kind = SymbolNodeKind.Other,
            ProjectName = projectName,
            FilePath = file,
            Line = DeclarationLine(type, file),
        });
    }

    private static void LinkHubs(ImpactGraph graph, string hub, INamedTypeSymbol related)
    {
        if (SymbolKeys.For(related) is { } relatedKey)
        {
            var relatedHub = DispatchHub(relatedKey);
            graph.AddEdge(hub, relatedHub, EdgeKind.ExternalDispatch);
            graph.AddEdge(relatedHub, hub, EdgeKind.ExternalDispatch);
        }
    }

    /// <summary>
    /// The key that stands for "an instance of this type, as code outside the solution sees it".
    /// </summary>
    /// <remarks>
    /// Deliberately not a documentation id. Everything that reads a key back - the old-side
    /// resolver, <see cref="SymbolKeys.TypePathOf"/> - expects a <c>T:</c> or <c>M:</c> id after
    /// the assembly, and must not mistake this for the type itself.
    /// </remarks>
    public static string DispatchHub(string typeKey)
    {
        var separator = typeKey.IndexOf('|', StringComparison.Ordinal);
        return separator < 0
            ? "dispatch:" + typeKey
            : string.Concat(typeKey.AsSpan(0, separator + 1), "dispatch:", typeKey.AsSpan(separator + 1));
    }

    /// <summary>
    /// Attributes that name the converter for values of the type they annotate. Matched on the
    /// attribute class and its bases, so a subclass of one counts as well.
    /// </summary>
    private static readonly HashSet<string> ValueConverterAttributes = new(StringComparer.Ordinal)
    {
        "System.ComponentModel.TypeConverterAttribute",
        "System.Text.Json.Serialization.JsonConverterAttribute",
        "Newtonsoft.Json.JsonConverterAttribute",
    };

    /// <summary>
    /// Connects a value converter named on a type to that type's dispatch hub, when
    /// <paramref name="attribute"/> is one of <see cref="ValueConverterAttributes"/>. False for any
    /// other attribute, which is then walked as usual.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>[TypeConverter(typeof(C))]</c> on <c>T</c> means <c>C</c> runs wherever a value of
    /// <c>T</c> is converted: by reflection in the solution - <c>TypeDescriptor.GetConverter</c> and
    /// the serializers are already reflection seeds - or by code outside it that was handed a
    /// <c>T</c>, which is what <c>T</c>'s hub stands for. So <c>C</c> and its own hub both lead
    /// there: the first for a change that reaches its constructor, which only the framework calls,
    /// and the second for one in <c>ConvertFrom</c>, an override the BCL dispatches into.
    /// </para>
    /// <para>
    /// The ordinary attribute edge went from <c>C</c> to the type itself. Nothing calls
    /// <c>C</c>'s constructor, so #40's construction step continued from there into every member
    /// of <c>T</c> and everything that names it: on NodaTime, whose core types all carry one, a
    /// change to <c>PeriodPattern</c> selected all 3,742 tests rather than 682 (#65). And a change
    /// to <c>ConvertFrom</c>, where a converter's behaviour actually lives, reached nothing at all.
    /// </para>
    /// <para>
    /// Only these attributes. NUnit's <c>[TestFixtureSource(typeof(X))]</c> or ASP.NET's
    /// <c>[ServiceFilter(typeof(F))]</c> on a type construct what they name to run the type's own
    /// members, so reaching every member is right for them.
    /// </para>
    /// </remarks>
    private bool AddValueConverterEdges(
        SemanticModel model,
        AttributeSyntax attribute,
        BaseTypeDeclarationSyntax declaration,
        string projectName,
        ImpactGraph graph,
        CancellationToken cancellationToken)
    {
        var attributeClass = (model.GetSymbolInfo(attribute, cancellationToken).Symbol as IMethodSymbol)?.ContainingType;
        while (attributeClass is not null && !ValueConverterAttributes.Contains(attributeClass.ToDisplayString()))
        {
            attributeClass = attributeClass.BaseType;
        }

        if (attributeClass is null ||
            model.GetDeclaredSymbol(declaration, cancellationToken) is not INamedTypeSymbol annotated ||
            SymbolKeys.For(annotated) is not { } annotatedKey)
        {
            return false;
        }

        var hub = DispatchHub(annotatedKey);
        AddHubNode(graph, hub, annotated, projectName);

        foreach (var argument in attribute.ArgumentList?.Arguments ?? default)
        {
            if (argument.Expression is TypeOfExpressionSyntax typeOf &&
                SymbolKeys.Normalize(model.GetTypeInfo(typeOf.Type, cancellationToken).Type) is { } converter &&
                IsTracked(converter) &&
                SymbolKeys.For(converter) is { } converterKey)
            {
                graph.AddEdge(converterKey, hub, EdgeKind.ExternalDispatch);
                graph.AddEdge(DispatchHub(converterKey), hub, EdgeKind.ExternalDispatch);

                // A serializer converts the type through it too (#125).
                graph.AddEdge(converterKey, SerializationHub(annotatedKey), EdgeKind.ExternalDispatch);
                graph.AddEdge(DispatchHub(converterKey), SerializationHub(annotatedKey), EdgeKind.ExternalDispatch);
            }
        }

        return true;
    }

    /// <summary>
    /// The key that stands for "an instance of this type, as a serializer sees it" (#125).
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="DispatchHub"/>, whose members are only what the BCL dispatches into.
    /// Leading every property into that hub would connect them to every assertion and every
    /// collection the type is handed to, which is the precision #30 was measured at and must not
    /// lose. This hub's sites are only the places that hand the type to a serializer.
    /// </remarks>
    public static string SerializationHub(string typeKey)
    {
        var separator = typeKey.IndexOf('|', StringComparison.Ordinal);
        return separator < 0
            ? "serialize:" + typeKey
            : string.Concat(typeKey.AsSpan(0, separator + 1), "serialize:", typeKey.AsSpan(separator + 1));
    }

    /// <summary>
    /// Types whose members serialize, or deserialize, the values they are handed or named by type
    /// argument. Matched on the declaring type of the called member, before any reduction to an
    /// extension method's receiver.
    /// </summary>
    private static readonly HashSet<string> SerializingTypes = new(StringComparer.Ordinal)
    {
        "System.Text.Json.JsonSerializer",
        "Newtonsoft.Json.JsonConvert",
        "Newtonsoft.Json.JsonSerializer",
        "System.Xml.Serialization.XmlSerializer",
        "System.Runtime.Serialization.DataContractSerializer",
        "System.Runtime.Serialization.Json.DataContractJsonSerializer",
        "System.Net.Http.Json.HttpClientJsonExtensions",
        "System.Net.Http.Json.HttpContentJsonExtensions",
        "System.Net.Http.Json.JsonContent",
        "Microsoft.AspNetCore.Http.Results",
        "Microsoft.AspNetCore.Http.TypedResults",
        "Microsoft.AspNetCore.Http.HttpResponseJsonExtensions",
        "Microsoft.AspNetCore.Http.HttpRequestJsonExtensions",
        "Microsoft.AspNetCore.Mvc.ControllerBase",
        "Microsoft.AspNetCore.Mvc.Controller",
    };

    /// <summary>Result types whose constructors take the value the framework will write.</summary>
    private static readonly HashSet<string> SerializingResultTypes = new(StringComparer.Ordinal)
    {
        "Microsoft.AspNetCore.Mvc.ObjectResult",
        "Microsoft.AspNetCore.Mvc.JsonResult",
    };

    /// <summary>
    /// Leads what a serializer reads and writes of a type into the type's serialization hub (#125).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A serializer calls every public property getter to write a value and the setters or a
    /// constructor to read one, with nothing in the source naming any of them. So
    /// <c>Results.Ok(contributor)</c> ran a changed <c>Contributor.Display</c> getter, a functional
    /// test asserted the JSON it produced, and the change selected nothing: the endpoint was a site
    /// of <c>Contributor</c>'s dispatch hub, but no property ever leads into that hub.
    /// </para>
    /// <para>
    /// Public instance properties and fields, which is what <c>System.Text.Json</c> and Newtonsoft
    /// write between them; declared constructors, public or marked <c>[JsonConstructor]</c>, which
    /// is how they read; and a converter named on any of those members. A member's type is
    /// serialized with it, so its hub leads into this one. Hubs are linked both ways along the
    /// solution's own type hierarchy: a value handed over as its base type can be any subclass,
    /// and a subclass carries what it inherits.
    /// </para>
    /// </remarks>
    private void AddSerializationEdges(ImpactGraph graph, INamedTypeSymbol type, string typeKey, string projectName)
    {
        if (type.TypeKind is not (TypeKind.Class or TypeKind.Struct) || type.IsStatic)
        {
            return;
        }

        var hub = SerializationHub(typeKey);
        var added = false;

        foreach (var member in type.GetMembers())
        {
            if (member.IsStatic)
            {
                continue;
            }

            var serialized = member switch
            {
                IPropertySymbol { IsIndexer: false, DeclaredAccessibility: Accessibility.Public } property => property.Type,
                IFieldSymbol { DeclaredAccessibility: Accessibility.Public, AssociatedSymbol: null, IsConst: false } field => field.Type,
                _ => null,
            };

            var constructs = member is IMethodSymbol { MethodKind: MethodKind.Constructor, IsImplicitlyDeclared: false } constructor &&
                             (constructor.DeclaredAccessibility == Accessibility.Public ||
                              constructor.GetAttributes().Any(a => a.AttributeClass?.Name == "JsonConstructorAttribute"));

            if ((serialized is null && !constructs) || SymbolKeys.For(member) is not { } memberKey)
            {
                continue;
            }

            graph.AddEdge(memberKey, hub, EdgeKind.ExternalDispatch);
            added = true;

            if (serialized is null)
            {
                continue;
            }

            AddSerializationSite(graph, serialized, hub);

            foreach (var attribute in member.GetAttributes())
            {
                if (!IsValueConverterAttribute(attribute.AttributeClass))
                {
                    continue;
                }

                foreach (var argument in attribute.ConstructorArguments)
                {
                    if (argument is { Kind: TypedConstantKind.Type, Value: INamedTypeSymbol converter } &&
                        IsTracked(converter) && SymbolKeys.For(converter) is { } converterKey)
                    {
                        graph.AddEdge(converterKey, hub, EdgeKind.ExternalDispatch);
                        graph.AddEdge(DispatchHub(converterKey), hub, EdgeKind.ExternalDispatch);
                    }
                }
            }
        }

        for (var baseType = type.BaseType; baseType is not null && IsTracked(baseType); baseType = baseType.BaseType)
        {
            if (SymbolKeys.For(baseType) is { } baseKey)
            {
                graph.AddEdge(hub, SerializationHub(baseKey), EdgeKind.ExternalDispatch);
                graph.AddEdge(SerializationHub(baseKey), hub, EdgeKind.ExternalDispatch);
                added = true;
            }
        }

        if (added)
        {
            var file = DeclarationFile(type);
            graph.AddNode(new SymbolNode
            {
                Key = hub,
                DisplayName = SymbolKeys.DisplayName(type) + " (as a serializer reads and writes it)",
                Kind = SymbolNodeKind.Other,
                ProjectName = projectName,
                FilePath = file,
                Line = DeclarationLine(type, file),
            });
        }
    }

    private static bool IsValueConverterAttribute(INamedTypeSymbol? attributeClass)
    {
        for (var current = attributeClass; current is not null; current = current.BaseType)
        {
            if (ValueConverterAttributes.Contains(current.ToDisplayString()))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Records <paramref name="source"/> as a place that hands <paramref name="type"/> - and every
    /// solution type among its type arguments - to a serializer.
    /// </summary>
    private void AddSerializationSite(ImpactGraph graph, ITypeSymbol? type, string source, int depth = 0)
    {
        if (type is null || depth > 4)
        {
            return;
        }

        switch (type)
        {
            case IArrayTypeSymbol array:
                AddSerializationSite(graph, array.ElementType, source, depth + 1);
                return;

            case INamedTypeSymbol named:
                if (IsTracked(named) && SymbolKeys.For(named) is { } key)
                {
                    graph.AddEdge(SerializationHub(key), source, EdgeKind.ExternalDispatch);
                }

                foreach (var argument in named.TypeArguments)
                {
                    AddSerializationSite(graph, argument, source, depth + 1);
                }

                return;
        }
    }

    /// <summary>
    /// A call that hands values to a serializer, or maps a route to a handler whose signature the
    /// framework serializes (#125).
    /// </summary>
    /// <remarks>
    /// Both the type arguments and the arguments' own types, before conversion: <c>Results.Ok&lt;T&gt;</c>
    /// names the type, while <c>ControllerBase.Ok(object)</c> only receives it.
    /// </remarks>
    private void AddSerializationSites(
        ImpactGraph graph,
        SemanticModel model,
        SyntaxNode node,
        IMethodSymbol method,
        string memberKey,
        CancellationToken cancellationToken)
    {
        if (IsTracked(method))
        {
            return;
        }

        var arguments = node switch
        {
            InvocationExpressionSyntax invocation => invocation.ArgumentList.Arguments,
            BaseObjectCreationExpressionSyntax creation => creation.ArgumentList?.Arguments ?? default,
            _ => default,
        };

        if (Serializes(method))
        {
            foreach (var argument in method.TypeArguments)
            {
                AddSerializationSite(graph, argument, memberKey);
            }

            foreach (var argument in arguments)
            {
                AddSerializationSite(graph, model.GetTypeInfo(argument.Expression, cancellationToken).Type, memberKey);
            }

            return;
        }

        if (!MapsAnEndpoint(method))
        {
            return;
        }

        // `app.MapGet("/c", () => new Contributor(...))`: the framework writes what the handler
        // returns and binds its parameters from the request. A lambda is keyed as the member that
        // declares it, which is where the route reaches.
        foreach (var argument in arguments)
        {
            if (model.GetTypeInfo(argument.Expression, cancellationToken).ConvertedType is not { } converted ||
                (converted.TypeKind != TypeKind.Delegate && converted.SpecialType != SpecialType.System_Delegate))
            {
                continue;
            }

            var handlerInfo = model.GetSymbolInfo(argument.Expression, cancellationToken);
            if ((handlerInfo.Symbol ?? handlerInfo.CandidateSymbols.FirstOrDefault()) is IMethodSymbol handler &&
                (SymbolKeys.For(handler) ?? memberKey) is { } handlerKey)
            {
                AddHandlerSignatureSites(graph, handler, handlerKey);
            }
        }
    }

    private bool Serializes(IMethodSymbol method)
    {
        var declaring = (method.ReducedFrom ?? method).ContainingType?.OriginalDefinition;
        if (declaring is null || IsTracked(declaring))
        {
            return false;
        }

        if (SerializingTypes.Contains(declaring.ToDisplayString()))
        {
            return true;
        }

        if (method.MethodKind != MethodKind.Constructor)
        {
            return false;
        }

        for (var current = declaring; current is not null; current = current.BaseType)
        {
            if (SerializingResultTypes.Contains(current.ToDisplayString()))
            {
                return true;
            }
        }

        return false;
    }

    private static bool MapsAnEndpoint(IMethodSymbol method) =>
        method.Name.StartsWith("Map", StringComparison.Ordinal) &&
        (method.ReducedFrom ?? method).ContainingNamespace?.ToDisplayString() == "Microsoft.AspNetCore.Builder";

    /// <summary>
    /// What a framework serializes of an endpoint: the value it returns and the parameters it binds,
    /// less those it resolves from the container.
    /// </summary>
    private void AddHandlerSignatureSites(ImpactGraph graph, IMethodSymbol handler, string handlerKey)
    {
        AddSerializationSite(graph, handler.ReturnType, handlerKey);

        foreach (var parameter in handler.Parameters)
        {
            if (!parameter.GetAttributes().Any(a => a.AttributeClass?.Name == "FromServicesAttribute"))
            {
                AddSerializationSite(graph, parameter.Type, handlerKey);
            }
        }
    }

    /// <summary>
    /// Every public action of an MVC controller is a site for what it returns and binds (#125).
    /// </summary>
    /// <remarks>
    /// An action that returns <c>ActionResult&lt;Contributor&gt;</c>, or a <c>Contributor</c>, is
    /// written by the framework after it returns; one that takes a <c>Contributor</c> is read into
    /// from the request body before it starts. Neither shows in the action's body.
    /// </remarks>
    private void AddControllerActionSites(ImpactGraph graph, INamedTypeSymbol type)
    {
        var controller = false;
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (current.ToDisplayString() == "Microsoft.AspNetCore.Mvc.ControllerBase")
            {
                controller = true;
                break;
            }
        }

        if (!controller)
        {
            return;
        }

        foreach (var method in type.GetMembers().OfType<IMethodSymbol>())
        {
            if (method is { MethodKind: MethodKind.Ordinary, IsStatic: false, DeclaredAccessibility: Accessibility.Public } &&
                !method.GetAttributes().Any(a => a.AttributeClass?.Name == "NonActionAttribute") &&
                SymbolKeys.For(method) is { } actionKey)
            {
                AddHandlerSignatureSites(graph, method, actionKey);
            }
        }
    }

    /// <summary>
    /// Records <paramref name="source"/> as a place that hands <paramref name="type"/> - and every
    /// solution type among its type arguments - to code outside the solution.
    /// </summary>
    private void AddDispatchSite(ImpactGraph graph, ITypeSymbol? type, string source, int depth = 0)
    {
        if (type is null || depth > 4)
        {
            return;
        }

        switch (type)
        {
            case IArrayTypeSymbol array:
                AddDispatchSite(graph, array.ElementType, source, depth + 1);
                return;

            case INamedTypeSymbol named:
                if (IsTracked(named) && SymbolKeys.For(named) is { } key)
                {
                    graph.AddEdge(DispatchHub(key), source, EdgeKind.ExternalDispatch);
                }

                foreach (var argument in named.TypeArguments)
                {
                    AddDispatchSite(graph, argument, source, depth + 1);
                }

                return;
        }
    }

    /// <summary>
    /// A reference to an external member or type constructed over solution types - the generic
    /// half of handing an instance over. <c>Assert.Equal&lt;Duration&gt;(a, b)</c> converts nothing,
    /// and still calls <c>Duration.Equals</c> from inside xUnit.
    /// </summary>
    private void AddGenericDispatchSites(ImpactGraph graph, ISymbol? symbol, string source)
    {
        if (symbol is null || IsTracked(symbol))
        {
            return;
        }

        switch (symbol)
        {
            case IMethodSymbol method:
                foreach (var argument in method.TypeArguments)
                {
                    AddDispatchSite(graph, argument, source);
                }

                AddContainingTypeArguments(method.ContainingType);
                break;

            case INamedTypeSymbol named:
                AddContainingTypeArguments(named);
                break;

            case IPropertySymbol or IFieldSymbol or IEventSymbol:
                AddContainingTypeArguments(symbol.ContainingType);
                break;
        }

        void AddContainingTypeArguments(INamedTypeSymbol? containing)
        {
            for (var current = containing; current is not null; current = current.ContainingType)
            {
                foreach (var argument in current.TypeArguments)
                {
                    AddDispatchSite(graph, argument, source);
                }
            }
        }
    }

    /// <summary>
    /// A conversion of a solution type to an external one - the other half of handing an instance
    /// over. <c>IComparer&lt;Period?&gt; CreateComparer() =&gt; new PeriodComparer()</c> and a value
    /// boxed into an <c>object</c> parameter are both this.
    /// </summary>
    private void AddConversionDispatchSite(
        ImpactGraph graph,
        SemanticModel model,
        ExpressionSyntax? expression,
        string source,
        CancellationToken cancellationToken)
    {
        if (expression is null)
        {
            return;
        }

        var info = model.GetTypeInfo(expression, cancellationToken);
        if (info.Type is null || info.ConvertedType is null ||
            SymbolEqualityComparer.Default.Equals(info.Type, info.ConvertedType) ||
            IsTracked(info.ConvertedType) ||
            info.ConvertedType.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
        {
            return;
        }

        AddDispatchSite(graph, info.Type, source);
    }

    /// <summary>
    /// Whether the chain of members this one overrides ends outside the solution. An override of
    /// an override of <c>object.ToString</c> is as reachable from the BCL as the first one.
    /// </summary>
    private bool OverridesOutsideTheSolution(ISymbol member)
    {
        for (var current = Overridden(member); current is not null; current = Overridden(current))
        {
            if (!IsTracked(current))
            {
                return true;
            }
        }

        return false;
    }

    private static ISymbol? Overridden(ISymbol symbol) => symbol switch
    {
        IMethodSymbol method => method.OverriddenMethod,
        IPropertySymbol property => property.OverriddenProperty,
        IEventSymbol @event => @event.OverriddenEvent,
        _ => null,
    };

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

    /// <summary>
    /// One declaration's position, spanning the declaration only.
    /// </summary>
    /// <remarks>
    /// <c>Span</c> rather than <c>FullSpan</c>, and the same node the resolver that reads these
    /// back would have matched. Leading trivia would stretch the first member of a file up over
    /// the using directives and swallow changes that belong to the file rather than to the member;
    /// attribute lists are part of the declaration node already, so an attribute edit lands on the
    /// annotated symbol, which is what it means.
    /// </remarks>
    private static DeclarationSite SiteFor(
        SyntaxTree tree,
        SyntaxNode node,
        string key,
        string projectName,
        string filePath,
        bool isType,
        ISymbol? declared)
    {
        var lines = ChangedSymbolResolver.Span(tree, node);

        return new DeclarationSite
        {
            ProjectName = projectName,
            FilePath = filePath,
            StartLine = lines.Start,
            EndLine = lines.End,
            Key = key,
            IsType = isType,
            IsInlined = declared is IFieldSymbol { IsConst: true } or IFieldSymbol { ContainingType.TypeKind: TypeKind.Enum },
        };
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

        var file = filePath ?? DeclarationFile(normalized);
        graph.AddNode(new SymbolNode
        {
            Key = key,
            DisplayName = SymbolKeys.DisplayName(normalized),
            Kind = SymbolKeys.KindOf(normalized),
            ProjectName = projectName,
            ContainingTypeKey = SymbolKeys.ForContainingType(normalized),
            FilePath = file,
            Line = DeclarationLine(normalized, file),
        });

        return key;
    }

    /// <summary>
    /// Where the declaration in <paramref name="file"/> starts: the identifier's line rather than
    /// the first attribute's, which is the line a reader looks for.
    /// </summary>
    private static int DeclarationLine(ISymbol symbol, string? file)
    {
        foreach (var location in symbol.Locations)
        {
            if (location.IsInSource && location.SourceTree is { } tree &&
                (file is null || string.Equals(tree.FilePath, file, StringComparison.OrdinalIgnoreCase)))
            {
                return location.GetLineSpan().StartLinePosition.Line + 1;
            }
        }

        return 0;
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
