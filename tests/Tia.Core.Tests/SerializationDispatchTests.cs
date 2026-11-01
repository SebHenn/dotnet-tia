using Microsoft.CodeAnalysis;
using Tia.Core.Analysis;

namespace Tia.Core.Tests;

/// <summary>
/// A serializer reads every public property of what it is handed, and nothing in the source says
/// so (#125).
/// </summary>
/// <remarks>
/// <c>Results.Ok(contributor)</c> runs <c>Contributor.Display</c>'s getter inside ASP.NET, a
/// functional test asserts the JSON it wrote, and a change to the getter selected nothing.
/// </remarks>
public sealed class SerializationDispatchTests
{
    /// <summary>Just enough of ASP.NET to bind against, in an assembly the graph does not track.</summary>
    private static readonly MetadataReference AspNet = CompilationHarness.CompileValid("""
        namespace Microsoft.AspNetCore.Http
        {
            public interface IResult { }
            public static class Results
            {
                public static IResult Ok<TValue>(TValue? value) => null!;
            }
            public delegate System.Threading.Tasks.Task RequestDelegate(object context);
        }

        namespace Microsoft.AspNetCore.Builder
        {
            public interface IEndpointRouteBuilder { }
            public static class EndpointRouteBuilderExtensions
            {
                public static object MapGet(this IEndpointRouteBuilder endpoints, string pattern, System.Delegate handler) => null!;
            }
        }

        namespace Microsoft.AspNetCore.Mvc
        {
            public interface IActionResult { }
            public class ActionResult<TValue> { public static implicit operator ActionResult<TValue>(TValue value) => null!; }
            public abstract class ControllerBase
            {
                public virtual IActionResult Ok(object? value) => null!;
            }
            public abstract class Controller : ControllerBase
            {
                public virtual IActionResult View(object? model) => null!;
            }
            [System.AttributeUsage(System.AttributeTargets.Parameter)]
            public sealed class FromBodyAttribute : System.Attribute { }
        }

        // The harness references neither; the names are what is matched.
        namespace System.Net.Http.Json
        {
            public static class HttpContentJsonExtensions
            {
                public static System.Threading.Tasks.Task<T?> ReadFromJsonAsync<T>(this object content) => null!;
            }
        }

        namespace Microsoft.AspNetCore.Mvc.Razor
        {
            public abstract class RazorPage<TModel>
            {
                public TModel Model => default!;
                public abstract System.Threading.Tasks.Task ExecuteAsync();
            }
        }

        namespace System.Text.Json.Serialization
        {
            [System.AttributeUsage(System.AttributeTargets.Constructor)]
            public sealed class JsonConstructorAttribute : System.Attribute { }
        }
        """, assemblyName: "Microsoft.AspNetCore.Stub").ToMetadataReference();

    /// <remarks>
    /// A class, not a record: a record's <c>PrintMembers</c> already leads its properties into the
    /// dispatch hub (#121), which reaches the same sites and would pass these tests without them.
    /// </remarks>
    private const string Contributor = """
        namespace App
        {
            public sealed class Contributor(int id, string name)
            {
                public int Id { get; } = id;
                public string Name { get; } = name;
                public string Display => Name.ToUpperInvariant();
            }
        }
        """;

    private static IReadOnlySet<string> Impacted(string source, string changedType, string changedMember, out Compilation compilation)
    {
        compilation = CompilationHarness.Compile(source).AddReferences(AspNet);
        Assert.Empty(compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));

        var graph = CompilationHarness.BuildGraph(compilation);
        return new ImpactSelector().Traverse(graph, [CompilationHarness.KeyOf(compilation, changedType, changedMember)]).Impacted;
    }

    [Fact]
    public void A_computed_property_of_a_value_handed_to_Results_Ok_reaches_the_endpoint()
    {
        var impacted = Impacted(Contributor + """
            namespace App
            {
                public static class Contributors
                {
                    public static Microsoft.AspNetCore.Http.IResult ById(int id) =>
                        Microsoft.AspNetCore.Http.Results.Ok(new Contributor(id, "ada"));
                }
            }
            """, "App.Contributor", "Display", out var compilation);

        Assert.Contains(CompilationHarness.KeyOf(compilation, "App.Contributors", "ById"), impacted);
    }

    [Fact]
    public void A_property_of_a_nested_type_reaches_the_endpoint_that_serializes_its_owner()
    {
        var impacted = Impacted("""
            namespace App
            {
                public sealed class Address { public string City => "x".ToUpperInvariant(); }
                public sealed class Customer { public Address Home { get; init; } = new(); }

                public static class Customers
                {
                    public static Microsoft.AspNetCore.Http.IResult List() =>
                        Microsoft.AspNetCore.Http.Results.Ok(new[] { new Customer() });
                }
            }
            """, "App.Address", "City", out var compilation);

        Assert.Contains(CompilationHarness.KeyOf(compilation, "App.Customers", "List"), impacted);
    }

    [Fact]
    public void A_value_handed_to_ControllerBase_Ok_and_an_action_signature_are_both_sites()
    {
        var impacted = Impacted(Contributor + """
            namespace App
            {
                public sealed class ContributorsController : Microsoft.AspNetCore.Mvc.ControllerBase
                {
                    public Microsoft.AspNetCore.Mvc.IActionResult Get() => Ok(new Contributor(1, "ada"));
                    public Microsoft.AspNetCore.Mvc.ActionResult<Contributor> Typed() => new Contributor(1, "ada");
                    public void Create([Microsoft.AspNetCore.Mvc.FromBody] Contributor contributor) { }
                }
            }
            """, "App.Contributor", "Display", out var compilation);

        Assert.Contains(CompilationHarness.KeyOf(compilation, "App.ContributorsController", "Get"), impacted);
        Assert.Contains(CompilationHarness.KeyOf(compilation, "App.ContributorsController", "Typed"), impacted);
        Assert.Contains(CompilationHarness.KeyOf(compilation, "App.ContributorsController", "Create"), impacted);
    }

    [Fact]
    public void A_minimal_api_lambda_returning_the_value_is_a_site()
    {
        var impacted = Impacted(Contributor + """
            namespace App
            {
                public static class Endpoints
                {
                    public static void Map(Microsoft.AspNetCore.Builder.IEndpointRouteBuilder app) =>
                        Microsoft.AspNetCore.Builder.EndpointRouteBuilderExtensions.MapGet(app, "/c", () => new Contributor(1, "ada"));
                }
            }
            """, "App.Contributor", "Display", out var compilation);

        Assert.Contains(CompilationHarness.KeyOf(compilation, "App.Endpoints", "Map"), impacted);
    }

    [Fact]
    public void A_constructor_reaches_a_test_that_deserializes_the_type()
    {
        var impacted = Impacted("""
            namespace App
            {
                public sealed class Payload
                {
                    [System.Text.Json.Serialization.JsonConstructor]
                    public Payload(int value) => Value = value * 2;
                    public int Value { get; }
                }

                public static class Factory
                {
                    // Called here, so #40's rule for a constructor only a framework calls - which
                    // would reach Reads by its naming the type - does not apply.
                    public static Payload Make() => new(1);
                }

                public static class Tests
                {
                    // Not JsonSerializer, whose callers are reflection seeds and always impacted.
                    public static System.Threading.Tasks.Task<Payload?> Reads(object body) =>
                        System.Net.Http.Json.HttpContentJsonExtensions.ReadFromJsonAsync<Payload>(body);
                }
            }
            """, "App.Payload", ".ctor", out var compilation);

        Assert.Contains(CompilationHarness.KeyOf(compilation, "App.Tests", "Reads"), impacted);
    }

    [Fact]
    public void A_property_of_a_base_type_reaches_a_site_that_serializes_the_derived_type()
    {
        var impacted = Impacted("""
            namespace App
            {
                public abstract class Entity { public string Label => "x".ToUpperInvariant(); }
                public sealed class Order : Entity { }

                public static class Orders
                {
                    public static Microsoft.AspNetCore.Http.IResult Get() => Microsoft.AspNetCore.Http.Results.Ok(new Order());
                }
            }
            """, "App.Entity", "Label", out var compilation);

        Assert.Contains(CompilationHarness.KeyOf(compilation, "App.Orders", "Get"), impacted);
    }

    [Fact]
    public void Handing_a_value_to_anything_but_a_serializer_does_not_reach_its_properties()
    {
        // The dispatch hub is where a list or an assertion lands, and properties stay out of it:
        // the precision #30 was measured at. A class rather than a record: a record's ToString
        // prints its properties, which #121 already leads into the dispatch hub.
        var impacted = Impacted("""
            namespace App
            {
                public sealed class Contact { public string Display => "x".ToUpperInvariant(); }

                public static class Store
                {
                    public static void Keep(System.Collections.Generic.List<Contact> all) => all.Add(new Contact());
                    public static bool Same(Contact a, Contact b) => object.Equals(a, b);
                }
            }
            """, "App.Contact", "Display", out var compilation);

        Assert.DoesNotContain(CompilationHarness.KeyOf(compilation, "App.Store", "Keep"), impacted);
        Assert.DoesNotContain(CompilationHarness.KeyOf(compilation, "App.Store", "Same"), impacted);
    }

    [Theory]
    [InlineData("object? value")]
    [InlineData("object value")]
    public void A_value_a_helper_serializes_as_object_reaches_the_endpoint_that_hands_it_over(string parameter)
    {
        // The helper sees only object, and the endpoint never calls a serializer itself (#157).
        var impacted = Impacted(Contributor + $$"""
            namespace App
            {
                public static class Respond
                {
                    public static Microsoft.AspNetCore.Http.IResult With({{parameter}}) => Microsoft.AspNetCore.Http.Results.Ok(value);
                }

                public static class Contributors
                {
                    public static Microsoft.AspNetCore.Http.IResult ById(int id) => Respond.With(new Contributor(id, "ada"));
                }
            }
            """, "App.Contributor", "Display", out var compilation);

        Assert.Contains(CompilationHarness.KeyOf(compilation, "App.Contributors", "ById"), impacted);
    }

    [Fact]
    public void A_value_a_generic_helper_serializes_reaches_the_endpoint_that_hands_it_over()
    {
        var impacted = Impacted(Contributor + """
            namespace App
            {
                public static class Respond
                {
                    public static Microsoft.AspNetCore.Http.IResult With<T>(T value) => Microsoft.AspNetCore.Http.Results.Ok(value);
                }

                public static class Contributors
                {
                    public static Microsoft.AspNetCore.Http.IResult ById(int id) => Respond.With(new Contributor(id, "ada"));
                }
            }
            """, "App.Contributor", "Display", out var compilation);

        Assert.Contains(CompilationHarness.KeyOf(compilation, "App.Contributors", "ById"), impacted);
    }

    [Fact]
    public void A_value_forwarded_through_a_chain_of_helpers_reaches_the_endpoint()
    {
        // An extension method in the middle, so the receiver is the forwarded parameter.
        var impacted = Impacted(Contributor + """
            namespace App
            {
                public abstract class Api : Microsoft.AspNetCore.Mvc.ControllerBase
                {
                    public Microsoft.AspNetCore.Mvc.IActionResult Reply(object value) => Ok(value);
                }

                public static class Replies
                {
                    public static Microsoft.AspNetCore.Mvc.IActionResult Wrapped(this object value, Api api) => api.Reply(value);
                }

                public sealed class Endpoints(Api api)
                {
                    public Microsoft.AspNetCore.Mvc.IActionResult ById(int id) => new Contributor(id, "ada").Wrapped(api);
                }
            }
            """, "App.Contributor", "Display", out var compilation);

        Assert.Contains(CompilationHarness.KeyOf(compilation, "App.Endpoints", "ById"), impacted);
    }

    [Fact]
    public void A_value_handed_to_a_helper_that_serializes_nothing_reaches_nothing()
    {
        // The parameter's node exists at the caller and leads nowhere: no edge, no selection.
        var impacted = Impacted(Contributor + """
            namespace App
            {
                public static class Audit
                {
                    public static int Note(object value) => value.GetHashCode();
                }

                public static class Contributors
                {
                    public static int Touch(int id) => Audit.Note(new Contributor(id, "ada"));
                }
            }
            """, "App.Contributor", "Display", out var compilation);

        Assert.DoesNotContain(CompilationHarness.KeyOf(compilation, "App.Contributors", "Touch"), impacted);
    }

    [Fact]
    public void A_method_a_razor_view_calls_on_its_model_reaches_the_action_that_renders_it()
    {
        // As the Razor generator emits a .cshtml: a class over RazorPage<TModel> that reads the
        // model. MVC finds it by name, and nothing in the source reaches it (#157).
        var impacted = Impacted(Contributor + """
            namespace App
            {
                public static class Formatting
                {
                    public static string Title(Contributor contributor) => contributor.Name + "!";
                }

                public sealed class Views_Contributors_Show : Microsoft.AspNetCore.Mvc.Razor.RazorPage<System.Collections.Generic.List<Contributor>>
                {
                    public override System.Threading.Tasks.Task ExecuteAsync()
                    {
                        _ = Formatting.Title(Model[0]);
                        return System.Threading.Tasks.Task.CompletedTask;
                    }
                }

                public sealed class ContributorsController : Microsoft.AspNetCore.Mvc.Controller
                {
                    public Microsoft.AspNetCore.Mvc.IActionResult Show() => View(new System.Collections.Generic.List<Contributor>());
                }
            }
            """, "App.Formatting", "Title", out var compilation);

        Assert.Contains(CompilationHarness.KeyOf(compilation, "App.ContributorsController", "Show"), impacted);
    }
}
