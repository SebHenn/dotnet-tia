using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Tia.Core.Model;

namespace Tia.Core.Analysis;

// ASP.NET Core edges: endpoint handlers, controller actions, Razor views and Razor Pages.
public sealed partial class ReferenceGraphBuilder
{
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
    /// What a Razor view reads of its model leads to wherever the model is handed to a view (#157).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The Razor generator compiles each <c>.cshtml</c> into a class deriving from
    /// <c>RazorPage&lt;TModel&gt;</c>, so <c>@Model.Display</c> is an ordinary reference from that
    /// class, and a change to it reached the view. Nothing reaches the view in turn: MVC finds it
    /// by name when an action returns <c>View(model)</c>. That call already makes the action a site
    /// of the model's serialization hub (#125), so each member of the view leads into the hubs of
    /// its model type and that type's type arguments, and the path is closed.
    /// </para>
    /// <para>
    /// By model type, not by view name: a change that one view reads reaches every action that
    /// hands the same type to a view or a serializer. A Razor Page is not reached this way, since
    /// no action hands its model over: see <see cref="AddRazorPageEdges"/>.
    /// </para>
    /// </remarks>
    private void AddRazorViewEdges(ImpactGraph graph, INamedTypeSymbol type)
    {
        ITypeSymbol? model = null;
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (current.OriginalDefinition.ToDisplayString() == "Microsoft.AspNetCore.Mvc.Razor.RazorPage<TModel>")
            {
                model = current.TypeArguments[0];
                break;
            }
        }

        if (model is null)
        {
            return;
        }

        foreach (var member in type.GetMembers().OfType<IMethodSymbol>())
        {
            if (member.MethodKind is MethodKind.Ordinary or MethodKind.PropertyGet && SymbolKeys.For(member) is { } memberKey)
            {
                AddRenderedType(graph, model, memberKey);
            }
        }
    }

    /// <summary>
    /// What a Razor Page's <c>PageModel</c> runs leads into the page (#169).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The framework creates the model, calls the handler the request names, and renders the page,
    /// so the response is decided by the model's constructors and handlers as much as by the page.
    /// Nothing in the solution calls any of them. The page is reached by its route, which
    /// <see cref="RouteTemplateIndex"/> records with the page's methods as its endpoints, so each
    /// constructor and each handler leads into those methods and the route carries them to the
    /// tests that request it. What the page reads of the model is already an ordinary reference.
    /// </para>
    /// <para>
    /// A handler is what the framework treats as one: a public instance method whose name starts
    /// with <c>On</c>, without <c>[NonHandler]</c>, declared on the model or on a base class in the
    /// solution. That also takes in the filter overrides such as <c>OnPageHandlerExecuting</c>,
    /// which the framework calls around every handler.
    /// </para>
    /// </remarks>
    private void AddRazorPageEdges(ImpactGraph graph, INamedTypeSymbol type)
    {
        if (!RouteTemplateIndex.IsRazorPage(type) ||
            type.GetMembers("Model").OfType<IPropertySymbol>().FirstOrDefault()?.Type is not INamedTypeSymbol model ||
            !IsPageModel(model))
        {
            return;
        }

        var pageMembers = type.GetMembers().OfType<IMethodSymbol>()
            .Where(m => m.MethodKind is MethodKind.Ordinary or MethodKind.PropertyGet)
            .Select(SymbolKeys.For)
            .OfType<string>()
            .ToList();

        for (INamedTypeSymbol? current = model; current is not null && IsTracked(current); current = current.BaseType)
        {
            foreach (var method in current.GetMembers().OfType<IMethodSymbol>())
            {
                var runs = method.MethodKind == MethodKind.Constructor && !method.IsStatic ||
                           method is { MethodKind: MethodKind.Ordinary, IsStatic: false, DeclaredAccessibility: Accessibility.Public } &&
                           method.Name.StartsWith("On", StringComparison.Ordinal) &&
                           !method.GetAttributes().Any(a => a.AttributeClass?.Name == "NonHandlerAttribute");

                if (!runs || SymbolKeys.For(method) is not { } methodKey)
                {
                    continue;
                }

                foreach (var pageMember in pageMembers)
                {
                    graph.AddEdge(methodKey, pageMember, EdgeKind.ExternalDispatch);
                }
            }
        }

        static bool IsPageModel(INamedTypeSymbol candidate)
        {
            for (var current = candidate.BaseType; current is not null; current = current.BaseType)
            {
                if (current.ToDisplayString() == "Microsoft.AspNetCore.Mvc.RazorPages.PageModel")
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// <see cref="AddSerializationSite"/> the other way round: <paramref name="source"/> reads a
    /// value of <paramref name="type"/>, so it leads into the type's hub and those of its type
    /// arguments.
    /// </summary>
    private void AddRenderedType(ImpactGraph graph, ITypeSymbol? type, string source, int depth = 0)
    {
        if (type is null || depth > 4)
        {
            return;
        }

        switch (type)
        {
            case IArrayTypeSymbol array:
                AddRenderedType(graph, array.ElementType, source, depth + 1);
                return;

            case INamedTypeSymbol named:
                if (IsTracked(named) && SymbolKeys.For(named) is { } key)
                {
                    graph.AddEdge(source, SerializationHub(key), EdgeKind.ExternalDispatch);
                }

                foreach (var argument in named.TypeArguments)
                {
                    AddRenderedType(graph, argument, source, depth + 1);
                }

                return;
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
}
