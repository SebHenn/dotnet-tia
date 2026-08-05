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
