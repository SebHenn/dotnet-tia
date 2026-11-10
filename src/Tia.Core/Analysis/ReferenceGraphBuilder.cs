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

public sealed partial class ReferenceGraphBuilder
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
            AddRazorViewEdges(graph, type);
            AddRazorPageEdges(graph, type);
        }
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
