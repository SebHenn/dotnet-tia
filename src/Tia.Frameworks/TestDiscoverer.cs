using Microsoft.CodeAnalysis;
using Tia.Core.Analysis;
using Tia.Core.Model;

namespace Tia.Frameworks;

/// <summary>
/// Finds test methods by scanning attributes, and wires the fixture edges that connect a class's
/// plumbing to the tests it serves.
/// </summary>
public sealed class TestDiscoverer
{
    /// <param name="graph">
    /// When supplied, fixture edges are added to it: constructors, setup and teardown methods,
    /// fields and class fixtures all belong to every test in the class, and none of that is
    /// visible as a call from the test method itself.
    /// </param>
    public IReadOnlyList<TestMethod> Discover(
        Compilation compilation,
        string projectName,
        TestFramework projectFramework,
        ImpactGraph? graph = null,
        CancellationToken cancellationToken = default)
    {
        var tests = new List<TestMethod>();

        foreach (var type in ReferenceGraphBuilder.EnumerateSourceTypes(compilation, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (type.TypeKind != TypeKind.Class || type.IsAbstract || type.IsStatic)
            {
                continue;
            }

            var classKey = SymbolKeys.For(type);
            if (classKey is null)
            {
                continue;
            }

            var classTests = new List<TestMethod>();

            foreach (var method in EnumerateMethodsIncludingInherited(type))
            {
                var framework = TestAttributeCatalog.MatchTestMethod(method, projectFramework);
                if (framework is null)
                {
                    continue;
                }

                var key = SymbolKeys.For(method);
                if (key is null)
                {
                    continue;
                }

                classTests.Add(new TestMethod
                {
                    SymbolKey = key,
                    ClassKey = classKey,
                    Namespace = type.ContainingNamespace is { IsGlobalNamespace: false } ns ? ns.ToDisplayString() : string.Empty,
                    ClassName = NestedClassName(type),
                    MethodName = method.Name,
                    ProjectName = projectName,
                    Framework = framework.Value,
                    IsParameterized = TestAttributeCatalog.IsParameterized(method),
                });
            }

            if (classTests.Count == 0)
            {
                continue;
            }

            tests.AddRange(classTests);

            if (graph is not null)
            {
                AddFixtureEdges(type, classTests, graph);
            }
        }

        return tests;
    }

    /// <summary>
    /// Everything a test class carries belongs to every test in it. Constructors run per test in
    /// xUnit, <c>[SetUp]</c> runs per test in NUnit, field initialisers run with the constructor,
    /// and class or collection fixtures are constructed before any of them - so a change to any of
    /// those has to reach every test in the class, even though no test calls them.
    /// </summary>
    private static void AddFixtureEdges(INamedTypeSymbol type, List<TestMethod> classTests, ImpactGraph graph)
    {
        var testKeys = new HashSet<string>(classTests.Select(t => t.SymbolKey), StringComparer.Ordinal);

        foreach (var member in EnumerateFixtureMembers(type))
        {
            var memberKey = SymbolKeys.For(member);
            if (memberKey is null || testKeys.Contains(memberKey))
            {
                continue;
            }

            foreach (var testKey in testKeys)
            {
                graph.AddEdge(memberKey, testKey, EdgeKind.Fixture);
            }
        }

        // xUnit's IClassFixture<T> / ICollectionFixture<T>: the fixture type itself is
        // constructed by the framework and shared across the class.
        foreach (var iface in type.AllInterfaces)
        {
            if (iface.Name is not ("IClassFixture" or "ICollectionFixture" or "IAssemblyFixture"))
            {
                continue;
            }

            foreach (var argument in iface.TypeArguments)
            {
                var fixtureKey = SymbolKeys.For(argument);
                if (fixtureKey is null)
                {
                    continue;
                }

                foreach (var testKey in testKeys)
                {
                    graph.AddEdge(fixtureKey, testKey, EdgeKind.Fixture);
                }
            }
        }
    }

    private static IEnumerable<ISymbol> EnumerateFixtureMembers(INamedTypeSymbol type)
    {
        for (var current = type; current is not null && current.SpecialType != SpecialType.System_Object; current = current.BaseType)
        {
            if (!current.Locations.Any(l => l.IsInSource))
            {
                yield break;
            }

            foreach (var member in current.GetMembers())
            {
                switch (member)
                {
                    case IMethodSymbol { MethodKind: MethodKind.Constructor }:
                    case IFieldSymbol { IsImplicitlyDeclared: false }:
                    case IPropertySymbol:
                        yield return member;
                        break;

                    case IMethodSymbol method when TestAttributeCatalog.IsFixtureLifecycle(method)
                                                   || method.Name is "Dispose" or "DisposeAsync" or "InitializeAsync":
                        yield return method;
                        break;
                }
            }
        }
    }

    private static IEnumerable<IMethodSymbol> EnumerateMethodsIncludingInherited(INamedTypeSymbol type)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (var current = type; current is not null && current.SpecialType != SpecialType.System_Object; current = current.BaseType)
        {
            foreach (var member in current.GetMembers())
            {
                if (member is not IMethodSymbol { MethodKind: MethodKind.Ordinary } method)
                {
                    continue;
                }

                // A method inherited from a base test class runs once per concrete subclass, and
                // the filter has to name the subclass, so it is reported per subclass here.
                if (seen.Add(method.Name))
                {
                    yield return method;
                }
            }
        }
    }

    internal static string NestedClassName(INamedTypeSymbol type)
    {
        var name = type.MetadataName;
        for (var containing = type.ContainingType; containing is not null; containing = containing.ContainingType)
        {
            name = containing.MetadataName + "+" + name;
        }

        return name;
    }
}
