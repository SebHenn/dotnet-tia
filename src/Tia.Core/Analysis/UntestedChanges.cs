using Tia.Core.Model;

namespace Tia.Core.Analysis;

/// <summary>
/// The changed symbols from which no test can be reached (#70).
/// </summary>
/// <remarks>
/// <para>
/// A selective run already knows this and used to keep it to itself: a change nothing tests shows
/// up only as a smaller selection, which is the one number nobody reads as a warning. A reviewer
/// wants it said - "this changed, and nothing exercises it" - and the graph is the thing that can
/// say it.
/// </para>
/// <para>
/// Advisory, and on the other side of the line from selection: nothing here changes which tests
/// run. That is also why an approximate answer is acceptable in one direction only. A symbol is
/// reported as untested only after a traversal of its own reached no test; the shortcuts below can
/// only ever call a symbol covered, so an error in them hides a warning rather than inventing one.
/// </para>
/// <para>
/// The cost is the reason for the shortcuts. The selection's traversal started from every changed
/// symbol at once, and each node it reached records one predecessor - so a test's path names only
/// the seed that happened to reach it first. Every other seed would need its own traversal, and
/// a diff touching a busy class has dozens. Most are settled without one: a seed on some test's
/// path is covered, and so is one with a plain edge onto such a path. What is left gets a traversal
/// each, up to <see cref="MaxTraversals"/>, and the rest are counted as unchecked rather than
/// silently passed.
/// </para>
/// </remarks>
public static class UntestedChanges
{
    /// <summary>How many symbols get a traversal of their own before the rest go unchecked.</summary>
    public const int MaxTraversals = 64;

    private const EdgeKind NotAShortcut =
        EdgeKind.ImplementationToInterface | EdgeKind.OverrideToVirtual |
        EdgeKind.DispatchByRequest | EdgeKind.DispatchByRoute;

    /// <param name="changed">The symbols the diff changed, without the reflecting members seeded
    /// after them: those are not changes, and none of them is a statement about this diff.</param>
    /// <param name="widenedProjects">Projects run whole because the graph could not be trusted for
    /// them. A symbol in one is not checked: an answer from the graph is exactly what was given up.</param>
    /// <param name="selector">Used for the per-symbol traversals. Not the selection's own instance,
    /// whose counters the report prints.</param>
    /// <param name="maxTraversals">Null checks every symbol.</param>
    public static (IReadOnlyList<string> Untested, int Unchecked) Find(
        ImpactGraph graph,
        IEnumerable<string> changed,
        ImpactTraversal traversal,
        IReadOnlyList<TestMethod> tests,
        IReadOnlySet<string> widenedProjects,
        ImpactSelector selector,
        int? maxTraversals = MaxTraversals,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(traversal);
        ArgumentNullException.ThrowIfNull(tests);

        var testKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var test in tests)
        {
            testKeys.Add(test.SymbolKey);
            testKeys.Add(test.ClassKey);
        }

        var onTestPath = OnTestPaths(traversal, testKeys);

        var untested = new List<string>();
        var skipped = 0;
        var traversals = 0;

        foreach (var key in changed.Distinct(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();

            // A deleted symbol has no node, and its callers changed with it or the code would not
            // compile, so they speak for it.
            if (graph.TryGetNode(key) is not { } node ||
                widenedProjects.Contains(node.ProjectName) ||
                ReachesAPath(graph, node, onTestPath))
            {
                continue;
            }

            if (traversals == maxTraversals)
            {
                skipped++;
                continue;
            }

            traversals++;
            var own = selector.Traverse(graph, [key], cancellationToken);
            if (!own.Impacted.Any(testKeys.Contains))
            {
                untested.Add(key);
            }
        }

        return (untested, skipped);
    }

    /// <summary>Every node on the path the traversal took to some test, the test included.</summary>
    private static HashSet<string> OnTestPaths(ImpactTraversal traversal, IReadOnlySet<string> testKeys)
    {
        var onPath = new HashSet<string>(StringComparer.Ordinal);

        foreach (var key in testKeys)
        {
            if (!traversal.Impacted.Contains(key))
            {
                continue;
            }

            // Stops at the first node already known, so shared prefixes are walked once.
            var current = key;
            while (onPath.Add(current) && traversal.Predecessors.TryGetValue(current, out var step))
            {
                current = step.From;
            }
        }

        return onPath;
    }

    /// <summary>
    /// True when the node is on a test's path, or one plain step from one. Upward and dispatch
    /// edges are left out: the traversal follows those only under conditions, and a shortcut that
    /// assumed them would call a symbol covered that its own traversal would not.
    /// </summary>
    private static bool ReachesAPath(ImpactGraph graph, SymbolNode node, IReadOnlySet<string> onTestPath)
    {
        if (onTestPath.Contains(node.Key))
        {
            return true;
        }

        foreach (var (dependent, kind) in graph.DependentsOf(node.Key))
        {
            if ((kind & NotAShortcut) == 0 && onTestPath.Contains(dependent))
            {
                return true;
            }
        }

        return node.Kind == SymbolNodeKind.Type && graph.MembersOfType(node.Key).Any(onTestPath.Contains);
    }
}
