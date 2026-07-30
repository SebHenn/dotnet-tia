using Tia.Core.Analysis;
using Tia.Core.Model;
using Tia.Frameworks;

namespace Tia.Core.Tests;

public sealed class TestDiscoveryTests
{
    [Fact]
    public void Facts_and_theories_are_discovered()
    {
        var compilation = CompilationHarness.CompileValid(CompilationHarness.XunitShim + """
            namespace App.Tests
            {
                public class WidgetTests
                {
                    [Xunit.Fact] public void Works() { }
                    [Xunit.Theory][Xunit.InlineData(1)] public void WorksWith(int value) { }
                    public void NotATest() { }
                }
            }
            """);

        var tests = new TestDiscoverer().Discover(compilation, "App.Tests", TestFramework.XUnitV2);

        Assert.Equal(["App.Tests.WidgetTests.Works", "App.Tests.WidgetTests.WorksWith"],
            tests.Select(t => t.FullyQualifiedName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Parameterized_tests_are_selected_whole()
    {
        var compilation = CompilationHarness.CompileValid(CompilationHarness.XunitShim + """
            namespace App.Tests
            {
                public class WidgetTests
                {
                    [Xunit.Theory][Xunit.InlineData(1)][Xunit.InlineData(2)] public void Cases(int value) { }
                }
            }
            """);

        var test = Assert.Single(new TestDiscoverer().Discover(compilation, "App.Tests", TestFramework.XUnitV2));

        Assert.True(test.IsParameterized);
    }

    [Fact]
    public void An_attribute_derived_from_Fact_still_counts()
    {
        var compilation = CompilationHarness.CompileValid(CompilationHarness.XunitShim + """
            namespace App.Tests
            {
                public class IntegrationFactAttribute : Xunit.FactAttribute { }
                public class WidgetTests { [IntegrationFact] public void Works() { } }
            }
            """);

        Assert.Single(new TestDiscoverer().Discover(compilation, "App.Tests", TestFramework.XUnitV2));
    }

    [Fact]
    public void Tests_inherited_from_a_base_class_are_reported_against_the_concrete_class()
    {
        var compilation = CompilationHarness.CompileValid(CompilationHarness.XunitShim + """
            namespace App.Tests
            {
                public abstract class SharedTests { [Xunit.Fact] public void Shared() { } }
                public class SqlTests : SharedTests { }
                public class MemoryTests : SharedTests { }
            }
            """);

        var tests = new TestDiscoverer().Discover(compilation, "App.Tests", TestFramework.XUnitV2);

        Assert.Equal(["App.Tests.MemoryTests.Shared", "App.Tests.SqlTests.Shared"],
            tests.Select(t => t.FullyQualifiedName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void The_constructor_of_a_test_class_reaches_every_test_in_it()
    {
        var compilation = CompilationHarness.CompileValid(CompilationHarness.XunitShim + """
            namespace App.Tests
            {
                public class WidgetTests
                {
                    private readonly int _value;
                    public WidgetTests() { _value = 1; }
                    [Xunit.Fact] public void A() { }
                    [Xunit.Fact] public void B() { }
                }
            }
            """);

        var graph = CompilationHarness.BuildGraph(compilation);
        new TestDiscoverer().Discover(compilation, "App.Tests", TestFramework.XUnitV2, graph);

        var constructor = CompilationHarness.KeyOf(compilation, "App.Tests.WidgetTests", ".ctor");
        var impacted = new ImpactSelector().Traverse(graph, [constructor]).Impacted;

        Assert.Contains(CompilationHarness.KeyOf(compilation, "App.Tests.WidgetTests", "A"), impacted);
        Assert.Contains(CompilationHarness.KeyOf(compilation, "App.Tests.WidgetTests", "B"), impacted);
    }

    [Fact]
    public void An_NUnit_SetUp_method_reaches_every_test_in_the_class()
    {
        var compilation = CompilationHarness.CompileValid(CompilationHarness.NUnitShim + """
            namespace App.Tests
            {
                public class WidgetTests
                {
                    [NUnit.Framework.SetUp] public void Prepare() { }
                    [NUnit.Framework.Test] public void Works() { }
                }
            }
            """);

        var graph = CompilationHarness.BuildGraph(compilation);
        new TestDiscoverer().Discover(compilation, "App.Tests", TestFramework.NUnit, graph);

        var setUp = CompilationHarness.KeyOf(compilation, "App.Tests.WidgetTests", "Prepare");
        var impacted = new ImpactSelector().Traverse(graph, [setUp]).Impacted;

        Assert.Contains(CompilationHarness.KeyOf(compilation, "App.Tests.WidgetTests", "Works"), impacted);
    }

    [Fact]
    public void A_class_fixture_reaches_every_test_in_the_class()
    {
        var compilation = CompilationHarness.CompileValid(CompilationHarness.XunitShim + """
            namespace App.Tests
            {
                public class DatabaseFixture { public int Connect() => 1; }
                public class WidgetTests : Xunit.IClassFixture<DatabaseFixture>
                {
                    [Xunit.Fact] public void Works() { }
                }
            }
            """);

        var graph = CompilationHarness.BuildGraph(compilation);
        new TestDiscoverer().Discover(compilation, "App.Tests", TestFramework.XUnitV2, graph);

        var fixture = CompilationHarness.KeyOf(compilation, "App.Tests.DatabaseFixture");
        var impacted = new ImpactSelector().Traverse(graph, [fixture]).Impacted;

        Assert.Contains(CompilationHarness.KeyOf(compilation, "App.Tests.WidgetTests", "Works"), impacted);
    }

    [Fact]
    public void Nested_test_classes_use_the_metadata_nesting_separator()
    {
        var compilation = CompilationHarness.CompileValid(CompilationHarness.XunitShim + """
            namespace App.Tests
            {
                public class Outer
                {
                    public class Inner { [Xunit.Fact] public void Works() { } }
                }
            }
            """);

        var test = Assert.Single(new TestDiscoverer().Discover(compilation, "App.Tests", TestFramework.XUnitV2));

        Assert.Equal("App.Tests.Outer+Inner.Works", test.FullyQualifiedName);
    }

    [Fact]
    public void An_xunit_v3_project_reports_its_tests_as_v3()
    {
        var compilation = CompilationHarness.CompileValid(CompilationHarness.XunitShim + """
            namespace App.Tests { public class T { [Xunit.Fact] public void Works() { } } }
            """);

        var test = Assert.Single(new TestDiscoverer().Discover(compilation, "App.Tests", TestFramework.XUnitV3));

        Assert.Equal(TestFramework.XUnitV3, test.Framework);
    }
}
