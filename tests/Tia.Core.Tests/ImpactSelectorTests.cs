using Tia.Core.Analysis;

namespace Tia.Core.Tests;

/// <summary>
/// The traversal branches that fail toward a miss.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ImpactSelector"/> is tested end to end by <c>ReferenceGraphTests</c>, which asserts
/// what a change reaches. The branches below are the ones where a wrong answer is a *missing* test
/// rather than an extra one, and each is a control-flow decision no shape-of-the-graph test
/// exercises on purpose. They are pinned by the shape that breaks when the branch is removed, not
/// by a count: each was checked by deleting the branch and confirming the test - and only that
/// test - fails.
/// </para>
/// <para>
/// One branch is deliberately absent. <c>Walk.Enqueue</c> re-enqueues a node that was reached
/// restricted and is later reached freely, so the downward edges skipped on the restricted visit
/// are taken after all. Suppressing that re-walk fails nothing in either suite, and no arrangement
/// of interfaces tried here reaches it: a node is marked restricted only by an upward hop, and
/// every other way of arriving at one - a reference from a caller, the containment fan-out from
/// its own type - already enqueues it free the first time. It is kept because it fails safe and
/// costs nothing, and recorded here so it is not mistaken for covered ground.
/// </para>
/// </remarks>
public sealed class ImpactSelectorTests
{
    [Fact]
    public void Generalisation_is_a_fixpoint_and_not_a_single_pass()
    {
        // Two hops are needed and neither is reachable in one round. Nothing calls Store.Read
        // directly, so the unqualified walk stops at the seed. Round one generalises it to
        // IStore.Read and reaches Repository.Load - which is allowed through the bound only
        // because Repository.Load is itself what constructs the Store. Repository.Load then
        // implements IRepository.Load, and *that* hop can only be taken in a later round, because
        // Repository.Load was not impacted when the first round took its snapshot. Collapse the
        // loop to one pass and Consumer.Use is silently dropped.
        var compilation = CompilationHarness.CompileValid("""
            namespace App
            {
                public interface IStore { int Read(); }
                public sealed class Store : IStore { public int Read() => 1; }

                public interface IRepository { int Load(IStore store); }
                public sealed class Repository : IRepository
                {
                    public int Load(IStore store)
                    {
                        IStore fallback = new Store();
                        return store.Read() + fallback.Read();
                    }
                }

                public sealed class Consumer { public int Use(IRepository repository) => repository.Load(null!); }
            }
            """);

        var graph = CompilationHarness.BuildGraph(compilation);
        var impacted = new ImpactSelector()
            .Traverse(graph, [CompilationHarness.KeyOf(compilation, "App.Store", "Read")]).Impacted;

        Assert.Contains(CompilationHarness.KeyOf(compilation, "App.Repository", "Load"), impacted);
        Assert.Contains(CompilationHarness.KeyOf(compilation, "App.Consumer", "Use"), impacted);
    }

    [Fact]
    public void A_change_to_the_declaration_itself_is_never_restricted()
    {
        // The restriction exists to stop a change to one implementation reaching its siblings
        // through the declaration they share. A change to the declaration is the opposite case and
        // must not inherit that caution: every implementation can be affected, and so can callers
        // that hold the interface and callers that name a concrete type. Restriction is a property
        // of how a node was arrived at, never of the node, and this is what would break if that
        // were confused.
        var compilation = CompilationHarness.CompileValid("""
            namespace App
            {
                public interface IGreeter { string Greet(); }
                public sealed class English : IGreeter { public string Greet() => "hello"; }
                public sealed class German : IGreeter { public string Greet() => "hallo"; }

                public sealed class Caller
                {
                    public string Direct() => new English().Greet();
                    public string Polymorphic(IGreeter greeter) => greeter.Greet();
                }
            }
            """);

        var graph = CompilationHarness.BuildGraph(compilation);
        var impacted = new ImpactSelector()
            .Traverse(graph, [CompilationHarness.KeyOf(compilation, "App.IGreeter", "Greet")]).Impacted;

        // Downward from the interface: both implementations, and the caller that names one of them.
        Assert.Contains(CompilationHarness.KeyOf(compilation, "App.English", "Greet"), impacted);
        Assert.Contains(CompilationHarness.KeyOf(compilation, "App.German", "Greet"), impacted);
        Assert.Contains(CompilationHarness.KeyOf(compilation, "App.Caller", "Direct"), impacted);
        Assert.Contains(CompilationHarness.KeyOf(compilation, "App.Caller", "Polymorphic"), impacted);
    }

    [Fact]
    public void A_changed_type_with_no_containing_type_generalises_unqualified()
    {
        // When the changed node is the type itself there is no containing type to draw a bound
        // from, and the sound reading of "no bound" is "no restriction" - not "no reach". Getting
        // this backwards would drop every consumer of an interface whenever the edit landed on the
        // implementing type's declaration rather than inside one of its members, which is what a
        // new base type, a new attribute or a changed type parameter list all look like.
        var compilation = CompilationHarness.CompileValid("""
            namespace App
            {
                public interface IGreeter { string Greet(); }
                public sealed class English : IGreeter { public string Greet() => "hello"; }
                public sealed class Caller { public string Run(IGreeter greeter) => greeter.Greet(); }
            }
            """);

        var graph = CompilationHarness.BuildGraph(compilation);
        var impacted = new ImpactSelector()
            .Traverse(graph, [CompilationHarness.KeyOf(compilation, "App.English")]).Impacted;

        Assert.Contains(CompilationHarness.KeyOf(compilation, "App.Caller", "Run"), impacted);
    }

    [Fact]
    public void A_seed_that_reaches_nothing_still_reports_itself()
    {
        // An empty traversal and a traversal of one are different answers: the caller counts what
        // was reached, and a seed that vanished would read as "this change affects nothing".
        var compilation = CompilationHarness.CompileValid("""
            namespace App
            {
                public sealed class Alone { public int Value() => 1; }
            }
            """);

        var seed = CompilationHarness.KeyOf(compilation, "App.Alone", "Value");
        var traversal = new ImpactSelector().Traverse(CompilationHarness.BuildGraph(compilation), [seed]);

        Assert.Contains(seed, traversal.Impacted);
        Assert.Equal([seed], traversal.Seeds);
    }

    [Fact]
    public void A_seed_that_is_not_in_the_graph_is_not_an_error()
    {
        // A changed symbol whose project failed to load, or a member that no longer exists on the
        // new side of the diff, arrives here as a key with no node behind it. Throwing would turn
        // an ordinary diff into a full run at best.
        var graph = CompilationHarness.BuildGraph(
            CompilationHarness.CompileValid("namespace App { public sealed class C { public int V() => 1; } }"));

        var traversal = new ImpactSelector().Traverse(graph, ["App.Ghost.Vanished"]);

        Assert.Equal(["App.Ghost.Vanished"], traversal.Impacted);
        Assert.Empty(traversal.PathTo("App.Nowhere"));
    }

    [Fact]
    public void A_cycle_does_not_hang_the_path_walk()
    {
        // PathTo follows predecessors backwards, and mutual recursion is ordinary code. The guard
        // that stops it is a visited set, which has no test.
        var compilation = CompilationHarness.CompileValid("""
            namespace App
            {
                public sealed class Ping
                {
                    public int A(int n) => n <= 0 ? 0 : B(n - 1);
                    public int B(int n) => A(n - 1);
                }
            }
            """);

        var seed = CompilationHarness.KeyOf(compilation, "App.Ping", "A");
        var traversal = new ImpactSelector().Traverse(CompilationHarness.BuildGraph(compilation), [seed]);

        var path = traversal.PathTo(CompilationHarness.KeyOf(compilation, "App.Ping", "B"));

        Assert.NotEmpty(path);
        Assert.Equal(path.Select(step => step.Key).Distinct(StringComparer.Ordinal).Count(), path.Count);
    }
}
