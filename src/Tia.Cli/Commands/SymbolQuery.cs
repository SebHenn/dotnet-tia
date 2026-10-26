using Tia.Core.Analysis;
using Tia.Core.Model;
using Tia.Workspace;

namespace Tia.Cli.Commands;

/// <summary>A test that reaches the symbol, and the path from the symbol to it.</summary>
public sealed record SymbolTest(string Test, IReadOnlyList<ExplainStep> Path);

/// <summary>
/// A symbol near one no test reaches, which some test does reach: how far it is from the symbol,
/// by what route, and which test reaches it in how many steps.
/// </summary>
public sealed record NearbyReached
{
    public required string Symbol { get; init; }

    /// <summary>Steps from the queried symbol, in either direction.</summary>
    public required int Distance { get; init; }

    /// <summary>From the queried symbol to this one; each step after the first says how it relates to the one before.</summary>
    public IReadOnlyList<ExplainStep> Route { get; init; } = [];

    public required string Test { get; init; }

    /// <summary>The length of the path from this symbol to <see cref="Test"/>, as <c>explain</c> prints it.</summary>
    public required int TestSteps { get; init; }
}

/// <summary>One symbol <c>explain --symbol</c> matched.</summary>
public sealed record SymbolFinding
{
    public required string Symbol { get; init; }

    public required string Kind { get; init; }

    public required string Project { get; init; }

    public string? File { get; init; }

    /// <summary>How many tests reach it. <see cref="Tests"/> lists the nearest of them.</summary>
    public int TestCount { get; init; }

    public IReadOnlyList<SymbolTest> Tests { get; init; } = [];

    /// <summary>When no test reaches it: the nearest symbols a test does reach.</summary>
    public IReadOnlyList<NearbyReached> Nearby { get; init; } = [];
}

/// <summary>What <c>explain --symbol</c> found (#98).</summary>
public sealed record SymbolResult
{
    public required string Query { get; init; }

    /// <summary>How many symbols matched. <see cref="Symbols"/> holds the first of them.</summary>
    public int Matched { get; init; }

    public IReadOnlyList<SymbolFinding> Symbols { get; init; } = [];

    /// <summary>When nothing matched: symbols whose name contains the query.</summary>
    public IReadOnlyList<string> Containing { get; init; } = [];

    public string? Error { get; init; }
}

/// <summary>
/// The reverse of <c>explain &lt;test&gt;</c> (#98): not "why was this test selected" but "what
/// reaches this symbol, and if nothing does, how close does a test get".
/// </summary>
/// <remarks>
/// <para>
/// The tests are found with the selection's own traversal, seeded with the symbol alone, so "these
/// tests reach it" is exactly "these tests would be selected if it changed". Nothing about the
/// selection changes.
/// </para>
/// <para>
/// When none does, the answer worth having is where the gap is. The search spreads out from the
/// symbol in both directions - what it uses, what declares it, what uses it - and stops at symbols
/// a test reaches. Those point at the call a new test would make, or at a missing edge. Candidates
/// are found with a cheap over-approximation (everything a test depends on, by plain edges) and
/// each is confirmed with the real traversal before it is printed, so a listed test genuinely
/// reaches the listed symbol.
/// </para>
/// </remarks>
public static class SymbolQuery
{
    public const int MaxSymbols = 5;

    public const int MaxTests = 20;

    public const int MaxNearby = 5;

    /// <summary>How far the search for a nearby reached symbol spreads.</summary>
    public const int MaxDistance = 4;

    /// <summary>How many candidates get a traversal of their own.</summary>
    private const int MaxConfirmations = 12;

    /// <param name="repositoryRoot">What file paths are printed relative to.</param>
    public static SymbolResult Run(AnalysisOutcome outcome, string query, string repositoryRoot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        ArgumentNullException.ThrowIfNull(query);

        if (outcome.Graph is not { } graph)
        {
            return new SymbolResult { Query = query, Error = "No graph is available for this analysis, so there is nothing to search." };
        }

        var matches = graph.Nodes.Values
            .Where(n => Matches(n.DisplayName, query))
            .OrderBy(n => n.DisplayName.Length)
            .ThenBy(n => n.DisplayName, StringComparer.Ordinal)
            .ThenBy(n => n.Key, StringComparer.Ordinal)
            .ToList();

        if (matches.Count == 0)
        {
            return new SymbolResult
            {
                Query = query,
                Containing =
                [
                    .. graph.Nodes.Values
                        .Where(n => n.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase))
                        .Select(n => n.DisplayName)
                        .Distinct(StringComparer.Ordinal)
                        .Order(StringComparer.Ordinal)
                        .Take(20),
                ],
            };
        }

        var context = new Context(graph, outcome.AllTests, new ImpactSelector(outcome.TypeFlow));

        return new SymbolResult
        {
            Query = query,
            Matched = matches.Count,
            Symbols = [.. matches.Take(MaxSymbols).Select(n => Find(context, n, repositoryRoot, cancellationToken))],
        };
    }

    /// <summary>A suffix of the display name, with or without its parameter list.</summary>
    public static bool Matches(string displayName, string query)
    {
        if (displayName.EndsWith(query, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var parameters = displayName.IndexOf('(', StringComparison.Ordinal);
        return parameters > 0 && displayName.AsSpan(0, parameters).EndsWith(query, StringComparison.OrdinalIgnoreCase);
    }

    private static SymbolFinding Find(Context context, SymbolNode node, string repositoryRoot, CancellationToken cancellationToken)
    {
        var own = context.Selector.Traverse(context.Graph, [node.Key], cancellationToken);
        var reached = context.TestsReachedBy(own);

        var finding = new SymbolFinding
        {
            Symbol = node.DisplayName,
            Kind = node.Kind.ToString(),
            Project = node.ProjectName,
            File = node.FilePath is { } path ? Path.GetRelativePath(repositoryRoot, path).Replace('\\', '/') : null,
            TestCount = reached.Count,
            Tests = [.. reached.Take(MaxTests).Select(t => new SymbolTest(t.Name, ExplainQuery.Steps(context.Graph, own.PathTo(t.Key))))],
        };

        return reached.Count > 0 ? finding : finding with { Nearby = Nearby(context, node, own, cancellationToken) };
    }

    private static List<NearbyReached> Nearby(Context context, SymbolNode start, ImpactTraversal own, CancellationToken cancellationToken)
    {
        var parents = new Dictionary<string, (string From, string Relation)>(StringComparer.Ordinal) { [start.Key] = (string.Empty, string.Empty) };
        var frontier = new List<string> { start.Key };
        var candidates = new List<(string Key, int Distance)>();

        for (var distance = 1; distance <= MaxDistance && frontier.Count > 0 && candidates.Count < MaxConfirmations; distance++)
        {
            var next = new List<string>();
            foreach (var key in frontier)
            {
                foreach (var (neighbour, relation) in context.Neighbours(key))
                {
                    // What the symbol reaches reaches no test either, or the symbol would.
                    if (own.Impacted.Contains(neighbour) || !parents.TryAdd(neighbour, (key, relation)))
                    {
                        continue;
                    }

                    next.Add(neighbour);
                    if (context.TestDependencies.Contains(neighbour))
                    {
                        candidates.Add((neighbour, distance));
                    }
                }
            }

            frontier = [.. next.Order(StringComparer.Ordinal)];
        }

        var nearby = new List<NearbyReached>();
        foreach (var (key, distance) in candidates.OrderBy(c => c.Distance).ThenBy(c => context.Graph.TryGetNode(c.Key)?.DisplayName, StringComparer.Ordinal).Take(MaxConfirmations))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var traversal = context.Selector.Traverse(context.Graph, [key], cancellationToken);
            var nearest = context.TestsReachedBy(traversal).FirstOrDefault();
            if (nearest is null)
            {
                continue;
            }

            nearby.Add(new NearbyReached
            {
                Symbol = context.Name(key),
                Distance = distance,
                Route = Route(context, parents, key),
                Test = nearest.Name,
                TestSteps = traversal.HopsTo(nearest.Key),
            });

            if (nearby.Count == MaxNearby)
            {
                break;
            }
        }

        return nearby;
    }

    private static List<ExplainStep> Route(Context context, Dictionary<string, (string From, string Relation)> parents, string key)
    {
        var route = new List<ExplainStep>();
        for (var current = key; parents.TryGetValue(current, out var step); current = step.From)
        {
            route.Add(new ExplainStep(context.Name(current), step.Relation.Length == 0 ? null : step.Relation));
            if (step.From.Length == 0)
            {
                break;
            }
        }

        route.Reverse();
        return route;
    }

    private sealed record ReachedTest(string Name, string Key, int Steps);

    private sealed class Context
    {
        private readonly Dictionary<string, List<string>> _dependencies = new(StringComparer.Ordinal);

        private readonly Dictionary<string, string> _testNames = new(StringComparer.Ordinal);

        public Context(ImpactGraph graph, IReadOnlyList<TestMethod> tests, ImpactSelector selector)
        {
            Graph = graph;
            Selector = selector;

            foreach (var (from, to, _) in graph.Edges)
            {
                if (!_dependencies.TryGetValue(to, out var list))
                {
                    list = [];
                    _dependencies[to] = list;
                }

                list.Add(from);
            }

            // The method first, so that a test reached through its own node is named for itself
            // rather than for the first test of its class.
            foreach (var test in tests)
            {
                _testNames.TryAdd(test.SymbolKey, test.FullyQualifiedName);
            }

            foreach (var test in tests)
            {
                _testNames.TryAdd(test.ClassKey, test.FullyQualifiedName);
            }

            TestDependencies = DependencyClosure(_testNames.Keys);
        }

        public ImpactGraph Graph { get; }

        public ImpactSelector Selector { get; }

        /// <summary>
        /// Everything a test depends on by plain edges. An over-approximation of what a test
        /// reaches, used only to find candidates; each is confirmed by a real traversal.
        /// </summary>
        public HashSet<string> TestDependencies { get; }

        public string Name(string key) => Graph.TryGetNode(key)?.DisplayName ?? key;

        public IEnumerable<(string Key, string Relation)> Neighbours(string key)
        {
            // Declaration is not an edge: the traversal fans a type out to its members itself. It is
            // the neighbour that matters most, though - a new method on a tested type is one step
            // from a type every test of it reaches.
            if (Graph.TryGetNode(key)?.ContainingTypeKey is { } containing)
            {
                yield return (containing, "declared by");
            }

            foreach (var member in Graph.MembersOfType(key))
            {
                yield return (member, "declares");
            }

            foreach (var dependency in _dependencies.GetValueOrDefault(key, []))
            {
                yield return (dependency, "uses");
            }

            foreach (var dependent in Graph.DependentsOf(key).Keys)
            {
                yield return (dependent, "used by");
            }
        }

        /// <summary>The tests a traversal reached, nearest first.</summary>
        public List<ReachedTest> TestsReachedBy(ImpactTraversal traversal) =>
        [
            .. _testNames
                .Where(t => traversal.Impacted.Contains(t.Key))
                .Select(t => new ReachedTest(t.Value, t.Key, traversal.HopsTo(t.Key)))
                .GroupBy(t => t.Name, StringComparer.Ordinal)
                .Select(g => g.MinBy(t => t.Steps)!)
                .OrderBy(t => t.Steps)
                .ThenBy(t => t.Name, StringComparer.Ordinal),
        ];

        private HashSet<string> DependencyClosure(IEnumerable<string> roots)
        {
            var seen = new HashSet<string>(roots, StringComparer.Ordinal);
            var pending = new Stack<string>(seen);
            while (pending.Count > 0)
            {
                foreach (var dependency in _dependencies.GetValueOrDefault(pending.Pop(), []))
                {
                    if (seen.Add(dependency))
                    {
                        pending.Push(dependency);
                    }
                }
            }

            return seen;
        }
    }
}
