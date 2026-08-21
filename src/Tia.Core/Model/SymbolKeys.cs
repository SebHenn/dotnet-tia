using Microsoft.CodeAnalysis;

namespace Tia.Core.Model;

/// <summary>
/// Stable, cross-compilation identifiers for symbols.
/// </summary>
/// <remarks>
/// Roslyn's own <c>SymbolKey</c> is internal, so the key is built from the documentation comment
/// id qualified by the declaring assembly. Doc ids are stable across recompiles (which is what
/// makes the on-disk graph cache viable) and are unique within an assembly; the assembly prefix
/// keeps two same-named types in different projects apart.
/// </remarks>
public static class SymbolKeys
{
    private static readonly SymbolDisplayFormat FallbackFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        memberOptions: SymbolDisplayMemberOptions.IncludeParameters | SymbolDisplayMemberOptions.IncludeContainingType,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    /// <summary>Computes the graph key for a symbol, or null when the symbol has no node.</summary>
    public static string? For(ISymbol? symbol)
    {
        symbol = Normalize(symbol);
        if (symbol is null)
        {
            return null;
        }

        var docId = symbol.GetDocumentationCommentId();
        if (string.IsNullOrEmpty(docId) || docId!.StartsWith("!:", StringComparison.Ordinal))
        {
            docId = symbol.Kind + ":" + symbol.ToDisplayString(FallbackFormat);
        }

        var assembly = symbol.ContainingAssembly?.Identity.Name ?? "<none>";
        return string.Concat(assembly, "|", docId);
    }

    /// <summary>
    /// The dotted name path of a type key - namespaces, then containing types, then the type -
    /// with generic arity dropped, or null when the key names something other than a type.
    /// </summary>
    /// <remarks>
    /// The inverse of the part of <see cref="For"/> that matters for name matching, and the reason
    /// the old side of a diff can be bound without a compilation. Arity is dropped because the
    /// caller matching against this has only an unbound syntax tree, where arity cannot be
    /// established reliably; over-matching there is safe and under-matching is a missed test.
    /// </remarks>
    public static string? TypePathOf(string key)
    {
        var documentationId = DocumentationIdOf(key);
        if (documentationId is null || !documentationId.StartsWith("T:", StringComparison.Ordinal))
        {
            return null;
        }

        // Per segment, not once: a type nested inside a generic one arrives as `N.Outer`1.Inner`,
        // and cutting at the first backtick would answer `N.Outer` - a different type, which would
        // then match a change to it and not to the one that moved.
        return string.Join('.', documentationId["T:".Length..].Split('.').Select(StripArity));
    }

    /// <summary>
    /// The declared name of the member a key names, without its containing type, parameters or
    /// generic arity, or null when the key is not a member key this can read.
    /// </summary>
    /// <remarks>
    /// An explicitly implemented member arrives as <c>Namespace#IInterface#Member</c>, because a
    /// documentation id cannot carry the dots; the name is the part after the last separator. A
    /// constructor stays <c>#ctor</c>, which matches nothing a caller looks up by name - and that
    /// is correct, because the syntax side has only the type's own identifier to offer, so the
    /// lookup falls through to the declaring type exactly as it should.
    /// </remarks>
    public static string? SimpleNameOf(string key)
    {
        var documentationId = DocumentationIdOf(key);
        if (documentationId is null || documentationId.Length < 3 || documentationId[1] != ':')
        {
            return null;
        }

        var body = documentationId[2..];

        // Parameters and a conversion operator's return type both sit after the name.
        var parameters = body.IndexOf('(', StringComparison.Ordinal);
        if (parameters >= 0)
        {
            body = body[..parameters];
        }

        var lastDot = body.LastIndexOf('.');
        if (lastDot >= 0)
        {
            body = body[(lastDot + 1)..];
        }

        var lastHash = body.LastIndexOf('#');
        if (lastHash > 0)
        {
            body = body[(lastHash + 1)..];
        }

        body = StripArity(body);
        return body.Length == 0 ? null : body;
    }

    /// <summary>
    /// The documentation id inside a key, or null when the key carries the fallback form instead -
    /// which is a display string, not an id, and cannot be taken apart the same way.
    /// </summary>
    private static string? DocumentationIdOf(string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        var separator = key.IndexOf('|', StringComparison.Ordinal);
        if (separator < 0)
        {
            return null;
        }

        var documentationId = key[(separator + 1)..];
        return documentationId.Length > 2 && documentationId[1] == ':' && char.IsUpper(documentationId[0])
            ? documentationId
            : null;
    }

    private static string StripArity(string value)
    {
        var arity = value.IndexOf('`', StringComparison.Ordinal);
        return arity < 0 ? value : value[..arity];
    }

    /// <summary>The key of the type that owns this symbol, if any.</summary>
    public static string? ForContainingType(ISymbol? symbol)
    {
        var normalized = Normalize(symbol);
        if (normalized is INamedTypeSymbol named)
        {
            return named.ContainingType is null ? null : For(named.ContainingType);
        }

        return normalized?.ContainingType is null ? null : For(normalized.ContainingType);
    }

    /// <summary>
    /// Collapses a symbol onto the entity the graph actually tracks: constructed generics become
    /// their original definition, accessors become their property or event, lambdas and locals
    /// become the member that declares them.
    /// </summary>
    public static ISymbol? Normalize(ISymbol? symbol)
    {
        for (var i = 0; symbol is not null && i < 16; i++)
        {
            switch (symbol)
            {
                case IErrorTypeSymbol:
                case IDynamicTypeSymbol:
                case INamespaceSymbol:
                case IPreprocessingSymbol:
                    return null;

                case IArrayTypeSymbol array:
                    symbol = array.ElementType;
                    continue;

                case IPointerTypeSymbol pointer:
                    symbol = pointer.PointedAtType;
                    continue;

                case ITypeParameterSymbol or IParameterSymbol or ILocalSymbol or IRangeVariableSymbol or ILabelSymbol:
                    symbol = symbol.ContainingSymbol;
                    continue;

                case IMethodSymbol { MethodKind: MethodKind.LocalFunction or MethodKind.AnonymousFunction } method:
                    symbol = method.ContainingSymbol;
                    continue;

                case IMethodSymbol { ReducedFrom: { } reduced }:
                    symbol = reduced;
                    continue;

                case IMethodSymbol { AssociatedSymbol: { } associated }:
                    // Property and event accessors are tracked as the property or event itself:
                    // a change to either half is a change to the member.
                    symbol = associated;
                    continue;
            }

            if (!SymbolEqualityComparer.Default.Equals(symbol, symbol.OriginalDefinition))
            {
                symbol = symbol.OriginalDefinition;
                continue;
            }

            break;
        }

        return symbol switch
        {
            null => null,
            INamespaceSymbol => null,
            IAssemblySymbol => null,
            IModuleSymbol => null,
            _ => symbol,
        };
    }

    public static SymbolNodeKind KindOf(ISymbol symbol) => symbol switch
    {
        INamedTypeSymbol => SymbolNodeKind.Type,
        IMethodSymbol => SymbolNodeKind.Method,
        IPropertySymbol => SymbolNodeKind.Property,
        IEventSymbol => SymbolNodeKind.Event,
        IFieldSymbol => SymbolNodeKind.Field,
        _ => SymbolNodeKind.Other,
    };

    private static readonly SymbolDisplayFormat DisplayFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypes,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        memberOptions: SymbolDisplayMemberOptions.IncludeParameters | SymbolDisplayMemberOptions.IncludeContainingType,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    public static string DisplayName(ISymbol symbol) => symbol.ToDisplayString(DisplayFormat);
}
