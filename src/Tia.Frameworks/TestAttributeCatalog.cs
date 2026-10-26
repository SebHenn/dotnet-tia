using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Tia.Core.Model;

namespace Tia.Frameworks;

/// <summary>
/// The attribute vocabulary of the four supported frameworks. Matching walks the attribute's base
/// chain, so a project's own <c>[IntegrationFact : FactAttribute]</c> is recognised as a test.
/// </summary>
public static class TestAttributeCatalog
{
    private static readonly Dictionary<string, TestFramework> TestMarkers = new(StringComparer.Ordinal)
    {
        ["Xunit.FactAttribute"] = TestFramework.XUnitV2,
        ["Xunit.TheoryAttribute"] = TestFramework.XUnitV2,
        ["NUnit.Framework.TestAttribute"] = TestFramework.NUnit,
        ["NUnit.Framework.TestCaseAttribute"] = TestFramework.NUnit,
        ["NUnit.Framework.TestCaseSourceAttribute"] = TestFramework.NUnit,
        ["NUnit.Framework.TheoryAttribute"] = TestFramework.NUnit,
        ["Microsoft.VisualStudio.TestTools.UnitTesting.TestMethodAttribute"] = TestFramework.MSTest,
        ["Microsoft.VisualStudio.TestTools.UnitTesting.DataTestMethodAttribute"] = TestFramework.MSTest,
        ["TUnit.Core.TestAttribute"] = TestFramework.TUnit,
    };

    private static readonly HashSet<string> ParameterizedMarkers = new(StringComparer.Ordinal)
    {
        "Xunit.TheoryAttribute",
        "Xunit.InlineDataAttribute",
        "Xunit.MemberDataAttribute",
        "Xunit.ClassDataAttribute",
        "NUnit.Framework.TestCaseAttribute",
        "NUnit.Framework.TestCaseSourceAttribute",
        "NUnit.Framework.ValuesAttribute",
        "NUnit.Framework.ValueSourceAttribute",
        "NUnit.Framework.TheoryAttribute",
        "NUnit.Framework.CombinatorialAttribute",
        "Microsoft.VisualStudio.TestTools.UnitTesting.DataRowAttribute",
        "Microsoft.VisualStudio.TestTools.UnitTesting.DynamicDataAttribute",
        "Microsoft.VisualStudio.TestTools.UnitTesting.DataTestMethodAttribute",
        "TUnit.Core.ArgumentsAttribute",
        "TUnit.Core.MethodDataSourceAttribute",
        "TUnit.Core.ClassDataSourceAttribute",
        "TUnit.Core.MatrixAttribute",
    };

    private static readonly HashSet<string> FixtureLifecycleMarkers = new(StringComparer.Ordinal)
    {
        "NUnit.Framework.SetUpAttribute",
        "NUnit.Framework.TearDownAttribute",
        "NUnit.Framework.OneTimeSetUpAttribute",
        "NUnit.Framework.OneTimeTearDownAttribute",
        "Microsoft.VisualStudio.TestTools.UnitTesting.TestInitializeAttribute",
        "Microsoft.VisualStudio.TestTools.UnitTesting.TestCleanupAttribute",
        "Microsoft.VisualStudio.TestTools.UnitTesting.ClassInitializeAttribute",
        "Microsoft.VisualStudio.TestTools.UnitTesting.ClassCleanupAttribute",
        "Microsoft.VisualStudio.TestTools.UnitTesting.AssemblyInitializeAttribute",
        "TUnit.Core.BeforeAttribute",
        "TUnit.Core.AfterAttribute",
    };

    /// <summary>Returns the framework this method belongs to, or null when it is not a test.</summary>
    public static TestFramework? MatchTestMethod(IMethodSymbol method, TestFramework projectFramework)
    {
        foreach (var attribute in method.GetAttributes())
        {
            foreach (var name in BaseChain(attribute.AttributeClass))
            {
                if (!TestMarkers.TryGetValue(name, out var framework))
                {
                    continue;
                }

                // xUnit v2 and v3 share an attribute namespace; only the referenced assemblies
                // tell them apart, and the project already knows which it is.
                if (framework == TestFramework.XUnitV2 && projectFramework == TestFramework.XUnitV3)
                {
                    return TestFramework.XUnitV3;
                }

                return framework;
            }
        }

        return null;
    }

    public static bool IsParameterized(IMethodSymbol method) =>
        // A test method that takes arguments is data-driven regardless of which attribute
        // supplies them.
        method.Parameters.Length > 0 || HasMarker(method.GetAttributes(), ParameterizedMarkers);

    /// <summary>
    /// Methods that run for every test in the assembly or the session, wherever they are declared
    /// (#41). Each used to reach no test at all when it sat in a class with no tests of its own,
    /// which is where they usually sit, and only its own class's tests otherwise.
    /// </summary>
    private static readonly HashSet<string> AssemblyHookMarkers = new(StringComparer.Ordinal)
    {
        "Microsoft.VisualStudio.TestTools.UnitTesting.AssemblyInitializeAttribute",
        "Microsoft.VisualStudio.TestTools.UnitTesting.AssemblyCleanupAttribute",
        "Microsoft.VisualStudio.TestTools.UnitTesting.GlobalTestInitializeAttribute",
        "Microsoft.VisualStudio.TestTools.UnitTesting.GlobalTestCleanupAttribute",
        "System.Runtime.CompilerServices.ModuleInitializerAttribute",
        "TUnit.Core.BeforeEveryAttribute",
        "TUnit.Core.AfterEveryAttribute",
    };

    /// <summary>TUnit's <c>[Before]</c> and <c>[After]</c>, which are assembly-wide for some hook types.</summary>
    private static readonly HashSet<string> ScopedHookMarkers = new(StringComparer.Ordinal)
    {
        "TUnit.Core.BeforeAttribute",
        "TUnit.Core.AfterAttribute",
    };

    /// <summary>
    /// A class whose whole lifecycle is setup for other classes' tests: NUnit's
    /// <c>[SetUpFixture]</c>, which applies to its namespace and everything below it.
    /// </summary>
    private static readonly HashSet<string> SetUpFixtureMarkers = new(StringComparer.Ordinal)
    {
        "NUnit.Framework.SetUpFixtureAttribute",
    };

    /// <summary>
    /// Assembly attributes naming a type the framework constructs once for the whole run: xUnit
    /// v3's <c>[assembly: AssemblyFixture(typeof(T))]</c> and <c>[assembly: TestPipelineStartup(typeof(T))]</c>.
    /// </summary>
    private static readonly HashSet<string> AssemblyFixtureMarkers = new(StringComparer.Ordinal)
    {
        "Xunit.AssemblyFixtureAttribute",
        "Xunit.v3.TestPipelineStartupAttribute",
        "Xunit.TestPipelineStartupAttribute",
    };

    public static bool IsAssemblyHook(IMethodSymbol method)
    {
        foreach (var attribute in method.GetAttributes())
        {
            foreach (var name in BaseChain(attribute.AttributeClass))
            {
                if (AssemblyHookMarkers.Contains(name))
                {
                    return true;
                }

                // `[Before(HookType.Assembly)]`, `[After(HookType.TestSession)]`: the scope is the
                // argument, and only these reach beyond the declaring class.
                if (ScopedHookMarkers.Contains(name) &&
                    attribute.ConstructorArguments is [{ Type: INamedTypeSymbol { TypeKind: TypeKind.Enum } scope, Value: { } value }] &&
                    scope.GetMembers().OfType<IFieldSymbol>().FirstOrDefault(f => Equals(f.ConstantValue, value))?.Name
                        is "Assembly" or "TestSession" or "TestDiscovery")
                {
                    return true;
                }
            }
        }

        return false;
    }

    public static bool IsSetUpFixture(INamedTypeSymbol type) =>
        HasMarker(type.GetAttributes(), SetUpFixtureMarkers);

    /// <summary>The types an assembly's attributes hand to the framework to construct once for the whole run.</summary>
    public static IEnumerable<INamedTypeSymbol> AssemblyFixtureTypes(IAssemblySymbol assembly) =>
        assembly.GetAttributes()
            .Where(a => BaseChain(a.AttributeClass).Any(AssemblyFixtureMarkers.Contains))
            .SelectMany(a => a.ConstructorArguments)
            .Select(argument => argument.Value)
            .OfType<INamedTypeSymbol>();

    public static bool IsFixtureLifecycle(IMethodSymbol method) =>
        HasMarker(method.GetAttributes(), FixtureLifecycleMarkers);

    private static bool HasMarker(ImmutableArray<AttributeData> attributes, HashSet<string> markers) =>
        attributes.Any(a => BaseChain(a.AttributeClass).Any(markers.Contains));

    /// <summary>Fully qualified names of an attribute class and every class it derives from.</summary>
    private static IEnumerable<string> BaseChain(INamedTypeSymbol? attributeClass)
    {
        for (var current = attributeClass; current is not null; current = current.BaseType)
        {
            if (current.SpecialType == SpecialType.System_Object)
            {
                yield break;
            }

            yield return FullName(current);
        }
    }

    internal static string FullName(INamedTypeSymbol type)
    {
        var ns = type.ContainingNamespace;
        var name = type.Name;
        for (var containing = type.ContainingType; containing is not null; containing = containing.ContainingType)
        {
            name = containing.Name + "." + name;
        }

        return ns is { IsGlobalNamespace: false } ? ns.ToDisplayString() + "." + name : name;
    }
}
