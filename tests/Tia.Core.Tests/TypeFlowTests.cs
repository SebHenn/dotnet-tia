using Tia.Core.Analysis;
using Tia.Core.Model;

namespace Tia.Core.Tests;

/// <summary>
/// The bound on an upward hop, sharpened from "can reach the type" to "can obtain an instance of
/// it".
/// </summary>
/// <remarks>
/// Two things are being tested and they pull in opposite directions. The narrowing has to be real,
/// or the flag costs a second semantic pass and buys nothing; and it has to stay sound, because a
/// bound that is too tight is a test that should have run and did not. Every case below is one or
/// the other, and the soundness ones outnumber the precision one deliberately - that is the ratio
/// in which this analysis can go wrong.
/// </remarks>
public class TypeFlowTests
{
    [Fact]
    public void Naming_a_type_is_not_holding_one()
    {
        // The precision case, and the whole reason for the analysis. Both callers reach German -
        // one by creating it, one because it reads a property whose body says typeof(German). Only
        // the first can dispatch to a German at run time.
        var source = Greeters + """
            namespace App
            {
                public class Service { public string Run(IGreeter g) => g.Greet(); }
                public static class Names { public static string OfGerman() => typeof(German).Name; }

                public class Holds { public string Go() => new Service().Run(new German()); }
                public class OnlyMentions
                {
                    public string Go() => new Service().Run(new English()) + Names.OfGerman();
                }
            }
            """;

        var compilation = CompilationHarness.CompileValid(source);
        var graph = CompilationHarness.BuildGraph(compilation);
        var changed = CompilationHarness.KeyOf(compilation, "App.German", "Greet");
        var mentions = CompilationHarness.KeyOf(compilation, "App.OnlyMentions", "Go");
        var holds = CompilationHarness.KeyOf(compilation, "App.Holds", "Go");

        // Reachability alone cannot separate them: the typeof is a reference like any other.
        var unbounded = new ImpactSelector().Traverse(graph, [changed]).Impacted;
        Assert.Contains(mentions, unbounded);

        var bounded = Traverse(compilation, graph, changed);
        Assert.Contains(holds, bounded);
        Assert.DoesNotContain(mentions, bounded);
    }

    [Fact]
    public void Holding_a_subclass_is_holding_its_base()
    {
        // The implementation that changed is declared on an abstract base, so the bound is drawn on
        // a type nothing can construct. Everything that reaches it holds a subclass instead, and a
        // bound that does not know a Sub is a Base excludes every one of them.
        var source = Greeters + """
            namespace App
            {
                public abstract class Formal : IGreeter { public string Greet() => "good day"; }
                public sealed class Sub : Formal { }

                public class Service { public string Run(IGreeter g) => g.Greet(); }
                public class Tests { public string Go() => new Service().Run(new Sub()); }
            }
            """;

        var compilation = CompilationHarness.CompileValid(source);
        var graph = CompilationHarness.BuildGraph(compilation);

        var impacted = Traverse(compilation, graph, CompilationHarness.KeyOf(compilation, "App.Formal", "Greet"));

        Assert.Contains(CompilationHarness.KeyOf(compilation, "App.Tests", "Go"), impacted);
    }

    [Fact]
    public void A_type_handed_to_a_factory_is_a_type_obtained()
    {
        // Registration names the implementation only as a type argument, and the call returns the
        // service collection. Nothing about the expression's own type says a German is involved.
        var source = Greeters + """
            namespace App
            {
                public interface IServices { IServices AddSingleton<TService, TImplementation>(); }
                public class Wiring { public IServices Register(IServices s) => s.AddSingleton<IGreeter, German>(); }
            }
            """;

        var compilation = CompilationHarness.CompileValid(source);
        var flow = Resolve(compilation, CompilationHarness.BuildGraph(compilation));

        Assert.True(flow.Permits(
            CompilationHarness.KeyOf(compilation, "App.Wiring", "Register"),
            CompilationHarness.KeyOf(compilation, "App.German")));
    }

    [Fact]
    public void What_a_member_calls_is_what_it_can_be_handed()
    {
        // The test never names German; the factory it calls does. Without the fixpoint the bound
        // would see a test that holds nothing and exclude it, which is the miss the reverted
        // attempt shipped.
        var source = Greeters + """
            namespace App
            {
                public class Factory { public IGreeter Make() => new German(); }
                public class Service { public string Run(IGreeter g) => g.Greet(); }
                public class Tests { public string Go() => new Service().Run(new Factory().Make()); }
            }
            """;

        var compilation = CompilationHarness.CompileValid(source);
        var graph = CompilationHarness.BuildGraph(compilation);

        var impacted = Traverse(compilation, graph, CompilationHarness.KeyOf(compilation, "App.German", "Greet"));

        Assert.Contains(CompilationHarness.KeyOf(compilation, "App.Tests", "Go"), impacted);
    }

    [Fact]
    public void A_field_written_in_one_member_is_readable_in_another()
    {
        // Setup builds it, the test reads it, and no reference edge runs from the writer to the
        // reader - the fixture edge that connects them for the traversal carries no value. So the
        // flow rule has to record the German on the field, and this asserts the bound rather than a
        // selection because the selection depends on plumbing that is not what is under test.
        var source = Greeters + """
            namespace App
            {
                public class Service { public string Run(IGreeter g) => g.Greet(); }

                public class Tests
                {
                    private IGreeter? _greeter;
                    public void Setup() { _greeter = new German(); }
                    public string Go() => new Service().Run(_greeter!);
                }
            }
            """;

        var compilation = CompilationHarness.CompileValid(source);
        var flow = Resolve(compilation, CompilationHarness.BuildGraph(compilation));

        Assert.True(flow.Permits(
            CompilationHarness.KeyOf(compilation, "App.Tests", "Go"),
            CompilationHarness.KeyOf(compilation, "App.German")));
    }

    [Fact]
    public void A_member_that_defeats_the_analysis_permits_every_hop()
    {
        // Reflection is handed in already decided - ReflectionScanner found it, and "this member
        // can reach things no edge records" is the same verdict both uses. The answer has to be
        // "any type", never "no types", and it has to travel to whoever calls it.
        var source = Greeters + """
            namespace App
            {
                public class Loader { public IGreeter Load() => null!; }
                public class Tests { public string Go() => new Loader().Load().Greet(); }
            }
            """;

        var compilation = CompilationHarness.CompileValid(source);
        var graph = CompilationHarness.BuildGraph(compilation);
        var loader = CompilationHarness.KeyOf(compilation, "App.Loader", "Load");
        var german = CompilationHarness.KeyOf(compilation, "App.German");

        var flow = TypeFlowIndex.Resolve(graph, Facts(compilation), [loader]);

        Assert.True(flow.Permits(loader, german));
        Assert.True(flow.Permits(CompilationHarness.KeyOf(compilation, "App.Tests", "Go"), german));
    }

    [Fact]
    public void An_instance_arriving_from_outside_the_graph_is_unknown()
    {
        // A container hands back an implementation it chose, and the call site says only which
        // interface was asked for. The resolver is outside the solution, so nothing here can name
        // what arrived - and a bound drawn from what the call site names would exclude every
        // implementation there is.
        var host = CompilationHarness.CompileValid("""
            namespace Host { public static class Container { public static T Resolve<T>() => default!; } }
            """, assemblyName: "Host", path: "/repo/host/Container.cs");

        var app = CompilationHarness.Compile("App", ("/repo/app/Tests.cs", Greeters + """
            namespace App
            {
                public class Tests { public string Go() => Host.Container.Resolve<IGreeter>().Greet(); }
            }
            """)).AddReferences(host.ToMetadataReference());

        // Host is deliberately left out of the tracked set: it stands for a package, which is what
        // a container always is.
        var tracked = new HashSet<string>(StringComparer.Ordinal) { "App" };
        var graph = new ReferenceGraphBuilder(tracked).Build(app, "App");
        var flow = TypeFlowIndex.Resolve(graph, TypeFlow.Scan(app, tracked), []);

        Assert.True(flow.Permits(
            CompilationHarness.KeyOf(app, "App.Tests", "Go"),
            CompilationHarness.KeyOf(app, "App.German")));
    }

    [Fact]
    public void A_type_nothing_is_seen_to_obtain_draws_no_bound()
    {
        // Same fallback the reachability bound already takes, for the same reason. Whatever creates
        // it is invisible, and an empty bound would exclude every caller of the interface rather
        // than reporting that it does not know.
        var source = Greeters + """
            namespace App
            {
                public class Service { public string Run(IGreeter g) => g.Greet(); }
                public class Tests { public string Go(Service s, IGreeter g) => s.Run(g); }
            }
            """;

        var compilation = CompilationHarness.CompileValid(source);
        var graph = CompilationHarness.BuildGraph(compilation);

        var impacted = Traverse(compilation, graph, CompilationHarness.KeyOf(compilation, "App.German", "Greet"));

        Assert.Contains(CompilationHarness.KeyOf(compilation, "App.Tests", "Go"), impacted);
    }

    [Fact]
    public void The_flag_can_only_ever_narrow()
    {
        // The bound is intersected with the reachability bound rather than replacing it, so no
        // selection can grow. Worth pinning: a flag that widened somewhere would make every
        // before-and-after measurement mean two things at once.
        var source = Greeters + """
            namespace App
            {
                public class Service { public string Run(IGreeter g) => g.Greet(); }
                public class Factory { public IGreeter Make() => new German(); }
                public class Tests { public string Go() => new Service().Run(new Factory().Make()); }
                public class Other { public string Go() => new Service().Run(new English()); }
            }
            """;

        var compilation = CompilationHarness.CompileValid(source);
        var graph = CompilationHarness.BuildGraph(compilation);
        var changed = CompilationHarness.KeyOf(compilation, "App.German", "Greet");

        var unbounded = new ImpactSelector().Traverse(graph, [changed]).Impacted;
        var bounded = Traverse(compilation, graph, changed);

        Assert.Empty(bounded.Except(unbounded, StringComparer.Ordinal));
    }

    private const string Greeters = """
        namespace App
        {
            public interface IGreeter { string Greet(); }
            public class English : IGreeter { public string Greet() => "hello"; }
            public class German : IGreeter { public string Greet() => "hallo"; }
        }
        """;

    private static IReadOnlySet<string> Traverse(Microsoft.CodeAnalysis.Compilation compilation, ImpactGraph graph, string seed) =>
        new ImpactSelector(Resolve(compilation, graph)).Traverse(graph, [seed]).Impacted;

    private static TypeFlowIndex Resolve(Microsoft.CodeAnalysis.Compilation compilation, ImpactGraph graph) =>
        TypeFlowIndex.Resolve(graph, Facts(compilation), []);

    private static IReadOnlyList<Caching.TypeFlowFact> Facts(Microsoft.CodeAnalysis.Compilation compilation) =>
        TypeFlow.Scan(compilation, new HashSet<string>(StringComparer.Ordinal) { compilation.AssemblyName! });
}
