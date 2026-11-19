using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Tia.Core.Analysis;
using Tia.Core.Model;

namespace Tia.DeadCode;

/// <summary>
/// What one compilation keeps alive without anything in the solution referring to it.
/// </summary>
/// <remarks>
/// Read from the compilation rather than the graph, because none of it is a reference between two
/// symbols: an assembly-level attribute has no symbol it is applied to, and a public member's
/// callers are in assemblies that have not been written yet.
/// </remarks>
public static class CompilationRoots
{
    /// <summary>
    /// Whatever an assembly- or module-level attribute names: the attribute, the constructor it
    /// runs, and every type handed to it with <c>typeof</c>. A framework reads those to find the
    /// type it should start - <c>[assembly: AvaloniaTestApplication(typeof(TestApp))]</c>.
    /// </summary>
    public static IEnumerable<string> AssemblyAttributes(Compilation compilation)
    {
        var attributes = compilation.Assembly.GetAttributes()
            .Concat(compilation.Assembly.Modules.SelectMany(module => module.GetAttributes()));

        foreach (var attribute in attributes)
        {
            if (SymbolKeys.For(attribute.AttributeClass) is { } attributeKey)
            {
                yield return attributeKey;
            }

            if (SymbolKeys.For(attribute.AttributeConstructor) is { } constructorKey)
            {
                yield return constructorKey;
            }

            var arguments = attribute.ConstructorArguments.Concat(attribute.NamedArguments.Select(named => named.Value));
            foreach (var type in arguments.SelectMany(TypesIn))
            {
                if (SymbolKeys.For(type) is { } typeKey)
                {
                    yield return typeKey;
                }
            }
        }

        // The arguments above are what the compiler computed, and a constant is folded:
        // `"Friend" + Properties.AssemblyInfo.PublicKeySuffix` arrives as one string and the type
        // that supplied it is gone. The syntax still names it, so every symbol an assembly- or
        // module-level attribute mentions is rooted, with the type that declares it. Rooting more
        // than needed only hides a finding; it cannot report live code.
        foreach (var tree in compilation.SyntaxTrees)
        {
            SemanticModel? model = null;
            foreach (var list in tree.GetRoot().DescendantNodes().OfType<AttributeListSyntax>()
                .Where(list => list.Target?.Identifier.Kind() is SyntaxKind.AssemblyKeyword or SyntaxKind.ModuleKeyword))
            {
                model ??= compilation.GetSemanticModel(tree);
                foreach (var name in list.DescendantNodes().OfType<SimpleNameSyntax>())
                {
                    if (model.GetSymbolInfo(name).Symbol is not { } symbol)
                    {
                        continue;
                    }

                    if (SymbolKeys.For(SymbolKeys.Normalize(symbol)) is { } key)
                    {
                        yield return key;
                    }

                    if (symbol.ContainingType is { } owner && SymbolKeys.For(SymbolKeys.Normalize(owner)) is { } ownerKey)
                    {
                        yield return ownerKey;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Operators the language only lets a type declare as a pair: <c>==</c> with <c>!=</c>,
    /// <c>&lt;</c> with <c>&gt;</c>, <c>&lt;=</c> with <c>&gt;=</c>, and <c>true</c> with <c>false</c>.
    /// </summary>
    /// <remarks>
    /// Deleting one half is error CS0216/CS0218 whatever its callers do, so it is never a finding
    /// to act on. Rooted whether or not the type is live: a dead type keeps its members out of the
    /// findings anyway, and rooting both halves of a dead pair only hides a finding.
    /// </remarks>
    public static IEnumerable<string> PairedOperators(Compilation compilation, CancellationToken cancellationToken = default)
    {
        foreach (var type in ReferenceGraphBuilder.EnumerateSourceTypes(compilation, cancellationToken))
        {
            foreach (var method in type.GetMembers().OfType<IMethodSymbol>())
            {
                if (method.MethodKind == MethodKind.UserDefinedOperator &&
                    method.Name is "op_Equality" or "op_Inequality" or "op_LessThan" or "op_GreaterThan"
                        or "op_LessThanOrEqual" or "op_GreaterThanOrEqual" or "op_True" or "op_False" &&
                    method.Locations.Any(location => location.IsInSource) &&
                    SymbolKeys.For(SymbolKeys.Normalize(method)) is { } key)
                {
                    yield return key;
                }
            }
        }
    }

    /// <summary>
    /// Members, declared on the type or inherited from a base class, that satisfy an interface
    /// declared outside the solution: <c>void IXmlSerializable.WriteXml</c>, or <c>Dispose</c> on a
    /// <c>Base</c> when <c>class Derived : Base, IDisposable</c>.
    /// </summary>
    /// <remarks>
    /// A type that names the interface cannot drop what satisfies it, whoever calls it. For an
    /// interface in the solution the graph's two-way edges already decide. Rooted whether or not the
    /// implementing type is live: a dead type keeps its members out of the findings anyway, and a
    /// dead derived type keeping its base's member out only hides a finding.
    /// </remarks>
    public static IEnumerable<string> ExternalInterfaceImplementations(Compilation compilation, CancellationToken cancellationToken = default)
    {
        foreach (var type in ReferenceGraphBuilder.EnumerateSourceTypes(compilation, cancellationToken))
        {
            foreach (var contract in type.AllInterfaces)
            {
                if (contract.Locations.Any(location => location.IsInSource))
                {
                    continue;
                }

                foreach (var interfaceMember in contract.GetMembers())
                {
                    var implementation = type.FindImplementationForInterfaceMember(interfaceMember);
                    if (implementation is not null &&
                        implementation.Locations.Any(location => location.IsInSource) &&
                        SymbolKeys.For(SymbolKeys.Normalize(implementation)) is { } key)
                    {
                        yield return key;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Every type and member another assembly can name: public, or protected where a subclass
    /// outside could reach it, in a type that is itself visible all the way out.
    /// </summary>
    /// <remarks>
    /// For a library these are roots, since its callers are not in the solution. For an executable
    /// or a test project the caller decides they are not: nothing references those, and
    /// <c>public</c> there is a habit rather than a contract.
    /// </remarks>
    public static IEnumerable<string> PublicApi(Compilation compilation, CancellationToken cancellationToken = default)
    {
        foreach (var type in ReferenceGraphBuilder.EnumerateSourceTypes(compilation, cancellationToken))
        {
            if (!IsVisibleOutside(type))
            {
                continue;
            }

            if (SymbolKeys.For(type) is { } typeKey)
            {
                yield return typeKey;
            }

            foreach (var member in type.GetMembers())
            {
                if (member is not INamedTypeSymbol && IsVisibleOutside(member) && SymbolKeys.For(SymbolKeys.Normalize(member)) is { } memberKey)
                {
                    yield return memberKey;
                }
            }
        }
    }

    private static bool IsVisibleOutside(ISymbol symbol)
    {
        for (var current = symbol; current is not null and not INamespaceSymbol; current = current.ContainingSymbol)
        {
            if (current.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Protected or Accessibility.ProtectedOrInternal))
            {
                return false;
            }
        }

        return true;
    }

    private static IEnumerable<ITypeSymbol> TypesIn(TypedConstant constant) => constant.Kind switch
    {
        TypedConstantKind.Type when constant.Value is ITypeSymbol type => [type],
        TypedConstantKind.Array when !constant.IsNull => constant.Values.SelectMany(TypesIn),
        _ => [],
    };
}
