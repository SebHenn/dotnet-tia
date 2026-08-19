using Tia.Core.Model;

namespace Tia.Core.Analysis;

/// <summary>
/// Which tests a traversal selects, and the order they should run in.
/// </summary>
/// <remarks>
/// <para>
/// Ordering is the only thing this tool can offer a repository where selection picks everything.
/// When <c>f</c> is 1 a selective run costs <c>A + fT</c> = <c>A + T</c> against <c>T</c>, so it
/// loses for any analysis cost whatsoever - no amount of making the analysis cheaper can change
/// that, and this file exists because that arithmetic has no other way out. Running the likeliest
/// failure first skips nothing, so unlike every other lever here it cannot introduce a miss.
/// </para>
/// <para>
/// Rank is hops from the change along the path the traversal actually took, which is deliberately
/// the path <c>explain</c> prints rather than a separate notion of distance: a rank whose
/// derivation a reader cannot see is a number to be taken on faith, and this one has a printable
/// proof.
/// </para>
/// </remarks>
public static class TestSelection
{
    /// <summary>
    /// How far a test sits from the change, or <see cref="int.MaxValue"/> when the traversal never
    /// reached it.
    /// </summary>
    /// <remarks>
    /// A test whose own method was reached ranks by that. One reached only through its class ranks
    /// by the class, which is right: the change touched something the whole fixture shares rather
    /// than this test. The nearer of the two wins, because reaching a test by two routes does not
    /// make it further away.
    /// </remarks>
    public static int HopsTo(ImpactTraversal traversal, TestMethod test)
    {
        ArgumentNullException.ThrowIfNull(traversal);
        ArgumentNullException.ThrowIfNull(test);

        return Math.Min(traversal.HopsTo(test.SymbolKey), traversal.HopsTo(test.ClassKey));
    }

    /// <summary>The impacted tests, nearest to the change first.</summary>
    /// <param name="widenedProjects">
    /// Projects selected whole because something defeated symbol granularity. A test that only
    /// qualifies through one of these was never reached by the traversal, so it sorts last - which
    /// is right: nothing connected it to the change except a rule that gave up.
    /// </param>
    public static List<TestMethod> InRunOrder(
        IReadOnlyList<TestMethod> allTests,
        ImpactTraversal traversal,
        IReadOnlySet<string> widenedProjects)
    {
        ArgumentNullException.ThrowIfNull(allTests);
        ArgumentNullException.ThrowIfNull(widenedProjects);

        var selected = new List<(TestMethod Test, int Hops)>();

        foreach (var test in allTests)
        {
            var hops = HopsTo(traversal, test);

            if (hops < int.MaxValue || widenedProjects.Contains(test.ProjectName))
            {
                selected.Add((test, hops));
            }
        }

        // Name as the tie-break, so a run is reproducible: two tests the same distance from the
        // change would otherwise be ordered by whatever discovery happened to return, and a metric
        // measured over an unstable order measures the order as much as the ranking.
        return
        [
            .. selected
                .OrderBy(s => s.Hops)
                .ThenBy(s => s.Test.FullyQualifiedName, StringComparer.Ordinal)
                .Select(s => s.Test),
        ];
    }
}
