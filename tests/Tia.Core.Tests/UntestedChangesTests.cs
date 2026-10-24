using Tia.Core.Analysis;
using Tia.Core.Model;

namespace Tia.Core.Tests;

/// <summary>
/// Changed symbols no test reaches (#70). Advisory, so the property that matters is one-sided: a
/// symbol some test reaches must never be reported, and one nothing reaches must be.
/// </summary>
public sealed class UntestedChangesTests
{
    private const string Tested = "Lib|M:App.Widget.Tested";
    private const string AlsoTested = "Lib|M:App.Widget.AlsoTested";
    private const string Orphan = "Lib|M:App.Widget.Orphan";
    private const string Test = "Tests|M:App.WidgetTests.Works";
    private const string TestClass = "Tests|T:App.WidgetTests";

    private static readonly TestMethod WidgetTest = new()
    {
        SymbolKey = Test,
        ClassKey = TestClass,
        Namespace = "App",
        ClassName = "WidgetTests",
        MethodName = "Works",
        ProjectName = "Tests",
        Framework = TestFramework.XUnitV3,
    };

    private static ImpactGraph Graph(Action<ImpactGraph> edges)
    {
        var graph = new ImpactGraph();
        Node(graph, "Lib|T:App.Widget", SymbolNodeKind.Type, "Lib");
        Node(graph, Tested, SymbolNodeKind.Method, "Lib", "Lib|T:App.Widget");
        Node(graph, AlsoTested, SymbolNodeKind.Method, "Lib", "Lib|T:App.Widget");
        Node(graph, Orphan, SymbolNodeKind.Method, "Lib", "Lib|T:App.Widget");
        Node(graph, TestClass, SymbolNodeKind.Type, "Tests");
        Node(graph, Test, SymbolNodeKind.Method, "Tests", TestClass);
        edges(graph);
        return graph;
    }

    private static void Node(ImpactGraph graph, string key, SymbolNodeKind kind, string project, string? containingType = null) =>
        graph.AddNode(new SymbolNode
        {
            Key = key,
            DisplayName = key,
            Kind = kind,
            ProjectName = project,
            ContainingTypeKey = containingType,
        });

    private static (IReadOnlyList<string> Untested, int Unchecked) Find(
        ImpactGraph graph, string[] changed, IReadOnlySet<string>? widened = null, int? maxTraversals = UntestedChanges.MaxTraversals)
    {
        var traversal = new ImpactSelector().Traverse(graph, changed);
        return UntestedChanges.Find(graph, changed, traversal, [WidgetTest], widened ?? new HashSet<string>(),
            new ImpactSelector(), maxTraversals);
    }

    [Fact]
    public void A_change_nothing_reaches_is_reported()
    {
        var graph = Graph(g => g.AddEdge(Tested, Test, EdgeKind.Reference));

        var (untested, skipped) = Find(graph, [Tested, Orphan]);

        Assert.Equal([Orphan], untested);
        Assert.Equal(0, skipped);
    }

    [Fact]
    public void A_seed_whose_test_was_first_reached_from_another_seed_is_still_covered()
    {
        // Both call into the test's path, but the shared traversal records only one predecessor for
        // the test, so the test's path names only one of them.
        var graph = Graph(g =>
        {
            g.AddEdge(Tested, Test, EdgeKind.Reference);
            g.AddEdge(AlsoTested, Test, EdgeKind.Reference);
        });

        var (untested, _) = Find(graph, [Tested, AlsoTested]);

        Assert.Empty(untested);
    }

    [Fact]
    public void A_seed_several_steps_from_a_test_through_another_seeds_path_is_covered()
    {
        const string Middle = "Lib|M:App.Widget.Middle";
        var graph = Graph(g =>
        {
            Node(g, Middle, SymbolNodeKind.Method, "Lib", "Lib|T:App.Widget");
            g.AddEdge(Tested, Middle, EdgeKind.Reference);
            g.AddEdge(Middle, Test, EdgeKind.Reference);
            g.AddEdge(AlsoTested, "Lib|M:App.Widget.Hop", EdgeKind.Reference);
            Node(g, "Lib|M:App.Widget.Hop", SymbolNodeKind.Method, "Lib", "Lib|T:App.Widget");
            g.AddEdge("Lib|M:App.Widget.Hop", Middle, EdgeKind.Reference);
        });

        // AlsoTested is two steps from the path, so the shortcut cannot settle it; its own
        // traversal must.
        var (untested, _) = Find(graph, [Tested, AlsoTested]);

        Assert.Empty(untested);
    }

    [Fact]
    public void An_implementation_reached_through_its_interface_is_covered_by_its_own_traversal()
    {
        // Caller reaches the test through the interface by an ordinary call, so the shared path
        // runs Caller -> Run -> test. Orphan's only step onto it is the upward edge, which the
        // shortcut ignores, so its own traversal settles it: the hop is bounded by what can reach
        // the implementing type, and the test constructs one.
        const string Interface = "Lib|M:App.IWidget.Run";
        const string Caller = "Lib|M:App.Caller.Use";
        var graph = Graph(g =>
        {
            Node(g, "Lib|T:App.IWidget", SymbolNodeKind.Type, "Lib");
            Node(g, Interface, SymbolNodeKind.Method, "Lib", "Lib|T:App.IWidget");
            Node(g, "Lib|T:App.Caller", SymbolNodeKind.Type, "Lib");
            Node(g, Caller, SymbolNodeKind.Method, "Lib", "Lib|T:App.Caller");
            g.AddEdge(Caller, Interface, EdgeKind.Reference);
            g.AddEdge(Orphan, Interface, EdgeKind.ImplementationToInterface);
            g.AddEdge(Interface, Test, EdgeKind.Reference);
            g.AddEdge("Lib|T:App.Widget", Test, EdgeKind.Reference);
        });

        var (untested, _) = Find(graph, [Caller, Orphan]);

        Assert.Empty(untested);
    }

    [Fact]
    public void An_implementation_nothing_obtains_is_not_covered_through_its_interface()
    {
        // The same interface call, but the test never gets hold of a Widget. The bound falls back to
        // everything the interface reaches only when nothing outside the type names it; something
        // does (Other), and it is not a test, so the hop is bounded away from the test.
        const string Interface = "Lib|M:App.IWidget.Run";
        const string Other = "Lib|M:App.Factory.Make";
        const string Caller = "Lib|M:App.Caller.Use";
        var graph = Graph(g =>
        {
            Node(g, "Lib|T:App.IWidget", SymbolNodeKind.Type, "Lib");
            Node(g, Interface, SymbolNodeKind.Method, "Lib", "Lib|T:App.IWidget");
            Node(g, "Lib|T:App.Factory", SymbolNodeKind.Type, "Lib");
            Node(g, Other, SymbolNodeKind.Method, "Lib", "Lib|T:App.Factory");
            Node(g, "Lib|T:App.Caller", SymbolNodeKind.Type, "Lib");
            Node(g, Caller, SymbolNodeKind.Method, "Lib", "Lib|T:App.Caller");
            g.AddEdge(Caller, Interface, EdgeKind.Reference);
            g.AddEdge(Orphan, Interface, EdgeKind.ImplementationToInterface);
            g.AddEdge(Interface, Test, EdgeKind.Reference);
            g.AddEdge("Lib|T:App.Widget", Other, EdgeKind.Reference);
        });

        var (untested, _) = Find(graph, [Caller, Orphan]);

        Assert.Equal([Orphan], untested);
    }

    [Fact]
    public void A_changed_test_is_covered_by_itself()
    {
        var graph = Graph(_ => { });

        var (untested, _) = Find(graph, [Test]);

        Assert.Empty(untested);
    }

    [Fact]
    public void A_deleted_symbol_is_not_reported()
    {
        var graph = Graph(_ => { });

        var (untested, _) = Find(graph, ["Lib|M:App.Widget.Gone"]);

        Assert.Empty(untested);
    }

    [Fact]
    public void A_symbol_in_a_widened_project_is_not_reported()
    {
        // The project runs whole because the graph could not be trusted there, and this answer
        // would come from the graph.
        var graph = Graph(_ => { });

        var (untested, _) = Find(graph, [Orphan], widened: new HashSet<string> { "Lib" });

        Assert.Empty(untested);
    }

    [Fact]
    public void Past_the_cap_symbols_are_counted_as_unchecked_not_passed()
    {
        var graph = Graph(_ => { });

        var (untested, skipped) = Find(graph, [Orphan, AlsoTested], maxTraversals: 1);

        Assert.Equal([Orphan], untested);
        Assert.Equal(1, skipped);
    }

    [Fact]
    public void With_no_cap_every_symbol_is_checked()
    {
        var graph = Graph(_ => { });

        var (untested, skipped) = Find(graph, [Orphan, AlsoTested], maxTraversals: null);

        Assert.Equal([Orphan, AlsoTested], untested);
        Assert.Equal(0, skipped);
    }
}
