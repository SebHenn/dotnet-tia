using Tia.Core.Analysis;
using Tia.Core.Model;
using Tia.Frameworks;

namespace Tia.Core.Tests;

/// <summary>
/// A value converter named on a type runs where a value of that type is converted, not wherever
/// the type is named (#65).
/// </summary>
/// <remarks>
/// NodaTime puts <c>[TypeConverter]</c> on every core type. Nothing calls a converter's
/// constructor, so #40's construction step continued from it into every member of the annotated
/// type, and a change to a pattern class selected the whole suite. A change to <c>ConvertFrom</c>
/// reached nothing at all. Both now lead to the annotated type's dispatch hub.
/// </remarks>
public sealed class ValueConverterTests
{
    private const string Money = """
        namespace App
        {
            [System.ComponentModel.TypeConverter(typeof(MoneyConverter))]
            public sealed class Money
            {
                public int Amount { get; }
                public Money Add(Money other) => this;
            }

            public static class Format { public static string Text() => "0.00"; }

            public sealed class MoneyConverter : System.ComponentModel.TypeConverter
            {
                private readonly string _format;
                public MoneyConverter() { _format = Format.Text(); }

                public override object? ConvertFrom(
                    System.ComponentModel.ITypeDescriptorContext? context,
                    System.Globalization.CultureInfo? culture,
                    object value) => new Money();
            }
        }

        namespace App.Tests
        {
            public class MoneyTests
            {
                [Xunit.Fact] public void Adds() { var money = new App.Money(); money.Add(money); }

                [Xunit.Fact] public void HandsOver()
                {
                    var amounts = new System.Collections.Generic.List<App.Money>();
                    amounts.Add(new App.Money());
                }
            }
        }
        """;

    [Fact]
    public void A_change_reaching_the_converters_constructor_selects_where_the_type_is_handed_over()
    {
        var (compilation, graph) = Build(Money);

        var traversal = new ImpactSelector().Traverse(graph, [CompilationHarness.KeyOf(compilation, "App.Format", "Text")]);

        Assert.Contains(CompilationHarness.KeyOf(compilation, "App.Tests.MoneyTests", "HandsOver"), traversal.Impacted);
    }

    /// <summary>
    /// The regression itself: using the type is not converting it, and a test that only does
    /// arithmetic on it does not run its converter.
    /// </summary>
    [Fact]
    public void A_change_reaching_the_converters_constructor_does_not_select_every_test_that_uses_the_type()
    {
        var (compilation, graph) = Build(Money);

        var traversal = new ImpactSelector().Traverse(graph, [CompilationHarness.KeyOf(compilation, "App.Format", "Text")]);

        Assert.DoesNotContain(CompilationHarness.KeyOf(compilation, "App.Tests.MoneyTests", "Adds"), traversal.Impacted);
        Assert.DoesNotContain(CompilationHarness.KeyOf(compilation, "App.Money", "Add"), traversal.Impacted);
    }

    /// <summary>
    /// Where a converter's behaviour lives. It overrides a BCL virtual and nothing in the source
    /// hands a converter to anyone, so before #65 this reached no test at all.
    /// </summary>
    [Fact]
    public void A_change_to_ConvertFrom_selects_where_the_type_is_handed_over()
    {
        var (compilation, graph) = Build(Money);

        var traversal = new ImpactSelector().Traverse(graph, [CompilationHarness.KeyOf(compilation, "App.MoneyConverter", "ConvertFrom")]);

        Assert.Contains(CompilationHarness.KeyOf(compilation, "App.Tests.MoneyTests", "HandsOver"), traversal.Impacted);
        Assert.DoesNotContain(CompilationHarness.KeyOf(compilation, "App.Tests.MoneyTests", "Adds"), traversal.Impacted);
    }

    [Theory]
    [InlineData("System.Text.Json.Serialization")]
    [InlineData("Newtonsoft.Json")]
    public void A_json_converter_is_a_value_converter_too(string attributeNamespace)
    {
        var (compilation, graph) = Build($$"""
            namespace {{attributeNamespace}}
            {
                public class JsonConverterAttribute : System.Attribute { public JsonConverterAttribute(System.Type converterType) { } }
            }

            namespace App
            {
                [{{attributeNamespace}}.JsonConverter(typeof(MoneyConverter))]
                public sealed class Money { public Money Add(Money other) => this; }

                public static class Format { public static string Text() => "0.00"; }

                public sealed class MoneyConverter
                {
                    private readonly string _format;
                    public MoneyConverter() { _format = Format.Text(); }
                }
            }

            namespace App.Tests
            {
                public class MoneyTests
                {
                    [Xunit.Fact] public void Adds() { var money = new App.Money(); money.Add(money); }

                    [Xunit.Fact] public void HandsOver() => new System.Collections.Generic.List<App.Money>().Add(new App.Money());
                }
            }
            """);

        var impacted = new ImpactSelector().Traverse(graph, [CompilationHarness.KeyOf(compilation, "App.Format", "Text")]).Impacted;

        Assert.Contains(CompilationHarness.KeyOf(compilation, "App.Tests.MoneyTests", "HandsOver"), impacted);
        Assert.DoesNotContain(CompilationHarness.KeyOf(compilation, "App.Tests.MoneyTests", "Adds"), impacted);
    }

    /// <summary>
    /// Any other attribute on a type keeps its reach. A filter named on a controller is
    /// constructed to run the controller's own actions, so a change to it has to reach them.
    /// </summary>
    [Fact]
    public void Another_attribute_naming_a_framework_constructed_type_still_reaches_the_annotated_types_members()
    {
        var (compilation, graph) = Build("""
            namespace App
            {
                public sealed class ServiceFilterAttribute : System.Attribute { public ServiceFilterAttribute(System.Type type) { } }

                public static class Audit { public static void Write() { } }

                public sealed class AuditFilter { public AuditFilter() { Audit.Write(); } }

                [ServiceFilter(typeof(AuditFilter))]
                public sealed class OrdersController { public int Get() => 1; }
            }

            namespace App.Tests
            {
                public class OrdersTests { [Xunit.Fact] public void Gets() => new App.OrdersController().Get(); }
            }
            """);

        var traversal = new ImpactSelector().Traverse(graph, [CompilationHarness.KeyOf(compilation, "App.Audit", "Write")]);
        var gets = CompilationHarness.KeyOf(compilation, "App.Tests.OrdersTests", "Gets");

        Assert.Contains(gets, traversal.Impacted);
        Assert.Contains(traversal.PathTo(gets), step => step.IncomingEdge == EdgeKind.Construction);
    }

    private static (Microsoft.CodeAnalysis.Compilation Compilation, ImpactGraph Graph) Build(string source)
    {
        var compilation = CompilationHarness.CompileValid(CompilationHarness.XunitShim + source);
        var graph = CompilationHarness.BuildGraph(compilation);
        new TestDiscoverer().Discover(compilation, "App.Tests", TestFramework.XUnitV2, graph);
        return (compilation, graph);
    }
}
