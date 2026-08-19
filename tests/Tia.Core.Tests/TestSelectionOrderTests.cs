using Tia.Core.Analysis;
using Tia.Core.Model;

namespace Tia.Core.Tests;

/// <summary>
/// Ordering the selection, which is the only value this tool has to offer when selection picks
/// everything - and the only lever here that cannot cause a miss, because it skips nothing.
/// </summary>
public class TestSelectionOrderTests
{
    private const string Seed = "Lib|M:App.Widget.Value";

    /// <summary>
    /// A chain: the change is in <c>Widget.Value</c>, <c>Near</c> tests it directly, and
    /// <c>Far</c> only reaches it through two more members.
    /// </summary>
    private static ImpactGraph Chain()
    {
        var graph = new ImpactGraph();

        void Node(string key, SymbolNodeKind kind, string? containingType = null) =>
            graph.AddNode(new SymbolNode
            {
                Key = key,
                DisplayName = key,
                Kind = kind,
                ProjectName = "Tests",
                ContainingTypeKey = containingType,
            });

        Node(Seed, SymbolNodeKind.Method);
        Node("Lib|M:App.Middle.Call", SymbolNodeKind.Method);
        Node("Lib|M:App.Outer.Call", SymbolNodeKind.Method);
        Node("Tests|T:App.NearTests", SymbolNodeKind.Type);
        Node("Tests|M:App.NearTests.Near", SymbolNodeKind.Method, "Tests|T:App.NearTests");
        Node("Tests|T:App.FarTests", SymbolNodeKind.Type);
        Node("Tests|M:App.FarTests.Far", SymbolNodeKind.Method, "Tests|T:App.FarTests");

        graph.AddEdge(Seed, "Tests|M:App.NearTests.Near", EdgeKind.Reference);
        graph.AddEdge(Seed, "Lib|M:App.Middle.Call", EdgeKind.Reference);
        graph.AddEdge("Lib|M:App.Middle.Call", "Lib|M:App.Outer.Call", EdgeKind.Reference);
        graph.AddEdge("Lib|M:App.Outer.Call", "Tests|M:App.FarTests.Far", EdgeKind.Reference);

        return graph;
    }

    private static TestMethod Test(string className, string methodName) => new()
    {
        SymbolKey = $"Tests|M:App.{className}.{methodName}",
        ClassKey = $"Tests|T:App.{className}",
        Namespace = "App",
        ClassName = className,
        MethodName = methodName,
        ProjectName = "Tests",
        Framework = TestFramework.XUnitV3,
    };

    [Fact]
    public void The_nearest_test_runs_first()
    {
        var traversal = new ImpactSelector().Traverse(Chain(), [Seed]);

        var ordered = TestSelection.InRunOrder([Test("FarTests", "Far"), Test("NearTests", "Near")], traversal, new HashSet<string>());

        Assert.Equal(["App.NearTests.Near", "App.FarTests.Far"], ordered.Select(t => t.FullyQualifiedName));
    }

    [Fact]
    public void Hops_are_the_length_of_the_path_explain_would_print()
    {
        // The rank is not a separate notion of distance - it is the number of steps in the
        // derivation the tool can already show, which is what makes it checkable by a reader.
        var traversal = new ImpactSelector().Traverse(Chain(), [Seed]);

        var near = TestSelection.HopsTo(traversal, Test("NearTests", "Near"));
        var far = TestSelection.HopsTo(traversal, Test("FarTests", "Far"));

        Assert.Equal(traversal.PathTo("Tests|M:App.NearTests.Near").Count, near);
        Assert.Equal(traversal.PathTo("Tests|M:App.FarTests.Far").Count, far);
        Assert.True(near < far);
    }

    [Fact]
    public void A_test_selected_only_because_its_project_was_widened_runs_last()
    {
        // Nothing connected it to the change except a rule that gave up, so it is the weakest
        // candidate in the run - and it must still be in the run, or widening would stop being a
        // safety net.
        var traversal = new ImpactSelector().Traverse(Chain(), [Seed]);
        var widened = new HashSet<string>(StringComparer.Ordinal) { "Tests" };

        var ordered = TestSelection.InRunOrder(
            [Test("AaaUnrelatedTests", "Untouched"), Test("NearTests", "Near")], traversal, widened);

        Assert.Equal(["App.NearTests.Near", "App.AaaUnrelatedTests.Untouched"], ordered.Select(t => t.FullyQualifiedName));
    }

    [Fact]
    public void An_unreached_test_is_not_selected_at_all_when_nothing_was_widened()
    {
        var traversal = new ImpactSelector().Traverse(Chain(), [Seed]);

        var ordered = TestSelection.InRunOrder(
            [Test("UnrelatedTests", "Untouched"), Test("NearTests", "Near")], traversal, new HashSet<string>());

        Assert.Equal(["App.NearTests.Near"], ordered.Select(t => t.FullyQualifiedName));
    }

    [Fact]
    public void Tests_the_same_distance_away_are_ordered_by_name()
    {
        // Without a tie-break the order is whatever discovery returned, and a measurement taken
        // over an unstable order measures the order as much as the ranking.
        var graph = new ImpactGraph();
        graph.AddNode(new SymbolNode { Key = Seed, DisplayName = Seed, Kind = SymbolNodeKind.Method, ProjectName = "Lib" });

        foreach (var name in new[] { "Bravo", "Alpha", "Charlie" })
        {
            graph.AddNode(new SymbolNode
            {
                Key = $"Tests|M:App.{name}Tests.Runs",
                DisplayName = name,
                Kind = SymbolNodeKind.Method,
                ProjectName = "Tests",
                ContainingTypeKey = $"Tests|T:App.{name}Tests",
            });
            graph.AddEdge(Seed, $"Tests|M:App.{name}Tests.Runs", EdgeKind.Reference);
        }

        var traversal = new ImpactSelector().Traverse(graph, [Seed]);

        var ordered = TestSelection.InRunOrder(
            [Test("CharlieTests", "Runs"), Test("BravoTests", "Runs"), Test("AlphaTests", "Runs")],
            traversal,
            new HashSet<string>());

        Assert.Equal(
            ["App.AlphaTests.Runs", "App.BravoTests.Runs", "App.CharlieTests.Runs"],
            ordered.Select(t => t.FullyQualifiedName));
    }

    [Fact]
    public void A_test_reached_only_through_its_class_ranks_by_the_class()
    {
        // The change touched something the whole fixture shares, not this test, so the class is
        // genuinely how far away it is.
        var graph = new ImpactGraph();
        graph.AddNode(new SymbolNode { Key = Seed, DisplayName = Seed, Kind = SymbolNodeKind.Method, ProjectName = "Lib" });
        graph.AddNode(new SymbolNode
        {
            Key = "Tests|T:App.FixtureTests",
            DisplayName = "FixtureTests",
            Kind = SymbolNodeKind.Type,
            ProjectName = "Tests",
        });
        graph.AddEdge(Seed, "Tests|T:App.FixtureTests", EdgeKind.Reference);

        var traversal = new ImpactSelector().Traverse(graph, [Seed]);
        var test = Test("FixtureTests", "Runs");

        Assert.False(traversal.Impacted.Contains(test.SymbolKey));
        Assert.Equal(traversal.PathTo(test.ClassKey).Count, TestSelection.HopsTo(traversal, test));
    }
}
