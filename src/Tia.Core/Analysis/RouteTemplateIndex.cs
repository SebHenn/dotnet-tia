using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Tia.Core.Caching;
using Tia.Core.Model;

namespace Tia.Core.Analysis;

/// <summary>
/// Finds the HTTP routes a project declares and the ones it names.
/// </summary>
/// <remarks>
/// <para>
/// Route templates are collected <b>positionally only</b>: the route argument of a <c>Map*</c> call,
/// and the argument of a routing attribute. That is the entire difference between this and joining
/// any two members that happen to share a string, which <c>docs/benchmarks.md</c> rejects as worse
/// than nothing. A literal collected anywhere else is only ever a <i>reference</i>, and a reference
/// joins to a template something actually declared or to nothing at all.
/// </para>
/// <para>
/// Comparison is ordinal and casing is folded with <see cref="string.ToLowerInvariant"/> throughout:
/// this is matched against user data on machines in every locale, and a culture-sensitive compare
/// here fails in ways that pass in CI.
/// </para>
/// </remarks>
public static class RouteTemplateIndex
{
    private static readonly string[] MapMethods =
        ["MapGet", "MapPost", "MapPut", "MapDelete", "MapPatch", "MapMethods"];

    private static readonly string[] RouteAttributes =
        ["Route", "HttpGet", "HttpPost", "HttpPut", "HttpDelete", "HttpPatch", "HttpHead", "HttpOptions"];

    public static IReadOnlyList<RouteRecord> Scan(Compilation compilation, CancellationToken cancellationToken = default)
    {
        var records = new List<RouteRecord>();

        foreach (var tree in compilation.SyntaxTrees)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var model = compilation.GetSemanticModel(tree);
            var root = tree.GetRoot(cancellationToken);

            var prefixes = GroupPrefixes(root, model, cancellationToken);

            ScanAttributes(root, model, records, cancellationToken);
            ScanMapCalls(root, model, prefixes, records, cancellationToken);
            ScanReferences(root, model, records, cancellationToken);
        }

        return records;
    }

    /// <summary>
    /// Local variables assigned from a <c>MapGroup("/prefix")</c> call, so that
    /// <c>api.MapGet(...)</c> can be resolved against the prefix <c>api</c> carries.
    /// </summary>
    /// <remarks>
    /// Deliberately shallow: one level, same file, initialiser only. A prefix this misses costs a
    /// missing edge, which leaves the existing gap exactly as wide as it already was; it cannot
    /// introduce a miss, because the edge it would have added only ever widens.
    /// </remarks>
    private static Dictionary<string, string> GroupPrefixes(
        SyntaxNode root,
        SemanticModel model,
        CancellationToken cancellationToken)
    {
        var prefixes = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var declarator in root.DescendantNodes().OfType<VariableDeclaratorSyntax>())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (declarator.Initializer?.Value is InvocationExpressionSyntax invocation &&
                NameOf(invocation) == "MapGroup" &&
                FirstStringArgument(invocation, model, cancellationToken) is { } prefix)
            {
                prefixes[declarator.Identifier.ValueText] = prefix;
            }
        }

        return prefixes;
    }

    private static void ScanMapCalls(
        SyntaxNode root,
        SemanticModel model,
        Dictionary<string, string> prefixes,
        List<RouteRecord> records,
        CancellationToken cancellationToken)
    {
        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (Array.IndexOf(MapMethods, NameOf(invocation)) < 0 ||
                FirstStringArgument(invocation, model, cancellationToken) is not { } route)
            {
                continue;
            }

            var template = Normalise(Combine(Prefix(invocation, prefixes, model, cancellationToken), route));
            if (template is null)
            {
                continue;
            }

            // The endpoint is what the route runs, not the line that registers it.
            var owners = HandlerKeys(invocation, model, cancellationToken);
            if (owners.Count == 0 && EnclosingMemberKey(invocation, model, cancellationToken) is { } enclosing)
            {
                owners = [enclosing];
            }

            foreach (var owner in owners)
            {
                records.Add(new RouteRecord(template, owner, IsEndpoint: true));
            }
        }
    }

    private static void ScanAttributes(
        SyntaxNode root,
        SemanticModel model,
        List<RouteRecord> records,
        CancellationToken cancellationToken)
    {
        foreach (var member in root.DescendantNodes().OfType<MemberDeclarationSyntax>())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (member is not (MethodDeclarationSyntax or TypeDeclarationSyntax) ||
                model.GetDeclaredSymbol(member, cancellationToken) is not { } symbol ||
                SymbolKeys.For(symbol) is not { } key)
            {
                continue;
            }

            var own = AttributeRoute(member, model, cancellationToken);
            if (own is null)
            {
                continue;
            }

            // A method's template is relative to whatever its containing type declares, which is
            // how [Route("projects")] on the class and a bare [HttpGet] on the action combine into
            // one endpoint.
            var containerRoute = member is MethodDeclarationSyntax
                ? member.Ancestors().OfType<TypeDeclarationSyntax>().Select(t => AttributeRoute(t, model, cancellationToken)).FirstOrDefault(r => r is not null)
                : null;

            if (Normalise(Combine(containerRoute, own)) is { } template)
            {
                records.Add(new RouteRecord(template, key, IsEndpoint: true));
            }
        }
    }

    /// <summary>
    /// The route argument of a routing attribute, or an empty string when the attribute is present
    /// without one - a bare <c>[HttpGet]</c> still declares the containing type's route.
    /// </summary>
    private static string? AttributeRoute(MemberDeclarationSyntax member, SemanticModel model, CancellationToken cancellationToken)
    {
        foreach (var attribute in member.AttributeLists.SelectMany(l => l.Attributes))
        {
            var name = attribute.Name.ToString();
            name = name[(name.LastIndexOf('.') + 1)..];
            if (name.EndsWith("Attribute", StringComparison.Ordinal))
            {
                name = name[..^"Attribute".Length];
            }

            if (Array.IndexOf(RouteAttributes, name) < 0)
            {
                continue;
            }

            var argument = attribute.ArgumentList?.Arguments.FirstOrDefault();
            if (argument is null)
            {
                return string.Empty;
            }

            return ConstantString(argument.Expression, model, cancellationToken) ?? string.Empty;
        }

        return null;
    }

    /// <summary>
    /// String constants that look like a path, recorded as references. They join only to templates
    /// declared elsewhere, so a literal matching nothing costs nothing but the storage.
    /// </summary>
    private static void ScanReferences(
        SyntaxNode root,
        SemanticModel model,
        List<RouteRecord> records,
        CancellationToken cancellationToken)
    {
        foreach (var literal in root.DescendantNodes().OfType<LiteralExpressionSyntax>())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!literal.IsKind(SyntaxKind.StringLiteralExpression) ||
                literal.Token.ValueText is not { Length: > 1 and < 200 } text ||
                text[0] != '/' ||
                Normalise(text) is not { } template ||
                EnclosingMemberKey(literal, model, cancellationToken) is not { } owner)
            {
                continue;
            }

            records.Add(new RouteRecord(template, owner, IsEndpoint: false));
        }
    }

    /// <summary>
    /// Folds a route to a comparable key: parameter segments become <c>*</c>, slashes are trimmed,
    /// casing is folded. Null when nothing usable is left.
    /// </summary>
    /// <remarks>
    /// A template with no literal segment at all - <c>"/"</c>, <c>""</c>, <c>"{id}"</c> - is
    /// rejected outright. Such a template matches almost any path, and an edge from it would
    /// connect every endpoint to every caller, which is the "worse than nothing" case.
    /// </remarks>
    public static string? Normalise(string? route)
    {
        if (route is null)
        {
            return null;
        }

        var segments = route.Trim().Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return null;
        }

        var normalised = new string[segments.Length];
        var sawLiteral = false;

        for (var i = 0; i < segments.Length; i++)
        {
            var segment = segments[i];
            if (segment.Contains('{', StringComparison.Ordinal))
            {
                normalised[i] = "*";
                continue;
            }

            normalised[i] = segment.ToLowerInvariant();
            sawLiteral = true;
        }

        return sawLiteral ? string.Join('/', normalised) : null;
    }

    /// <summary>
    /// Whether a caller's path meets an endpoint's template. Segment counts must agree and every
    /// template segment must be a wildcard or an exact match, so <c>contributors/7</c> meets
    /// <c>contributors/*</c> and nothing meets a template of a different shape.
    /// </summary>
    public static bool Matches(string template, string reference)
    {
        var left = template.Split('/');
        var right = reference.Split('/');

        if (left.Length != right.Length)
        {
            return false;
        }

        for (var i = 0; i < left.Length; i++)
        {
            if (left[i] != "*" && !string.Equals(left[i], right[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static string? Combine(string? prefix, string? route) =>
        string.IsNullOrEmpty(prefix) ? route
        : string.IsNullOrEmpty(route) ? prefix
        : prefix.TrimEnd('/') + "/" + route.TrimStart('/');

    private static string? Prefix(
        InvocationExpressionSyntax invocation,
        Dictionary<string, string> prefixes,
        SemanticModel model,
        CancellationToken cancellationToken) =>
        invocation.Expression switch
        {
            // api.MapGet(...) where api came from MapGroup("/api")
            MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax identifier }
                when prefixes.TryGetValue(identifier.Identifier.ValueText, out var prefix) => prefix,

            // app.MapGroup("/api").MapGet(...)
            MemberAccessExpressionSyntax { Expression: InvocationExpressionSyntax inner }
                when NameOf(inner) == "MapGroup" => FirstStringArgument(inner, model, cancellationToken),

            _ => null,
        };

    private static string? NameOf(InvocationExpressionSyntax invocation) => invocation.Expression switch
    {
        MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
        IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
        _ => null,
    };

    private static string? FirstStringArgument(
        InvocationExpressionSyntax invocation,
        SemanticModel model,
        CancellationToken cancellationToken)
    {
        var argument = invocation.ArgumentList.Arguments.FirstOrDefault();
        return argument is null ? null : ConstantString(argument.Expression, model, cancellationToken);
    }

    /// <summary>
    /// The compile-time value of an expression, when it has one. Goes through
    /// <see cref="SemanticModel.GetConstantValue(SyntaxNode, CancellationToken)"/> rather than
    /// matching literals, so a route held as a <c>const</c> in a shared class resolves.
    /// </summary>
    private static string? ConstantString(ExpressionSyntax expression, SemanticModel model, CancellationToken cancellationToken) =>
        model.GetConstantValue(expression, cancellationToken) is { HasValue: true, Value: string value } ? value : null;

    /// <summary>
    /// The members a <c>Map*</c> call dispatches to.
    /// </summary>
    /// <remarks>
    /// A method group - <c>MapGet("/x", Contributors.List)</c> - names its handler directly. A
    /// lambda does not: it is its own symbol, and pinning the route to it reaches nothing, because
    /// a lambda is not a node in the graph. What the route actually runs in that case is whatever
    /// the lambda body calls, so those are the endpoints. Missing this was a real miss:
    /// <c>MapGet("/contributors/{id}", (int id) =&gt; Contributors.ById(id))</c> selected no test at
    /// all for a change to <c>ById</c>, because the only edge led to the lambda.
    /// </remarks>
    private static List<string> HandlerKeys(InvocationExpressionSyntax invocation, SemanticModel model, CancellationToken cancellationToken)
    {
        var keys = new List<string>();

        foreach (var argument in invocation.ArgumentList.Arguments.Skip(1))
        {
            if (argument.Expression is AnonymousFunctionExpressionSyntax lambda)
            {
                foreach (var call in lambda.DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    if (model.GetSymbolInfo(call, cancellationToken).Symbol is IMethodSymbol called &&
                        SymbolKeys.For(called) is { } calledKey)
                    {
                        keys.Add(calledKey);
                    }
                }

                continue;
            }

            var info = model.GetSymbolInfo(argument.Expression, cancellationToken);
            if ((info.Symbol ?? info.CandidateSymbols.FirstOrDefault()) is IMethodSymbol handler &&
                handler.MethodKind != MethodKind.LambdaMethod &&
                SymbolKeys.For(handler) is { } key)
            {
                keys.Add(key);
            }
        }

        return keys;
    }

    private static string? EnclosingMemberKey(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken)
    {
        for (var current = node; current is not null; current = current.Parent)
        {
            if (current is MemberDeclarationSyntax or LocalFunctionStatementSyntax &&
                model.GetDeclaredSymbol(current, cancellationToken) is { } symbol &&
                SymbolKeys.For(symbol) is { } key)
            {
                return key;
            }
        }

        return null;
    }
}
