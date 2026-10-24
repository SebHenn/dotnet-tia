using Tia.Core.Analysis;
using Tia.Core.Model;
using Tia.Frameworks;

namespace Tia.Core.Tests;

/// <summary>
/// Setup that runs for every test in the assembly or the session reaches every test (#41).
/// </summary>
/// <remarks>
/// These hooks usually sit in a class with no tests of its own, and discovery skipped such a class
/// before it wired anything, so a change to one selected nothing. Each case puts the hook in a
/// class of its own and two tests in two other classes, and changes the hook.
/// </remarks>
public sealed class AssemblyHookTests
{
    private const string Tests = """
        namespace App.Tests
        {
            public class FirstTests { [TEST] public void A() { } }
            public class SecondTests { [TEST] public void B() { } }
        }
        """;

    public static TheoryData<string, TestFramework, string, string, string> Hooks() => new()
    {
        {
            "NUnit [SetUpFixture]", TestFramework.NUnit, CompilationHarness.NUnitShim + """
            namespace App.Tests
            {
                [NUnit.Framework.SetUpFixture]
                public class Setup { [NUnit.Framework.OneTimeSetUp] public void Start() { } }
            }
            """, "App.Tests.Setup", "Start"
        },
        {
            "MSTest [AssemblyInitialize]", TestFramework.MSTest, CompilationHarness.MSTestShim + """
            namespace App.Tests
            {
                public class Setup { [Microsoft.VisualStudio.TestTools.UnitTesting.AssemblyInitialize] public static void Start(Microsoft.VisualStudio.TestTools.UnitTesting.TestContext context) { } }
            }
            """, "App.Tests.Setup", "Start"
        },
        {
            "MSTest [AssemblyCleanup]", TestFramework.MSTest, CompilationHarness.MSTestShim + """
            namespace App.Tests
            {
                public class Setup { [Microsoft.VisualStudio.TestTools.UnitTesting.AssemblyCleanup] public static void Stop() { } }
            }
            """, "App.Tests.Setup", "Stop"
        },
        {
            "[ModuleInitializer]", TestFramework.XUnitV3, CompilationHarness.XunitShim + """
            namespace App.Tests
            {
                internal static class Setup { [System.Runtime.CompilerServices.ModuleInitializer] internal static void Start() { } }
            }
            """, "App.Tests.Setup", "Start"
        },
        {
            // Ahead of the shim: an assembly attribute has to precede every namespace in its file.
            "xUnit v3 [assembly: AssemblyFixture]", TestFramework.XUnitV3, "[assembly: Xunit.AssemblyFixture(typeof(App.Tests.Database))]\n" + CompilationHarness.XunitShim + """
            namespace App.Tests
            {
                public class Database { public Database() { Seeded = true; } public bool Seeded { get; } }
            }
            """, "App.Tests.Database", ".ctor"
        },
        {
            "TUnit [Before(Assembly)]", TestFramework.TUnit, CompilationHarness.TUnitShim + """
            namespace App.Tests
            {
                public static class Setup { [TUnit.Core.Before(TUnit.Core.HookType.Assembly)] public static void Start() { } }
            }
            """, "App.Tests.Setup", "Start"
        },
        {
            "TUnit [After(TestSession)]", TestFramework.TUnit, CompilationHarness.TUnitShim + """
            namespace App.Tests
            {
                public static class Setup { [TUnit.Core.After(TUnit.Core.HookType.TestSession)] public static void Stop() { } }
            }
            """, "App.Tests.Setup", "Stop"
        },
        {
            "TUnit [BeforeEvery(Test)]", TestFramework.TUnit, CompilationHarness.TUnitShim + """
            namespace App.Tests
            {
                public static class Setup { [TUnit.Core.BeforeEvery(TUnit.Core.HookType.Test)] public static void Each() { } }
            }
            """, "App.Tests.Setup", "Each"
        },
    };

    [Theory]
    [MemberData(nameof(Hooks))]
    public void A_hook_in_a_class_without_tests_reaches_every_test(
        string shape, TestFramework framework, string hook, string hookType, string hookMember)
    {
        var compilation = CompilationHarness.CompileValid(hook + Tests.Replace("TEST", TestAttribute(framework), StringComparison.Ordinal));
        var graph = CompilationHarness.BuildGraph(compilation);

        var tests = new TestDiscoverer().Discover(compilation, "App.Tests", framework, graph);
        Assert.Equal(2, tests.Count);

        var impacted = new ImpactSelector().Traverse(graph, [CompilationHarness.KeyOf(compilation, hookType, hookMember)]).Impacted;

        Assert.True(impacted.Contains(CompilationHarness.KeyOf(compilation, "App.Tests.FirstTests", "A")), shape);
        Assert.True(impacted.Contains(CompilationHarness.KeyOf(compilation, "App.Tests.SecondTests", "B")), shape);
    }

    /// <summary>
    /// A class-scoped TUnit hook still belongs to its own class only. Routing it to every test
    /// would select a whole project for a change that affects one class.
    /// </summary>
    [Fact]
    public void A_class_scoped_hook_does_not_reach_other_classes()
    {
        var compilation = CompilationHarness.CompileValid(CompilationHarness.TUnitShim + """
            namespace App.Tests
            {
                public class FirstTests
                {
                    [TUnit.Core.Before(TUnit.Core.HookType.Class)] public static void Start() { }
                    [TUnit.Core.Test] public void A() { }
                }

                public class SecondTests { [TUnit.Core.Test] public void B() { } }
            }
            """);
        var graph = CompilationHarness.BuildGraph(compilation);
        new TestDiscoverer().Discover(compilation, "App.Tests", TestFramework.TUnit, graph);

        var impacted = new ImpactSelector().Traverse(graph, [CompilationHarness.KeyOf(compilation, "App.Tests.FirstTests", "Start")]).Impacted;

        Assert.Contains(CompilationHarness.KeyOf(compilation, "App.Tests.FirstTests", "A"), impacted);
        Assert.DoesNotContain(CompilationHarness.KeyOf(compilation, "App.Tests.SecondTests", "B"), impacted);
    }

    /// <summary>
    /// One hub per project, so the edges grow with hooks plus tests: three hooks and two tests
    /// make five hub edges, not six direct ones, and it stays linear as either grows.
    /// </summary>
    [Fact]
    public void Hooks_reach_tests_through_one_hub()
    {
        var compilation = CompilationHarness.CompileValid(CompilationHarness.MSTestShim + """
            namespace App.Tests
            {
                public class Setup
                {
                    [Microsoft.VisualStudio.TestTools.UnitTesting.AssemblyInitialize] public static void Start(Microsoft.VisualStudio.TestTools.UnitTesting.TestContext context) { }
                    [Microsoft.VisualStudio.TestTools.UnitTesting.AssemblyCleanup] public static void Stop() { }
                    [System.Runtime.CompilerServices.ModuleInitializer] internal static void Load() { }
                }
            }
            """ + Tests.Replace("TEST", TestAttribute(TestFramework.MSTest), StringComparison.Ordinal));
        var graph = CompilationHarness.BuildGraph(compilation);
        new TestDiscoverer().Discover(compilation, "App.Tests", TestFramework.MSTest, graph);

        var hub = TestDiscoverer.AssemblySetupHub(compilation.AssemblyName!);

        Assert.Equal(2, graph.DependentsOf(hub).Count);
        foreach (var hook in new[] { "Start", "Stop", "Load" })
        {
            Assert.Equal(EdgeKind.Fixture, graph.DependentsOf(CompilationHarness.KeyOf(compilation, "App.Tests.Setup", hook))[hub]);
        }
    }

    private static string TestAttribute(TestFramework framework) => framework switch
    {
        TestFramework.NUnit => "NUnit.Framework.Test",
        TestFramework.MSTest => "Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod",
        TestFramework.TUnit => "TUnit.Core.Test",
        _ => "Xunit.Fact",
    };
}
