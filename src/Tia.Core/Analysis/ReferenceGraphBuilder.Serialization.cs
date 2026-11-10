using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using Tia.Core.Model;

namespace Tia.Core.Analysis;

// Serialization edges: contracts, value converters, and the sites that hand a type to a serializer.
public sealed partial class ReferenceGraphBuilder
{
    /// <summary>
    /// Interfaces only a serializer calls - <c>IXmlSerializable</c>, <c>ISerializable</c> and their
    /// kind. Left out of the hubs because serializers are already reflection seeds, so whatever
    /// round-trips a value through one is impacted by any change at all. Counting them here as well
    /// made <c>Instant.ReadXml</c> a dispatchable member of <c>Instant</c>, and every test that hands
    /// an <c>Instant</c> to an assertion reachable from any change to a pattern parser.
    /// </summary>
    private static bool IsSerializationContract(INamedTypeSymbol iface) =>
        iface.ContainingNamespace?.ToDisplayString() is "System.Xml.Serialization" or "System.Runtime.Serialization";

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
            AddForwardedArguments(graph, model, node, method, memberKey, cancellationToken);
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

                // `IResult Send(object value) => Results.Ok(value)`: the type is gone here, and
                // only the callers know it. They lead it into this parameter's node (#157).
                if (Unconverted(model.GetOperation(argument.Expression, cancellationToken)) is IParameterReferenceOperation { Parameter: var parameter } &&
                    SymbolKeys.For(parameter.ContainingSymbol) == memberKey)
                {
                    graph.AddEdge(SerializedParameter(memberKey, parameter.Ordinal), memberKey, EdgeKind.ExternalDispatch);
                }
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

    /// <summary>
    /// The node that stands for "a value handed to parameter <paramref name="ordinal"/> of a
    /// solution method that passes it on to a serializer" (#157).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A helper that takes <c>object</c>, or a type parameter, and calls <c>Results.Ok</c> or
    /// <c>Ok</c> with it hands nothing a serializer could be traced from: the type is known only
    /// at its callers, which may be in another project. A fragment's edges may depend on its own
    /// source and its dependencies' declarations, never their bodies, so neither side can ask the
    /// other. They meet at this node instead. A caller leads the hub of each type it passes into
    /// the parameter's node, and the helper leads the node to itself when it serializes the
    /// parameter. A node no helper leads anywhere is a dead end, which costs nothing.
    /// </para>
    /// <para>
    /// Per helper rather than per caller: a change to a type one caller passes reaches the helper,
    /// and through it every caller. That is more than was changed, for one helper's callers only,
    /// and the alternative was reaching none of them.
    /// </para>
    /// </remarks>
    public static string SerializedParameter(string methodKey, int ordinal)
    {
        var separator = methodKey.IndexOf('|', StringComparison.Ordinal);
        var prefix = string.Create(CultureInfo.InvariantCulture, $"serialize-arg{ordinal}:");
        return separator < 0
            ? prefix + methodKey
            : string.Concat(methodKey.AsSpan(0, separator + 1), prefix, methodKey.AsSpan(separator + 1));
    }

    /// <summary>
    /// A call to a solution method: each value handed to a parameter that does not name its type
    /// leads into that parameter's <see cref="SerializedParameter"/> node (#157).
    /// </summary>
    /// <remarks>
    /// Only parameters typed <c>object</c>, <c>dynamic</c> or a type parameter. One that names a
    /// solution type already makes the helper's own serializer call a site of that type. A value
    /// that is itself the caller's parameter forwards that parameter's node, so a chain of helpers
    /// reaches the one that serializes.
    /// </remarks>
    private void AddForwardedArguments(
        ImpactGraph graph,
        SemanticModel model,
        SyntaxNode node,
        IMethodSymbol method,
        string memberKey,
        CancellationToken cancellationToken)
    {
        var arguments = model.GetOperation(node, cancellationToken) switch
        {
            IInvocationOperation invocation => invocation.Arguments,
            IObjectCreationOperation creation => creation.Arguments,
            _ => default,
        };

        if (arguments.IsDefaultOrEmpty)
        {
            return;
        }

        var target = (method.ReducedFrom ?? method).OriginalDefinition;
        if (SymbolKeys.For(target) is not { } targetKey)
        {
            return;
        }

        foreach (var argument in arguments)
        {
            if (argument.Parameter is not { } parameter ||
                parameter.OriginalDefinition.Type is not ({ SpecialType: SpecialType.System_Object } or ITypeParameterSymbol or IDynamicTypeSymbol))
            {
                continue;
            }

            var parameterNode = SerializedParameter(targetKey, parameter.Ordinal);
            var value = Unconverted(argument.Value);
            if (value is IParameterReferenceOperation { Parameter: var forwarded } &&
                SymbolKeys.For(forwarded.ContainingSymbol) == memberKey)
            {
                graph.AddEdge(SerializedParameter(memberKey, forwarded.Ordinal), parameterNode, EdgeKind.ExternalDispatch);
                continue;
            }

            AddSerializationSite(graph, value?.Type, parameterNode);
        }
    }

    private static IOperation? Unconverted(IOperation? operation)
    {
        // Explicit casts too: `(object)contributor` still hands over a Contributor.
        while (operation is IConversionOperation conversion)
        {
            operation = conversion.Operand;
        }

        return operation;
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
}
