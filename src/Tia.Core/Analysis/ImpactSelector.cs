using Tia.Core.Model;

namespace Tia.Core.Analysis;

public sealed record ImpactTraversal
{
    public required IReadOnlySet<string> Impacted { get; init; }

    public required IReadOnlySet<string> Seeds { get; init; }

    /// <summary>For every reached node, the node it was reached from and the edge that led there.
    /// This is what <c>explain</c> replays.</summary>
    public required IReadOnlyDictionary<string, (string From, EdgeKind Kind)> Predecessors { get; init; }

    /// <summary>Walks back from a node to the seed it was reached from, seed first.</summary>
    public IReadOnlyList<(string Key, EdgeKind IncomingEdge)> PathTo(string key)
    {
        if (!Impacted.Contains(key))
        {
            return [];
        }

        var path = new List<(string, EdgeKind)>();
        var current = key;
        var incoming = EdgeKind.None;
        var guard = 0;

        while (guard++ < 4096)
        {
            path.Add((current, incoming));
            if (!Predecessors.TryGetValue(current, out var step))
            {
                break;
            }

            incoming = step.Kind;
            current = step.From;
        }

        path.Reverse();
        return path;
    }
}

/// <summary>
/// Breadth-first traversal of the reverse reference graph from the changed symbols.
/// </summary>
/// <remarks>
/// <para>
/// Reaching a type node fans out to the members it declares: a type is only ever reached because
/// its declaration - base list, attributes, type parameters - depends on something that changed,
/// and that affects everything inside it.
/// </para>
/// <para>
/// The traversal is not a plain reachability walk, because polymorphism edges must not compose.
/// Both directions are needed - a change to an implementation has to reach code that only knows
/// the interface, and a change to the interface has to reach every implementation - but going up
/// from one implementation and straight back down to its siblings asserts something false:
/// changing <c>EnglishGreeter.Greet</c> says nothing about <c>GermanGreeter.Greet</c>. So a node
/// reached by an upward edge is marked restricted and may not be left by a downward one.
/// </para>
/// </remarks>
public sealed class ImpactSelector
{
    private const EdgeKind Upward = EdgeKind.ImplementationToInterface | EdgeKind.OverrideToVirtual;

    private const EdgeKind Downward = EdgeKind.InterfaceToImplementation | EdgeKind.VirtualToOverride;

    public ImpactTraversal Traverse(ImpactGraph graph, IEnumerable<string> seeds, CancellationToken cancellationToken = default)
    {
        var seedSet = new HashSet<string>(seeds, StringComparer.Ordinal);

        // A node reached without restriction dominates the same node reached with one, so the two
        // states are tracked separately and a free arrival can supersede a restricted one.
        var free = new HashSet<string>(seedSet, StringComparer.Ordinal);
        var restricted = new HashSet<string>(StringComparer.Ordinal);
        var predecessors = new Dictionary<string, (string From, EdgeKind Kind)>(StringComparer.Ordinal);

        var queue = new Queue<(string Key, bool Restricted)>(seedSet.Select(s => (s, false)));

        while (queue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (current, isRestricted) = queue.Dequeue();

            foreach (var (dependent, kind) in graph.DependentsOf(current))
            {
                // An edge that is purely a polymorphism edge in one direction carries only that
                // meaning; one that also records a real reference is a real reference too.
                if (isRestricted && IsOnly(kind, Downward))
                {
                    continue;
                }

                Enqueue(dependent, IsOnly(kind, Upward), current, kind);
            }

            if (graph.TryGetNode(current)?.Kind != SymbolNodeKind.Type)
            {
                continue;
            }

            foreach (var member in graph.MembersOfType(current))
            {
                // The restriction blocks the immediate downward hop only - that is the whole of
                // the false inference - so it does not travel with any other kind of edge.
                Enqueue(member, nextRestricted: false, current, EdgeKind.Containment);
            }
        }

        free.UnionWith(restricted);

        return new ImpactTraversal
        {
            Impacted = free,
            Seeds = seedSet,
            Predecessors = predecessors,
        };

        void Enqueue(string key, bool nextRestricted, string from, EdgeKind kind)
        {
            if (free.Contains(key))
            {
                return;
            }

            if (nextRestricted)
            {
                if (!restricted.Add(key))
                {
                    return;
                }
            }
            else
            {
                // Arriving unrestricted at a node previously reached restricted opens up edges
                // that were skipped, so it has to be walked again.
                restricted.Remove(key);
                free.Add(key);
            }

            predecessors.TryAdd(key, (from, kind));
            queue.Enqueue((key, nextRestricted));
        }
    }

    private static bool IsOnly(EdgeKind kind, EdgeKind mask) => (kind & mask) != 0 && (kind & ~mask) == 0;
}
