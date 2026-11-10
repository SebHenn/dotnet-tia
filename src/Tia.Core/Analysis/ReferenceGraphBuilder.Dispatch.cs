using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Tia.Core.Model;

namespace Tia.Core.Analysis;

// Dispatch edges: request handlers, members reachable from outside the solution, value equality, and the hubs that connect them.
public sealed partial class ReferenceGraphBuilder
{
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
}
