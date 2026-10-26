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
            var byName = new Dictionary<string, int>(StringComparer.Ordinal);
            var overloads = new List<(string From, string To)>();

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

                // Overloads of a test share one reported name, and no filter dialect can tell
                // them apart, so they are one test - but one that every overload's body reaches.
                // Keeping only the first and dropping the rest made a change reached solely
                // through a later overload select nothing.
                if (byName.TryGetValue(method.Name, out var index))
                {
                    overloads.Add((key, classTests[index].SymbolKey));

                    if (!classTests[index].IsParameterized && TestAttributeCatalog.IsParameterized(method))
                    {
                        classTests[index] = classTests[index] with { IsParameterized = true };
                    }

                    continue;
                }

                byName.Add(method.Name, classTests.Count);
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

                foreach (var (from, to) in overloads)
                {
                    graph.AddEdge(from, to, EdgeKind.SharedTestName);
                }
            }
        }

        if (graph is not null && tests.Count > 0)
        {
            AddAssemblyHookEdges(compilation, projectName, tests, graph, cancellationToken);
        }

        return tests;
    }

    /// <summary>
    /// Setup and teardown that run for every test in the assembly or the session, wherever they
    /// are declared, through one hub per test project (#41).
    /// </summary>
    /// <remarks>
    /// <para>
    /// NUnit's <c>[SetUpFixture]</c>, MSTest's <c>[AssemblyInitialize]</c> and
    /// <c>[AssemblyCleanup]</c>, <c>[ModuleInitializer]</c>, xUnit v3's assembly fixtures and
    /// TUnit's assembly- and session-scoped hooks usually sit in a class with no tests of its own.
    /// Discovery skipped such a class before it wired anything, so a change to one reached no test.
    /// </para>
    /// <para>
    /// A hub rather than an edge from each hook to each test: every hook points at the hub and the
    /// hub points at every test, so the edge count grows with hooks plus tests rather than with
    /// their product. The hub is not a symbol, and its key is not a documentation id.
    /// </para>
    /// </remarks>
    private static void AddAssemblyHookEdges(
        Compilation compilation,
        string projectName,
        IReadOnlyList<TestMethod> tests,
        ImpactGraph graph,
        CancellationToken cancellationToken)
    {
        var hooks = new List<ISymbol>();

        foreach (var type in ReferenceGraphBuilder.EnumerateSourceTypes(compilation, cancellationToken))
        {
            if (TestAttributeCatalog.IsSetUpFixture(type))
            {
                hooks.Add(type);
                hooks.AddRange(EnumerateFixtureMembers(type));
                continue;
            }

            hooks.AddRange(type.GetMembers().OfType<IMethodSymbol>().Where(TestAttributeCatalog.IsAssemblyHook));
        }

        foreach (var fixture in TestAttributeCatalog.AssemblyFixtureTypes(compilation.Assembly))
        {
            if (fixture.Locations.Any(l => l.IsInSource))
            {
                hooks.Add(fixture);
                hooks.AddRange(EnumerateFixtureMembers(fixture));
            }
        }

        var hookKeys = hooks.Select(SymbolKeys.For).OfType<string>().ToList();

        // xUnit applies a BeforeAfterTestAttribute given at assembly level to every test, and calls
        // what it overrides. The graph has no node for the assembly to hang that usage on, so the
        // attribute's hub leads here instead (#119).
        foreach (var attribute in compilation.Assembly.GetAttributes())
        {
            if (attribute.AttributeClass is { } attributeClass &&
                attributeClass.Locations.Any(l => l.IsInSource) &&
                SymbolKeys.For(attributeClass) is { } attributeKey)
            {
                hookKeys.Add(ReferenceGraphBuilder.DispatchHub(attributeKey));
            }
        }

        hookKeys = hookKeys.Distinct(StringComparer.Ordinal).ToList();
        if (hookKeys.Count == 0)
        {
            return;
        }

        var hub = AssemblySetupHub(compilation.AssemblyName ?? projectName);
        graph.AddNode(new SymbolNode
        {
            Key = hub,
            DisplayName = projectName + " (assembly- and session-wide setup)",
            Kind = SymbolNodeKind.Other,
            ProjectName = projectName,
        });

        foreach (var hookKey in hookKeys)
        {
            graph.AddEdge(hookKey, hub, EdgeKind.Fixture);
        }

        foreach (var test in tests)
        {
            graph.AddEdge(hub, test.SymbolKey, EdgeKind.Fixture);
        }
    }

    /// <remarks>
    /// Shaped like <c>ReferenceGraphBuilder.DispatchHub</c>: an assembly prefix, then something no
    /// documentation id can be, so nothing that reads a key back mistakes it for a symbol.
    /// </remarks>
    internal static string AssemblySetupHub(string assemblyName) => assemblyName + "|setup:assembly";

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

    /// <summary>
    /// Every ordinary method the class has, its own first and then each base class's in turn.
    /// </summary>
    /// <remarks>
    /// A method inherited from a base test class runs once per concrete subclass, and the filter
    /// has to name the subclass, so it is reported per subclass. Nothing is dropped here by name:
    /// that happens in <see cref="Discover"/>, among test methods only. Dropping it here, before
    /// the attribute check, let a private helper declared above a test of the same name hide the
    /// test entirely.
    /// </remarks>
    private static IEnumerable<IMethodSymbol> EnumerateMethodsIncludingInherited(INamedTypeSymbol type)
    {
        for (var current = type; current is not null && current.SpecialType != SpecialType.System_Object; current = current.BaseType)
        {
            foreach (var member in current.GetMembers())
            {
                if (member is IMethodSymbol { MethodKind: MethodKind.Ordinary } method)
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
