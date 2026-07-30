using Tia.Core.Analysis;
using Tia.Core.Model;

namespace Tia.Core.Tests;

public sealed class ReferenceGraphTests
{
    [Fact]
    public void Callee_reaches_its_caller()
    {
        var compilation = CompilationHarness.CompileValid("""
            namespace App
            {
                public class Math { public int Add(int a, int b) => a + b; }
                public class Consumer { public int Use() => new Math().Add(1, 2); }
            }
            """);

        var graph = CompilationHarness.BuildGraph(compilation);
        var impacted = Traverse(graph, CompilationHarness.KeyOf(compilation, "App.Math", "Add"));

        Assert.Contains(CompilationHarness.KeyOf(compilation, "App.Consumer", "Use"), impacted);
    }

    [Fact]
    public void Changing_an_interface_member_reaches_every_implementation()
    {
        var compilation = CompilationHarness.CompileValid(Greeters);

        var impacted = Traverse(graph: CompilationHarness.BuildGraph(compilation),
            CompilationHarness.KeyOf(compilation, "App.IGreeter", "Greet"));

        Assert.Contains(CompilationHarness.KeyOf(compilation, "App.English", "Greet"), impacted);
        Assert.Contains(CompilationHarness.KeyOf(compilation, "App.German", "Greet"), impacted);
    }

    [Fact]
    public void Changing_an_implementation_reaches_the_interface_member_it_implements()
    {
        var compilation = CompilationHarness.CompileValid(Greeters);

        var impacted = Traverse(graph: CompilationHarness.BuildGraph(compilation),
            CompilationHarness.KeyOf(compilation, "App.English", "Greet"));

        Assert.Contains(CompilationHarness.KeyOf(compilation, "App.IGreeter", "Greet"), impacted);
    }

    [Fact]
    public void Changing_an_implementation_does_not_reach_its_siblings()
    {
        // Both directions of the interface edge are needed, but composing them asserts something
        // false: what English.Greet returns says nothing about what German.Greet returns. Letting
        // them compose is what makes selection collapse on a polymorphic codebase.
        var compilation = CompilationHarness.CompileValid(Greeters);

        var impacted = Traverse(graph: CompilationHarness.BuildGraph(compilation),
            CompilationHarness.KeyOf(compilation, "App.English", "Greet"));

        Assert.DoesNotContain(CompilationHarness.KeyOf(compilation, "App.German", "Greet"), impacted);
    }

    [Fact]
    public void Changing_an_override_does_not_reach_its_siblings()
    {
        var compilation = CompilationHarness.CompileValid("""
            namespace App
            {
                public abstract class Shape { public abstract int Area(); }
                public class Square : Shape { public override int Area() => 1; }
                public class Circle : Shape { public override int Area() => 3; }
            }
            """);

        var graph = CompilationHarness.BuildGraph(compilation);
        var impacted = Traverse(graph, CompilationHarness.KeyOf(compilation, "App.Square", "Area"));

        Assert.Contains(CompilationHarness.KeyOf(compilation, "App.Shape", "Area"), impacted);
        Assert.DoesNotContain(CompilationHarness.KeyOf(compilation, "App.Circle", "Area"), impacted);
    }

    [Fact]
    public void A_caller_that_only_knows_the_interface_is_still_reached_from_a_sibling_free_path()
    {
        // The restriction blocks the immediate downward hop only. Callers of the interface member
        // still have to be reached, because at run time they may dispatch to the changed type.
        var compilation = CompilationHarness.CompileValid(Greeters + """
            namespace App
            {
                public class Caller
                {
                    public string Run(IGreeter greeter) => greeter.Greet();
                }
            }
            """);

        var graph = CompilationHarness.BuildGraph(compilation);
        var impacted = Traverse(graph, CompilationHarness.KeyOf(compilation, "App.German", "Greet"));

        Assert.Contains(CompilationHarness.KeyOf(compilation, "App.Caller", "Run"), impacted);
    }

    private const string Greeters = """
        namespace App
        {
            public interface IGreeter { string Greet(); }
            public class English : IGreeter { public string Greet() => "hello"; }
            public class German : IGreeter { public string Greet() => "hallo"; }
        }
        """;

    [Fact]
    public void Dependency_injection_needs_no_special_case()
    {
        // The test only ever sees IGreeter; the concrete type is wired up elsewhere. The interface
        // edge is what connects a change in English.Greet to the caller of IGreeter.Greet.
        var compilation = CompilationHarness.CompileValid("""
            namespace App
            {
                public interface IGreeter { string Greet(); }
                public class English : IGreeter { public string Greet() => "hello"; }
                public class Caller
                {
                    private readonly IGreeter _greeter;
                    public Caller(IGreeter greeter) { _greeter = greeter; }
                    public string Run() => _greeter.Greet();
                }
            }
            """);

        var graph = CompilationHarness.BuildGraph(compilation);
        var impacted = Traverse(graph, CompilationHarness.KeyOf(compilation, "App.English", "Greet"));

        Assert.Contains(CompilationHarness.KeyOf(compilation, "App.Caller", "Run"), impacted);
    }

    [Fact]
    public void Virtual_member_reaches_its_override_and_back()
    {
        var compilation = CompilationHarness.CompileValid("""
            namespace App
            {
                public class Base { public virtual int Value() => 1; }
                public class Derived : Base { public override int Value() => 2; }
            }
            """);

        var graph = CompilationHarness.BuildGraph(compilation);

        Assert.Contains(
            CompilationHarness.KeyOf(compilation, "App.Derived", "Value"),
            Traverse(graph, CompilationHarness.KeyOf(compilation, "App.Base", "Value")));

        Assert.Contains(
            CompilationHarness.KeyOf(compilation, "App.Base", "Value"),
            Traverse(graph, CompilationHarness.KeyOf(compilation, "App.Derived", "Value")));
    }

    [Fact]
    public void Base_type_reaches_the_members_of_derived_types()
    {
        var compilation = CompilationHarness.CompileValid("""
            namespace App
            {
                public class Base { }
                public class Derived : Base { public int Untouched() => 1; }
            }
            """);

        var graph = CompilationHarness.BuildGraph(compilation);
        var impacted = Traverse(graph, CompilationHarness.KeyOf(compilation, "App.Base"));

        Assert.Contains(CompilationHarness.KeyOf(compilation, "App.Derived"), impacted);
        Assert.Contains(CompilationHarness.KeyOf(compilation, "App.Derived", "Untouched"), impacted);
    }

    [Fact]
    public void Constructed_generics_reduce_to_their_original_definition()
    {
        var compilation = CompilationHarness.CompileValid("""
            namespace App
            {
                public class Box<T> { public T? Value; public void Set(T value) { Value = value; } }
                public class Consumer { public void Use() { new Box<int>().Set(3); } }
            }
            """);

        var graph = CompilationHarness.BuildGraph(compilation);
        var impacted = Traverse(graph, CompilationHarness.KeyOf(compilation, "App.Box`1", "Set"));

        Assert.Contains(CompilationHarness.KeyOf(compilation, "App.Consumer", "Use"), impacted);
    }

    [Fact]
    public void Property_accessors_are_tracked_as_the_property()
    {
        var compilation = CompilationHarness.CompileValid("""
            namespace App
            {
                public class Config { public int Retries { get; set; } }
                public class Consumer { public int Use(Config c) => c.Retries; }
            }
            """);

        var graph = CompilationHarness.BuildGraph(compilation);
        var impacted = Traverse(graph, CompilationHarness.KeyOf(compilation, "App.Config", "Retries"));

        Assert.Contains(CompilationHarness.KeyOf(compilation, "App.Consumer", "Use"), impacted);
    }

    [Fact]
    public void Edges_cross_project_boundaries()
    {
        var library = CompilationHarness.CompileValid("""
            namespace Lib { public class Service { public int Compute() => 42; } }
            """, assemblyName: "Lib", path: "/repo/lib/Service.cs");

        var consumer = CompilationHarness.Compile("App", ("/repo/app/Consumer.cs", """
            namespace App { public class Consumer { public int Use() => new Lib.Service().Compute(); } }
            """)).AddReferences(library.ToMetadataReference());

        var graph = CompilationHarness.BuildGraph([library, consumer]);
        var impacted = Traverse(graph, CompilationHarness.KeyOf(library, "Lib.Service", "Compute"));

        Assert.Contains(CompilationHarness.KeyOf(consumer, "App.Consumer", "Use"), impacted);
    }

    [Fact]
    public void Attribute_class_reaches_the_symbols_it_annotates()
    {
        var compilation = CompilationHarness.CompileValid("""
            namespace App
            {
                public class MarkAttribute : System.Attribute { public int Order; }
                [Mark(Order = 1)] public class Annotated { public int Value() => 1; }
            }
            """);

        var graph = CompilationHarness.BuildGraph(compilation);
        var impacted = Traverse(graph, CompilationHarness.KeyOf(compilation, "App.MarkAttribute"));

        Assert.Contains(CompilationHarness.KeyOf(compilation, "App.Annotated"), impacted);
    }

    [Fact]
    public void Traversal_records_the_path_that_explain_replays()
    {
        var compilation = CompilationHarness.CompileValid("""
            namespace App
            {
                public interface IStore { int Read(); }
                public class Store : IStore { public int Read() => 1; }
                public class Service { public int Load(IStore s) => s.Read(); }
            }
            """);

        var graph = CompilationHarness.BuildGraph(compilation);
        var seed = CompilationHarness.KeyOf(compilation, "App.Store", "Read");
        var traversal = new ImpactSelector().Traverse(graph, [seed]);

        var path = traversal.PathTo(CompilationHarness.KeyOf(compilation, "App.Service", "Load"));

        Assert.Equal(seed, path[0].Key);
        Assert.Contains(path, step => step.IncomingEdge.HasFlag(EdgeKind.ImplementationToInterface));
        Assert.Equal(CompilationHarness.KeyOf(compilation, "App.Service", "Load"), path[^1].Key);
    }

    private static IReadOnlySet<string> Traverse(ImpactGraph graph, params string[] seeds) =>
        new ImpactSelector().Traverse(graph, seeds).Impacted;
}
