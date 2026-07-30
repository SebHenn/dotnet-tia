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

    [Fact]
    public void A_caller_that_cannot_obtain_the_changed_type_is_not_reached()
    {
        // Both tests call IGreeter.Greet through the service, so both reach the interface member.
        // Only one of them can ever hold an English, so only one can dispatch to it.
        var compilation = CompilationHarness.CompileValid(Greeters + """
            namespace App
            {
                public class Service { public string Run(IGreeter g) => g.Greet(); }

                public class EnglishTests { public string Go() => new Service().Run(new English()); }
                public class GermanTests { public string Go() => new Service().Run(new German()); }
            }
            """);

        var graph = CompilationHarness.BuildGraph(compilation);
        var impacted = Traverse(graph, CompilationHarness.KeyOf(compilation, "App.English", "Greet"));

        Assert.Contains(CompilationHarness.KeyOf(compilation, "App.EnglishTests", "Go"), impacted);
        Assert.DoesNotContain(CompilationHarness.KeyOf(compilation, "App.GermanTests", "Go"), impacted);
    }

    [Fact]
    public void A_type_nothing_constructs_falls_back_to_reaching_every_consumer()
    {
        // Nothing in the solution mentions English, so whatever creates it is invisible - a
        // container registering by convention, a plugin, a deserialiser. A bound drawn from an
        // empty set would exclude every caller, so there is no bound to draw.
        var compilation = CompilationHarness.CompileValid(Greeters + """
            namespace App
            {
                public class Service { public string Run(IGreeter g) => g.Greet(); }
                public class Caller { public string Go(Service s, IGreeter g) => s.Run(g); }
            }
            """);

        var graph = CompilationHarness.BuildGraph(compilation);
        var impacted = Traverse(graph, CompilationHarness.KeyOf(compilation, "App.English", "Greet"));

        Assert.Contains(CompilationHarness.KeyOf(compilation, "App.Caller", "Go"), impacted);
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

    [Fact]
    public void An_interpolated_value_reaches_the_ToString_it_will_call()
    {
        // Nothing in $"{value}" names Widget.ToString - the interpolation binds to the handler's
        // AppendFormatted, and the call to the value's own formatting happens inside it. The
        // NodaTime gate found this the hard way: ZoneInterval.ToString formats an Instant, so
        // breaking the Instant pattern parser broke it, and no edge connected the two.
        var compilation = CompilationHarness.CompileValid("""
            namespace App
            {
                public class Widget { public override string ToString() => "widget"; }

                public class Report
                {
                    public string Describe(Widget widget) => $"a {widget} here";
                }
            }
            """);

        var graph = CompilationHarness.BuildGraph(compilation);

        var toString = CompilationHarness.KeyOf(compilation, "App.Widget", "ToString");
        var describe = CompilationHarness.KeyOf(compilation, "App.Report", "Describe");

        Assert.Contains(describe, new ImpactSelector().Traverse(graph, [toString]).Impacted);
    }

    [Fact]
    public void An_interpolated_value_of_an_untracked_type_adds_nothing()
    {
        // The rule reaches through to a type's ToString, not to every member of every formatted
        // value: a BCL type is not in the graph and must not be put there.
        var compilation = CompilationHarness.CompileValid("""
            namespace App
            {
                public class Report
                {
                    public string Describe(int count) => $"{count} items";
                }
            }
            """);

        var graph = CompilationHarness.BuildGraph(compilation);

        Assert.All(graph.Nodes.Keys, key => Assert.StartsWith("TestAsm|", key, System.StringComparison.Ordinal));
    }

    [Fact]
    public void Reading_a_static_member_reaches_the_type_initializer()
    {
        // Nothing in the source calls the static constructor; reading Registry.Default runs it.
        var compilation = CompilationHarness.CompileValid("""
            namespace App
            {
                public static class Seed { public static int Make() => 1; }

                public static class Registry
                {
                    static Registry() { Default = Seed.Make(); }
                    public static int Default { get; }
                }

                public class Consumer { public int Read() => Registry.Default; }
            }
            """);

        var graph = CompilationHarness.BuildGraph(compilation);

        var make = CompilationHarness.KeyOf(compilation, "App.Seed", "Make");
        var read = CompilationHarness.KeyOf(compilation, "App.Consumer", "Read");

        Assert.Contains(read, new ImpactSelector().Traverse(graph, [make]).Impacted);
    }

    [Fact]
    public void Naming_a_type_does_not_reach_its_initializer()
    {
        // typeof(T) and a declaration do not run the static constructor, so they must not carry
        // its dependencies with them - that would make every mention of a type with statics as
        // expensive as using them.
        var compilation = CompilationHarness.CompileValid("""
            namespace App
            {
                public static class Seed { public static int Make() => 1; }

                public static class Registry
                {
                    static Registry() { Default = Seed.Make(); }
                    public static int Default { get; }
                }

                public class Consumer { public string Name() => typeof(Registry).Name; }
            }
            """);

        var graph = CompilationHarness.BuildGraph(compilation);

        var make = CompilationHarness.KeyOf(compilation, "App.Seed", "Make");
        var name = CompilationHarness.KeyOf(compilation, "App.Consumer", "Name");

        Assert.DoesNotContain(name, new ImpactSelector().Traverse(graph, [make]).Impacted);
    }

    [Fact]
    public void A_using_statement_reaches_the_Dispose_it_will_call()
    {
        // Same family as the interpolation and static-initializer cases: the compiler emits the
        // call, the source never names it.
        var compilation = CompilationHarness.CompileValid("""
            namespace App
            {
                public sealed class Handle : System.IDisposable { public void Dispose() { } }

                public class Consumer
                {
                    public void Use() { using var handle = new Handle(); }
                }
            }
            """);

        var graph = CompilationHarness.BuildGraph(compilation);

        var dispose = CompilationHarness.KeyOf(compilation, "App.Handle", "Dispose");
        var use = CompilationHarness.KeyOf(compilation, "App.Consumer", "Use");

        Assert.Contains(use, new ImpactSelector().Traverse(graph, [dispose]).Impacted);
    }

    [Fact]
    public void A_deconstruction_reaches_the_Deconstruct_it_will_call()
    {
        var compilation = CompilationHarness.CompileValid("""
            namespace App
            {
                public class Point
                {
                    public void Deconstruct(out int x, out int y) { x = 1; y = 2; }
                }

                public class Consumer
                {
                    public int Sum(Point point) { var (x, y) = point; return x + y; }
                }
            }
            """);

        var graph = CompilationHarness.BuildGraph(compilation);

        var deconstruct = CompilationHarness.KeyOf(compilation, "App.Point", "Deconstruct");
        var sum = CompilationHarness.KeyOf(compilation, "App.Consumer", "Sum");

        Assert.Contains(sum, new ImpactSelector().Traverse(graph, [deconstruct]).Impacted);
    }

    [Fact]
    public void A_collection_initializer_reaches_the_Add_it_will_call()
    {
        var compilation = CompilationHarness.CompileValid("""
            namespace App
            {
                public class Bag : System.Collections.IEnumerable
                {
                    public void Add(int value) { }
                    public System.Collections.IEnumerator GetEnumerator() => null!;
                }

                public class Consumer
                {
                    public Bag Make() => new Bag { 1, 2 };
                }
            }
            """);

        var graph = CompilationHarness.BuildGraph(compilation);

        var add = CompilationHarness.KeyOf(compilation, "App.Bag", "Add");
        var make = CompilationHarness.KeyOf(compilation, "App.Consumer", "Make");

        Assert.Contains(make, new ImpactSelector().Traverse(graph, [add]).Impacted);
    }
}
