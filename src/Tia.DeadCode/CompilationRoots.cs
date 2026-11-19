using Microsoft.CodeAnalysis;
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
    }

    /// <summary>
    /// Members a type inherits from its base class that satisfy an interface it declares outside
    /// the solution: <c>class Derived : Base, IDisposable</c> with <c>Dispose</c> on <c>Base</c>.
    /// The graph links a type's own members to the interface's callers; this one is declared
    /// elsewhere, and deleting it leaves <c>Derived</c> not implementing what it names.
    /// </summary>
    /// <remarks>
    /// Rooted whether or not <c>Derived</c> is live. A dead <c>Derived</c> keeps its base's member
    /// out of the findings, which only hides one - the direction that cannot cause a bad deletion.
    /// </remarks>
    public static IEnumerable<string> InheritedInterfaceImplementations(Compilation compilation, CancellationToken cancellationToken = default)
    {
        foreach (var type in ReferenceGraphBuilder.EnumerateSourceTypes(compilation, cancellationToken))
        {
            foreach (var contract in type.AllInterfaces)
            {
                foreach (var interfaceMember in contract.GetMembers())
                {
                    var implementation = type.FindImplementationForInterfaceMember(interfaceMember);
                    if (implementation is not null &&
                        !SymbolEqualityComparer.Default.Equals(implementation.ContainingType, type) &&
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
