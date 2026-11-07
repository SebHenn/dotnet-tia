using Microsoft.CodeAnalysis;
using Tia.Core.Analysis;

namespace Tia.Core.Tests;

/// <summary>
/// A Razor Page is found by its file path and its <c>@page</c> template, and its <c>PageModel</c> is
/// created and called by the framework, so nothing in the solution names either (#169).
/// </summary>
/// <remarks>
/// The page source here is shaped as the Razor generator emits it, attributes included.
/// </remarks>
public sealed class RazorPageTests
{
    /// <summary>Just enough of ASP.NET to bind against, in an assembly the graph does not track.</summary>
    private static readonly MetadataReference AspNet = CompilationHarness.CompileValid("""
        namespace Microsoft.AspNetCore.Razor.Hosting
        {
            [System.AttributeUsage(System.AttributeTargets.Class, AllowMultiple = true)]
            public sealed class RazorCompiledItemMetadataAttribute(string key, string value) : System.Attribute { }
        }

        namespace Microsoft.AspNetCore.Mvc.RazorPages
        {
            public abstract class PageBase
            {
                public abstract System.Threading.Tasks.Task ExecuteAsync();
            }

            public abstract class Page : PageBase { }

            public abstract class PageModel { }

            [System.AttributeUsage(System.AttributeTargets.Method)]
            public sealed class NonHandlerAttribute : System.Attribute { }
        }
        """, assemblyName: "Microsoft.AspNetCore.Stub").ToMetadataReference();

    private const string App = """
        namespace App
        {
            public sealed class ContributorCard(int id)
            {
                public int Id { get; } = id;
                public string Display => "contributor #" + Id;
            }

            public sealed class ShowModel : Microsoft.AspNetCore.Mvc.RazorPages.PageModel
            {
                public ShowModel() => Heading = "?";

                public string Heading { get; private set; }

                public ContributorCard Card { get; private set; } = new(0);

                public void OnGet(int id) => Card = new ContributorCard(id);

                public void OnPostDelete(int id) { }

                [Microsoft.AspNetCore.Mvc.RazorPages.NonHandler]
                public void OnlyAHelper() { }

                [Microsoft.AspNetCore.Mvc.RazorPages.NonHandler]
                public void OnSomethingElse() { }
            }

            [global::Microsoft.AspNetCore.Razor.Hosting.RazorCompiledItemMetadataAttribute("RouteTemplate", "{id:int}")]
            [global::Microsoft.AspNetCore.Razor.Hosting.RazorCompiledItemMetadataAttribute("Identifier", "/Pages/Contributors/Show.cshtml")]
            internal sealed class Pages_Contributors_Show : global::Microsoft.AspNetCore.Mvc.RazorPages.Page
            {
                public override System.Threading.Tasks.Task ExecuteAsync()
                {
                    _ = Model.Heading;
                    _ = Model.Card.Display;
                    return System.Threading.Tasks.Task.CompletedTask;
                }

                public ShowModel Model => default!;
            }

            public static class PageTests
            {
                public static string Path() => "/contributors/show/7";
            }
        }
        """;

    private static Compilation Compile()
    {
        var compilation = CompilationHarness.Compile(App).AddReferences(AspNet);
        Assert.Empty(compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
        return compilation;
    }

    [Fact]
    public void A_page_declares_its_route_with_its_methods_as_the_endpoints()
    {
        var compilation = Compile();

        var records = RouteTemplateIndex.Scan(compilation, "TestProject");

        var execute = CompilationHarness.KeyOf(compilation, "App.Pages_Contributors_Show", "ExecuteAsync");
        Assert.Contains(records, r => r.IsEndpoint && r.Template == "contributors/show/*" && r.OwningMemberKey == execute);
        Assert.Contains(records, r => !r.IsEndpoint && r.Template == "contributors/show/7");
    }

    [Fact]
    public void A_class_that_is_not_a_page_declares_no_route_from_the_metadata()
    {
        var compilation = CompilationHarness.Compile("""
            namespace App
            {
                [global::Microsoft.AspNetCore.Razor.Hosting.RazorCompiledItemMetadataAttribute("Identifier", "/Views/Home/Index.cshtml")]
                internal sealed class Views_Home_Index
                {
                    public void Render() { }
                }
            }
            """).AddReferences(AspNet);

        Assert.DoesNotContain(RouteTemplateIndex.Scan(compilation, "TestProject"), r => r.IsEndpoint);
    }

    [Theory]
    [InlineData("OnGet")]
    [InlineData("OnPostDelete")]
    [InlineData(".ctor")]
    public void What_the_page_model_runs_reaches_the_page(string member)
    {
        var compilation = Compile();
        var graph = CompilationHarness.BuildGraph(compilation);

        var impacted = new ImpactSelector().Traverse(graph, [CompilationHarness.KeyOf(compilation, "App.ShowModel", member)]).Impacted;

        Assert.Contains(CompilationHarness.KeyOf(compilation, "App.Pages_Contributors_Show", "ExecuteAsync"), impacted);
    }

    [Theory]
    [InlineData("OnlyAHelper")]
    [InlineData("OnSomethingElse")]
    public void A_method_the_framework_does_not_call_does_not_reach_the_page(string member)
    {
        var compilation = Compile();
        var graph = CompilationHarness.BuildGraph(compilation);

        var impacted = new ImpactSelector().Traverse(graph, [CompilationHarness.KeyOf(compilation, "App.ShowModel", member)]).Impacted;

        Assert.DoesNotContain(CompilationHarness.KeyOf(compilation, "App.Pages_Contributors_Show", "ExecuteAsync"), impacted);
    }

    [Fact]
    public void A_getter_only_the_page_reads_reaches_the_page()
    {
        var compilation = Compile();
        var graph = CompilationHarness.BuildGraph(compilation);

        var impacted = new ImpactSelector().Traverse(graph, [CompilationHarness.KeyOf(compilation, "App.ContributorCard", "Display")]).Impacted;

        Assert.Contains(CompilationHarness.KeyOf(compilation, "App.Pages_Contributors_Show", "ExecuteAsync"), impacted);
    }
}
