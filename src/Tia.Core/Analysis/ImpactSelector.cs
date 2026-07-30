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
/// Reaching a type node fans out to the members it declares: a type is only ever reached because
/// its declaration - base list, attributes, type parameters - depends on something that changed,
/// and that affects everything inside it.
/// </remarks>
public sealed class ImpactSelector
{
    public ImpactTraversal Traverse(ImpactGraph graph, IEnumerable<string> seeds, CancellationToken cancellationToken = default)
    {
        var seedSet = new HashSet<string>(seeds, StringComparer.Ordinal);
        var impacted = new HashSet<string>(seedSet, StringComparer.Ordinal);
        var predecessors = new Dictionary<string, (string From, EdgeKind Kind)>(StringComparer.Ordinal);
        var queue = new Queue<string>(seedSet);

        while (queue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = queue.Dequeue();

            foreach (var (dependent, kind) in graph.DependentsOf(current))
            {
                if (!impacted.Add(dependent))
                {
                    continue;
                }

                predecessors[dependent] = (current, kind);
                queue.Enqueue(dependent);
            }

            if (graph.TryGetNode(current)?.Kind != SymbolNodeKind.Type)
            {
                continue;
            }

            foreach (var member in graph.MembersOfType(current))
            {
                if (!impacted.Add(member))
                {
                    continue;
                }

                predecessors[member] = (current, EdgeKind.Containment);
                queue.Enqueue(member);
            }
        }

        return new ImpactTraversal
        {
            Impacted = impacted,
            Seeds = seedSet,
            Predecessors = predecessors,
        };
    }
}
