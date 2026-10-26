using Tia.Core.Analysis;

namespace Tia.Core.Tests;

/// <summary>
/// An attribute's overrides run where the attribute is applied (#119).
/// </summary>
/// <remarks>
/// A framework constructs the attribute by reflection and calls what it overrides: xUnit's
/// <c>DataAttribute.GetData</c> and <c>BeforeAfterTestAttribute.Before</c>, NUnit's
/// <c>ITestAction.BeforeTest</c>, an ASP.NET filter's <c>OnActionExecuting</c>. Nothing in the
/// solution calls any of them, so a change to one reached none of the code that carries the
/// attribute. <see cref="Attribute.Match"/> stands in for them here: it overrides a member of a type
/// outside the solution, which is all that makes the others different.
/// </remarks>
public sealed class AttributeDispatchTests
{
    private const string Attribute = """
        namespace App
        {
            public sealed class CultureAttribute : System.Attribute
            {
                public override bool Match(object? obj) => obj is null;
            }
        }
        """;

    [Fact]
    public void A_change_to_an_attribute_override_reaches_the_method_that_carries_it()
    {
        var compilation = CompilationHarness.CompileValid(Attribute + """
            namespace App
            {
                public class Checks
                {
                    [Culture] public void Formats() { }
                    public void Parses() { }
                }
            }
            """);
        var graph = CompilationHarness.BuildGraph(compilation);

        var impacted = new ImpactSelector().Traverse(graph, [CompilationHarness.KeyOf(compilation, "App.CultureAttribute", "Match")]).Impacted;

        Assert.Contains(CompilationHarness.KeyOf(compilation, "App.Checks", "Formats"), impacted);
        // The attribute is on one method, and the other is not affected by it.
        Assert.DoesNotContain(CompilationHarness.KeyOf(compilation, "App.Checks", "Parses"), impacted);
    }

    [Fact]
    public void A_change_to_an_attribute_override_reaches_every_member_of_the_class_that_carries_it()
    {
        var compilation = CompilationHarness.CompileValid(Attribute + """
            namespace App
            {
                [Culture]
                public class Checks
                {
                    public void Formats() { }
                    public void Parses() { }
                }
            }
            """);
        var graph = CompilationHarness.BuildGraph(compilation);

        var impacted = new ImpactSelector().Traverse(graph, [CompilationHarness.KeyOf(compilation, "App.CultureAttribute", "Match")]).Impacted;

        Assert.Contains(CompilationHarness.KeyOf(compilation, "App.Checks", "Formats"), impacted);
        Assert.Contains(CompilationHarness.KeyOf(compilation, "App.Checks", "Parses"), impacted);
    }

    [Fact]
    public void An_attribute_without_an_override_of_outside_code_adds_nothing()
    {
        // Only members a framework dispatches to are reachable this way. A helper the attribute
        // calls itself is reached through that call, as before, and a member nothing outside calls
        // does not make the attributed method impacted.
        var compilation = CompilationHarness.CompileValid("""
            namespace App
            {
                public sealed class TagAttribute : System.Attribute
                {
                    public string Describe() => "tag";
                }

                public class Checks { [Tag] public void Formats() { } }
            }
            """);
        var graph = CompilationHarness.BuildGraph(compilation);

        var impacted = new ImpactSelector().Traverse(graph, [CompilationHarness.KeyOf(compilation, "App.TagAttribute", "Describe")]).Impacted;

        Assert.DoesNotContain(CompilationHarness.KeyOf(compilation, "App.Checks", "Formats"), impacted);
    }
}
